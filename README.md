# MARKET Catálogo

Sitio web oficial de **MARKET arg**: institucional (misión, visión, quiénes somos), noticias y un
catálogo navegable de productos.

Proyecto independiente de [MARKETweb](https://github.com/israelbastarrica/MARKETweb), con el mismo
stack. MARKETweb es el sistema **interno** de gestión; este es el sitio **público**.

## Stack

- **.NET 9**
- **MarketCatalogo.Web** — Blazor Web App con **SSR** (render mode estático, sin WebAssembly).
  Es la única que abre conexión a SQL Server. Sirve las páginas y los thumbnails.
- **MarketCatalogo.Application** — lógica y acceso a datos (Dapper).
- **MarketCatalogo.Shared** — DTOs.

```
Navegador  ──HTML/SSR──►  MarketCatalogo.Web  ──►  MarketCatalogo.Application  ──►  SQL Server
```

### Por qué SSR y no WebAssembly (como MARKETweb)

MARKETweb es una app interna: el usuario la abre una vez y trabaja horas, así que pagar 2 MB de
runtime .NET en la primera carga se amortiza. Un catálogo público es lo opuesto — visitas cortas,
mobile, y **el SEO y los previews al compartir son la mitad del valor**. Con SSR el crawler ve HTML
real y el primer pixel aparece en milisegundos.

La decisión es reversible y granular: si una pantalla necesita interactividad en C#, se agrega un
proyecto `.Client` y `@rendermode InteractiveWebAssembly` en ese componente, sin tocar el resto.

## Configurar la conexión a la base

La contraseña **no** se versiona:

```powershell
dotnet user-secrets set "ConnectionStrings:MarketDb" "Server=TU_SERVIDOR;Database=MARKET;User Id=USUARIO;Password=LA_PASS;TrustServerCertificate=True;MultipleActiveResultSets=True;Encrypt=True" --project src/MarketCatalogo.Web
```

Este sitio **solo lee**. Cuando se pase a la Alternativa B del plan, la cadena debe usar un login
**read-only** con permiso únicamente sobre las tablas `CatalogoWeb*`.

## Correr

```powershell
dotnet run --project src/MarketCatalogo.Web
```

## Documentación

- [docs/PLAN.md](docs/PLAN.md) — plan de implementación: alternativas de acceso a datos, taxonomía de
  navegación, modelo de datos, mapa de URLs, fases y puntos abiertos.
- [docs/MEDICION.md](docs/MEDICION.md) — **números reales**: 985 artículos armados en locales, 678 con
  foto. Corrigió tres decisiones del plan. Leerlo antes que el resto.
- [docs/CONSULTAS.md](docs/CONSULTAS.md) — el flujo de datos: qué SQL corre, cuándo, y hasta dónde
  escala. **Una consulta cada 5 minutos**, el resto en memoria.
- [docs/DECISION-TABLAS.md](docs/DECISION-TABLAS.md) — por qué el catálogo lee **en vivo** y guarda casi
  nada, con los tiempos medidos. Incluye por qué no sirve reutilizar `GoogleDriveFotosArticulos`.
- [docs/CATALOGO-SYNC.md](docs/CATALOGO-SYNC.md) — diseño materializado (5 tablas + job).
  **No vigente**: es el camino si el catálogo crece ~5x.
- [docs/DISENO.md](docs/DISENO.md) — sistema de diseño derivado del Manual de Marca. **Leer antes de
  tocar CSS.** La marca es monocromática: el rosa y el verde menta están prohibidos.
- [docs/CONTENIDO.md](docs/CONTENIDO.md) — copy institucional aprobada (quiénes somos, misión, visión,
  valores), transcripta del manual.
- [docs/ManualDeMarca.pdf](docs/ManualDeMarca.pdf) — el manual oficial. Es la autoridad final.

## Base de datos

- [sql/01_catalogo_schema.sql](sql/01_catalogo_schema.sql) — 2 tablas (`CatalogoArticulo` rala +
  `CatalogoTalles` con su seed de 53 talles). Aditivo e idempotente. **Todavía no ejecutado.**

Todo lo demás (descripción, rubro, género, familia, precio, combo, locales, talles y colores) se lee
**en vivo** de `DRAGONFISH_CENTRAL` y `MARKET`, con `OutputCache` adelante. Nada se replica.

## Convenciones heredadas de MARKET

- **Nunca DELETE físico**: baja lógica con `Eliminado = 1`.
- Consultas **siempre parametrizadas**.
- Campo `Auditoria` con formato `Acción | origen | fecha`.
