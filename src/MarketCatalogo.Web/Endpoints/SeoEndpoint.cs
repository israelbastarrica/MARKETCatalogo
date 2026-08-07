using System.Text;
using System.Xml;
using MarketCatalogo.Catalogo.Contratos;

namespace MarketCatalogo.Web.Endpoints;

/// <summary>
/// <c>/robots.txt</c> y <c>/sitemap.xml</c>. El sitemap se arma en vivo desde el caché en memoria
/// (cero SQL): una entrada por rubro, por rubro/género y por producto. El <c>lastmod</c> sale de cuándo
/// se generó el snapshot.
///
/// El host (esquema + dominio) se toma del request, no se hardcodea: así el mismo binario sirve un
/// sitemap correcto en localhost, en staging y en producción sin recompilar.
///
/// Las URLs con filtros (query string) NO van al sitemap y ya salen con <c>noindex</c> desde la página:
/// son miles de combinaciones casi duplicadas y sólo diluirían el presupuesto de crawleo.
/// </summary>
public static class SeoEndpoint
{
    public static void MapSeo(this WebApplication app)
    {
        app.MapGet("/robots.txt", (HttpContext ctx) =>
        {
            var baseUrl = BaseUrl(ctx);
            var sb = new StringBuilder();
            sb.AppendLine("User-agent: *");
            sb.AppendLine("Allow: /");
            // Los thumbnails no aportan a la búsqueda de texto y son muchísimos; que no gasten crawleo.
            sb.AppendLine("Disallow: /fotos/");
            sb.AppendLine();
            sb.AppendLine($"Sitemap: {baseUrl}/sitemap.xml");
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        app.MapGet("/sitemap.xml", async (ICatalogoConsulta catalogo, HttpContext ct, CancellationToken token) =>
        {
            var snap = await catalogo.SnapshotAsync(token);
            var baseUrl = BaseUrl(ct);
            // Si el catálogo todavía no cargó, igual se emite un sitemap válido con las páginas fijas.
            var lastmod = snap.Generado > DateTimeOffset.MinValue
                ? snap.Generado.UtcDateTime.ToString("yyyy-MM-dd")
                : null;

            // Se escribe a un stream UTF-8, NO a un StringBuilder: XmlWriter sobre un StringBuilder emite
            // siempre encoding="utf-16" en la declaración (el string es UTF-16 en memoria), y eso no
            // coincidiría con el charset UTF-8 del header — validadores estrictos lo rechazan.
            using var ms = new MemoryStream();
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), Async = true };
            await using (var w = XmlWriter.Create(ms, settings))
            {
            await w.WriteStartDocumentAsync();
            w.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");

            void Url(string ruta, string? mod, string prioridad)
            {
                w.WriteStartElement("url");
                w.WriteElementString("loc", baseUrl + ruta);
                if (mod is not null) w.WriteElementString("lastmod", mod);
                w.WriteElementString("priority", prioridad);
                w.WriteEndElement();
            }

            Url("/", lastmod, "1.0");
            Url("/catalogo", lastmod, "0.9");
            Url("/nosotros", null, "0.5");

            // Rubros y géneros (las landings navegables, indexables).
            foreach (var r in snap.Menu)
            {
                Url($"/catalogo/{r.Slug}", lastmod, "0.8");
                foreach (var g in r.Generos)
                    Url($"/catalogo/{r.Slug}/{g.Slug}", lastmod, "0.7");
            }

            // Una entrada por producto: son ~981, cabe de sobra en un sitemap (límite 50.000).
            foreach (var a in snap.Articulos)
                Url($"/producto/{a.Slug}", lastmod, "0.6");

            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
            await w.FlushAsync();
            }

            return Results.Bytes(ms.ToArray(), "application/xml; charset=utf-8");
        });
    }

    private static string BaseUrl(HttpContext ctx)
    {
        var req = ctx.Request;
        return $"{req.Scheme}://{req.Host}";
    }
}
