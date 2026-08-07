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
    /// catálogo, no tiene foto, o el original no está en disco.</summary>
    Task<FotoResultado?> ObtenerAsync(string? artCod, int ancho, CancellationToken ct = default);
}
