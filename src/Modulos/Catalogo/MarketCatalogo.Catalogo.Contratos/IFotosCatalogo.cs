namespace MarketCatalogo.Catalogo.Contratos;

/// <summary>Un thumbnail listo para servir: ruta en disco y su content-type.</summary>
public sealed record FotoResultado(string RutaArchivo, string ContentType);

/// <summary>
/// Puerto de fotos del módulo. El host expone el endpoint HTTP; la generación, el cacheo en disco y
/// la resolución de la ruta original viven adentro del módulo.
/// </summary>
public interface IFotosCatalogo
{
    /// <summary>Anchos que se pueden pedir. Lista cerrada a propósito: si el ancho viniera libre del
    /// query string, cualquiera podría hacernos generar miles de tamaños y llenar el disco.</summary>
    IReadOnlyList<int> AnchosPermitidos { get; }

    /// <summary>Devuelve el thumbnail, generándolo si hace falta. null si el artículo no está en el
    /// catálogo, no tiene foto, o el original no está en disco.
    /// <paramref name="version"/> es el token <c>?v=</c> de la URL (fecha del original): forma parte del
    /// nombre del archivo cacheado, así un cambio de foto (p. ej. disco→IA) genera un nombre nuevo y se
    /// regenera solo, sin depender de comparar fechas ni de borrar la carpeta.
    /// <paramref name="incluirNoPublicados"/> = true (sólo para staff logueado) sirve también fotos de
    /// artículos NO publicados; esos thumbnails se cachean en un namespace aparte para que nunca se
    /// sirvan al público aunque un usuario interno los haya generado.</summary>
    Task<FotoResultado?> ObtenerAsync(string? artCod, int ancho, string? version,
        bool incluirNoPublicados = false, CancellationToken ct = default);

    /// <summary>La foto de PRODUCCIÓN del artículo (la que sube el equipo mientras se fabrica). Es otra
    /// fuente que la del catálogo: lo que un proveedor está fabricando todavía no está publicado, así que
    /// por la vía normal su foto no se puede resolver. Se cachea en un namespace propio, de modo que nunca
    /// se sirva por el endpoint público. <b>No valida permisos</b>: quien la pide tiene que haber verificado
    /// antes que ese artículo le corresponde.</summary>
    Task<FotoResultado?> ObtenerDeProduccionAsync(string? artCod, int ancho, string? version,
        CancellationToken ct = default);
}
