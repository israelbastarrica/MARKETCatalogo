# MARKET Catálogo

Sitio web oficial de **MARKET arg**: institucional (misión, visión, quiénes somos) y un catálogo de
productos navegable, con dos vistas:

- **Público** — el catálogo que ve el cliente (`/catalogo`), pensado para SEO y mobile.
- **Interno** — la vista del staff logueado (`/interno`), con costo, margen, depósito, stock/ventas
  en vivo y acciones (ocultar del público, bloquear reposición).

Proyecto independiente de [MARKETweb](https://github.com/israelbastarrica/MARKETweb), con el mismo
stack. MARKETweb es el sistema **interno** de gestión; este es el sitio del **catálogo**.

## Stack

- **.NET 9**, **Blazor Web App** con **SSR** (render estático, sin WebAssembly). En prod corre como
  **Servicio de Windows** (`Host.UseWindowsService()`).
- **Dapper** para SQL. Solo el host abre conexiones.

### Monolito modular

Un host + módulos bajo `src/Modulos/<Nombre>/`. Cada módulo usa las capas que necesita
(Contratos → Aplicacion → Datos → Ui); tres es un máximo, no un mínimo.

```
src/
  MarketCatalogo.Web              host: cablea los módulos, pipeline, endpoints (fotos, SEO, auth, interno)
  Compartido                     infra transversal (SqlConnectionFactory, helpers de texto/slug)
  Modulos/
    Catalogo/                    Contratos · Aplicacion · Datos · Ui  (el catálogo público + interno)
    Auth/                        login (cookie + Google @marketarg.com + usuario/clave) + política "Interno"
    Institucional/               Ui: páginas estáticas (nosotros, misión, visión)
```

Regla: **`Datos` depende de `Aplicacion`, nunca al revés**, y nada fuera de un módulo pasa de su
proyecto `Contratos`.

### Por qué SSR y no WebAssembly (como MARKETweb)

MARKETweb es interna: se abre una vez y se trabaja horas, así que pagar el runtime .NET en la primera
carga se amortiza. Un catálogo público es lo opuesto — visitas cortas, mobile, y **el SEO y los
previews al compartir son la mitad del valor**. Con SSR el crawler ve HTML real y el primer pixel
aparece en milisegundos. Es reversible por pantalla: si algo necesita interactividad en C#, se agrega
`@rendermode InteractiveWebAssembly` en ese componente.

## Cómo funcionan los datos

Dos bases, **nunca joineadas en SQL** (se cruzan en C#): **MARKET** (mapeos/ubicaciones, combos, fotos,
la decisión de ocultar y los bloqueos) y **DRAGONFISH** (`_CENTRAL`/`_LURO`/`_PERALTA`: cabecera,
taxonomía, precio, compras, stock, ventas).

**La tabla materializada `MARKET.dbo.Catalogo` ES el caché** (modelo tabla-como-caché): no hay snapshot
en RAM ni `OutputCache`. `CatalogoStore` la reconstruye desde las dos bases (TTL + single-flight,
*stale-while-revalidate*: si venció, se rearma en background y se sigue sirviendo lo último). Talle y
color viven normalizados en las tablas hijas `dbo.CatalogoTalle` / `dbo.CatalogoColor`.

**La grilla se resuelve EN SQL** (pública e interna): `WHERE` con los filtros, `OFFSET/FETCH` para la
página, `COUNT` para el total y un `GROUP BY` por faceta — todo en un viaje. No se trae toda la tabla a
memoria. Los slugs de la URL se traducen a valor con un mapa chico en memoria (rearmado con la base).

**La ficha interna** (stock por local, ventas y margen realizado, características, ubicaciones, órdenes,
bloqueo) se consulta **en vivo** a demanda al abrirla — no se materializa. Usa *streaming render*: la
ficha aparece enseguida y el benchmark de familia (lo más pesado, cacheado por familia) entra después.

Este sitio es **casi de solo lectura**: las únicas escrituras son en MARKET — `OcultarManual`
(mostrar/ocultar del público) y `RepoArticulosBloqueados` (bloqueo de reposición). Nunca toca Dragon.

## Configurar la conexión a la base

La contraseña **no** se versiona:

```powershell
dotnet user-secrets set "ConnectionStrings:MarketDb" "Server=TU_SERVIDOR;Database=MARKET;User Id=USUARIO;Password=LA_PASS;TrustServerCertificate=True;MultipleActiveResultSets=True;Encrypt=True" --project src/MarketCatalogo.Web
```

Las conexiones a Dragon (`_CENTRAL`/`_LURO`/`_PERALTA`) se derivan de `MarketDb` cambiando la base
(override por `ConnectionStrings:DragonDb`/`DragonLuroDb`/`DragonPeraltaDb` o `Catalogo:Base*`).

## Correr

```powershell
dotnet run --project src/MarketCatalogo.Web
```

```powershell
dotnet build MarketCatalogo.sln
```

No hay proyectos de test ni comando de lint más allá de los warnings de `dotnet build`.

## Base de datos

`dbo.Catalogo` se regenera sola desde las bases; si se borra, se rearma. Scripts (aditivos e
idempotentes salvo el de limpieza):

- [sql/01_catalogo_tabla.sql](sql/01_catalogo_tabla.sql) — esquema de `dbo.Catalogo` + tablas hijas
  `CatalogoTalle`/`CatalogoColor` + índices. Es el esquema de referencia.
- [sql/03_drop_columnas_ficha.sql](sql/03_drop_columnas_ficha.sql) — dropea columnas viejas de ficha.
  Superado por `06`.
- [sql/04_combo_columnas.sql](sql/04_combo_columnas.sql) — combo en `ComboCantidad`/`ComboTotal`.
- [sql/05_facetas_sql.sql](sql/05_facetas_sql.sql) — tablas hijas + índices para faceteo en SQL.
- [sql/06_limpieza_columnas.sql](sql/06_limpieza_columnas.sql) — **destructivo**: dropea columnas
  muertas + `TallesCsv`/`ColoresCsv`. Correr **al final**, tras desplegar el código nuevo.

## Documentación

Autoritativos / vigentes:

- [docs/DISENO.md](docs/DISENO.md) — sistema de diseño. **Leer antes de tocar CSS.** La marca es
  **monocromática** (`#000`/`#fff` + escala de opacidad); rosa y verde menta **prohibidos**. Fuentes
  Poppins (títulos) + Open Sans (texto), self-hosted.
- [docs/CATALOGO-PUBLICACION.md](docs/CATALOGO-PUBLICACION.md) — qué se publica y qué se descarta
  (incluido el filtro temporal de **solo Indumentaria**).
- [docs/FOTOS.md](docs/FOTOS.md) — pipeline de thumbnails: **IA primero, disco después**, resize
  (SkiaSharp) + caché versionada.
- [docs/CONSULTAS.md](docs/CONSULTAS.md) — timing/escala de las consultas del rebuild.
- [docs/CONTENIDO.md](docs/CONTENIDO.md) — copy institucional aprobada (se transcribe verbatim).
- [docs/ManualDeMarca.pdf](docs/ManualDeMarca.pdf) — el manual oficial. Autoridad final.

De contexto:

- [docs/MEDICION.md](docs/MEDICION.md) — mediciones reales contra producción (volúmenes, tiempos) que
  fundamentaron el diseño. Son datos, no decisiones (algunas conclusiones de la época cambiaron).

## Convenciones heredadas de MARKET

- **Nunca DELETE físico**: baja lógica con `Eliminado = 1`.
- Consultas **siempre parametrizadas**.
- Campo `Auditoria` con formato `Acción | origen | fecha`.
