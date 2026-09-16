using MarketCatalogo.Catalogo.Contratos.Interno;

namespace MarketCatalogo.Catalogo.Ui;

/// <summary>
/// Arma los <see cref="FiltrosInterno"/> a partir de los parámetros crudos del query string. La contracara
/// de <see cref="UrlInterno"/>, que hace el camino de vuelta (filtros → URL).
///
/// Vive aparte porque lo usan DOS páginas: la grilla (<c>/interno</c>) y la ficha
/// (<c>/interno/producto/{codigo}</c>), que recibe los mismos parámetros colgados de su URL para saber
/// dentro de qué listado está parada y cuál es el artículo de al lado. Si cada una los interpretara por su
/// cuenta, alcanzaría con que una tratara distinto un CSV vacío o un <c>pub</c> raro para que el "siguiente"
/// de la ficha saliera de un listado que no es el que el usuario está viendo.
/// </summary>
public static class FiltrosInternoUrl
{
    /// <summary>Los parámetros del query string (ya decodificados por Blazor) como filtros. Cada página
    /// los declara con <c>[SupplyParameterFromQuery]</c> y los pasa acá.</summary>
    public static FiltrosInterno Armar(
        string? ubic, string? cruce, string? gen, string? rubro, string? prenda,
        string? talle, string? color, string? prov, string? marca, string? temp,
        string? anio, string? combo, string? pub, decimal? margenMax,
        string? q, string? orden, int? pag) => new()
        {
            Ubicaciones = Csv(ubic),
            CruceDepoLocal = Vacio(cruce),
            Generos = Csv(gen),
            Rubros = Csv(rubro),
            Prendas = Csv(prenda),
            Talles = Csv(talle),
            Colores = Csv(color),
            Proveedores = Csv(prov),
            Marcas = Csv(marca),
            Temporadas = Csv(temp),
            Anios = Csv(anio),
            ComboDetalles = Csv(combo),
            // Cualquier otra cosa que "si"/"no" es como no haber filtrado: null = todos.
            Publicado = pub is null ? null
                      : pub.Equals("si", StringComparison.OrdinalIgnoreCase) ? true
                      : pub.Equals("no", StringComparison.OrdinalIgnoreCase) ? false : null,
            MargenMax = margenMax,
            Texto = Vacio(q),
            Orden = string.IsNullOrWhiteSpace(orden) ? "codigo" : orden!.Trim(),
            Pagina = pag is > 0 ? pag.Value : 1,
        };

    private static string? Vacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> Csv(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? []
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
