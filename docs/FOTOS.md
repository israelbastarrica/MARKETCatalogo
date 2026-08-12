# Fotos del catálogo

Cómo el sitio decide **qué imagen mostrar** para cada artículo, cómo la **redimensiona y cachea**, y cómo
se asegura de que el visitante **siempre vea la más reciente** sin perder velocidad.

Archivos involucrados:

- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Datos/CatalogoRepositorio.cs` → `TraerRutasFotoAsync` (SQL).
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Dominio/RutasFoto.cs` → resolución de rutas.
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Servicios/CatalogoCache.cs` → `VersionFoto`, armado del snapshot.
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Servicios/FotosService.cs` → resize + caché en disco.
- `src/MarketCatalogo.Web/Endpoints/FotosEndpoint.cs` → el endpoint HTTP `/fotos/...`.
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Ui/Componentes/CardArticulo.razor` y `.../Paginas/Producto.razor` → las `<img>`.

---

## 1. De dónde sale la foto: IA primero, disco después

Las fotos se registran en la tabla **`MARKET.dbo.GoogleDriveFotosArticulos`**, con dos columnas de ruta
por artículo:

- **`LinkIADisco`** — la foto generada por IA (nombre con sufijo `_ia`, ej. `D:\FotosArticulos\IU109.140_ia.jpg`).
- **`LinkDriveDisco`** — la foto "normal" subida a mano (ej. `D:\FotosArticulos\IU109.140.jpg`).

Ambas apuntan a archivos en **la misma carpeta**; sólo cambia el sufijo `_ia`.

La regla es **IA primero; si no hay IA, la de disco**. Se resuelve en SQL con un `COALESCE`
(`TraerRutasFotoAsync`):

```sql
Ruta = COALESCE(
           NULLIF(RTRIM(ISNULL(F.LinkIADisco,   '')), ''),   -- 1º: IA
           NULLIF(RTRIM(ISNULL(F.LinkDriveDisco, '')), ''),   -- 2º: disco
           '')                                                -- vacío = sin foto
```

Por código puede haber **varias filas** (el sync inserta filas nuevas). Se toma la **más reciente**
(`ROW_NUMBER() OVER (PARTITION BY Codigo ORDER BY ID DESC)`, `Fila = 1`) y se descartan las que quedan
sin ninguna ruta (`LEN(Ruta) > 0`).

> **Estado actual de los datos** (medido): ~64 artículos con IA, ~7.181 sólo con disco, ~773 sin nada.
> O sea: hoy la enorme mayoría se sirve con la foto de disco; la IA recién arranca.

## 2. `TieneFoto` es por LINK, no por archivo en disco

En el armado del caché (`CatalogoCache.ConstruirAsync`):

```csharp
var ruta = fotoPorCodigo.GetValueOrDefault(a.ArtCod);
var tieneFoto = !string.IsNullOrWhiteSpace(ruta);   // ¿hay link en la DB? — NO chequea el disco
```

Consecuencia importante: si la DB tiene link pero **el `.jpg` no está en disco**, `TieneFoto` es `true`
igual. La card entonces **renderiza la `<img>`** y, al no encontrar el archivo, el endpoint responde 404 →
se ve **imagen vacía** (no el placeholder gris de "sin foto todavía", que sólo aparece cuando `TieneFoto`
es `false`).

Para distinguir "sin link" de "con link pero falta el archivo" está el **diagnóstico** — ver
[CATALOGO-PUBLICACION.md](CATALOGO-PUBLICACION.md).

## 3. Resolución de la ruta física

`RutasFoto.Resolver(rutaEnBase, dirOverride)`:

- Sin override (`Fotos:DirOriginales` vacío) → usa la ruta de la DB **tal cual** (ej. `D:\FotosArticulos\...`).
- Con override → **reemplaza la carpeta** conservando el nombre del archivo. Sirve cuando la web corre en
  otra máquina que mapea las fotos en otra unidad/carpeta.

`RutasFoto.NombreSeguro` limpia el código que viene por URL (deja letras, dígitos, `.` y `-`) para que un
código no pueda salirse de la carpeta (`..\..\web.config`).

## 4. Thumbnails: resize bajo demanda + caché en disco

`FotosService` (usa **SkiaSharp**, MIT) sirve las fotos **redimensionadas**, no las originales:

- Las originales pesan megas y se muestran en cards de ~300 px. Servirlas crudas son **~72 MB por página**
  de catálogo. El resize las baja **~37×** — *ese* es el ahorro. El formato WebP suma sólo ~30% más: lo que
  importa es el tamaño, no el formato.
- **Anchos permitidos: 400 y 1200** (lista cerrada, para que nadie nos haga generar miles de tamaños).
  La card usa `400` (con `srcset` a `1200` para pantallas retina); la ficha usa `1200`.
- **Bajo demanda**: el primer visitante de cada foto paga el resize una vez; el resultado se cachea en disco
  (`Fotos:DirCache`). Si se borra la carpeta, se regenera sola.
- Si una foto puntual no se puede decodificar (corrupta), se sirve **la original** tal cual (peor que un
  thumbnail, mejor que una imagen rota) y queda logueado.

### Config (`Fotos:`)

| Clave | Qué es | Dónde |
|---|---|---|
| `Fotos:DirCache` | Carpeta de thumbnails generados (descartable). | `appsettings.json` (prod: `D:\FotosCatalogo`), dev: `_fotoscache`. |
| `Fotos:DirOriginales` | Override de la carpeta de originales. Vacío = ruta de la DB tal cual. | `appsettings.json` (prod: vacío), dev: `_fotostest`. |

## 5. Que el visitante SIEMPRE vea la más reciente

El nombre del thumbnail es sólo **`{código}_{ancho}.webp`** — **no** codifica de qué original salió. Sin
cuidado, esto genera dos desfasajes cuando una foto cambia (ej. a un artículo que sólo tenía foto de disco
se le genera la IA). Se resuelven en dos capas, **ambas usando la misma señal: la fecha de modificación del
archivo original**.

### 5.1 Servidor — regenera el thumbnail si el original es más nuevo

En `FotosService.ObtenerAsync`, antes de servir el `.webp` cacheado se compara su fecha con la del original:

```csharp
if (File.Exists(destino) && File.GetLastWriteTimeUtc(origen) <= File.GetLastWriteTimeUtc(destino))
    return destino;   // el thumbnail está al día
// si no: se regenera desde el original nuevo
```

Así, cuando aparece un `_ia.jpg` nuevo (fecha reciente), el `.webp` viejo queda desactualizado y se
**regenera solo** en la próxima visita — sin borrar carpetas a mano.

### 5.2 Navegador — URL versionada (`?v=`)

Aunque el servidor ya sirva la nueva, el navegador cachea la imagen (header `immutable`, 30 días) y, con la
**misma URL**, no la volvería a pedir. La solución estándar es **cambiar la URL cuando cambia la foto**:

- `ArticuloDto.FotoVersion` = token de versión, calculado en `CatalogoCache.VersionFoto` como la **fecha de
  modificación del original** (`GetLastWriteTimeUtc(...).Ticks`). Si el archivo no está accesible en esa
  máquina, cae al hash de la ruta (que igual cubre el caso disco→IA, porque ahí cambia el nombre).
- Las `<img>` piden `/fotos/{código}_{ancho}.webp?v={FotoVersion}`.

Resultado:

- Foto sin cambios → misma URL → el navegador usa su caché (rápido, sin requests).
- Foto cambia / se genera la IA → `?v=` distinto → **URL nueva → baja la nueva al instante**.

El `?v=` es sólo query string: el endpoint lo ignora al parsear (`{archivo}` sigue siendo
`{código}_{ancho}.webp`), y el `.webp` en disco **no** se versiona (se regenera en su lugar, ver 5.1).

### Flujo completo cuando se le genera la IA a un artículo

1. En ≤5 min el caché en memoria del catálogo se refresca y toma el nuevo `LinkIADisco` (COALESCE IA primero).
2. Su `FotoVersion` cambia (el `_ia.jpg` tiene fecha nueva) → cambia el `?v=` de sus `<img>`.
3. El navegador ve una URL nueva → pide la foto → el servidor ve que el original es más nuevo → regenera el
   `.webp` desde la IA → **se ve la IA**. Todo automático.

## 6. El endpoint

`GET /fotos/{archivo}` (`FotosEndpoint`), con `archivo` = `{código}_{ancho}.webp`:

- Parsea código y ancho (el código puede tener puntos, ej. `IM013.056`).
- Delega en `IFotosCatalogo` (el host no sabe de SkiaSharp ni de dónde se cachea).
- Responde con `Cache-Control: public, max-age=2592000, immutable` (30 días). Es correcto **porque la URL
  cambia cuando cambia la foto** (ver 5.2); sin el `?v=` este header dejaría fotos viejas pegadas.
- `robots.txt` bloquea `/fotos/` (no queremos que se indexen los thumbnails).
