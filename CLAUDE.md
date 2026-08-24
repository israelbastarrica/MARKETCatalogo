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

The catalog reads live from two SQL Server databases every refresh cycle — nothing is replicated
except two small tables (`sql/01_catalogo_schema.sql`):

- **MARKET**: `MapeoRegistro`/`Mapeo`/`Ubicaciones` (which article is stocked where), `PruebaCombos`
  (combo pricing), `GoogleDriveFotosArticulos` (photo paths), `CatalogoArticulo` (editorial overrides
  — sparse, empty by default, wrapped in try/catch so the site works without it).
- **DRAGONFISH_CENTRAL** (`ZooLogic`, aliased "Dragon"): `ART`/`TIPOART`/`CATEARTI`/`FAMILIA`/
  `PRECIOAR` (header, taxonomy, live price), `PRECOMPRADET`/`REMCOMPRADET` (color/talle variants,
  cascading fallback between the two sources).

`CatalogoRepositorio` (`Catalogo.Datos`) runs **one query per source, batched 500 codes at a time**
to stay under SQL Server's 2100-param limit — explicitly never a cross-database JOIN. All merging
happens in `CatalogoCache.ConstruirAsync` (`Catalogo.Aplicacion`). `SqlConnectionFactory`
(`Compartido`) derives the Dragon connection string from `ConnectionStrings:MarketDb` by swapping
`Initial Catalog` for `Catalogo:BaseDragon`, unless `ConnectionStrings:DragonDb` is set explicitly.

`CatalogoCache` is an in-memory singleton snapshot, refreshed by `CatalogoWarmup` (a `BackgroundService`
on a `PeriodicTimer`, interval = `Catalogo:MinutosCache` config value minus 30s, default every ~4.5
min). A failed refresh logs and keeps serving the previous snapshot — it never throws. This custom
cache is the actual caching layer; there is **no** `AddOutputCache()`/`UseOutputCache()` anywhere
despite older docs mentioning OutputCache.

Only the `Indumentaria` rubro is currently published — a temporary hardcoded filter in
`CatalogoCache.cs`, not a config toggle.

### Photo/thumbnail pipeline

`GET /fotos/{codigo}_{ancho}.webp?v={version}` (`Endpoints/FotosEndpoint.cs`) → `FotosService`
(`Catalogo.Aplicacion`). Widths are restricted to a closed list (400/1200px). Cache filename embeds
the version token (`{codigo}_{ancho}_{version}.webp` under `Fotos:DirCache`); if it exists, it's
served with no SQL involved. Otherwise the original is resolved from `CatalogoCache` (preferring
`LinkIADisco` over `LinkDriveDisco` — "IA primero, disco después"), resized/encoded with **SkiaSharp**
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
- `docs/CATALOGO-SYNC.md` — **not current**: an alternative materialized-table design, only relevant
  if the catalog grows ~5x.
- `docs/CONTENIDO.md` — approved institutional copy; `Institucional.Ui` pages transcribe it verbatim,
  including preserved typos — edit the manual/doc first, never the copy in `.razor` directly.
