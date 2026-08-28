# Qué se publica y qué se descarta

Reglas que deciden si un artículo aparece en el catálogo público.

Todo esto vive en `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Servicios/CatalogoStore.cs`
(`ConstruirFilasAsync`), que arma las filas cruzando las fuentes en C# y calcula, por artículo, el bit
`PublicadoBase`. Esas filas se **persisten** en la tabla materializada `MARKET.dbo.Catalogo` (vía
`GuardarBaseAsync`, un MERGE) — es el modelo **tabla-como-caché**: la tabla *es* el caché, no hay un
snapshot en RAM que sirva el sitio. La grilla pública (listado, ficha, búsqueda, facetas, mega-menú,
home) se resuelve **en SQL** con `WHERE Publicado = 1`, así que ve el resultado apenas queda
materializado. Ver [CONSULTAS.md](CONSULTAS.md) para el porqué de la tabla-como-caché.

---

## 1. Filtros de publicación

Hay que distinguir dos cosas:

- **Lo único que se descarta por completo** (no entra ni a la tabla) es la **taxonomía inválida**: rubro
  o género vacío o `"No aplica"`. Descarta pseudo-artículos de promoción (ej. `"2X15000"`) y datos mal
  cargados. Se evalúa primero (un `continue` en `ConstruirFilasAsync`).
- **Todo lo demás se persiste igual**, con su bit de publicación calculado, para que la **vista interna**
  lo vea. Un artículo aparece en el catálogo público sólo si `PublicadoBase` es verdadero **y** no está
  oculto manualmente.

`PublicadoBase` (criterio objetivo, en `ConstruirFilasAsync`) es verdadero cuando el artículo cumple
**todo** esto:

1. **Rubro = Indumentaria** — *filtro temporal*, ver §2.
2. **En algún local** — está stockeado en LURO o PERALTA (no sólo en depósito).
3. **Tiene variantes** — tiene al menos una fila de color/talle en `PRECOMPRA` o `REMCOMPRA`. Mejor no
   mostrarlo que mostrarlo sin talles. (Excepción: Lencería, que no usa esa cascada.)

Que un artículo **no tenga foto NO lo descarta**: se publica igual, con un placeholder. Ver
[FOTOS.md](FOTOS.md) §2.

### Override manual de visibilidad (3 estados)

Además del criterio objetivo hay una **decisión editorial** por artículo, en la columna
`dbo.Catalogo.VisibilidadManual` (la misma tabla; ya no existe una tabla `CatalogoArticulo` de overrides).
Tiene 3 estados: **`auto`** (default — vale el criterio objetivo), **`mostrar`** (fuerza publicar, sirve
para cualquier rubro, no solo Indumentaria) y **`ocultar`** (fuerza esconder). El botón mostrar/ocultar
(`CambiarVisibilidadAsync`) hace **una sola escritura** sobre `dbo.Catalogo`: setea `VisibilidadManual`
+ `Auditoria` (formato `Acción | origen | fecha`) y recalcula `Publicado` al instante, así que la grilla
lo refleja en el próximo request.

El **rebuild preserva** `VisibilidadManual`: el MERGE nunca lo pisa y recomputa `Publicado`
respetándolo — `'ocultar'→0`, `'mostrar'→1`, `'auto'→PublicadoBase`. De esa forma la reconstrucción
periódica no borra la decisión humana, y lo publicado a mano sobrevive los rebuilds.

## 2. Filtro temporal: sólo Indumentaria

> **POR AHORA el sitio publica únicamente el rubro `Indumentaria`.** El resto (Accesorios, Lencería,
> Calzado…) queda fuera hasta que se decida sumarlos.

Está implementado como una condición del cálculo de `PublicadoBase` en `ConstruirFilasAsync`:

```csharp
var publicadoBase =
    Texto.SinAcentos(rubro) == "indumentaria"
    && enAlgunLocal
    && (tieneVariantes || esLenceria);
```

- Se compara sin acentos y en minúsculas (mismo criterio que el de Lencería), para no depender de
  mayúsculas/tildes que vienen del ERP.
- A diferencia de antes, esto **no descarta** los otros rubros de la tabla: quedan persistidos con
  `Publicado = 0` (visibles en el interno), simplemente no salen en el público. Como la grilla pública
  filtra por `Publicado = 1`, al quedar un solo rubro la faceta "Tipo" se **auto-oculta**; el mega-menú
  muestra sólo géneros; etc.
- **Para revertir** (volver a publicar todos los rubros): quitar la condición
  `Texto.SinAcentos(rubro) == "indumentaria"` de `PublicadoBase` (y la condición equivalente en
  `CambiarVisibilidadAsync`, que espeja el mismo criterio para reflejar `Publicado` al mostrar).
