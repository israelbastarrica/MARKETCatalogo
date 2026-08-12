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

## 2. La decisión que cambia todo: el universo vive en memoria

El catálogo entero son **~981 artículos y ~14.225 variantes**. Eso son **~2 MB en RAM**.

Entonces no hace falta consultar SQL en cada request. El sitio mantiene el universo completo en
`IMemoryCache` y lo refresca **cada 5 minutos**:

```
Arranque / cada 5 min ──► 1 query a SQL (~300 ms) ──► ~981 artículos + variantes en memoria (~2 MB)

Cada request del usuario ──► filtra, cuenta facetas, ordena y pagina EN MEMORIA (LINQ) ──► HTML
                              └── 0 consultas a SQL, microsegundos
```

**Consecuencia: el sitio hace una consulta a SQL cada 5 minutos, sin importar si lo visitan 10 personas
o 10.000.** Es materializar el catálogo, pero en RAM en vez de en tablas — y a 981 artículos eso es lo
proporcionado.

Ventajas sobre `OutputCache` por URL (que era el plan anterior):

- **No hay problema de *hit rate*.** `OutputCache` cachea por URL, y con rubro × género × 6 filtros ×
  orden × página el espacio de URLs es enorme: la mayoría de las combinaciones se pediría una sola vez y
  cada una costaría 300 ms. Cacheando **el universo** en vez de **las respuestas**, cualquier combinación
  de filtros sale gratis, incluso una que nadie pidió antes.
- **Un bot recorriendo el espacio de filtros no genera ni una consulta.**
- **Los precios tienen 5 minutos de atraso como máximo**, y sin ningún estado que pueda quedar roto: si
  el refresh falla, se reintenta a los 5 minutos con los datos viejos todavía servibles.

`OutputCache` se puede sumar igual por encima para las 8 rutas principales, pero deja de ser lo que
sostiene la performance.

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

Por eso el repositorio tiene **un método por fuente**, cada uno con su propia conexión, y el cruce se
hace **en C#** al armar el cache:

```
  ┌── conexión "DragonDb" ──────────────────────────────┐
  │  TraerArticulosAsync()   ART + TIPOART + CATEGART   │
  │                          + FAMILIA + PRECIOAR       │   ~1.000 filas
  │  TraerVariantesAsync()   COMB + DPCOLOR             │  ~14.225 filas
  └─────────────────────────────────────────────────────┘
  ┌── conexión "MarketDb" ──────────────────────────────┐
  │  TraerArmadosAsync()     MapeoRegistro → Ubicaciones│   ~1.441 filas
  │  TraerRutasFotoAsync()   GoogleDriveFotosArticulos  │   ~4.776 filas
  │  TraerOverridesAsync()   CatalogoArticulo           │       pocas
  └─────────────────────────────────────────────────────┘
                          ↓
              CatalogoCache: join en C# por ARTCOD (diccionarios)
```

**Dos connection strings desde el día uno.** Hoy las dos apuntan al mismo servidor —`DragonDb` con
`Database=DRAGONFISH_CENTRAL`— así que no cambia nada en la práctica. Si mañana se separan, se cambia la
config y listo: **cero cambios de código**.

Lo que cuesta: unas cuantas queries simples en vez de una grande, y la lógica de cruce en C#. A 981
artículos eso no es un costo — y además tiene una ventaja: toda la parte sucia (los `RTRIM` por todos
lados, el `ROW_NUMBER` de las fotos, el parseo del combo, el fallback de taxonomía) queda en **código
testeable** en vez de enterrada en SQL.

Lo que cuesta si las bases se separan de verdad: mover ~1 MB por la red cada 5 minutos. Nada.

> La query grande del §3 queda como **referencia de qué campos hace falta traer**, no como la query a
> escribir. En la implementación se parte en las cinco de arriba.

---

## 2.ter "¿Cachear en vez de tener tablas es una práctica profesional?"

Sí, y conviene dejarlo escrito para no rediscutirlo.

**Es un patrón con nombre**: *cached read model* (proyección en memoria / caché de datos de referencia).
Es lo que se hace con datos acotados, muy leídos y que cambian poco: tablas de precios, configuración,
feature flags, catálogos chicos. **.NET 9 —la versión que usamos— shippeó `HybridCache`**, una API de
primera clase para esto con protección contra *cache stampede*. Antes ya estaban `IMemoryCache` e
`IHostedService` para el precalentamiento.

### El punto que reencuadra la pregunta: la tabla también es un caché

`CatalogoWeb` **no sería una fuente de verdad**: sería una copia derivada de Dragon y de los mapeos,
mantenida por un job. Es decir, **un caché guardado en SQL Server con invalidación manual**. La
comparación real no es "cachear vs. tablas":

| | Caché en RAM | Caché en tablas |
|---|---|---|
| Dónde vive la copia | Memoria del proceso | SQL Server |
| Cómo se refresca | TTL de 5 min, automático | Un job que hay que escribir, programar y monitorear |
| Si el refresh falla | Sirve la copia anterior y reintenta solo | **Queda viejo hasta que alguien se entere** |
| Puede quedar inconsistente | No: se reconstruye entera | Sí: filas huérfanas, bajas no detectadas, columnas pisadas |
| Se cura reiniciando | Sí | No |
| Piezas móviles | 1 | 5 tablas + job + log + candado de máquina |

**La versión con tablas no es "no cachear": es cachear con más maquinaria y más formas de estar mal.**

### Los cinco guardarraíles (sin estos, sí está mal hecho)

El patrón es estándar; lo profesional está en estos puntos:

1. **Documentado.** Un caché con un TTL invisible es una trampa. Está acá, y el sitio muestra
   **"datos actualizados hace X"**.
2. **Observable.** Se loguea cada refresh y se alerta si la copia envejece. Crítico ahora que los precios
   son públicos: servir precios viejos en silencio es el riesgo a evitar.
3. **Precalentado al arranque** con `IHostedService`: ningún usuario paga los ~300 ms del primer llenado.
4. **Memoria acotada y medida.** 981 artículos ≈ 2 MB. Si el catálogo se multiplica, la RAM también →
   hay que monitorearlo, no asumirlo.
5. **La divergencia entre instancias es una decisión, no un accidente.** Hasta 5 min de diferencia entre
   instancias; para un catálogo es irrelevante.

### Cuándo dejar de ser la opción correcta

Si el catálogo pasa de ~20.000 artículos, si hace falta consistencia inmediata, o si aparecen varias
instancias que deban coincidir. Ahí corresponde materializar
([CATALOGO-SYNC.md](CATALOGO-SYNC.md)) o pasar a un índice de búsqueda (Meilisearch, Elastic, Azure AI
Search), que es lo que usan los catálogos de decenas de miles de SKU y da facetas y full-text nativo.

Lo que **sí** sería poco profesional hoy: montar 5 tablas y un job con detección de bajas, marcas de agua
y hashes **para 981 filas**. Más maquinaria no es más profesional; elegir el mecanismo del tamaño del
problema, sí.

> **Caso aparte: ventas / descuento de stock.** El caché es un patrón de *lectura* y no se traslada al
> momento de vender, que es *escritura transaccional*. Eso NO obliga a rehacer nada: el lado de ventas
> se agrega como módulo transaccional aparte y el catálogo queda como el lado de lectura. El porqué, qué
> se reutiliza y la regla de la autoridad del stock están en
> [EXTENSIBILIDAD-VENTAS.md](EXTENSIBILIDAD-VENTAS.md).

---

## 3. Los campos del universo (referencia)

Trae los ~981 artículos publicables con todo lo que necesita la grilla. **Se implementa partida por
fuente** (§2.bis); acá va junta para que se lea de una:

```sql
-- Universo publicable: armado en algún local, con rubro y género válidos.
WITH Armados AS (
    SELECT ARTCOD = RTRIM(REG.ARTCOD), IDUbicacion = UB.ID, Local = UB.Descripcion
    FROM MARKET.dbo.MapeoRegistro      REG WITH (NOLOCK)
    JOIN MARKET.dbo.Mapeo              MAP WITH (NOLOCK) ON MAP.ID = REG.IDMapeo
    JOIN MARKET.dbo.Ubicaciones        UB  WITH (NOLOCK) ON UB.ID  = MAP.IDUbicacion
    JOIN MARKET.dbo.UbicacionesTipo    UT  WITH (NOLOCK) ON UT.ID  = UB.IDTipo
    WHERE REG.Eliminado = 0 AND MAP.Eliminado = 0 AND UT.Descripcion <> 'DEPOSITO'
    GROUP BY RTRIM(REG.ARTCOD), UB.ID, UB.Descripcion
)
SELECT  C.ARTCOD,
        -- El override editorial gana sobre ARTDES, que no sirve para el público
        Titulo       = ISNULL(NULLIF(RTRIM(O.NombreComercial), ''), RTRIM(A.ARTDES)),
        Descripcion  = RTRIM(A.ARTDES),
        Marketing    = O.DescripcionMarketing,
        Rubro        = RTRIM(TIPO.DESCRIP),
        Genero       = RTRIM(CATE.DESCRIP),
        FamiliaCod   = RTRIM(A.FAMILIA),
        Familia      = RTRIM(FAM.DESCRIP),
        Combo        = UPPER(RTRIM(ISNULL(A.CLASIFART, ''))),
        PrecioSuelta = PV.PDIRECTO,          -- LISTA1; el del combo se calcula en C#
        RutaFoto     = G.LinkDriveDisco,     -- NULL/'' → placeholder (D7)
        Destacado    = ISNULL(O.Destacado, 0),
        Locales      = C.Locales
FROM (
    SELECT ARTCOD,
           Locales = STRING_AGG(Local, ',') WITHIN GROUP (ORDER BY Local)
    FROM Armados GROUP BY ARTCOD
) C
JOIN      DRAGONFISH_CENTRAL.ZooLogic.ART      A    WITH (NOLOCK) ON RTRIM(A.ARTCOD) = C.ARTCOD
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.TIPOART  TIPO WITH (NOLOCK) ON TIPO.COD = A.TIPOARTI
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.CATEGART CATE WITH (NOLOCK) ON CATE.COD = A.CATEARTI
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.FAMILIA  FAM  WITH (NOLOCK) ON FAM.COD  = A.FAMILIA
LEFT JOIN MARKET.dbo.CatalogoArticulo          O    WITH (NOLOCK) ON O.ARTCOD = C.ARTCOD AND O.Eliminado = 0
-- Precio VIGENTE: sin el FECHAVIG <= hoy publicaríamos un precio que no entró en vigencia
OUTER APPLY (SELECT TOP 1 P.PDIRECTO
             FROM DRAGONFISH_CENTRAL.ZooLogic.PRECIOAR P WITH (NOLOCK)
             WHERE P.ARTICULO = A.ARTCOD AND P.LISTAPRE = 'LISTA1' AND P.FECHAVIG <= GETDATE()
             ORDER BY P.FECHAVIG DESC, P.HMODIFW DESC) PV
-- GoogleDriveFotosArticulos tiene VARIAS filas por código (hasta 70): siempre la última
OUTER APPLY (SELECT TOP 1 F.LinkDriveDisco
             FROM MARKET.dbo.GoogleDriveFotosArticulos F WITH (NOLOCK)
             WHERE F.Codigo = C.ARTCOD AND ISNULL(F.Eliminado, 0) = 0
             ORDER BY F.ID DESC) G
WHERE ISNULL(O.OcultarManual, 0) = 0
  -- Filtro de basura: obligatorio ahora que se publican los sin foto (D7).
  -- Sin esto saldría el pseudo-artículo de promoción '2X15000' como si fuera un producto.
  AND LEN(RTRIM(ISNULL(TIPO.DESCRIP, ''))) > 0 AND RTRIM(TIPO.DESCRIP) <> 'No aplica'
  AND LEN(RTRIM(ISNULL(CATE.DESCRIP, ''))) > 0 AND RTRIM(CATE.DESCRIP) <> 'No aplica';
```

> **Nota (posterior a esta ilustración):** el `RutaFoto = G.LinkDriveDisco` de arriba quedó desactualizado.
> Hoy la foto se resuelve **IA primero, disco después** (`COALESCE(LinkIADisco, LinkDriveDisco)`), en su
> propia consulta (`TraerRutasFotoAsync`). Y además del filtro de basura de acá, el armado del snapshot
> aplica más filtros de publicación (entre ellos el temporal de **sólo Indumentaria**). El detalle vive en
> [FOTOS.md](FOTOS.md) y [CATALOGO-PUBLICACION.md](CATALOGO-PUBLICACION.md).

Y en el mismo batch, las variantes (~14.225 filas):

```sql
SELECT ARTCOD = RTRIM(CB.COART),
       ColorCod  = RTRIM(CB.COCOL),
       ColorDesc = RTRIM(ISNULL(DPC.DESCRIP, '')),
       Talle     = RTRIM(CB.TALLE),
       T.Orden, T.Grupo, T.Etiqueta
FROM DRAGONFISH_CENTRAL.ZooLogic.COMB CB WITH (NOLOCK)
JOIN      DRAGONFISH_CENTRAL.ZooLogic.ART     A   WITH (NOLOCK) ON RTRIM(A.ARTCOD) = RTRIM(CB.COART)
LEFT JOIN DRAGONFISH_CENTRAL.ZooLogic.DPCOLOR DPC WITH (NOLOCK) ON RTRIM(DPC.CODIGO) = RTRIM(A.PALCOL)
                                                                AND RTRIM(DPC.CODCOL) = RTRIM(CB.COCOL)
LEFT JOIN MARKET.dbo.CatalogoTalles           T   WITH (NOLOCK) ON T.Talle = RTRIM(CB.TALLE)
GROUP BY RTRIM(CB.COART), RTRIM(CB.COCOL), RTRIM(ISNULL(DPC.DESCRIP,'')), RTRIM(CB.TALLE),
         T.Orden, T.Grupo, T.Etiqueta;
```

Las dos van en **un round trip** con `QueryMultiple` de Dapper. El precio del combo
(`ComboTotal / ComboCantidad`) y la validación de la regla `+$5.000` se calculan en C# al armar el cache.

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
  │    └─ SERVER: Catalogo.razor
  │         ├─ lee rubro/genero de la ruta y el resto del query string
  │         ├─ pide el universo al IMemoryCache            ← 0 consultas SQL
  │         ├─ filtra en memoria (LINQ):                     ~0,1 ms
  │         │    rubro=Indumentaria, genero=Mujer, familia=CAMPERA
  │         ├─ cuenta las facetas (familia, talle, color, local)
  │         ├─ ordena y toma la página (48)
  │         └─ escribe el HTML                                ~2 ms
  │
  ├─ recibe ~25 KB de HTML
  ├─ reemplaza el <body>, conserva CSS/JS y la posición de scroll
  └─ pinta
       └─ los <img loading="lazy"> visibles piden sus thumbnails
          (~12 requests de ~30 KB, desde disco, sin SQL)
```

**Nada se busca de a pedazos.** Cada navegación es un render completo del server; enhanced navigation lo
hace *sentir* incremental porque solo cambia el `<body>`. Y con JavaScript apagado el mismo click
funciona igual, solo con repintado completo.

### Las facetas: el detalle que se arruina fácil

Cada faceta se cuenta **excluyendo su propio filtro**:

```csharp
var baseSet = universo.Where(x => x.Rubro == rubro && x.Genero == genero);

// Para la faceta de familia se aplican todos los filtros MENOS familia
var paraFamilia = baseSet.Where(SinFamilia(filtros));
var facetaFamilia = paraFamilia.GroupBy(x => x.Familia)
                               .Select(g => (g.Key, Cantidad: g.Count()));
```

Si no, después de elegir "Campera" el panel mostraría solo "Campera (28)" y quedarías encerrado sin
poder pasar a "Pantalón". Los contadores además hacen que **las opciones en cero desaparezcan solas**.

---

## 5. La ficha del producto

`/producto/buzo-plush-c-r-im013-056` → se extrae `IM013.056` del final del slug, se busca en el universo
cacheado (diccionario por `ARTCOD`), y las variantes salen del mismo cache ordenadas por `TalleOrden`.

**Cero consultas SQL.** Si el slug recibido no coincide con el canónico (porque cambió el título), 301 al
canónico.

---

## 6. ¿Es escalable?

### Por tráfico: sí, prácticamente sin límite

El costo en SQL es **una consulta cada 5 minutos**, constante. 100 visitas por día o 100.000 por hora
consumen lo mismo de la base. Lo único que escala con el tráfico es CPU de render y ancho de banda de
las imágenes, que es lo que un servidor web hace bien.

Es la diferencia central con el plan anterior de `OutputCache` por URL: ahí cada combinación de filtros
no vista antes costaba 300 ms sobre la base de logística.

### Por tamaño del catálogo: hasta ~10× el actual

| Artículos | Universo en RAM | Query de refresh | Filtrado en memoria |
|---|---|---|---|
| **981 (hoy)** | ~2 MB | ~300 ms | microsegundos |
| 5.000 | ~10 MB | ~1,5 s | < 1 ms |
| 20.000 | ~40 MB | ~6 s | pocos ms |
| 100.000 | ~200 MB | decenas de s | decenas de ms |

Hasta ~20.000 artículos esto sigue siendo razonable. Más arriba, el refresh empieza a ser un problema y
ahí sí corresponde materializar en tablas con refresh incremental — el diseño está en
[CATALOGO-SYNC.md](CATALOGO-SYNC.md).

### Por combinaciones de filtros: indiferente

Es la ventaja grande de cachear el universo en vez de las respuestas. Cualquier combinación, vista o no,
se resuelve en memoria. **El espacio combinatorio de filtros deja de ser un problema de performance.**

### Los límites reales, sin maquillar

1. **Cada instancia tiene su propia copia.** Con un solo server no importa. Con varios, cada uno
   refresca por separado y pueden diferir hasta 5 minutos entre sí. Aceptable para un catálogo.
2. **Precios con hasta 5 minutos de atraso.** Configurable. Es mejor que el job de 15–30 minutos del
   diseño anterior, pero no es tiempo real.
3. **El primer request después de arrancar paga los 300 ms.** Se resuelve precargando el cache al
   arranque (`IHostedService`), así el primer visitante nunca lo paga.
4. **Si el refresh falla**, se sigue sirviendo la copia vieja y se reintenta. Hay que **loguearlo y
   exponer "datos actualizados hace X"**, porque servir datos viejos en silencio es justamente el riesgo
   que se quería evitar al publicar precios.
5. **Búsqueda por texto en memoria** con `Contains` sobre ~981 títulos es instantáneo. Con decenas de
   miles convendría índice full-text en SQL.
