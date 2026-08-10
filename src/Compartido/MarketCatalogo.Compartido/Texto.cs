using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarketCatalogo.Compartido;

/// <summary>Slugs y normalización de texto. Todo determinístico: los slugs no se guardan en ninguna
/// tabla, se derivan (docs/DECISION-TABLAS.md §8).</summary>
public static partial class Texto
{
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NoAlfanum();
    [GeneratedRegex(@"-{2,}")]      private static partial Regex GuionesRepetidos();

    // Un '?' rodeado de letras. En el texto del ERP (español, sin signos de pregunta reales) eso es
    // siempre una 'ñ' que se perdió en una conversión de codepage al cargar el dato. Ver RepararEnie.
    [GeneratedRegex(@"(?<=\p{L})\?(?=\p{L})")] private static partial Regex EniePerdida();

    /// <summary>Quita acentos y pasa a minúsculas. "Lencería" → "lenceria".</summary>
    public static string SinAcentos(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>"Casa blanquería" → "casa-blanqueria". "IM013.056" → "im013-056".</summary>
    public static string Slug(string? s)
    {
        var t = NoAlfanum().Replace(SinAcentos(s), "-");
        t = GuionesRepetidos().Replace(t, "-");
        return t.Trim('-');
    }

    /// <summary>Repara la 'ñ' que el ERP guardó como '?' por una conversión de codepage con pérdida
    /// (el dato entró mal upstream; no se puede corregir en Dragonfish desde el sitio, que sólo lee).
    /// Criterio seguro: un '?' con una letra a cada lado → 'ñ' ('Ñ' si la letra previa es mayúscula).
    /// "SACO TAPADO DE PA?O" → "…PAÑO", "ni?o" → "niño". No toca un '?' que no esté entre letras.
    /// Los ARTDES/descripciones del ERP nunca traen signos de pregunta reales, así que no hay falsos
    /// positivos en la práctica.</summary>
    [return: NotNullIfNotNull(nameof(s))]
    public static string? RepararEnie(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains('?')) return s;
        return EniePerdida().Replace(s, m => char.IsUpper(s[m.Index - 1]) ? "Ñ" : "ñ");
    }

    /// <summary>Slug de producto: título + código al final, para que sea legible y único.</summary>
    public static string SlugProducto(string titulo, string artCod)
    {
        var t = Slug(titulo);
        var c = Slug(artCod);
        if (t.Length > 90) t = t[..90].TrimEnd('-');
        return t.Length == 0 ? c : $"{t}-{c}";
    }
}
