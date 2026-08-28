# Flujo de datos y consultas

Qué pasa exactamente cuando alguien navega el catálogo, qué SQL se ejecuta, y hasta dónde escala.

---

## 1. El modelo: la URL es todo el estado

No hay sesión, ni cookie, ni JavaScript guardando qué filtros están puestos. Recargar da lo mismo,
"atrás" saca el último filtro, y el link compartido muestra exactamente lo mismo.

```
/catalogo/indumentaria/mujer?familia=CAMPERA&talle=M&local=luro&orden=precio-asc&pag=2
         └──── ruta ────┘ └──────────────── refinamiento ────────────────┘
          indexable              noindex + canonical → la ruta limpia
```

---

## 2. La decisión que cambia todo: la tabla materializada ES el caché

El catálogo no se replica ni se guarda en RAM. La **tabla materializada `MARKET.dbo.Catalogo`** —con sus
tablas hijas `dbo.CatalogoTalle` y `dbo.CatalogoColor` para el talle/color (multi-valor)— **es el caché**:
una copia derivada de Dragon y de los mapeos, mantenida por un rebuild. El universo interno son
**~1.600 filas** (`Eliminado = 0`), de las cuales **~600 están publicadas**.

Es un caché **read-through** con invalidación por tiempo, no un job que corra siempre:

```
Arranque en frío ──► CatalogoBaseWarmup: 1 rebuild BLOQUEANTE ──► dbo.Catalogo poblada
                     (ningún visitante paga el primer llenado)

Cada request ──► AsegurarBaseFresca():
                   ├─ base fresca (edad < TTL) → no hace nada, se lee la tabla
                   └─ base vencida → dispara UN rebuild EN BACKGROUND y vuelve al instante
                                     (se sigue sirviendo lo último bueno; stale-while-revalidate)
```

**Política del rebuild: TTL + single-flight + stale-while-revalidate.**

- **TTL**: `Catalogo:MinutosTtl` (fallback `Catalogo:MinutosCache`, default **20 min**). Un timestamp
  global en memoria (`CatalogoStore._baseActualizada`) es el reloj; al reiniciar la app queda en null y
  el primer acceso dispara un rebuild.
- **Single-flight**: un candado (`SemaphoreSlim`) garantiza que N requests concurrentes generen **un
  solo** rebuild, no N.
- **Stale-while-revalidate**: una lectura que encuentra la base vencida no espera —dispara el rebuild en
  background y sigue sirviendo la tabla anterior. El próximo request ya ve lo nuevo.
- **Un rebuild fallido loguea y conserva lo anterior**: nunca tira la app ni pisa la tabla (el
  `GuardarBaseAsync` no borra ante universo vacío y el MERGE es atómico).

**Consecuencia: el trabajo pesado (cruzar las dos bases) ocurre como mucho una vez por TTL**, sin importar
si lo visitan 10 personas o 10.000. Las lecturas van directo a la tabla —indexada, filtrada y paginada en
SQL (§4)— así que servir la grilla no vuelve a cruzar las bases.

No hay `OutputCache` en ningún lado: la tabla es la capa de caché. Cachear las **respuestas** por URL
tendría un problema de *hit rate* (con rubro × género × 6 filtros × orden × página el espacio de URLs es
enorme y la mayoría de las combinaciones se pediría una sola vez); materializar **la tabla** hace que
cualquier combinación de filtros se resuelva contra la misma copia ya armada.

---

## 2.bis Regla de arquitectura: **nunca un JOIN entre MARKET y DRAGONFISH**

Hoy `MARKET` y `DRAGONFISH_CENTRAL` están en la misma instancia SQL, así que un `JOIN` cruzado
*funciona*. **Igual no se hace.**

El motivo es concreto: si algún día las bases se suben a la nube **separadas** (dos Azure SQL Database,
por ejemplo), el join cruzado **deja de existir**. Azure SQL Database no soporta consultas
cross-database: no hay nombres de tres partes entre bases ni linked servers como on-prem. La única
salida sería Elastic Query, que es lento y engorroso.

Un diseño que dependa del join se tendría que **reescribir entero**. Uno que no, se muda cambiando dos
líneas de configuración.

Por eso el rebuild tiene **un método por fuente**, cada uno con su propia conexión, y el cruce se hace
**en C#** (`CatalogoStore.ConstruirFilasAsync`), no en SQL:

```
  ┌── conexión "DragonDb" ──────────────────────────────────────┐
  │  TraerArticulosBaseAsync()       ART + TIPOART + CATEGART    │
  │                                  + FAMILIA + PRECIOAR        │   ~1.600 filas
  │  TraerVariantesPrecompraAsync()  PRECOMPRADET (color+talle)  │  fuente 1 (cascada)
  │  TraerVariantesRemcompraAsync()  REMCOMPRADET (color+talle)  │  fuente 2 (cascada)
  │  TraerCurvasTalleAsync()         ART.CURTALL → CTALLE/DCTALLE│  fallback de talles
  └───────────────────────────────────────────────────────────────┘
  ┌── conexión "MarketDb" ──────────────────────────────┐
  │  TraerUbicacionesAsync()  MapeoRegistro → Ubicaciones│   universo + bits Luro/Peralta/Depósito
  │  TraerRutasFotoAsync()    GoogleDriveFotosArticulos  │   una ruta por código
  └─────────────────────────────────────────────────────┘
                          ↓
   CatalogoStore.ConstruirFilasAsync: cruce en C# por ARTCOD (diccionarios)
                          ↓
   CatalogoStore.GuardarBaseAsync: bulk-copy a #stage + un MERGE (atómico),
   con CatalogoTalle/CatalogoColor reconstruidas en la MISMA transacción
                          ↓
                   MARKET.dbo.Catalogo (+ tablas hijas)
```

**Dos connection strings desde el día uno.** Hoy las dos apuntan al mismo servidor —`DragonDb` con
`Database=DRAGONFISH_CENTRAL`— así que no cambia nada en la práctica. Si mañana se separan, se cambia la
config y listo: **cero cambios de código**.

Lo que cuesta: unas cuantas queries simples en vez de una grande, y la lógica de cruce en C#. A ~1.600
artículos eso no es un costo — y además tiene una ventaja: toda la parte sucia (los `RTRIM` por todos
lados, el `ROW_NUMBER` de las fotos, el parseo del combo, el fallback de taxonomía) queda en **código**
en vez de enterrada en SQL.

Lo que cuesta si las bases se separan de verdad: mover ~1 MB por la red en cada rebuild. Nada.

> La query grande del §3 queda como **referencia de qué campos hace falta traer**, no como la query a
> escribir. En la implementación se parte en las de arriba.

---

## 2.ter "¿Una tabla mantenida por un job es una práctica profesional?"

Sí, y conviene dejarlo escrito para no rediscutirlo.

**Es un patrón con nombre**: caché *read-through* materializado (proyección persistida / caché de datos de
referencia). Es lo que se hace con datos acotados, muy leídos y que cambian poco: tablas de precios,
configuración, feature flags, catálogos chicos.

### El punto que reencuadra la pregunta: la tabla no es la fuente de verdad

`dbo.Catalogo` **no es una fuente de verdad**: es una copia derivada de Dragon y de los mapeos, mantenida
por el rebuild. Es decir, **un caché guardado en SQL Server**. Lo que lo hace correcto no es evitar la
tabla, sino cómo se la mantiene:

| | Cómo lo resuelve este diseño |
|---|---|
| Cómo se refresca | TTL read-through, automático (no un cron que haya que vigilar) |
| Concurrencia | Single-flight: N requests → un solo rebuild |
| Si el refresh falla | Sirve la copia anterior y reintenta on-read (nunca tira, nunca pisa la tabla) |
| Consistencia de las hijas | MERGE + reconstrucción de talle/color en **una** transacción: nadie las ve a medias |
| Bajas | El MERGE marca `Eliminado = 1` lo que ya no está (baja lógica, nunca DELETE físico) |
| Decisión humana | `VisibilidadManual` (auto/mostrar/ocultar) se **preserva** en el MERGE; `Publicado` se recomputa respetándola |

**No es "cachear con más maquinaria": es un caché con las garantías puestas donde importan.**

### Los guardarraíles (sin estos, sí está mal hecho)

El patrón es estándar; lo profesional está en estos puntos:

1. **Documentado.** Un caché con un TTL invisible es una trampa. Está acá, y el sitio muestra
   **"datos actualizados hace X"**.
2. **Observable.** Se loguea cada rebuild (con el tiempo y los conteos publicables / sólo-depósito) y un
   rebuild fallido queda logueado. Crítico ahora que los precios son públicos: servir precios viejos en
   silencio es el riesgo a evitar.
3. **Precalentado al arranque** con `CatalogoBaseWarmup` (un `BackgroundService`): ningún usuario paga el
   primer rebuild tras un deploy.
4. **Acotado y medido.** ~1.600 filas hoy. Si el catálogo se multiplica, el tiempo de rebuild también →
   hay que monitorearlo, no asumirlo.
5. **El TTL es una decisión, no un accidente.** Hasta `MinutosTtl` de atraso en los precios; para un
   catálogo es aceptable, y es configurable.

### Cuándo dejar de ser la opción correcta

Si el catálogo crece un orden de magnitud, si hace falta consistencia inmediata (precios en tiempo real),
o si el rebuild completo se vuelve caro. Ahí correspondería un refresh **incremental** de la tabla (sólo
lo que cambió) o pasar a un índice de búsqueda (Meilisearch, Elastic, Azure AI Search), que es lo que
usan los catálogos de decenas de miles de SKU y da facetas y full-text nativo.

> **Caso aparte: ventas / descuento de stock.** El caché es un patrón de *lectura* y no se traslada al
> momento de vender, que es *escritura transaccional*. Este sitio es casi de solo lectura: las únicas
> escrituras van a MARKET —`VisibilidadManual` (mostrar/ocultar del público) y `RepoArticulosBloqueados`
> (bloqueo de reposición)—; nunca toca Dragon.

---

## 3. Los campos del universo (referencia)

El rebuild arma las ~1.600 filas del universo interno con todo lo que necesita la grilla. **Se implementa
partida por fuente** (§2.bis) y se cruza en C#; acá va junta para que se lea de una:

```sql
-- Universo: todo lo mapeado (incluido depósito). El bit Publicado se calcula por fila; sólo se descarta
-- la basura del ERP (taxonomía "No aplica" / pseudo-artículos de promoción).
WITH Armados AS (
    SELECT ARTCOD = RTRIM(REG.ARTCOD), IDUbicacion = UB.ID, Local = UB.Descripcion, EsDeposito = ...
    FROM MARKET.dbo.MapeoRegistro      REG WITH (NOLOCK)
    JOIN MARKET.dbo.Mapeo              MAP WITH (NOLOCK) ON MAP.ID = REG.IDMapeo
    JOIN MARKET.dbo.Ubicaciones        UB  WITH (NOLOCK) ON UB.ID  = MAP.IDUbicacion
    JOIN MARKET.dbo.UbicacionesTipo    UT  WITH (NOLOCK) ON UT.ID  = UB.IDTipo
    WHERE REG.Eliminado = 0 AND MAP.Eliminado = 0
    GROUP BY RTRIM(REG.ARTCOD), UB.ID, UB.Descripcion
)
SELECT  A.ARTCOD,
        -- El nombre de vidriera se deriva de ARTDES en C# (TituloArticulo.Derivar): no hay override manual.
        Descripcion  = RTRIM(A.ARTDES),
        Rubro        = RTRIM(TIPO.DESCRIP),
        Genero       = RTRIM(CATE.DESCRIP),
        FamiliaCod   = RTRIM(A.FAMILIA),
        Familia      = RTRIM(FAM.DESCRIP),
        Combo        = UPPER(RTRIM(ISNULL(A.CLASIFART, ''))),
        PrecioSuelta = PV.PDIRECTO           -- LISTA1; el del combo se calcula en C#
FROM      DRAGONFISH_CENTRAL.ZooLogic.ART      A    WITH (NOLOCK)
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.TIPOART  TIPO WITH (NOLOCK) ON TIPO.COD = A.TIPOARTI
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.CATEGART CATE WITH (NOLOCK) ON CATE.COD = A.CATEARTI
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.FAMILIA  FAM  WITH (NOLOCK) ON FAM.COD  = A.FAMILIA
-- Precio VIGENTE: sin el FECHAVIG <= hoy publicaríamos un precio que no entró en vigencia
OUTER APPLY (SELECT TOP 1 P.PDIRECTO
             FROM DRAGONFISH_CENTRAL.ZooLogic.PRECIOAR P WITH (NOLOCK)
             WHERE P.ARTICULO = A.ARTCOD AND P.LISTAPRE = 'LISTA1' AND P.FECHAVIG <= GETDATE()
             ORDER BY P.FECHAVIG DESC, P.HMODIFW DESC) PV
WHERE LEN(RTRIM(ISNULL(TIPO.DESCRIP, ''))) > 0 AND RTRIM(TIPO.DESCRIP) <> 'No aplica'
  AND LEN(RTRIM(ISNULL(CATE.DESCRIP, ''))) > 0 AND RTRIM(CATE.DESCRIP) <> 'No aplica';
```

> **Notas.** La foto se resuelve **IA primero, disco después** (`COALESCE(LinkIADisco, LinkDriveDisco)`)
> en su propia consulta (`TraerRutasFotoAsync`), no en la de arriba. El **override de visibilidad** vive
> en la columna `dbo.Catalogo.VisibilidadManual` (auto/mostrar/ocultar; no hay tabla de overrides): el
> MERGE la **preserva** y recomputa `Publicado`. Y además del filtro de basura de acá, el bit
> `PublicadoBase` aplica los criterios de
> publicación (entre ellos el temporal de **sólo Indumentaria**). El detalle vive en [FOTOS.md](FOTOS.md)
> y [CATALOGO-PUBLICACION.md](CATALOGO-PUBLICACION.md).

### Color y talle: de las COMPRAS, no de `COMB`

> ⚠️ El diseño original de este doc leía las variantes de `COMB` + `DPCOLOR`. **Ya no es así.** `COMB`
> traía los colores por código y había que matchearlos contra `DPCOLOR` (por `PALCOL` + `CODCOL`), lo que
> dejaba variantes sin nombre, y en general sus datos venían sucios. Se descartó.

Hoy el color y el talle salen de lo que **realmente se compró**, en una cascada de dos fuentes por
artículo (`CatalogoStore.ConstruirFilasAsync`), y se persisten normalizados en las tablas hijas
`dbo.CatalogoTalle` / `dbo.CatalogoColor`:

1. **`TraerVariantesPrecompraAsync`** — `PRECOMPRADET` (órdenes de compra). El color viene como texto
   directo del remito (`FCOTXT`), sin el problema de matcheo de `COMB`. Se excluyen las anuladas.
2. **`TraerVariantesRemcompraAsync`** — `REMCOMPRADET` (remitos de compra). Mismo criterio. Se usa
   **sólo** para los códigos que no aparecieron en `PRECOMPRA`.

Un artículo sin nada en ninguna de las dos **no se publica** (mejor no mostrarlo que mostrarlo sin
color/talle). Lencería es la excepción: no usa esta cascada (talle y color "Único" fijos).

```sql
-- Fuente 1 (idéntica para REMCOMPRADET). Color = texto del remito, sin join a DPCOLOR.
SELECT ArtCod   = RTRIM(PC.FART),
       ColorCod = RTRIM(PC.FCOLO),
       Color    = RTRIM(ISNULL(PC.FCOTXT, '')),
       Talle    = RTRIM(PC.FTALL)
FROM ZooLogic.PRECOMPRADET PC WITH (NOLOCK)
JOIN ZooLogic.PRECOMPRA    PH WITH (NOLOCK) ON PH.CODIGO = PC.CODIGO
WHERE RTRIM(PC.FART) IN @codigos AND ISNULL(PH.ANULADO, 0) = 0
GROUP BY RTRIM(PC.FART), RTRIM(PC.FCOLO), RTRIM(ISNULL(PC.FCOTXT, '')), RTRIM(PC.FTALL);
```

### Fallback de talles: la curva definida (`ART.CURTALL`)

Como los talles salen de lo comprado, un artículo cargado en la compra como **un solo renglón sin
talle** (`ST`/`U`/`X`/vacío) quedaba mostrando "Talle único" aunque físicamente tuviera una curva real.
Ejemplo medido: `IH066.160` — su única compra está como `NEUTRO`/`ST`, pero `ART.CURTALL = '007'` define
la curva `2XL, 3XL, 4XL, 5XL`.

Por eso hay un **fallback, sólo para talles** (los colores siguen 100% de las compras): si de las compras
un artículo sale sin ningún talle real, se busca su curva definida y se usa esa.

```sql
-- TraerCurvasTalleAsync — la curva DEFINIDA del artículo (no lo comprado).
SELECT ArtCod = RTRIM(A.ARTCOD),
       Talle  = RTRIM(D.CODTALL),
       Orden  = CAST(D.ORDEN AS int)            -- DCTALLE.ORDEN es numeric → cast para Dapper
FROM ZooLogic.ART     A WITH (NOLOCK)
JOIN ZooLogic.DCTALLE D WITH (NOLOCK) ON RTRIM(D.CODIGO) = RTRIM(A.CURTALL)  -- CTALLE=cabecera, DCTALLE=detalle
WHERE RTRIM(A.ARTCOD) IN @codigos AND RTRIM(ISNULL(A.CURTALL, '')) <> ''
ORDER BY RTRIM(A.ARTCOD), D.ORDEN;
```

Precedencia final de la lista `Talles` de un artículo:

```
talles de PRECOMPRA/REMCOMPRA (sin ST/U/X/vacío)
   └─ si queda vacía → curva de CURTALL (DCTALLE, en su ORDEN de fábrica)
        └─ si tampoco hay curva → sin talles → la card muestra "Talle único"
```

El precio del combo (`ComboTotal / ComboCantidad`) se calcula en C# al armar las filas y se guarda en las
columnas `ComboCantidad` / `ComboTotal` de la tabla.

---

## 4. Un click, paso por paso

El usuario está en `/catalogo/indumentaria/mujer` y hace click en **"Campera (28)"** — que es un
`<a href="/catalogo/indumentaria/mujer?familia=CAMPERA">`.

```
click
  │
  ├─ Blazor intercepta (enhanced navigation): no recarga la página entera
  ├─ fetch GET /catalogo/indumentaria/mujer?familia=CAMPERA
  │    │
  │    └─ SERVER: Catalogo.razor → LectorCatalogo.BuscarAsync
  │         ├─ AsegurarBaseFresca(): revalida en background si la base venció
  │         ├─ traduce los slugs de la URL a valores (TaxonomiaMapa, en RAM)
  │         │    rubro=indumentaria→Indumentaria, genero=mujer→Mujer, familia=CAMPERA
  │         ├─ CatalogoRepositorio.BuscarPublicoAsync — UN viaje (QueryMultiple):
  │         │    · WHERE con los filtros + ORDER BY + OFFSET/FETCH (48 por página)
  │         │    · COUNT(*) para el total
  │         │    · un GROUP BY por faceta (familia, talle, color, local, combo…)
  │         └─ arma el HTML
  │
  ├─ recibe ~25 KB de HTML
  ├─ reemplaza el <body>, conserva CSS/JS y la posición de scroll
  └─ pinta
       └─ los <img loading="lazy"> visibles piden sus thumbnails
          (desde disco/caché de fotos, sin volver a SQL)
```

**La grilla se resuelve EN SQL, no en memoria.** No se trae toda la tabla para filtrar en C#: la base
hace el `WHERE`, ordena, pagina, cuenta el total y calcula las facetas, todo en un solo `QueryMultiple`.
Talle/color se filtran con `EXISTS` sobre las tablas hijas; el combo, por las columnas
`ComboCantidad`/`ComboTotal`. Lo único que queda en RAM del catálogo es el mapita slug→valor
(`TaxonomiaMapa`, ~decenas de entradas), rearmado con cada rebuild. Con JavaScript apagado el mismo click
funciona igual, solo con repintado completo.

### Las facetas: el detalle que se arruina fácil

Cada faceta se cuenta **excluyendo su propio filtro**, dentro del mismo `QueryMultiple`:

```sql
-- Faceta de familia: el WHERE lleva todos los filtros MENOS "familia"
SELECT Valor = c.Prenda, Etiqueta = c.Prenda, Cantidad = COUNT(*)
FROM dbo.Catalogo c WHERE {todos-los-filtros-menos-familia}
GROUP BY c.Prenda;
```

El helper `Combinar(base, preds, excepto)` arma cada `WHERE` salteando el predicado de la faceta que se
está contando. Si no, después de elegir "Campera" el panel mostraría solo "Campera (28)" y quedarías
encerrado sin poder pasar a "Pantalón". Los contadores además hacen que **las opciones en cero
desaparezcan solas**.

---

## 5. La ficha del producto

**Pública** — `/producto/buzo-plush-c-r-im013-056` → se extrae `IM013.056` del final del slug y se busca
en el conjunto publicado leído de la tabla (`LeerAsync`, diccionarios por slug y por código). Si el slug
recibido no coincide con el canónico (porque cambió el título), 301 al canónico.

**Interna** — la ficha del staff (`/interno`) se consulta **en vivo a demanda** al abrirla: stock por
local, ventas y margen realizado, características, ubicaciones, órdenes y estado de bloqueo. No se
materializa en la tabla. Usa *streaming render*: la ficha aparece enseguida y el **benchmark de familia**
—lo más pesado— entra después; ese número se **cachea por familia (prenda)** con el mismo TTL de la base.

---

## 6. ¿Es escalable?

### Por tráfico: sí, prácticamente sin límite

El cruce de las dos bases ocurre **como mucho una vez por TTL** (single-flight, en background). Las
lecturas van a la tabla `dbo.Catalogo`, indexada y paginada en SQL: 100 visitas por día o 100.000 por
hora no vuelven a cruzar las bases. Lo que escala con el tráfico es CPU de render, ancho de banda de las
imágenes y las lecturas paginadas a la tabla —todo lo que un servidor web + SQL hacen bien.

### Por tamaño del catálogo

Hoy son ~1.600 filas en el universo interno (~600 publicadas). El costo que crece con el tamaño es el
**rebuild** (cruzar y persistir todo el universo) y no la lectura, que ya es una consulta paginada. Hasta
un orden de magnitud más esto sigue siendo razonable; más arriba, el rebuild completo empieza a pesar y
ahí correspondería un refresh **incremental** (sólo lo que cambió) o un índice de búsqueda dedicado.

### Por combinaciones de filtros: indiferente

Cualquier combinación de filtros, vista o no, se resuelve contra la misma tabla indexada con un `WHERE` +
`OFFSET/FETCH`. **El espacio combinatorio de filtros deja de ser un problema de performance** — y sin
`OutputCache`, no depende del *hit rate* de una caché por URL.

### Los límites reales, sin maquillar

1. **Precios con hasta `MinutosTtl` de atraso.** Configurable. No es tiempo real.
2. **El primer request tras arrancar dispararía el rebuild** — lo cubre `CatalogoBaseWarmup` con un
   rebuild bloqueante al arranque, así el primer visitante no lo paga.
3. **Si el rebuild falla**, se sigue sirviendo la tabla vieja y se reintenta on-read. Hay que
   **loguearlo y exponer "datos actualizados hace X"**, porque servir datos viejos en silencio es
   justamente el riesgo que se quería evitar al publicar precios.
4. **Búsqueda por texto** con `LIKE` sobre la columna `TextoBusqueda` (pública) / campos concatenados con
   `COLLATE _CI_AI` (interna) es instantáneo a este tamaño. Con decenas de miles de filas convendría
   índice full-text en SQL.
