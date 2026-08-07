using System.Text.RegularExpressions;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// El precio de MARKET no es un precio por artículo: es un COMBO. <c>ART.CLASIFART</c> guarda un texto
/// como <c>"2X15000"</c> = 2 unidades por $15.000, que es lo que dice la etiqueta en el local.
/// <c>PRECIOAR.PDIRECTO</c> (LISTA1) es lo que cuesta UNA unidad sola.
///
/// Medido sobre los 678 artículos con foto, sin una sola excepción:
/// <code>LISTA1 = (combo_total / combo_cantidad) + $5.000</code>
/// O sea, un recargo fijo por comprar de a una. Ver docs/MEDICION.md §6.
///
/// Mismo regex que <c>PrecioPorUnidadDelCombo</c> en MARKETweb, para no divergir.
/// </summary>
public static partial class Combo
{
    [GeneratedRegex(@"^(\d+)[Xx](\d+(?:[.,]\d+)?)$")] private static partial Regex Formato();

    /// <summary>Recargo fijo por unidad suelta, verificado en 678/678 artículos. Se usa sólo para
    /// validar la coherencia de los datos, no para calcular: el precio suelto sale de PRECIOAR.</summary>
    public const decimal RecargoUnidadSuelta = 5000m;

    public sealed record Datos(string Texto, int Cantidad, decimal Total, decimal PrecioUnidad);

    /// <summary>Parsea "2X15000". Devuelve null si está vacío o no respeta el formato NxTOTAL
    /// (en la medición no había ninguno raro, pero un dato nuevo no puede romper el catálogo).</summary>
    public static Datos? Parsear(string? clasifart)
    {
        var s = (clasifart ?? "").Trim();
        if (s.Length == 0) return null;

        var m = Formato().Match(s);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var cantidad) || cantidad <= 0) return null;
        if (!decimal.TryParse(m.Groups[2].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var total) || total <= 0) return null;

        return new Datos(s.ToUpperInvariant(), cantidad, total, total / cantidad);
    }

    /// <summary>Cómo se muestra la oferta: "2 x $15.000".</summary>
    public static string Mostrar(int cantidad, decimal total)
        => $"{cantidad} x {Plata(total)}";

    /// <summary>Formato de moneda argentino sin decimales: $15.000.</summary>
    public static string Plata(decimal monto)
        => "$" + monto.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-AR"));
}
