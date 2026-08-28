# Fotos del catálogo

Cómo el sitio decide **qué imagen mostrar** para cada artículo, cómo la **redimensiona y cachea**, y cómo
se asegura de que el visitante **siempre vea la más reciente** sin perder velocidad.

Archivos involucrados:

- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Datos/CatalogoRepositorio.cs` → `TraerRutasFotoAsync` (SQL).
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Dominio/RutasFoto.cs` → resolución de rutas.
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Servicios/CatalogoStore.cs` → `VersionFoto`, armado de las filas del rebuild (`ConstruirFilasAsync`).
- `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Datos/CatalogoRepositorio.cs` → `LeerRutaFotoAsync` (lookup por PK del original).
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

La regla es **IA primero; si no hay IA, la de disco**. Se arma durante el rebuild del catálogo
(`CatalogoStore.ConstruirFilasAsync`), leyendo las rutas con `TraerRutasFotoAsync`, que ya resuelve la
preferencia en SQL con un `COALESCE`:

```sql
Ruta = COALESCE(
           NULLIF(RTRIM(ISNULL(F.LinkIADisco,   '')), ''),   -- 1º: IA
           NULLIF(RTRIM(ISNULL(F.LinkDriveDisco, '')), ''),   -- 2º: disco
           '')                                                -- vacío = sin foto
```

Por código puede haber **varias filas** (el sync inserta filas nuevas). Se toma la **más reciente**
(`ROW_NUMBER() OVER (PARTITION BY Codigo ORDER BY ID DESC)`, `Fila = 1`) y se descartan las que quedan
sin ninguna ruta (`LEN(Ruta) > 0`). La ruta resultante se persiste en la columna **`FotosJson`** de
`dbo.Catalogo` (`$[0].link`), de donde la lee después el endpoint de fotos.

> **Estado actual de los datos** (medido): ~64 artículos con IA, ~7.181 sólo con disco, ~773 sin nada.
> O sea: hoy la enorme mayoría se sirve con la foto de disco; la IA recién arranca.

## 2. `TieneFoto` es por LINK, no por archivo en disco

En el armado de las filas del rebuild (`CatalogoStore.ConstruirFilasAsync`):

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
cuidado, esto deja fotos viejas pegadas cuando una foto cambia (ej. a un artículo que sólo tenía foto de
disco se le genera la IA). Se resuelve con un **token de versión** — la fecha de modificación del original —
que es a la vez parte de la **URL** (para el navegador) y del **nombre del archivo cacheado** (para el
servidor). Es el patrón estándar de *fingerprint de assets*.

> **Por qué versión y no comparar fechas.** Una tentación es "regenerar si el original es más nuevo que el
> thumbnail". Falla en el caso principal: al pasar de disco a IA, el `_ia.jpg` puede ser **más viejo** que el
> `.webp` que se generó desde la foto de disco → la comparación no detecta el cambio. El token usa la fecha
> como **identidad** (igual/distinto), no como orden: si cambia el archivo de origen, cambia el token, y con
> eso el nombre del thumbnail. Funciona aunque las fechas estén "al revés".

### 5.1 El token de versión

- `ArticuloDto.FotoVersion` se calcula en `CatalogoStore.VersionFoto` **durante el rebuild** como la **fecha
  de modificación del original** (`GetLastWriteTimeUtc(...).Ticks`, en hex) y se guarda en la columna
  `FotoPrincipalVersion` de `dbo.Catalogo` (y dentro de `FotosJson`). Si el archivo no está accesible en esa
  máquina, cae al hash de la ruta (que igual cubre disco→IA, porque ahí cambia el nombre).
- Las `<img>` piden `/fotos/{código}_{ancho}.webp?v={FotoVersion}`.

### 5.2 Navegador — URL versionada

- Foto sin cambios → misma URL → el navegador usa su caché (rápido, sin requests). Por eso el header sigue
  siendo `immutable` a 30 días: es correcto **porque la URL cambia cuando cambia la foto**.
- Foto cambia → `?v=` distinto → **URL nueva → el navegador baja la nueva al instante**.

### 5.3 Servidor — el `.webp` en disco lleva la versión en el nombre

`FotosService.ObtenerAsync` lee el `?v=` (lo sanitiza: sólo letras/dígitos) y arma el nombre del thumbnail
con él: **`{código}_{ancho}_{versión}.webp`**.

- Si existe → lo sirve (existe = ya se generó para ESTA versión → está bien).
- Si no existe → lo genera desde el original **actual** (IA-primero), cuya ruta resuelve con un lookup por
  PK sobre `dbo.Catalogo` (`CatalogoRepositorio.LeerRutaFotoAsync`, que lee `FotosJson`, `$[0].link`).

Así el servidor **no** puede quedar sirviendo un thumbnail viejo: un cambio de foto produce un nombre nuevo
que todavía no existe → se regenera. (Los links viejos sin `?v=` caen al nombre sin versión, sólo por
compatibilidad.)

### 5.4 Limpieza automática de huérfanos

Al generar `{código}_{ancho}_{versión}.webp`, `LimpiarVersionesViejas` **borra los otros
`{código}_{ancho}_*.webp`** de ese artículo (versiones anteriores, y el nombre viejo sin versión),
conservando el recién generado. Así el disco no acumula huérfanos y **no hace falta un job aparte ni borrar
la carpeta a mano**. Es best effort: si un borrado falla (archivo en uso), se loguea y sigue.

### Flujo completo cuando se le genera la IA a un artículo

1. En el próximo rebuild (TTL ~20 min, config `Catalogo:MinutosTtl`) la base toma el nuevo `LinkIADisco`
   (COALESCE IA primero).
2. Su `FotoVersion` cambia (el `_ia.jpg` es otro archivo, otra fecha) → cambia el `?v=` de sus `<img>`.
3. El navegador ve una URL nueva → pide `..._{versiónNueva}.webp` → el servidor no lo tiene → lo genera
   desde la IA, borra las versiones viejas de esa foto → **se ve la IA**. Todo automático.

## 6. El endpoint

`GET /fotos/{archivo}` (`FotosEndpoint`), con `archivo` = `{código}_{ancho}.webp`:

- Parsea código y ancho (el código puede tener puntos, ej. `IM013.056`) y lee el `?v=` de la query, que le
  pasa al servicio para que forme parte del nombre del thumbnail (ver 5.3).
- Delega en `IFotosCatalogo` (el host no sabe de SkiaSharp ni de dónde se cachea).
- Responde con `Cache-Control: public, max-age=2592000, immutable` (30 días). Es correcto **porque la URL
  cambia cuando cambia la foto** (ver 5.2); sin el `?v=` este header dejaría fotos viejas pegadas.
- `robots.txt` bloquea `/fotos/` (no queremos que se indexen los thumbnails).
