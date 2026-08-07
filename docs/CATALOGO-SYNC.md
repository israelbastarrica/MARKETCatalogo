# `CatalogoWeb`: campos y sincronización

Diseño detallado de la Alternativa B del [PLAN.md](PLAN.md): las tablas que lee el sitio público y
el job que las mantiene.

> ## 📦 Documento de ESCALAMIENTO — no es el diseño vigente
> **Hoy no hay tablas materializadas ni job de sincronización.** El catálogo lee en vivo con
> `OutputCache` y guarda solo overrides editoriales — ver
> [DECISION-TABLAS.md §8](DECISION-TABLAS.md#8-veredicto-final-en-vivo-con-2-tablas).
>
> Este documento se conserva porque es el diseño correcto **si el catálogo crece ~5x**, y porque el §4
> (dueño de cada columna), el §6 (qué devuelve la URL de un producto despublicado) y el §7 (log del
> sync) son razonamientos que siguen valiendo cuando llegue ese momento.

> ### ✅ La medición ya decidió: refresh completo, sin incremental
> [MEDICION.md](MEDICION.md) midió **985 artículos y una query de universo de 0,1 s**. A ese volumen,
> **todo el aparato incremental de este documento sobra**: `SyncPendiente`, `HashOrigen`, las marcas de
> agua sobre `ID`, el filtro por `FMODIFW` y la comparación de `LastWriteTime`. El job hace **un solo
> modo: recalcular todo**, y puede correr cada 15 minutos sin que nadie lo note.
>
> Los §2 y §3 quedan como **registro de por qué** se descartó el incremental — y porque si algún día el
> catálogo crece un orden de magnitud, acá está el diseño. Lo que **sigue vigente y es lo importante**
> es el §4 (los campos y quién es dueño de cada uno), el §5 (variantes), el §6 (qué pasa con los
> despublicados) y el §7 (el log).

---

## 1. La idea de partida, y dónde se rompe

La propuesta era: **poblar la tabla una vez, y después que el job vaya marcando cuáles se muestran y
cuáles no, y aplique los cambios pendientes.**

La primera mitad es exactamente lo correcto. La segunda tiene un problema concreto: **"aplicar los
cambios pendientes" supone que los cambios se pueden detectar, y en la mitad de las fuentes no se
puede.** Verifiqué la metadata real de las tablas:

| Fuente | ¿Detecta altas? | ¿Detecta modificaciones? | ¿Detecta bajas? |
|---|---|---|---|
| `ZooLogic.ART` | `FALTAFW` ✅ | `FMODIFW` ✅ | ❌ el artículo simplemente deja de estar |
| `ZooLogic.COMB` (talles/colores) | `FALTAFW` ✅ | `FMODIFW` ✅ | ❌ la variante desaparece |
| `MapeoRegistro` | `ID` identity + `FechaHora` ✅ | — | ❌ **`Eliminado=1` no tiene fecha** |
| `GoogleDriveFotosArticulos` | `ID` identity ✅ | ❌ **no tiene ninguna columna de fecha** | ❌ |

Y **Change Tracking de SQL Server no está habilitado** en ninguna base (lo verifiqué), así que no hay
`CHANGETABLE` para apoyarse. Habilitarlo en las bases `DRAGONFISH_*` además no es una decisión nuestra:
esas bases las gobierna Zoologic.

Los dos agujeros que importan:

**Agujero 1 — las bajas son invisibles.** Cuando un artículo se desarma de un local, la fila de
`MapeoRegistro` pasa a `Eliminado=1`, pero **`FechaHora` no se toca** (es la fecha de creación) y no
hay ninguna columna que registre *cuándo* se dio de baja. Un job que pregunta "¿qué cambió desde la
última corrida?" **nunca ve la baja**. Resultado: artículos fantasma que siguen en el catálogo público
para siempre.

**Agujero 2 — la foto nueva es un UPDATE, no un INSERT.** `FotosArticuloService.GuardarFotoAsync`
hace `UPDATE ... SET LinkDriveDisco = @path WHERE ID = (el último ID de ese código)`. No inserta fila
nueva. Así que una marca de agua sobre `ID` **no detecta que se subió una foto**, y la tabla no tiene
ni una columna de fecha para apoyarse.

---

## 2. El diseño que sí funciona: job de dos velocidades

La clave es separar **qué se muestra** (barato, y hay que recalcularlo entero) de **con qué datos**
(caro, y solo para lo que hace falta).

### Paso A — Recalcular el universo completo (barato, frecuente)

Una query que devuelve **el conjunto de `ARTCOD` armados en locales hoy**. Una sola columna:

```sql
SELECT DISTINCT RTRIM(REG.ARTCOD) AS ARTCOD
FROM MARKET.dbo.MapeoRegistro     REG
INNER JOIN MARKET.dbo.Mapeo           MAP ON MAP.ID = REG.IDMapeo
INNER JOIN MARKET.dbo.Ubicaciones     U   ON U.ID   = MAP.IDUbicacion
INNER JOIN MARKET.dbo.UbicacionesTipo UT  ON UT.ID  = U.IDTipo
WHERE REG.Eliminado = 0
  AND MAP.Eliminado = 0
  AND UT.Descripcion <> 'DEPOSITO';
```

(Es el mismo predicado que ya usan `ArticulosService` y `PreciosService` en MARKETweb para "está
armado en un local" — no inventamos una definición nueva.)

Y después un `MERGE` contra `CatalogoWeb`:

```sql
MERGE MARKET.dbo.CatalogoWeb AS destino
USING #Universo AS origen ON destino.ARTCOD = origen.ARTCOD

-- Volvió a armarse: se republica y se marca para re-enriquecer.
WHEN MATCHED AND destino.ArmadoEnLocales = 0 THEN
    UPDATE SET ArmadoEnLocales = 1, FechaBaja = NULL, SyncPendiente = 1

-- Nuevo en el catálogo.
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ARTCOD, ArmadoEnLocales, SyncPendiente, FechaAlta)
    VALUES (origen.ARTCOD, 1, 1, SYSUTCDATETIME())

-- ⭐ LA LÍNEA QUE TAPA EL AGUJERO 1: está en la tabla pero ya no en el universo.
WHEN NOT MATCHED BY SOURCE AND destino.ArmadoEnLocales = 1 THEN
    UPDATE SET ArmadoEnLocales = 0, FechaBaja = SYSUTCDATETIME();
```

**`WHEN NOT MATCHED BY SOURCE` es el corazón del diseño.** Detecta las bajas sin necesitar ningún
timestamp, porque **compara conjuntos en vez de buscar cambios**. Es la diferencia entre "¿qué se
borró?" (imposible de responder) y "¿qué ya no está?" (trivial).

Costo: unos pocos miles de filas y un `MERGE` sobre una tabla chica. Corre en segundos y puede ir
cada 15 minutos sin molestar a nadie.

### Paso B — Enriquecer solo lo pendiente (caro, selectivo)

Para las filas con `SyncPendiente = 1` (nuevas, republicadas, o marcadas por el Paso C): traer
`ARTDES`, género/familia/tipo, resolver la taxonomía híbrida (§3.bis del PLAN), cargar las variantes
de `COMB` y generar los thumbnails. Al terminar: `SyncPendiente = 0, FechaSync = SYSUTCDATETIME()`.

Se puede sumar como pendiente lo que Dragon dice que cambió:

```sql
UPDATE C SET SyncPendiente = 1
FROM CatalogoWeb C
JOIN DRAGONFISH_CENTRAL.ZooLogic.ART A ON RTRIM(A.ARTCOD) = C.ARTCOD
WHERE A.FMODIFW >= CAST(C.FechaSync AS DATE);
```

> ⚠️ **`FMODIFW` tiene granularidad de día, no de segundo.** Dragon parte fecha y hora en dos
> columnas (`FMODIFW` datetime + `HMODIFW` varchar), y el código de MARKETweb lo delata: ordena por
> `FALTAFW DESC, HALTAFW DESC` — si el datetime trajera la hora, ese segundo criterio no haría falta.
> Consecuencia práctica: un job que corre cada 15 minutos **reprocesa todo lo modificado en el día,
> en cada corrida**. Para frescura intradiaria, el filtro por `FMODIFW` no ahorra casi nada.

### Paso C — Cambios sin timestamp: la fecha del archivo

Para las fotos, que no tienen ninguna columna de fecha, hay una señal gratis que la base no tiene:
**el `LastWriteTime` del archivo en disco.** `LinkDriveDisco` apunta a `D:\FotosArticulos\<COD>.jpg`,
y el filesystem sí sabe cuándo se escribió.

```
si File.GetLastWriteTimeUtc(rutaFoto) > CatalogoWeb.FechaSyncFoto
    → regenerar thumbnails y actualizar FechaSyncFoto
```

Tapa el Agujero 2 sin tocar el esquema de `GoogleDriveFotosArticulos` ni pedirle nada a nadie.

### Paso D — Resync completo, nocturno (la red de seguridad)

Una corrida que recalcula **todo**, ignorando marcas de pendiente. Existe porque **todo esquema
incremental deriva**: una corrida que falló, un `FMODIFW` que no se actualizó, una fila editada a mano
en Dragon, un thumbnail que se corrompió. Sin un full periódico, esos errores son permanentes.

---

## 3. Lo que decidió la medición

El consejo era: *empezá con solo el Paso D, medí, y agregá A/B/C únicamente si el volumen lo pide.*

**Se midió, y el volumen no lo pide.** 985 artículos, 14.225 variantes, universo en 0,1 s. Un refresh
completo corre en segundos.

Así que el job es **solo el Paso D**: recalcular todo, cada 15–30 minutos. Se elimina `SyncPendiente`,
`HashOrigen`, las marcas de agua y los filtros por fecha. Y con ellos se elimina su costo real, que
nunca fue el CPU sino **la clase de bug que deja un dato viejo en silencio durante meses**.

Igual conviene dejar `FechaSync` en la tabla: cuesta nada y sirve para mostrar "última sincronización:
hace 12 minutos".

**El Paso A no desaparece — se vuelve parte del refresh completo.** El `MERGE` con
`WHEN NOT MATCHED BY SOURCE` sigue siendo la forma correcta de detectar bajas, porque compara conjuntos.
Lo que desaparece es la idea de correrlo *en vez de* recalcular el resto.

### Y una consecuencia incómoda pero honesta

A este volumen, **el argumento de performance para tener la tabla `CatalogoWeb` desaparece**: la
Alternativa A (query en vivo con `OutputCache`) andaría bien. La tabla se justifica igual, pero por
otras tres razones, ninguna de las cuales es velocidad:

1. **Los thumbnails hay que generarlos de todos modos** → ya hace falta un job. Con el job andando, la
   tabla sale casi gratis.
2. **Los campos propios no tienen dónde vivir**: `Slug`, `NombreComercial`, `Destacado`,
   `OcultarManual`. En Dragon no se pueden guardar.
3. **Hay basura que filtrar** (§5 de MEDICION.md: el pseudo-artículo `2X15000`) y un 31% sin foto que
   decidir. Necesitás un lugar donde tomar esa decisión y dejarla escrita.

---

## 4. Los campos, agrupados por quién es el dueño

Ésta es la parte que más importa del diseño, más que la lista en sí:

> ### 🔒 Invariante central
> **El job nunca escribe una columna cuyo dueño es una persona.**
> Si el job pisa `OcultarManual` o `NombreComercial`, el trabajo de marketing se borra solo en la
> próxima corrida y nadie entiende por qué.

### Dueño: el JOB (se sobrescriben en cada sync, sin miedo)

| Campo | Tipo | Origen |
|---|---|---|
| `ARTCOD` | `varchar(20)` PK | clave |
| `Descripcion` | `varchar(200)` | `ART.ARTDES` |
| `RubroCod` / `RubroDesc` | `char(1)` / `varchar(40)` | `I`/`A`/`C` — taxonomía híbrida (PLAN §3.bis) |
| `GeneroCod` / `GeneroDesc` | `varchar(2)` / `varchar(40)` | `H`/`M`/`U`/`NE`/`NA` |
| `ProveedorCod` | `varchar(10)` | del `ARTCOD` |
| `FamiliaCod` / `FamiliaDesc` | | `ZooLogic.FAMILIA` |
| `TipoArtCod` / `TipoArtDesc` | | `ZooLogic.TIPOART` (¡no es el "Tipo" del manual!) |
| `TaxonomiaOrigen` | `varchar(20)` | `DRAGON` / `ARTCOD` / `DISCREPANCIA` — para auditar |
| `TieneFoto` | `bit` | disco primero, blob de fallback |
| `FotoWidth` / `FotoHeight` | `int` | para poner el `width`/`height` en el `<img>` y evitar saltos de layout |
| `ArmadoEnLocales` | `bit` | **Paso A**: la verdad del mapeo |
| `Locales` | `varchar(200)` | `'LURO,PERALTA'` |
| `Anio` | `int` | `ART.ANO` |

### Dueño: una PERSONA (el job no las toca jamás)

| Campo | Tipo | Para qué |
|---|---|---|
| `OcultarManual` | `bit` | Bajar un artículo del sitio a mano (foto mala, prenda discontinuada) **sin pelearse con el job** |
| `NombreComercial` | `varchar(200)` | Título lindo para el público, cuando `ARTDES` es un código interno feo |
| `DescripcionMarketing` | `varchar(1000)` | Texto de venta |
| `Destacado` | `int` | Orden en el home. `0` = no destacado |
| `Eliminado` | `bit` | Baja lógica, convención MARKET |
| `Auditoria` | `varchar(200)` | `Acción \| origen \| fecha`, convención MARKET |

### Calculado por la base (nadie lo escribe)

```sql
Publicado AS CAST(CASE WHEN ArmadoEnLocales = 1
                        AND OcultarManual   = 0
                        AND TieneFoto       = 1
                        AND Eliminado       = 0
                       THEN 1 ELSE 0 END AS BIT) PERSISTED
```

**Por qué columna calculada y no una que el job escriba:** si `Publicado` fuera una columna común,
tarde o temprano el job y una persona se pisan, o queda desincronizada de sus insumos. Siendo
`PERSISTED`, la base la mantiene siempre coherente, **es indexable**, y la regla de negocio vive en un
solo lugar en vez de repetida en cada query del sitio.

### Bookkeeping del sync (dueño: el job)

| Campo | Tipo | Para qué |
|---|---|---|
| `FechaSync` | `datetime2` | Última corrida OK sobre esta fila |
| `FechaAlta` | `datetime2` | Cuándo entró al catálogo → habilita una sección "Novedades" gratis |
| `FechaBaja` | `datetime2` | Cuándo salió → decide cuándo la URL pasa a 410 (§6) |
| `Slug` | `varchar(160)` | URL. **Se genera una vez y no se vuelve a tocar**: si cambia, se rompen los links compartidos y lo indexado |

### Índices

```sql
-- El eje de navegación (PLAN §3.bis): es el índice que más se usa.
CREATE INDEX IX_CatalogoWeb_Nav ON CatalogoWeb (Publicado, RubroCod, GeneroCod, FamiliaCod)
    INCLUDE (ARTCOD, Slug, Descripcion, NombreComercial, Destacado);
CREATE UNIQUE INDEX UX_CatalogoWeb_Slug ON CatalogoWeb (Slug);
```

El `INCLUDE` del primero cubre entera la query de la grilla: SQL la resuelve sin ir a la tabla.

---

## 5. Variantes (talles y colores)

```sql
CatalogoWebVariantes (
    ARTCOD    varchar(20)  NOT NULL,
    ColorCod  varchar(10)  NOT NULL,
    ColorDesc varchar(100) NULL,      -- DPCOLOR.DESCRIP vía ART.PALCOL + COMB.COCOL
    Talle     varchar(20)  NOT NULL,
    Orden     int          NOT NULL,  -- el manual ordena "del talle más chico al más grande"
    PRIMARY KEY (ARTCOD, ColorCod, Talle)
)
```

Se sincronizan con el mismo criterio de conjuntos: para cada `ARTCOD` pendiente, **borrar y reinsertar
sus variantes** dentro de la transacción. Son pocas filas por artículo y evita todo el problema de
detectar variantes eliminadas. Acá `DELETE` físico está bien: es una tabla derivada, no un registro de
negocio, así que no aplica la convención de baja lógica.

**`Orden` importa:** el manual dice explícitamente que la mercadería se ordena *del talle más chico al
más grande*. Alfabéticamente `L` va antes que `M` y `10` antes que `2`, así que hace falta una tabla
de orden de talles. **Punto a resolver con datos reales**: hay que ver qué talles existen (numéricos,
S/M/L/XL, o mezclados por rubro).

---

## 6. Qué pasa con un artículo que sale del catálogo

Cuando `ArmadoEnLocales` pasa a 0, la tentación es que su URL devuelva 404. **Es la decisión
equivocada**, por dos razones:

1. Los artículos se desarman y se vuelven a armar todo el tiempo (reorganización de un local). Una URL
   que alterna 404 → 200 → 404 hace que Google la desindexe y desconfíe.
2. Alguien compartió ese link por WhatsApp la semana pasada.

Propuesta:

| Estado | Grilla y sitemap | La URL directa |
|---|---|---|
| Publicado | Aparece | 200, ficha normal |
| Recién despublicado (< 90 días de `FechaBaja`) | No aparece | **200** con un cartel "este artículo ya no está disponible" + link al rubro/género |
| Despublicado hace más de 90 días | No aparece | **410 Gone** (le dice a Google que lo saque, a diferencia del 404 que sugiere "quizá vuelva") |

Esto es gratis de implementar y sale directo de tener `FechaBaja` en la tabla.

---

## 7. Log del sync (no opcional)

Ya estaba anotado en el PLAN que "el job puede fallar en silencio". Eso se arregla con una tabla:

```sql
CatalogoWebSync (
    Id            int identity PRIMARY KEY,
    Inicio        datetime2  NOT NULL,
    Fin           datetime2  NULL,      -- NULL al terminar = se murió a mitad de camino
    Modo          varchar(20) NOT NULL, -- 'COMPLETO' (hoy el único) | 'MANUAL'
    Altas         int, Bajas int, Actualizados int, Thumbnails int,
    Discrepancias int,                  -- taxonomía Dragon vs ARTCOD (PLAN §3.bis)
    Error         varchar(2000) NULL,
    MachineName   varchar(100) NOT NULL -- mismo candado que TareasProgramadasLog en MARKETweb
)
```

Con eso el sitio puede mostrar en una pantalla de admin "última sincronización OK: hace 12 minutos", y
alcanza para alertar si pasan N horas sin una corrida exitosa. **Un job de sincronización sin
observabilidad es un job que ya falló y todavía no te enteraste.**

---

## 8. Puntos a confirmar

Los cuatro primeros de la versión original **quedaron cerrados por [MEDICION.md](MEDICION.md)**:
volumen (985/678), `Historico` (siempre 0), `FMODIFW` (sin hora) y el dimensionamiento de los talles
(53 valores). Quedan:

1. **Orden de talles.** 53 valores de familias incompatibles. Se ordena a mano una vez, pero hay que
   hacerlo antes de mostrar chips de talle en la ficha.
2. **Un artículo armado en un local pero sin stock**: ¿se publica? Yo diría que sí — el catálogo es una
   vidriera, no un e-commerce, y el stock cambia varias veces por día.
3. **Frecuencia del job.** A 0,1 s por corrida, cada 15–30 minutos es gratis.
4. **Los rubros que el manual no documenta** (Lencería, Casa blanquería, Juguetería): cómo se llaman de
   cara al público, y si los tres van al menú.
