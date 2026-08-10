# MARKET Catálogo — plan de implementación

Sitio web oficial de MARKET arg: institucional (misión, visión, quiénes somos), noticias y —
lo primero que se desarrolla — un **catálogo navegable de productos**.

Proyecto independiente de MARKETweb, mismo stack (.NET 9 + Blazor + Dapper + SQL Server).

> Este documento es **el plan vigente**. El razonamiento y las mediciones que llevaron a cada decisión
> están en los docs de al lado: [MEDICION.md](MEDICION.md) (números reales),
> [DECISION-TABLAS.md](DECISION-TABLAS.md) (por qué casi no se guarda nada),
> [DISENO.md](DISENO.md) (marca), [CONTENIDO.md](CONTENIDO.md) (copy aprobada),
> [CATALOGO-SYNC.md](CATALOGO-SYNC.md) (camino de escalamiento, no vigente) y
> [EXTENSIBILIDAD-VENTAS.md](EXTENSIBILIDAD-VENTAS.md) (cómo se extiende a ventas/descuento de stock si
> algún día se vende; no vigente).

---

## 1. Decisiones tomadas

| # | Decisión | Detalle |
|---|---|---|
| **D1** | **Blazor Web App con SSR puro** | Sin WebAssembly. Render estático + *enhanced navigation*. El catálogo es de navegación y necesita SEO y previews sociales; con una sola foto por artículo no queda interacción que justifique bajar el runtime .NET al browser. Reversible por componente. |
| **D2** | **Hosting on-prem**, mismo server que MARKETweb | El sitio tiene línea de vista al SQL `MARKET` y a `DRAGONFISH_CENTRAL`, que están en **la misma instancia**. |
| **D3** | **Datos por artículo** | Foto, descripción, rubro, género, familia, **talles y colores**, y **precio ya en la card de la grilla**. Sin stock. |
| **D4** | **Precio = combo + unidad** | MARKET vende por combo (`2 x $15.000`) con recargo fijo de $5.000 por unidad suelta. Se muestran los dos, con el combo como titular. Regla verificada en 678/678 artículos. |
| **D5** | **Universo: solo lo armado en locales** | Según los mapeos de MARKET, excluyendo depósito. |
| **D6** | **El catálogo es por local** | 54% de los artículos está en un solo local. Se resuelve con **una URL canónica única + chip "Disponible en: Luro · Peralta" + filtro `?local=`**, no con el local en la ruta (duplicaría todas las URLs indexables). |
| **D7** | **Los artículos sin foto se muestran con placeholder** | Se publican los ~981 (no solo los 678 con foto). Los 307 sin foto salen con un recuadro vacío en `--mk-tinta-20`. **Ojo: esto obliga a filtrar la basura explícitamente** — antes la descartaba sola el requisito de foto (ver §5). |
| **D8** | **Lecturas en vivo, sin materializar** | 2 tablas chicas para lo que no existe en otra parte, y ningún job de sincronización. Ver [DECISION-TABLAS.md §8](DECISION-TABLAS.md). |
| **D8b** | **El universo se cachea en RAM, 5 min** | Los ~981 artículos + 14.225 variantes son ~2 MB. Se traen **cada 5 minutos** y el filtrado, las facetas, el orden y la paginación se hacen en memoria. Resultado: **el SQL no depende del tráfico**. Ver [CONSULTAS.md](CONSULTAS.md). |
| **D8c** | **Nunca un JOIN entre MARKET y DRAGONFISH** | Dos connection strings separadas y el cruce en C#. Hoy las bases están en la misma instancia y el join *funcionaría*, pero **si se suben a la nube separadas el join cruzado deja de existir** (Azure SQL Database no soporta cross-database). Así la mudanza es cambiar config, no reescribir. Ver [CONSULTAS.md §2.bis](CONSULTAS.md). |
| **D9** | **Diseño monocromático** según el Manual de Marca | `#000`, `#FFF` y opacidades 100/70/40/20%. Poppins + Open Sans. El rosa y el verde menta están **prohibidos** por el manual. |

---

## 2. Los números del catálogo (medidos)

| | |
|---|---|
| Artículos armados en locales | **985** |
| Basura descartada (rubro/género "No aplica" o vacío) | ~4 |
| **Publicados** | **~981** |
| Con foto | **678 (69%)** |
| Con placeholder (D7) — tarea del equipo, no técnica | **303** |
| Armados en LURO / PERALTA | 726 / 715 |
| En un solo local | 529 (54%) |
| Variantes (color × talle) | 14.225 |

**Por rubro** (publicados, con y sin foto):

| Rubro | Publicados | de esos, con foto |
|---|---|---|
| Indumentaria | 587 | 409 |
| Lencería | 344 | 244 |
| Casa blanquería | 21 | 4 |
| Calzado | 18 | 17 |
| Accesorios | 9 | 4 |
| Juguetería | 3 | 0 |

**Ocho secciones concentran el 93%:** Indumentaria/Mujer 225, Indumentaria/Hombre 207, Lencería/Mujer
152, Indumentaria/Nena 87, Lencería/Hombre 75, Indumentaria/Nene 68, Lencería/Nene 63, Lencería/Nena 35.

Consecuencia: el menú arranca con **Indumentaria** y **Lencería** × (Mujer / Hombre / Nena / Nene).
**Juguetería (3 artículos) no merece sección propia**; Casa blanquería (21) y Calzado (18) están en el
límite — decisión de negocio, no técnica.

---

## 2.bis El sitio no es solo el catálogo

Importa tenerlo presente al dimensionar la arquitectura: **el catálogo es un módulo, no el sitio.** Y
uno de los otros —Noticias— necesita **tablas propias y camino de escritura**: ahí el sitio *es* la
fuente de verdad, no un caché de otro sistema.

| Módulo | Datos | ¿Escribe? |
|---|---|---|
| Home | Destacados del catálogo | no |
| **Nosotros** (quiénes somos, misión, visión, valores) | Contenido versionado en el repo. Copy aprobada en [CONTENIDO.md](CONTENIDO.md) | no |
| **Catálogo** | Dragon + mapeos, **cacheado en RAM** (D8b) | no |
| **Noticias** | **Tabla propia** | **sí** |
| **Sucursales** | Luro y Peralta. Se enlaza con el filtro por local del catálogo | quizá tabla |
| **Medios de pago / cómo comprar** | Contenido del manual (ver CONTENIDO.md) | no |
| Contacto | Formulario → mail | no |
| **Admin** (Google SSO, `@marketarg.com`) | Noticias + overrides del catálogo | **sí** |

> El encuadre correcto de D8/D8b: **el sitio es una app normal con tablas y escritura. Lo que es un
> caché es el camino de LECTURA del catálogo**, porque esos datos son de otro sistema y acá sólo se
> muestran. Esa decisión queda contenida en el módulo del catálogo y no condiciona a los demás.

### Los títulos: un problema a resolver antes de publicar

`ART.ARTDES` dice cosas como `PALAZ DARLON MICRORIB DO VIVO` o
`MEDIA TERM C/PIEL 1/3 CAÑA EST ART 9400`. Publicar 981 artículos con esos títulos es una vidriera fea;
escribir 981 nombres comerciales a mano es mucho trabajo. Tres caminos:

1. `ARTDES` crudo — gratis y feo.
2. **Título derivado automáticamente** ⭐ — `FAMILIA` ya da el sustantivo limpio (Campera, Media,
   Corpiño) y un diccionario chico de abreviaturas (`CAMP`→Campera, `PANT`→Pantalón, `REM`→Remera,
   `POLE`→Polera, `BUZ`→Buzo, `SOQ`→Soquete…) más *title case* llega a algo presentable en la mayoría de
   los casos, sin trabajo manual.
3. **Overrides a mano** (`CatalogoArticulo.NombreComercial`) sólo para los que queden mal.

**Recomendado: 2 como default + 3 para corregir.** Así se publica sin depender de que alguien escriba
mil títulos, y se mejora de a poco.

---

## 3. Arquitectura

```
                    ┌─────────────────────────────────────┐
   Navegador ──────►│  MarketCatalogo.Web  (Blazor SSR)   │
   (HTML, sin WASM) │  ├── OutputCache por URL            │
                    │  └── CatalogoService (Dapper)       │
                    └──────────────┬──────────────────────┘
                                   │ una sola conexión, login READ-ONLY
                                   ▼
                    ┌─────────────────────────────────────┐
                    │  SQL Server (una instancia)         │
                    │                                     │
                    │  DRAGONFISH_CENTRAL.ZooLogic        │
                    │    ART · TIPOART · CATEGART         │
                    │    FAMILIA · COMB · DPCOLOR         │
                    │    PRECIOAR                         │
                    │                                     │
                    │  MARKET.dbo                         │
                    │    MapeoRegistro · Mapeo            │
                    │    Ubicaciones · UbicacionesTipo    │
                    │    GoogleDriveFotosArticulos        │
                    │    CatalogoArticulo  ← nuestra      │
                    │    CatalogoTalles    ← nuestra      │
                    └─────────────────────────────────────┘

   D:\FotosArticulos  (originales, solo lectura)
        └──► /fotos/*  endpoint que redimensiona bajo demanda
                 └──► D:\FotosCatalogo  (cache de thumbnails, descartable)
```

Tres proyectos, calcados de MARKETweb: **`MarketCatalogo.Web`** (única que abre conexión), 
**`MarketCatalogo.Application`** (Dapper), **`MarketCatalogo.Shared`** (DTOs).

### Estructura modular (un módulo por sección, como MARKETweb)

```
MarketCatalogo.Web/
  Components/Layout/                 header positivo, footer negativo
  Components/Pages/
    Home.razor
    Institucional/                   Nosotros · Valores · Sucursales · MediosDePago · Contacto
    Catalogo/                        Grilla · Ficha
    Noticias/                        Lista · Nota
    Admin/                           protegido: Noticias · Overrides del catálogo
  Endpoints/                         /fotos · /api/catalogo/sugerencias · sitemap.xml
  wwwroot/{css,fonts}/

MarketCatalogo.Application/
  Catalogo/       repos por fuente (D8c) + CatalogoCache + CatalogoService
  Noticias/       CRUD normal contra tabla propia
  Common/         conexiones, imágenes, slugs
MarketCatalogo.Shared/
  Catalogo/  Noticias/               DTOs
```

Cada módulo elige lo que necesita: el catálogo usa el caché en memoria; Noticias, Dapper contra su tabla.
**La decisión de cachear no es global, es del módulo del catálogo.**

---

## 4. De dónde sale cada dato

| Dato | Fuente | ¿Se guarda? |
|---|---|---|
| Código, descripción | `ART.ARTCOD`, `ART.ARTDES` | no |
| **Rubro** | `TIPOART` — Indumentaria, Lencería, Casa blanquería, Calzado, Accesorios, Juguetería | no |
| **Género** | `CATEGART` — Mujer, Hombre, Nena, Nene, Bebé, Unisex | no |
| **Familia** (tipo de prenda) | `FAMILIA` — Campera, Media, Corpiño… **el filtro más usado** | no |
| Talles y colores | `COMB` + `DPCOLOR` (vía `ART.PALCOL`) | no |
| **Combo** | `ART.CLASIFART` (`2X15000`) | no |
| **Precio unidad suelta** | `PRECIOAR.PDIRECTO` `LISTA1` con **`FECHAVIG <= hoy`** | no |
| En qué locales está | `MapeoRegistro → Mapeo → Ubicaciones → UbicacionesTipo`, excluyendo depósito | no |
| Ruta de la foto | `GoogleDriveFotosArticulos.LinkDriveDisco` (última fila por `Codigo`) | no |
| Orden de talles | **`CatalogoTalles`** | **sí** |
| Nombre comercial, destacados, ocultar | **`CatalogoArticulo`** | **sí** |

La taxonomía de Dragon tiene **99,8% de cobertura**, así que es la fuente. El `ARTCOD` (que codifica
rubro y género en sus primeras letras) queda solo para rellenar 2–3 huecos y **detectar códigos
malformados** — ya apareció uno: el pseudo-artículo `2X15000`.

Dos notas de implementación que evitan bugs conocidos:
- `GoogleDriveFotosArticulos` tiene **varias filas por código** (hasta 70). Siempre
  `ROW_NUMBER() OVER (PARTITION BY Codigo ORDER BY ID DESC)`.
- El **fallback al blob no hace falta**: la medición encontró 0 artículos con blob. Solo `LinkDriveDisco`.

---

## 5. Lo que sí se guarda: 2 tablas

[`sql/01_catalogo_schema.sql`](../sql/01_catalogo_schema.sql) — aditivo e idempotente.

**`CatalogoArticulo`** — tabla **rala**, arranca vacía. Solo tiene fila para artículos que alguien editó.
`ARTCOD` · `NombreComercial` · `DescripcionMarketing` · `Destacado` · `OcultarManual` · `Eliminado` ·
`Auditoria`. **Ni una columna duplica algo de Dragon o de los mapeos.**

**`CatalogoTalles`** — 53 filas. `Talle` · `Grupo` · `Orden` · `Etiqueta`. El orden no se puede derivar
de nada: alfabéticamente `L` va antes que `M` y `10` antes que `2`.

**El `Slug` no se guarda.** Se deriva del título + `ARTCOD`, y la ruta resuelve extrayendo el `ARTCOD`
del final: `/producto/buzo-plush-c-r-im013-056` → `IM013.056`. Sin lookup y sin almacenar. Si el título
cambia, resuelve igual por el código y hace 301 al slug canónico nuevo.

### La regla de visibilidad

Vive en el `WHERE` de `CatalogoService`, en un solo lugar:

```
está armado en al menos un local
AND rubro válido    (TIPOART no vacío y distinto de 'No aplica')
AND género válido   (CATEGART no vacío y distinto de 'No aplica')
AND ISNULL(OcultarManual, 0) = 0
```

**No hay filtro por foto** (D7): los sin foto salen con placeholder.

> ⚠️ **El filtro de rubro/género válido no es opcional.** Mientras se exigía foto, la basura se
> descartaba sola porque no la tiene. Al publicar los sin foto, **sin este filtro aparecerían como
> productos**: el pseudo-artículo de promoción `2X15000` (rubro y género = "No aplica"), 2 artículos sin
> rubro y 3 sin género. Son ~4 filas, pero saldrían en la vidriera.

Cuentas: 985 armados − ~4 de basura = **~981 publicados**, de los cuales **678 con foto y 303 con
placeholder**.

---

## 6. Mapa de URLs

| URL | Contenido | Indexable |
|---|---|---|
| `/` | Home institucional + destacados | ✅ |
| `/nosotros` | Quiénes somos, misión, visión, valores | ✅ |
| `/catalogo` | Grilla completa | ✅ |
| `/catalogo/{rubro}` | `indumentaria` · `lenceria` · … | ✅ |
| `/catalogo/{rubro}/{genero}` | `/catalogo/indumentaria/mujer` — **el eje de navegación** | ✅ |
| `/catalogo/…?familia=…&talle=…` | Cualquier refinamiento | ❌ `noindex,follow` + canonical a la ruta limpia |
| `/producto/{slug}` | Ficha completa | ✅ |
| `/noticias` · `/noticias/{slug}` | Listado y nota | ✅ |
| `/api/catalogo/sugerencias?q=` | JSON para el autocompletado | — |
| `/fotos/{cod}_{ancho}.webp` | Thumbnail, generado bajo demanda | — |

El `noindex` en las URLs con filtros no es un detalle: las combinaciones son miles de URLs con
contenido casi duplicado, y si Google las indexa se come el presupuesto de crawleo y el sitio compite
consigo mismo.

Además desde el arranque: Open Graph por producto (para el preview de WhatsApp/Instagram),
`sitemap.xml`, `robots.txt`, y **paginación `?pag=`, no scroll infinito** (con scroll infinito la página
5 deja de tener URL propia).

---

## 7. Navegación y filtros

**La URL es todo el estado.** No hay sesión, ni cookie, ni JavaScript guardando filtros. Recargar da lo
mismo, "atrás" saca el último filtro, y el link compartido muestra exactamente lo mismo.

| Filtro | Parámetro | Origen |
|---|---|---|
| **Prenda** | `?familia=CAMPERA` | `FAMILIA`. El más usado — va visible, no escondido |
| Talle | `?talle=M` | `COMB`, agrupado por `CatalogoTalles.Grupo` |
| Color | `?color=NEGRO` | `COMB` + `DPCOLOR` |
| Precio | `?precioMin=&precioMax=` | Por el precio del combo por unidad ($1.500–$50.000) |
| Combo | `?combo=2` \| `4` | 519 artículos son `2X`, 159 son `4X` |
| Local | `?local=luro` | Mapeos. Default: todos, con chip por card |
| Orden | `?orden=` | destacados · nuevos · precio-asc · precio-desc |
| Texto | `?q=` | `ARTDES` + `NombreComercial` |
| Página | `?pag=2` | 48 por página |

Cada opción de filtro es un `<a href>` con el query string ya recalculado del lado del server. **Funciona
sin JavaScript**; con *enhanced navigation* se siente instantáneo porque solo se reemplaza el `<body>`.

Tres detalles que hay que hacer bien:

**Contadores en cada opción** — "Campera (28)". Evita que alguien haga tres clicks para llegar a un
resultado vacío, y hace que las opciones en cero desaparezcan solas.

**Cada faceta se calcula excluyendo su propio filtro.** Si no, después de elegir "Campera" el panel de
familia mostraría solo "Campera (28)" y quedarías encerrado sin poder pasar a "Pantalón". Es el bug
clásico de los filtros facetados.

**Estado vacío con salida**: un link para quitar el último filtro, no solo para limpiar todo.

### La card

```
┌──────────────────────┐
│        [foto]        │  ← _400.webp, loading="lazy", con width/height
├──────────────────────┤
│ CAMPERA INFLABLE     │  ← NombreComercial ?? ARTDES
│ 2 x $15.000          │  ← titular: el combo
│ $12.500 la unidad    │  ← tinta 70%, más chico
│ LURO · PERALTA       │  ← disponibilidad (D6)
└──────────────────────┘
```

Sin talles ni colores: serían ~700 filas de `COMB` por página para no mostrar nada útil. Van en la ficha.

Y en el catálogo, un **"precios actualizados al …"** — publicar precios convierte al sitio en una oferta
comercial que hay que poder honrar.

---

## 8. Fotos

Las originales de `D:\FotosArticulos` pesan megas y se muestran en cards de ~300 px. Servirlas directo
serían ~72 MB por página de catálogo.

**Un endpoint `/fotos/{cod}_{ancho}.webp` que redimensiona bajo demanda**: mira si el archivo existe en
`D:\FotosCatalogo`, y si no lo genera desde el original y lo guarda. Dos tamaños: `_400` para la grilla
(~30 KB) y `_1200` para la ficha.

Bajo demanda y no en un job porque así **solo se genera lo que alguien realmente mira**, no hay estado
"se olvidó de generar este thumbnail", y si borrás la carpeta se regenera sola. Costo: el primer
visitante de cada foto paga el resize una vez.

La carpeta cache se sirve con `UseStaticFiles` y cache de 30 días, así **ninguna imagen toca SQL**.

### El placeholder de los sin foto (D7)

303 de los ~981 artículos publicados no tienen foto. En lugar de esconderlos, la card muestra un
**recuadro vacío en `--mk-tinta-20`** con la misma relación de aspecto que una foto, para que la grilla
no se descuadre.

Nada de iconos de "imagen rota" ni de librerías genéricas: en un diseño monocromático un rectángulo gris
liso se lee como "todavía no hay foto" sin parecer un error. El manual además tiene su propia
iconografía, así que **no se mezcla con iconos de terceros**.

Queda como decisión de diseño si el recuadro lleva algo adentro (el código en estilo `.mk-rotulo`, un
"Foto en camino") o si va liso. **El wordmark ahí no va**: el manual fija una reducción mínima de 183 px
y una card mide ~300, así que entraría justo y a baja opacidad, lo que roza la norma de "el logo solo en
negro o blanco".

---

## 9. Fases

**Fase 0 — Scaffold + sistema de diseño** ✅ hecho
Solución que compila y corre. Tokens de marca, shell de sitio público (header positivo, footer
negativo), Bootstrap retemado a monocromático.

Tipografías **listas**: Poppins 400/500/600/700 y Open Sans 400/600/700 auto-hospedadas en
`wwwroot/fonts/` (14 archivos, 297 KB, subsets latin + latin-ext), con las `@font-face` generadas en
`wwwroot/css/fonts.css` conservando los `unicode-range` de Google. Verificado en el navegador.

Arrastra dos pendientes, los dos de diseño: el **logo en SVG** (hoy es un stand-in en CSS con
`letter-spacing`) y el **favicon** (el manual prohíbe usar la "M" sola).

**Fase 1 — Catálogo** ← acá estamos
1. `ISqlConnectionFactory` + `DependencyInjection`, calcados de MARKETweb.
2. Correr [`sql/01_catalogo_schema.sql`](../sql/01_catalogo_schema.sql) (2 tablas).
3. `CatalogoRepositorio`: la consulta del universo + variantes en un round trip
   ([CONSULTAS.md §3](CONSULTAS.md)).
4. `CatalogoCache`: `IMemoryCache` con refresh cada 5 min, precargado al arranque con un
   `IHostedService`, y "datos actualizados hace X" expuesto.
5. `CatalogoService`: filtrado, facetas, orden y paginación **en memoria**.
6. Grilla y ficha en SSR con el diseño de marca, incluido el placeholder de los sin foto.
7. Endpoint de fotos con resize bajo demanda.
8. SEO: Open Graph, sitemap, canónicas, `noindex` en los filtros.
9. Buscador con autocompletado (endpoint + ~40 líneas de JS).

**Fase 2 — Institucional**
La copy ya está transcripta y aprobada en [CONTENIDO.md](CONTENIDO.md) — sale del propio manual. Va
versionada en el repo: cambiarla requiere deploy, que para páginas que se editan dos veces por año es
lo correcto.

**Fase 3 — Noticias**
Lo único que necesita ABM. Tabla `Noticias` + admin protegido con Google SSO restringido a
`@marketarg.com`, reutilizando el patrón de MARKETweb.

**Fase 4 — Deploy**
Sitio propio, puerto propio, entrada en el reverse proxy. Y lo que MARKETweb no necesita: **dominio y
certificado público.**

---

## 10. Puntos abiertos

**De negocio, no técnicos:**
1. **¿Aprobado publicar precios en internet?** Obliga a honrarlos y expone la lista completa a la
   competencia. Si la respuesta fuera no, se ocultan dos campos y el resto funciona igual.
2. **Los 307 artículos sin foto (31%).** Es la restricción más grande del catálogo y se resuelve con una
   cámara. Buena métrica para seguir: cada foto agranda el catálogo.
3. **Cómo se llaman los rubros de cara al público**, sobre todo Lencería, Casa blanquería y Juguetería,
   que el manual **no documenta** (y Lencería es el 35% del catálogo).
4. **Favicon**, porque el manual prohíbe la "M" sola.
5. **Dominio, certificado y exposición a internet.**

**Técnicos:**
6. **Tres talles sin clasificar** — `20`, `24`, `25`, con 1 a 5 artículos cada uno. Quedaron en grupo
   `REVISAR` en el seed; no los adiviné.
7. **Se resignó `FechaAlta`/`FechaBaja`** (D8): los productos discontinuados dan 404 pelado en vez del
   200-con-aviso → 410, y no hay sección "Novedades". Se recupera con una tabla de dos fechas si importan.
8. **Login SQL read-only** dedicado para el sitio, con permiso mínimo.
9. **¿Se publica un artículo armado pero sin stock?** Yo diría que sí: es una vidriera, no un
   e-commerce, y el stock cambia varias veces por día.
