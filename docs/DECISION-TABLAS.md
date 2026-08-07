# ¿Hacen falta tablas nuevas? ¿Se puede reutilizar `GoogleDriveFotosArticulos`?

Evaluación con números medidos, no con opiniones. Incluye el requisito de **filtrar por local**.

> ## ⚠️ VEREDICTO FINAL, corregido — leer §8
> Este documento concluía en el §4 que hacían falta **5 tablas + un job**. **Se revirtió.** La decisión
> vigente es **2 tablas y ningún job**: lecturas en vivo con `OutputCache`, y almacenar únicamente lo
> que no existe en ninguna otra parte. El razonamiento y los tres datos que lo dieron vuelta están en el
> **[§8](#8-veredicto-final-en-vivo-con-2-tablas)**. Los §1 a §7 siguen siendo válidos como análisis y
> como diseño de escalamiento — solo el veredicto del §4 quedó viejo.

---

## 1. Primero, el requisito nuevo: el catálogo es distinto en cada local

| Métrica | Valor |
|---|---|
| Artículos armados en **LURO** | **726** |
| Artículos armados en **PERALTA** | **715** |
| En **un solo** local | **529 (54%)** |
| En **ambos** locales | 456 |
| Pares (artículo, local) | **1.441** |

**Más de la mitad del catálogo está en un solo local.** Eso convierte "filtrar por local" en un requisito
de fondo, no un adorno: si mostrás un catálogo único, más de un tercio de lo que ve un cliente de Luro
no está en Peralta.

Y cambia la forma del modelo: el universo **no es un conjunto de `ARTCOD`**, es un conjunto de pares
**(artículo, local)**.

---

## 2. ¿Qué tan mal estaría sin tablas nuevas? — medido

Armé la query en vivo real de la grilla —página 1 de `/catalogo/indumentaria/mujer` en LURO, con
universo por mapeos + `ART` + `TIPOART` + `CATEGART` + `FAMILIA` + `PRECIOAR` + foto— y la corrí tres
veces:

| Query en vivo | Tiempo (3 corridas) |
|---|---|
| Grilla, página 1 (48 items) | **335 / 298 / 308 ms** |
| Contadores de **una** faceta (familia) | **333 / 304 / 303 ms** |

O sea: **~600 ms de SQL por vista de página**, y eso con **una sola** faceta. Sumando talle, color y
rango de precio, cada carga de página pasa el segundo.

Contra leer de una tabla `CatalogoWeb` indexada de 1.441 filas: **milisegundos de un dígito.** Es una
diferencia de ~60×.

### Cómo interpretarlo, honestamente

**No es una catástrofe.** Con `OutputCache` de 15 minutos, los hits cacheados cuestan cero, y 300 ms en
un miss es tolerable. Si el proyecto fuera solo la grilla, se podría vivir así.

Lo que sí es un problema real:

- **La superficie de cacheo es grande.** rubro × género × local × familia × talle × color × orden ×
  página son muchísimas URLs. El *hit rate* del cache sería mediocre, y **cada miss son 600 ms sobre el
  SQL que corre la logística del depósito**.
- **Un bot recorriendo el espacio de filtros genera miles de misses.** No es hipotético: es lo que hacen
  los crawlers.
- **Los tiempos ya no bajan.** 300 ms es con la base tibia y el depósito tranquilo. En hora pico de
  logística, con backups o con un linked server lento, se degrada — y se degrada **la web pública**.

---

## 3. ¿Se puede reutilizar `GoogleDriveFotosArticulos`?

La intuición es buena: esa tabla **ya parece** una tabla de catálogo. Tiene `Categoria`, `Familia`,
`EsTop` (destacado) y —lo más tentador— **`ActivaLuro` y `ActivaPeralta`**, que es exactamente el filtro
por local que hace falta.

Pero al medirla, no sirve. Cinco razones, en orden de gravedad:

### 3.1 No tiene clave única por artículo

| Métrica | Valor |
|---|---|
| Filas totales | **7.769** |
| Códigos distintos | **4.776** |
| **Filas para el código que más tiene** | **70** |

Hay un artículo con **70 filas**. MARKETweb convive con esto resolviendo
`ROW_NUMBER() OVER (PARTITION BY Codigo ORDER BY ID DESC)` en cada consulta.

Como tabla de catálogo eso es descalificante: **no podés poner una PK en `Codigo`, ni un índice único en
`Slug`, y toda query del sitio arrastra el `ROW_NUMBER` para siempre.** El índice cubridor que hace que
la grilla sea instantánea deja de ser posible.

### 3.2 `ActivaLuro` / `ActivaPeralta` son datos rancios

| `ActivaLuro` | `ActivaPeralta` | `EsTop` | Filas |
|---|---|---|---|
| 0 | 0 | 0 | 6.728 |
| 1 | 1 | 0 | 418 |
| 1 | 0 | 0 | 353 |
| 0 | 1 | 0 | 256 |
| 1 | 1 | 1 | 10 |
| 1 | 0 | 1 | 4 |

Están poblados, así que parecen útiles. Pero **MARKETweb no los lee en ningún lado** — los busqué en
todo el código: aparecen únicamente en cuatro `INSERT` que los escriben **en 0** literal
(`FotosArticuloService`, `AsanaService`, `OrdenesService`).

O sea: son datos de algo viejo que ya nadie mantiene, y todo lo nuevo entra en 0. **Publicar el catálogo
por local usando esas columnas sería publicar información que nadie actualiza.**

(Las 785 filas con `ActivaLuro=1` no son comparables directamente con los 726 artículos medidos en Luro,
porque la tabla tiene varias filas por código. Pero el argumento no depende de esa cuenta: **el código
nunca las lee y solo las escribe en 0**, así que no hay proceso que las mantenga.)

Dato peor que dato ausente: un `0` que parece "no está en Luro" cuando en realidad significa "nadie
tocó este campo".

### 3.3 Es la forma equivocada para "por local"

Dos columnas hardcodeadas, una por sucursal. Un tercer local = `ALTER TABLE` + cambios en cada query.
MARKET **ya tiene la tabla `Ubicaciones`**; lo correcto es una tabla hija con `IDUbicacion`, que soporta
N locales sin tocar el esquema.

### 3.4 Su universo es el equivocado

4.776 códigos (todo aquello para lo que alguien gestionó una foto en algún momento) contra los **985
armados en locales**. Y al revés: 236 de los 985 **no están** en la tabla. No es el catálogo, es el
histórico de gestión de fotos.

### 3.5 La escriben dos sistemas

MARKETweb inserta y actualiza desde cuatro lugares. Si el job del catálogo también escribe ahí, tenés
**dos aplicaciones dueñas de la misma tabla**, sin código compartido que garantice la invariante de "el
job no pisa lo que escribió una persona". Es el problema que el diseño evita con la separación de dueños
([CATALOGO-SYNC.md §4](CATALOGO-SYNC.md)), pero ahora entre sistemas distintos y sin forma de imponerlo.

### Lo que sí se reutiliza

**El dato, no la tabla.** `LinkDriveDisco` sigue siendo la única fuente de la foto: el job la lee y no la
duplica. Y la tabla es un buen precedente — muestra que alguien ya vio la necesidad de un artículo
desnormalizado; solo que creció como tabla de gestión de fotos, no como catálogo.

---

## 4. Veredicto preliminar ~~(vigente)~~ → **revertido, ver §8**

> Lo que sigue en esta sección fue el veredicto inicial. **Ya no es la decisión.** Se conserva porque es
> el diseño correcto **si el catálogo crece ~5x**, y porque explica qué compra cada tabla.

**Sí valen la pena las tablas nuevas**, y son chicas:

| Tabla | Filas | Para qué |
|---|---|---|
| `CatalogoWeb` | ~985 | Un artículo. Lo que no depende del local. |
| **`CatalogoWebLocales`** | **~1.441** | **(artículo, local). El requisito nuevo.** |
| `CatalogoWebVariantes` | ~14.225 | (artículo, color, talle) |
| `CatalogoTalles` | 53 | Orden y agrupación de talles |
| `CatalogoWebSync` | 1 por corrida | Log |

**~16.700 filas en total.** Es nada. Un refresh completo las reescribe en segundos.

Las cuatro razones que lo justifican, y **ninguna es "es más rápido"** (aunque lo sea 60×):

1. **Los thumbnails hay que generarlos igual.** Ninguna grilla de 48 fotos originales funciona en
   mobile, y eso ya obliga a tener un job. Con el job andando, las tablas salen casi gratis.
2. **No hay dónde poner los campos editoriales.** `Slug`, `NombreComercial` (porque `ARTDES` dice
   `PALAZ DARLON MICRORIB DO VIVO`, que no es un título de vidriera), `Destacado`, `OcultarManual`.
3. **El precio ahora es público** y eso exige control: filtrar por `FECHAVIG <= hoy`, guardar de qué
   vigencia salió, y poder mostrar "precios actualizados al …".
4. **Hay basura que filtrar** (el pseudo-artículo `2X15000`) y un 31% sin foto que decidir. Necesitás un
   lugar donde tomar esa decisión y que quede escrita.

Y ahora una quinta: **el filtro por local multiplica el costo de la query en vivo**, porque cada local es
un universo distinto y las facetas hay que calcularlas por local.

---

## 5. `CatalogoWebLocales`

```sql
CatalogoWebLocales (
    ARTCOD      varchar(20) NOT NULL,
    IDUbicacion int         NOT NULL,   -- FK a MARKET.dbo.Ubicaciones (N locales, sin ALTER TABLE)
    Local       varchar(50) NOT NULL,   -- 'LURO' — desnormalizado para no joinear en cada query
    Slug        varchar(50) NOT NULL,   -- 'luro' — para la URL
    FechaAlta   datetime2   NOT NULL,
    FechaBaja   datetime2   NULL,       -- se desarmó de ESTE local
    Activo      bit         NOT NULL,
    PRIMARY KEY (ARTCOD, IDUbicacion)
)

CREATE INDEX IX_CatalogoWebLocales_Local ON CatalogoWebLocales (IDUbicacion, Activo) INCLUDE (ARTCOD);
```

El `MERGE` del job es el mismo patrón, pero sobre pares: el `WHEN NOT MATCHED BY SOURCE` ahora detecta
"este artículo se desarmó **de este local**", que es más fino y más correcto que lo que teníamos.

`CatalogoWeb.ArmadoEnLocales` pasa a ser derivado: `1` si tiene al menos una fila activa acá.

### Las variantes NO van por local

`CatalogoWebVariantes` sale de `DRAGONFISH_CENTRAL.COMB`, que es **el rango de talles y colores que el
artículo tiene**, no el stock de cada sucursal. Para una vidriera sin carrito, eso es lo correcto. El día
que se quiera mostrar stock real por local, ahí sí hace falta leer `DRAGONFISH_LURO.COMB` /
`DRAGONFISH_PERALTA.COMB`, que es otro proyecto.

---

## 6. Cómo se expone el local en el sitio (decisión de SEO)

Tres opciones:

| Opción | SEO | Problema |
|---|---|---|
| Local en la ruta: `/catalogo/luro/indumentaria/mujer` | ❌ | **Duplica todas las URLs indexables** con contenido casi idéntico (456 artículos están en los dos locales). Google elige una y desconfía del resto. |
| Solo un selector de local que filtra todo | ⚠️ | El contenido cambia sin que cambie la URL: mal para compartir y para indexar. |
| **Catálogo canónico único + filtro `?local=` + chip de disponibilidad** ⭐ | ✅ | Ninguno |

**Recomendada:** una sola URL canónica por rubro/género con los 678 artículos, cada card y cada ficha con
un chip **"Disponible en: Luro · Peralta"**, y `?local=luro` como filtro de refinamiento marcado
`noindex,follow` (igual que el resto de los filtros, [PLAN.md §6.bis](PLAN.md)).

Es además lo que la persona realmente quiere saber: no "mostrame el catálogo de Luro", sino **"¿puedo ir
a Luro y comprar esto?"**. El chip contesta eso en la card, sin obligar a elegir un local antes de
mirar nada.

---

## 8. Veredicto final: en vivo, con 2 tablas

El §4 concluía "5 tablas + un job". Al revisarlo, **eso era sobre-diseñar**. Tres datos lo dan vuelta:

**1. Lo caro no era lo que más se iba a replicar.** La query de mapeos son **23 ms**; los 300 ms son los
joins a Dragon. Y `DRAGONFISH_CENTRAL` **no es un linked server remoto**: está en la misma instancia SQL
que `MARKET` — por eso el código de MARKETweb lo consulta directo, mientras que a LURO y PERALTA les pega
con `OPENQUERY` a hosts `ddns.net`. Todas las lecturas del catálogo son locales a una instancia, así que
la fragilidad que justificaba aislarse es mucho menor de lo asumido.

**2. 300 ms con `OutputCache` alcanza a este volumen.** El grueso del tráfico va a ocho rutas que
concentran el 94% del catálogo (§ MEDICION.md). Esas se cachean y cuestan cero. Un catálogo de 985
artículos no necesita materialización; uno de 50.000 sí, sin discusión.

**3. El que decide: con datos en vivo el precio no puede estar viejo.** [MEDICION.md §6](MEDICION.md)
argumentaba que publicar precios volvía crítico el log de sincronización. Al revés: **si no hay
sincronización, no hay precio desactualizado posible.** El riesgo de "el job falló seis horas y el sitio
publicó precios que no vamos a honrar" desaparece por construcción. Ahora que los precios son públicos,
eso pesa más que 300 ms.

### El esquema que queda

Ver [sql/01_catalogo_schema.sql](../sql/01_catalogo_schema.sql).

| Tabla | Filas | Por qué existe |
|---|---|---|
| `CatalogoArticulo` | **rala, arranca vacía** | Overrides editoriales: `NombreComercial`, `DescripcionMarketing`, `Destacado`, `OcultarManual`. **Ni una columna duplica nada.** Solo hay fila para lo que alguien editó. |
| `CatalogoTalles` | 53 | El orden de talles no se puede derivar de nada (`L` iría antes que `M`). Se carga a mano una vez. |

Todo lo demás se lee en vivo: descripción, rubro, género, familia, precio, combo, locales, talles y
colores.

**Y el `Slug` tampoco se guarda.** Se deriva del título + `ARTCOD`, y la ruta resuelve extrayendo el
`ARTCOD` del final del slug (`/producto/buzo-plush-c-r-im013-056` → `IM013.056`). Determinístico, sin
almacenar nada y sin lookup. Si el título cambia, se resuelve igual por el `ARTCOD` y se hace 301 al slug
canónico nuevo.

### Qué se resigna, explícitamente

**`FechaAlta` / `FechaBaja`.** No se pueden calcular en vivo: `MapeoRegistro` no registra cuándo un
artículo se dio de baja, y ese dato se perdió para siempre. Consecuencias: los productos discontinuados
dan **404 pelado** en vez del 200-con-aviso → 410 que se había diseñado (§6), y no hay sección
"Novedades". **Se recupera después** con una tabla de dos fechas, si resultan importar.

**El aislamiento del SQL de logística.** Cada miss de cache pega a la base que corre el depósito. Se
mitiga con un login **read-only** y `OutputCache` generoso, pero es real.

**Acoplamiento al esquema de Dragon y al predicado de los mapeos.** Queda contenido en una sola clase
(`CatalogoService`), que es donde tiene que estar.

### El criterio para materializar (y no antes)

- El catálogo pasa de ~5.000 artículos publicables (hoy 678).
- Las rutas cacheadas dejan de cubrir el tráfico real.
- Se empieza a notar la carga en la base de logística.

Si pasa alguna, el camino está diseñado: los §4 a §6 de este documento y
[CATALOGO-SYNC.md](CATALOGO-SYNC.md) siguen siendo la respuesta correcta a ese volumen.
