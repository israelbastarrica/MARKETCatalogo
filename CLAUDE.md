# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**MARKET Catálogo** — public-facing website for MARKET arg: institutional pages (misión/visión/quiénes
somos) + a live-read product catalog. Sibling project to the internal MARKETweb system (same stack,
different codebase). This app is **read-only** against the databases.

## Commands

```powershell
# Run (dev)
dotnet run --project src/MarketCatalogo.Web

# Build
dotnet build MarketCatalogo.sln

# Set the DB password locally (never commit it)
dotnet user-secrets set "ConnectionStrings:MarketDb" "Server=...;Database=MARKET;User Id=...;Password=...;TrustServerCertificate=True;MultipleActiveResultSets=True;Encrypt=True" --project src/MarketCatalogo.Web
```

There are **no test projects** in the solution (`MarketCatalogo.sln` has exactly 7 projects, all
product code) and no `.editorconfig` / `Directory.Build.props` / `global.json` — nullable + implicit
usings are set per-`.csproj` individually. There's no lint command beyond `dotnet build` warnings.

The app runs as a Windows Service in production (`Host.UseWindowsService()` in `Program.cs`, a no-op
under `dotnet run`). Every build stamps `wwwroot/buildinfo.txt` with the current git commit (MSBuild
target in `MarketCatalogo.Web.csproj`) — the footer greys out its version indicator when the deployed
commit falls behind `main`.

## Architecture

Modular monolith, one host + modules under `src/Modulos/<Nombre>/`. Each module can use as many of
Contratos → Aplicacion → Datos → Ui as it needs — **three layers is a maximum, not a minimum** (the
Institucional module skips Aplicacion/Datos entirely because its content is static copy).

```
Catalogo.Contratos  ←──────────────────────────┐
  (interfaces + DTOs, zero deps — the ONLY      │
   surface other modules/host may reference)    │
       ↑                    ↑                   │
Catalogo.Aplicacion ←── Catalogo.Datos       Catalogo.Ui (RCL, refs Contratos ONLY)
  (business logic,        (Dapper/SQL,           — Razor pages/components, injects
   declares                implements                ICatalogoConsulta/IFotosCatalogo,
   ICatalogoRepositorio    ICatalogoRepositorio,      never touches SQL or the cache)
   but doesn't             is the module's DI
   implement it)           composition root)

MarketCatalogo.Compartido — cross-cutting infra only (SqlConnectionFactory, text/slug helpers).
No business logic, no module knowledge.

MarketCatalogo.Institucional.Ui — RCL, no project references at all. Static pages only.

MarketCatalogo.Web — the host. Wires everything with one call, AgregarModuloCatalogo(), and
registers both UI RCLs as additional assemblies for MapRazorComponents (SSR routing) — separate
from the AdditionalAssemblies on Routes.razor's <Router>, which only affects client-side routing.
```

Rule enforced by convention (not the compiler): **Datos depends on Aplicacion, never the reverse**,
and nothing outside a module reaches past its `Contratos` project.

### Data flow — two databases, merged in C#, never joined in SQL

The catalog is rebuilt from two SQL Server databases and materialized into `MARKET.dbo.Catalogo`
(schema `sql/01_catalogo_tabla.sql`) plus its child tables `dbo.CatalogoTalle`/`dbo.CatalogoColor`.
Nothing else is replicated:

- **MARKET**: `MapeoRegistro`/`Mapeo`/`Ubicaciones` (which article is stocked where), `PruebaCombos`
  (combo pricing), `GoogleDriveFotosArticulos` (photo paths). The catalog materializes into a single
  table `dbo.Catalogo`; the manual hide decision (`OcultarManual` + audit) lives in that same table and
  the rebuild MERGE preserves it (there is no longer a separate `CatalogoArticulo` overrides table).
- **DRAGONFISH** (`ZooLogic`, aliased "Dragon") — `_CENTRAL` for the rebuild and `_LURO`/`_PERALTA`
  replicas for per-store stock/sales on the ficha: `ART`/`TIPOART`/`CATEARTI`/`FAMILIA`/`PRECIOAR`
  (header, taxonomy, cost + live price), `PRECOMPRADET`/`REMCOMPRADET` (color/talle variants, cascading
  fallback), `DCTALLE` (size curve), `COMB` (stock), `COMPROBANTEV*` (sales).

`CatalogoRepositorio` (`Catalogo.Datos`) runs **one query per source, batched 500 codes at a time**
to stay under SQL Server's 2100-param limit — explicitly never a cross-database JOIN. All merging for
the rebuild happens in `CatalogoStore.ConstruirFilasAsync` (`Catalogo.Aplicacion`), which then persists
via `GuardarBaseAsync` (bulk-copy to a stage table + one MERGE, with the child tables rebuilt inside the
same transaction). `SqlConnectionFactory` (`Compartido`) derives the Dragon connections from
`ConnectionStrings:MarketDb` by swapping `Initial Catalog` (`CrearDragon`/`CrearLuro`/`CrearPeralta`),
overridable via `ConnectionStrings:DragonDb`/`DragonLuroDb`/`DragonPeraltaDb` or `Catalogo:Base*`.

**The table IS the cache** (tabla-como-caché). There is no in-RAM snapshot and no `AddOutputCache()`.
`CatalogoStore` owns the rebuild with **TTL + single-flight + stale-while-revalidate**: a read calls
`AsegurarBaseFresca()`, and if the base is older than `Catalogo:MinutosTtl` (fallback `MinutosCache`,
default 20) it rebuilds **in the background** while still serving the last good data. `CatalogoBaseWarmup`
does one blocking rebuild at startup. A failed rebuild logs and keeps the previous data — it never throws.
The one piece kept in RAM is a tiny slug→value taxonomy map (`TaxonomiaMapa`), rebuilt with the base.

**The grid is resolved in SQL, not in memory.** `LectorCatalogo.BuscarAsync` (public) and
`LectorInterno.BuscarAsync` (internal) translate the URL slugs to values and call
`CatalogoRepositorio.BuscarPublicoAsync`/`BuscarInternoAsync` (`CatalogoRepositorio.Busqueda.cs`), which
does `WHERE` + `ORDER` + `OFFSET/FETCH` for the page, `COUNT` for the total, and one `GROUP BY` per facet
(each excluding its own filter) — all in a single `QueryMultiple`. Talle/color filter via `EXISTS` on the
child tables and the display list is rebuilt with `STRING_AGG`. The **internal ficha** loads live on
demand (stock/sales/margin, características, ubicaciones, órdenes, bloqueo) via `LectorInterno.PorCodigoAsync`,
with the page rendered by streaming (`[StreamRendering]`) and the family-average benchmark loaded
afterwards (`BenchmarkFamiliaAsync`, cached per prenda).

Only the `Indumentaria` rubro is currently published — a temporary hardcoded filter in the publish
criteria (`CatalogoStore.ConstruirFilasAsync`), not a config toggle.

### Photo/thumbnail pipeline

`GET /fotos/{codigo}_{ancho}.webp?v={version}` (`Endpoints/FotosEndpoint.cs`) → `FotosService`
(`Catalogo.Aplicacion`). Widths are restricted to a closed list (400/1200px). Cache filename embeds
the version token (`{codigo}_{ancho}_{version}.webp` under `Fotos:DirCache`); if it exists, it's
served with no SQL involved. Otherwise the original path is resolved by a PK lookup on `dbo.Catalogo`
(`CatalogoRepositorio.LeerRutaFotoAsync`, reading `FotosJson`; the rebuild already picked `LinkIADisco`
over `LinkDriveDisco` — "IA primero, disco después"), resized/encoded with **SkiaSharp**
(not ImageSharp — v4 needs a paid license), and written atomically (temp file + `File.Move`). The
`?v=` token comes from the source file's `LastWriteTimeUtc`, so old files auto-invalidate and stale
versions are opportunistically deleted — this is what lets `Cache-Control: immutable, max-age=30d`
be safe.

### Conventions inherited from MARKET (the internal sibling system)

- Never physical `DELETE` — logical deletion via an `Eliminado = 1` column.
- SQL queries are always parameterized.
- Audit columns follow the `Acción | origen | fecha` string format.

### Design system

`docs/DISENO.md` is authoritative for anything touching CSS/branding — read it before styling work.
Hard constraints: the brand is **strictly monochromatic** (`#000000`/`#FFFFFF` + an opacity scale as
the only grays) — **pink and mint green are explicitly prohibited**, no exceptions for accents or
error states. No text may render below 70% opacity (40%/20% are for borders/icons only — both fail
contrast as text). Fonts are Poppins (titles) + Open Sans (body), self-hosted from `wwwroot/fonts/`,
not loaded from a CDN.

### Other docs worth opening before touching related code

- `docs/CONSULTAS.md` — exact query timing/scaling budget behind the cache design above.
- `docs/CATALOGO-PUBLICACION.md` — full publish/discard filter logic (including the temporary
  Indumentaria-only restriction) and the dev diagnostic `.txt` dump.
- `docs/FOTOS.md` — full photo pipeline design (summarized above).
- `docs/CONTENIDO.md` — approved institutional copy; `Institucional.Ui` pages transcribe it verbatim,
  including preserved typos — edit the manual/doc first, never the copy in `.razor` directly.
