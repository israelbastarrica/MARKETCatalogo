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

1. **En algún local** — está stockeado en LURO o PERALTA (no sólo en depósito).
2. **Tiene variantes** — tiene al menos una fila de color/talle en `PRECOMPRA` o `REMCOMPRA`. Mejor no
   mostrarlo que mostrarlo sin talles. (Excepción: Lencería, que no usa esa cascada.)
3. **Tiene foto** — tiene foto de **IA o de disco** (drive). Es lo que marca el bit `tieneFoto`
   (`LinkIADisco` primero, `LinkDriveDisco` después — ver [FOTOS.md](FOTOS.md) §2). **La foto es la
   curación del catálogo**: se publican TODOS los rubros (Indumentaria, Accesorios, Lencería, Calzado…),
   y lo único que decide qué sale es tener foto.

> **Nota histórica:** antes (1) era "Rubro = Indumentaria" y la foto **no** era requisito. Se cambió: se
> abrió a todos los rubros y la foto pasó a ser el filtro de curación. Ver §2.

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

## 2. Curación por foto (todos los rubros)

> **El sitio publica TODOS los rubros, pero sólo los artículos que tienen foto (de IA o de disco).**
> La foto es lo que curó qué sale: si el staff le puso foto, sale; si no, no.

Está implementado como el cálculo de `PublicadoBase` en `ConstruirFilasAsync`:

```csharp
var publicadoBase =
    enAlgunLocal
    && (tieneVariantes || esLenceria)
    && tieneFoto;
```

- `tieneFoto` es verdadero cuando el rebuild resolvió una ruta de foto (IA primero, disco después) para
  el artículo — ver [FOTOS.md](FOTOS.md) §2.
- Los artículos sin foto quedan persistidos con `Publicado = 0` (visibles en el **interno**), no salen en
  el público. Ídem los que no están en ningún local o no tienen variantes.
- El override manual sigue mandando sobre esto: `VisibilidadManual = 'mostrar'` publica igual (cualquier
  rubro, con o sin foto) y `'ocultar'` esconde; `'auto'` respeta este criterio (ver §1).
