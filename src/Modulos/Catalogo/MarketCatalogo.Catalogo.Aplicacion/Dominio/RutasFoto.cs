namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Rutas de las fotos de artículo. <c>GoogleDriveFotosArticulos.LinkDriveDisco</c> guarda una ruta
/// absoluta de la máquina donde se subió la foto (ej. <c>D:\FotosArticulos\IH001.086.jpg</c>).
/// Mismo criterio que <c>FotosArticulo</c> en MARKETweb.
/// </summary>
public static class RutasFoto
{
    /// <summary>Resuelve la ruta física. Si hay override de carpeta (config <c>Fotos:DirOriginales</c>),
    /// reemplaza el directorio conservando el nombre del archivo — útil cuando la web corre en otra
    /// máquina que mapea la carpeta en otra letra de unidad.</summary>
    public static string? Resolver(string? rutaEnBase, string? dirOverride)
    {
        if (string.IsNullOrWhiteSpace(rutaEnBase)) return null;
        var p = rutaEnBase.Trim();
        if (string.IsNullOrWhiteSpace(dirOverride)) return p;
        try { return Path.Combine(dirOverride.Trim(), Path.GetFileName(p)); }
        catch { return p; }
    }

    /// <summary>Nombre de archivo seguro a partir de un código que vino de la URL. Deja sólo letras,
    /// dígitos, punto y guión: sin esto, un código como <c>..\..\web.config</c> saldría de la carpeta.</summary>
    public static string NombreSeguro(string? codigo)
    {
        var s = (codigo ?? "").Trim().ToUpperInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-') sb.Append(c);
        return sb.ToString();
    }
}
