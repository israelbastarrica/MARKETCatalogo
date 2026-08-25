using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Deriva un título presentable desde <c>ART.ARTDES</c>, que está escrito para el depósito y no para el
/// público: dice cosas como <c>"PALAZ DARLON MICRORIB DO VIVO"</c> o
/// <c>"MEDIA TERM C/PIEL 1/3 CAÑA EST ART 9400"</c>.
///
/// Publicar 981 artículos con esos títulos es una vidriera fea; escribir 981 nombres comerciales a mano
/// es mucho trabajo. Esto es el camino del medio: expandir las abreviaturas conocidas, sacar los códigos
/// internos y capitalizar. Ver docs/PLAN.md §2.bis.
///
/// Es BEST EFFORT a propósito: los tokens que no conoce pasan capitalizados. Es la única fuente del nombre
/// de vidriera (el override manual <c>NombreComercial</c> se retiró al pasar a una sola tabla).
/// </summary>
public static partial class TituloArticulo
{
    // "ART 9400", "ART. 111" → códigos internos de fábrica, no van en un título público.
    [GeneratedRegex(@"\bART\.?\s*\d+\b", RegexOptions.IgnoreCase)] private static partial Regex CodigoArt();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex EspaciosRepetidos();

    // Códigos internos de fábrica sueltos: "5833", "5917E", "F30893", "225-346". Se detectan por tener
    // 2+ dígitos seguidos. Una fracción como "1/3" (medida de caña) NO cae acá: sus dígitos no son
    // consecutivos, así que se conserva.
    [GeneratedRegex(@"\d{2,}")] private static partial Regex TieneCodigoNumerico();

    /// <summary>Abreviaturas vistas en los datos reales. Sólo las de significado claro: adivinar es peor
    /// que dejar el token como está, porque un título inventado engaña al cliente.</summary>
    private static readonly Dictionary<string, string> Abreviaturas = new(StringComparer.OrdinalIgnoreCase)
    {
        // Prendas
        ["CAMP"] = "Campera",   ["PANT"] = "Pantalón",  ["PALAZ"] = "Palazzo",
        ["REM"] = "Remera",     ["POLE"] = "Polera",    ["BUZ"] = "Buzo",
        ["SOQ"] = "Soquete",    ["SAB"] = "Sábana",     ["JGO"] = "Juego",
        ["TOALLON"] = "Toallón", ["CORP"] = "Corpiño",  ["BOMB"] = "Bombacha",
        ["CONJ"] = "Conjunto",  ["CHAL"] = "Chaleco",   ["SWEAT"] = "Sweater",
        ["PANTU"] = "Pantufla",

        // Materiales y características
        ["TERM"] = "Térmica",   ["EST"] = "Estampada",  ["ALG"] = "Algodón",
        ["POLI"] = "Poliéster", ["VISCO"] = "Viscosa",  ["STRECH"] = "Stretch",
        ["MELANGE"] = "Melange", ["REGU"] = "Regular",  ["AJUST"] = "Ajustable",
        ["LISO"] = "Liso",      ["CANA"] = "Caña",      ["CAÑA"] = "Caña",
        ["INFANTIL"] = "Infantil", ["FANTASIA"] = "Fantasía",

        // Compuestos con barra (llegan como un solo token al partir por espacios)
        ["C/R"] = "cuello redondo",  ["M/L"] = "manga larga",  ["ML"] = "manga larga",
        ["M/C"] = "manga corta",     ["M/RANG"] = "manga ranglan",
        ["C/CAPU"] = "con capucha",  ["C/PIEL"] = "con piel",  ["C/GROSS"] = "con gross",
        ["B/CANG"] = "bolsillo canguro",
        ["S/MANGA"] = "sin manga",
    };

    public static string Derivar(string? artDes, string? familia)
    {
        var crudo = (artDes ?? "").Trim();
        if (crudo.Length == 0)
            return string.IsNullOrWhiteSpace(familia) ? "Artículo" : familia!.Trim();

        crudo = CodigoArt().Replace(crudo, " ");

        var partes = crudo.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(Convertir)
                          .Where(p => p.Length > 0);

        var titulo = EspaciosRepetidos().Replace(string.Join(' ', partes), " ").Trim();
        if (titulo.Length == 0)
            return string.IsNullOrWhiteSpace(familia) ? "Artículo" : familia!.Trim();

        // La primera letra siempre en mayúscula (si el primer token era "c/piel" quedaría en minúscula).
        return char.ToUpper(titulo[0], CultureInfo.CurrentCulture) + titulo[1..];
    }

    private static string Convertir(string token)
    {
        if (Abreviaturas.TryGetValue(token, out var expandido)) return expandido;

        // Códigos internos de fábrica ("5833", "5917E", "F30893", "225-346"): se descartan (cadena vacía,
        // que Derivar filtra). Van antes que el resto para que ningún código quede como "palabra".
        if (TieneCodigoNumerico().IsMatch(token)) return "";

        // Cosas como "X2", "X3" (unidades por pack) quedan mejor en minúscula.
        if (token.Length is 2 or 3 && (token[0] is 'X' or 'x') && token[1..].All(char.IsDigit))
            return "x" + token[1..];

        // Fracciones y medidas ("1/3", "2", "1,00") se dejan tal cual.
        if (token.All(c => char.IsDigit(c) || c is '/' or ',' or '.' or '-')) return token;

        // Talles y siglas que se escriben en mayúscula.
        if (token.Length <= 3 && token.All(char.IsUpper) && Talles.EsDesconocido(token) == false) return token;

        return Capitalizar(token);
    }

    private static string Capitalizar(string t)
        => t.Length switch
        {
            0 => t,
            1 => t.ToUpperInvariant(),
            _ => char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant(),
        };
}
