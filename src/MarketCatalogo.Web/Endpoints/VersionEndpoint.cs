namespace MarketCatalogo.Web.Endpoints;

/// <summary>
/// Qué versión está viva. Sale de <c>wwwroot/buildinfo.txt</c>, que el build escribe con el commit del que
/// salió (ver build-info.ps1 + el Target del .csproj).
///
/// Existe porque sin esto no había forma de saber si un deploy entró: el 10/08/2026 comparamos títulos de
/// páginas a ojo, concluimos mal que el sync no había copiado, y borramos la carpeta del sitio al vacío.
/// El propio workflow de deploy consulta este endpoint y falla si lo que quedó arriba no es el commit que
/// acaba de compilar.
/// </summary>
public static class VersionEndpoint
{
    private static string? _cache;

    /// <summary>Línea completa del build: "sha\tfecha\tasunto". Vacío si el build no pudo leer git.</summary>
    public static string Leer(IWebHostEnvironment env)
    {
        if (_cache is not null) return _cache;
        try
        {
            var f = Path.Combine(env.WebRootPath ?? "", "buildinfo.txt");
            _cache = File.Exists(f) ? File.ReadAllText(f).Trim() : "";
        }
        catch { _cache = ""; }
        return _cache;
    }

    /// <summary>Solo el hash corto, para mostrar en el pie del sitio.</summary>
    public static string Sha(IWebHostEnvironment env)
    {
        var t = Leer(env);
        if (t.Length == 0) return "";
        var sha = t.Split('\t', ' ')[0].Trim();
        return sha.Length is > 0 and <= 12 ? sha : "";
    }

    public static void MapVersion(this WebApplication app)
    {
        // Texto plano y sin caché: lo consulta el deploy para verificar, así que tiene que ser el valor del
        // proceso vivo y no algo que quedó en un caché intermedio.
        app.MapGet("/version", (IWebHostEnvironment env, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var t = Leer(env);
            return Results.Text(t.Length == 0 ? "(sin buildinfo)" : t, "text/plain; charset=utf-8");
        });
    }
}
