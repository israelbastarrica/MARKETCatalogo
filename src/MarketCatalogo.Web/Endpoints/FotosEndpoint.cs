using MarketCatalogo.Auth.Contratos;
using MarketCatalogo.Catalogo.Contratos;

namespace MarketCatalogo.Web.Endpoints;

/// <summary>
/// Sirve los thumbnails del catálogo. Ruta: <c>/fotos/{codigo}_{ancho}.webp</c>.
///
/// El host sólo conoce <see cref="IFotosCatalogo"/> (en Catalogo.Contratos): no sabe que la
/// generación usa SkiaSharp, ni dónde se cachean los archivos.
///
/// Ninguna imagen toca SQL: la ruta del original sale del caché en memoria y el archivo se lee de disco.
/// Se cachea 30 días en el navegador — los thumbnails son inmutables porque el nombre incluye el ancho,
/// y si cambia la foto original cambia el archivo generado.
/// </summary>
public static class FotosEndpoint
{
    public static void MapFotos(this WebApplication app)
    {
        app.MapGet("/fotos/{archivo}", async (
            string archivo, IFotosCatalogo fotos, HttpContext ctx, CancellationToken ct) =>
        {
            // "IM013.056_400.webp" → código IM013.056, ancho 400
            if (!archivo.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            var sinExt = archivo[..^5];

            var corte = sinExt.LastIndexOf('_');
            if (corte <= 0 || !int.TryParse(sinExt[(corte + 1)..], out var ancho)) return Results.NotFound();

            var codigo = sinExt[..corte];
            // ?v= es el token de versión de la foto (fecha del original). Forma parte del nombre del
            // thumbnail cacheado, así un cambio de foto (disco→IA) genera un archivo nuevo automáticamente.
            var version = ctx.Request.Query["v"].ToString();
            // Staff logueado (estado = ok): puede ver también fotos de artículos NO publicados (depósito,
            // ocultos). El público sólo las publicadas. Esos thumbnails internos se cachean aparte.
            var interno = ctx.User?.HasClaim(PoliticasAuth.ClaimEstado, PoliticasAuth.EstadoOk) == true;
            var res = await fotos.ObtenerAsync(codigo, ancho, version, interno, ct);
            if (res is null) return Results.NotFound();

            // Inmutable: el nombre del archivo identifica el contenido.
            ctx.Response.Headers.CacheControl = "public, max-age=2592000, immutable";
            return Results.File(res.RutaArchivo, res.ContentType, enableRangeProcessing: false);
        });
    }
}
