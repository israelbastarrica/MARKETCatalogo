using MarketCatalogo.Catalogo.Contratos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Sirve las fotos del catálogo redimensionadas, generándolas <b>bajo demanda</b> y cacheándolas en disco.
///
/// El problema que resuelve: las originales de <c>D:\FotosArticulos</c> pesan megas y se muestran en
/// cards de ~300 px. Servirlas tal cual son ~72 MB por página de catálogo. Redimensionar baja eso ~37×;
/// el formato WebP aporta sólo un 30% más — <b>lo que importa es el tamaño, no el formato</b>.
///
/// Bajo demanda y no en un job porque así sólo se genera lo que alguien realmente mira, no queda el
/// estado "el job se olvidó de generar este thumbnail", y si se borra la carpeta se regenera sola.
/// Precio: el primer visitante de cada foto paga el resize una vez.
///
/// Se usa SkiaSharp (MIT) y no ImageSharp: ImageSharp 4 exige licencia comercial de Six Labors y falla
/// en tiempo de compilación sin ella.
/// </summary>
public sealed class FotosService : IFotosCatalogo
{
    // Lista cerrada a propósito: si el ancho viniera libre del query string, cualquiera podría
    // hacernos generar miles de tamaños distintos y llenar el disco.
    private static readonly int[] Anchos = [400, 1200];
    public IReadOnlyList<int> AnchosPermitidos => Anchos;

    private const int CalidadWebp = 100;

    private readonly CatalogoCache _cache;
    private readonly ILogger<FotosService> _log;
    private readonly string _dirCache;
    private readonly string? _dirOriginalesOverride;

    public FotosService(CatalogoCache cache, IConfiguration cfg, ILogger<FotosService> log)
    {
        _cache = cache;
        _log = log;
        _dirCache = cfg["Fotos:DirCache"] is { Length: > 0 } d ? d.Trim() : @"D:\FotosCatalogo";
        // Si la web corre en otra máquina que mapea la carpeta en otra unidad, se reemplaza el
        // directorio conservando el nombre del archivo (mismo criterio que MARKETweb).
        _dirOriginalesOverride = cfg["Fotos:DirOriginales"];
    }

    /// <summary>Devuelve el thumbnail pedido, generándolo si no existe. null si el artículo no está en el
    /// catálogo, si no tiene foto, o si el original no está en disco.</summary>
    public async Task<FotoResultado?> ObtenerAsync(string? artCod, int ancho, CancellationToken ct = default)
    {
        var cod = (artCod ?? "").Trim();
        if (cod.Length == 0 || !Anchos.Contains(ancho)) return null;

        // El código viene de la URL: nunca se concatena crudo a una ruta (evita ../.. y caracteres raros).
        var seguro = RutasFoto.NombreSeguro(cod);
        if (seguro.Length == 0) return null;

        var destino = Path.Combine(_dirCache, $"{seguro}_{ancho}.webp");

        // Se resuelve el original ANTES de servir el thumbnail cacheado: hace falta su fecha para saber si
        // el .webp quedó viejo. El nombre del thumbnail es sólo código+ancho, así que NO cambia cuando la
        // foto de origen cambia (p. ej. al artículo que sólo tenía foto de disco se le genera una IA). Sin
        // este chequeo, se seguiría sirviendo el thumbnail viejo para siempre.
        var snap = await _cache.ObtenerAsync(ct);
        if (!snap.RutaFotoPorCodigo.TryGetValue(cod, out var rutaOriginal)) return null;

        var origen = RutasFoto.Resolver(rutaOriginal, _dirOriginalesOverride);
        if (origen is null || !File.Exists(origen)) return null;

        // Se sirve el thumbnail cacheado SÓLO si está al día: existe y se generó DESPUÉS del original.
        // Si el original es más nuevo (foto reemplazada, o recién generada la IA), se cae abajo y se
        // regenera solo — sin borrar la carpeta a mano.
        if (File.Exists(destino) && File.GetLastWriteTimeUtc(origen) <= File.GetLastWriteTimeUtc(destino))
            return new FotoResultado(destino, "image/webp");

        try
        {
            Directory.CreateDirectory(_dirCache);
            GenerarWebp(origen, destino, ancho);
            return new FotoResultado(destino, "image/webp");
        }
        catch (Exception ex)
        {
            // Si el resize falla (foto corrupta, permisos), se sirve la original: preferible a una imagen
            // rota, aunque pese. Queda logueado para que no pase inadvertido.
            _log.LogWarning(ex, "No se pudo generar el thumbnail de {Codigo} a {Ancho}px; se sirve la original.", cod, ancho);
            return new FotoResultado(origen, "image/jpeg");
        }
    }

    private static void GenerarWebp(string origen, string destino, int anchoMax)
    {
        using var original = SKBitmap.Decode(origen)
            ?? throw new InvalidOperationException($"No se pudo decodificar '{origen}'.");

        // Max: nunca agranda una foto chica (quedaría borrosa) y conserva la proporción.
        var escala = Math.Min(1f, (float)anchoMax / original.Width);
        var ancho = Math.Max(1, (int)MathF.Round(original.Width * escala));
        var alto = Math.Max(1, (int)MathF.Round(original.Height * escala));

        using var redimensionada = escala < 1f
            ? original.Resize(new SKImageInfo(ancho, alto), SKFilterQuality.High)
            : original.Copy();
        if (redimensionada is null) throw new InvalidOperationException("Falló el redimensionado.");

        using var imagen = SKImage.FromBitmap(redimensionada);
        using var datos = imagen.Encode(SKEncodedImageFormat.Webp, CalidadWebp);

        // Se escribe a un temporal y se mueve: si entran dos requests a la vez por la misma foto,
        // ninguno alcanza a leer un archivo a medio escribir.
        var temporal = destino + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = File.Create(temporal)) datos.SaveTo(fs);
            File.Move(temporal, destino, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporal)) File.Delete(temporal); } catch { /* best effort */ }
        }
    }
}
