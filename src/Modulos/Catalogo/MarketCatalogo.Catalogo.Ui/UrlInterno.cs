using System.Text;
using MarketCatalogo.Catalogo.Contratos.Interno;

namespace MarketCatalogo.Catalogo.Ui;

/// <summary>Construye las URLs de la grilla interna (SSR, todo el estado en el query string). Serializa
/// los <see cref="FiltrosInterno"/> a parámetros, aplica un cambio (setear / alternar en un CSV / quitar)
/// y arma <c>/interno?...</c>. Al cambiar cualquier filtro se vuelve a la página 1.</summary>
public static class UrlInterno
{
    private const string Base = "/interno";

    // Serializa los filtros actuales al mapa de parámetros (sólo los que tienen valor).
    private static Dictionary<string, string> Mapa(FiltrosInterno f)
    {
        var m = new Dictionary<string, string>();
        if (f.Ubicaciones.Count > 0) m["ubic"] = string.Join(',', f.Ubicaciones);
        if (!string.IsNullOrEmpty(f.CruceDepoLocal)) m["cruce"] = f.CruceDepoLocal!;
        if (f.Generos.Count > 0) m["gen"] = string.Join(',', f.Generos);
        if (f.Rubros.Count > 0) m["rubro"] = string.Join(',', f.Rubros);
        if (f.Prendas.Count > 0) m["prenda"] = string.Join(',', f.Prendas);
        if (f.Talles.Count > 0) m["talle"] = string.Join(',', f.Talles);
        if (f.Colores.Count > 0) m["color"] = string.Join(',', f.Colores);
        if (f.Proveedores.Count > 0) m["prov"] = string.Join(',', f.Proveedores);
        if (f.Marcas.Count > 0) m["marca"] = string.Join(',', f.Marcas);
        if (f.Temporadas.Count > 0) m["temp"] = string.Join(',', f.Temporadas);
        if (f.ComboDetalles.Count > 0) m["combo"] = string.Join(',', f.ComboDetalles);
        if (f.Publicado is bool p) m["pub"] = p ? "si" : "no";
        if (f.MargenMax is decimal mm) m["margenMax"] = mm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(f.Texto)) m["q"] = f.Texto!;
        if (!string.IsNullOrWhiteSpace(f.Orden) && f.Orden != "codigo") m["orden"] = f.Orden;
        return m;
    }

    private static string Construir(Dictionary<string, string> m)
    {
        // "pag" nunca se preserva al cambiar un filtro: siempre se vuelve a la página 1.
        m.Remove("pag");
        if (m.Count == 0) return Base;
        var sb = new StringBuilder(Base).Append('?');
        var primero = true;
        foreach (var (k, v) in m)
        {
            if (!primero) sb.Append('&');
            sb.Append(k).Append('=').Append(Uri.EscapeDataString(v));
            primero = false;
        }
        return sb.ToString();
    }

    /// <summary>Setea (o quita, si valor es null/"") un parámetro de un solo valor.</summary>
    public static string Set(FiltrosInterno f, string clave, string? valor)
    {
        var m = Mapa(f);
        if (string.IsNullOrWhiteSpace(valor)) m.Remove(clave);
        else m[clave] = valor.Trim();
        return Construir(m);
    }

    /// <summary>Alterna un valor dentro de un parámetro CSV (multi-selección).</summary>
    public static string Alternar(FiltrosInterno f, string clave, string valor)
    {
        var m = Mapa(f);
        var actuales = m.TryGetValue(clave, out var csv)
            ? csv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();
        if (actuales.RemoveAll(x => x.Equals(valor, StringComparison.OrdinalIgnoreCase)) == 0)
            actuales.Add(valor);
        if (actuales.Count == 0) m.Remove(clave);
        else m[clave] = string.Join(',', actuales);
        return Construir(m);
    }

    /// <summary>Página N conservando todos los filtros.</summary>
    public static string Pagina(FiltrosInterno f, int pagina)
    {
        var m = Mapa(f);
        if (pagina > 1) m["pag"] = pagina.ToString();
        // Construir quita "pag"; para paginar lo re-agrego después.
        var url = Construir(m);
        return pagina > 1 ? (url == Base ? $"{Base}?pag={pagina}" : $"{url}&pag={pagina}") : url;
    }

    public static bool CsvContiene(IReadOnlyList<string> lista, string valor)
        => lista.Contains(valor, StringComparer.OrdinalIgnoreCase);

    /// <summary>URL sin ningún filtro (limpiar todo) — vuelve a /interno.</summary>
    public static string Limpiar() => Base;

    /// <summary>Filtros activos como chips: etiqueta + URL que quita ese filtro. Mismo patrón que el
    /// público (mk-chips), para el drawer/mobile y también en desktop.</summary>
    public static IEnumerable<(string Etiqueta, string UrlQuitar)> Chips(FiltrosInterno f)
    {
        foreach (var v in f.Ubicaciones) yield return (EtiquetaUbicacion(v), Alternar(f, "ubic", v));
        if (f.CruceDepoLocal == "solo-deposito") yield return ("En depósito sin local", Set(f, "cruce", null));
        if (f.CruceDepoLocal == "en-local") yield return ("En algún local", Set(f, "cruce", null));
        if (f.Publicado == true) yield return ("Se ve en el público", Set(f, "pub", null));
        if (f.Publicado == false) yield return ("No se ve", Set(f, "pub", null));
        foreach (var v in f.Rubros) yield return (v, Alternar(f, "rubro", v));
        foreach (var v in f.Prendas) yield return (v, Alternar(f, "prenda", v));
        foreach (var v in f.Proveedores) yield return (v, Alternar(f, "prov", v));
        foreach (var v in f.Marcas) yield return (v, Alternar(f, "marca", v));
        foreach (var v in f.Temporadas) yield return (v, Alternar(f, "temp", v));
        foreach (var v in f.ComboDetalles) yield return (EtiquetaCombo(v), Alternar(f, "combo", v));
        if (f.MargenMax is decimal mm) yield return ($"Margen ≤ {mm:0.#}%", Set(f, "margenMax", null));
        if (!string.IsNullOrWhiteSpace(f.Texto)) yield return ($"“{f.Texto!.Trim()}”", Set(f, "q", null));
    }

    private static string EtiquetaUbicacion(string v) => v.ToLowerInvariant() switch
    {
        "luro" => "Luro",
        "peralta" => "Peralta",
        "deposito" => "Depósito",
        _ => v,
    };

    // "2-15000" -> "2x15.000" (mismo formato que el público). Si no parsea, muestra el valor crudo.
    private static string EtiquetaCombo(string v)
    {
        var partes = v.Split('-', 2);
        if (partes.Length == 2 && int.TryParse(partes[0], out var cant) && int.TryParse(partes[1], out var total))
            return $"{cant}x{total:#,0}".Replace(",", ".");
        return v;
    }
}
