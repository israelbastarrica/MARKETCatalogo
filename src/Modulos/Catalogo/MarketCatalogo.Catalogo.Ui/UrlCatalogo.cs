using System.Globalization;
using MarketCatalogo.Catalogo.Contratos;

namespace MarketCatalogo.Catalogo.Ui;

/// <summary>
/// Arma las URLs de la grilla. Todo filtro es un link con el query string ya recalculado del lado del
/// servidor: no hay JavaScript manejando el estado, la URL <b>es</b> el estado (docs/CONSULTAS.md §1).
/// Por eso el botón "atrás" saca el último filtro y el link compartido muestra lo mismo.
/// </summary>
public static class UrlCatalogo
{
    /// <summary>URL de la grilla con los filtros dados, cambiando opcionalmente uno.
    /// <paramref name="valor"/> en null QUITA ese filtro (es lo que usa la "×" de los chips).
    /// Cualquier cambio de filtro vuelve a la página 1: quedarse en la 7 con otro filtro puesto casi
    /// siempre cae en una página vacía.</summary>
    public static string Construir(FiltrosCatalogo f, string? clave = null, string? valor = null)
    {
        string? Val(string k, string? actual) => clave == k ? valor : actual;

        var rubro = Val("rubro", f.RubroSlug);
        var genero = Val("genero", f.GeneroSlug);

        // Rubro y género van en la RUTA (son lo indexable); el resto en query string.
        var ruta = "/catalogo";
        if (!string.IsNullOrEmpty(rubro))
        {
            ruta += "/" + rubro;
            if (!string.IsNullOrEmpty(genero)) ruta += "/" + genero;
        }

        var q = new List<string>();
        void Agregar(string k, string? v)
        {
            if (!string.IsNullOrWhiteSpace(v)) q.Add($"{k}={Uri.EscapeDataString(v)}");
        }

        // Todos multi-valor: viajan como CSV. Val() deja pasar el valor nuevo cuando se está tocando esa
        // clave (ya viene como CSV armado por Toggle), o el CSV del estado actual si no.
        Agregar("tipo", Val("tipo", Csv(f.Rubros)));
        Agregar("gen", Val("gen", Csv(f.Generos)));
        Agregar("familia", Val("familia", Csv(f.Familias)));
        Agregar("talle", Val("talle", Csv(f.Talles)));
        Agregar("color", Val("color", Csv(f.Colores)));
        Agregar("local", Val("local", Csv(f.Locales)));
        Agregar("combo", Val("combo", Csv(f.ComboDetalles)));
        Agregar("precioMin", Val("precioMin", f.PrecioMin?.ToString("0")));
        Agregar("precioMax", Val("precioMax", f.PrecioMax?.ToString("0")));
        Agregar("q", Val("q", f.Texto));

        var orden = Val("orden", f.Orden);
        if (!string.IsNullOrWhiteSpace(orden) && orden != "destacados") Agregar("orden", orden);

        // La página sólo se conserva si es lo que se está cambiando.
        if (clave == "pag" && valor is not null && valor != "1") Agregar("pag", valor);
        else if (clave is null && f.Pagina > 1) Agregar("pag", f.Pagina.ToString());

        return q.Count == 0 ? ruta : ruta + "?" + string.Join("&", q);
    }

    /// <summary>Suma o quita un valor del conjunto de una faceta multi-selección (familia, talle, color,
    /// local, combo). Recalcula desde el estado actual, así el link siempre refleja el conjunto correcto.</summary>
    public static string Toggle(FiltrosCatalogo f, string clave, string valor)
    {
        var actual = ValoresDe(f, clave);
        var set = actual.Where(x => !x.Equals(valor, StringComparison.OrdinalIgnoreCase)).ToList();
        if (set.Count == actual.Count) set.Add(valor);   // no estaba: se agrega
        return Construir(f, clave, Csv(set));
    }

    /// <summary>La usa cada opción del panel de facetas. Con multi-selección, "activar/desactivar" es
    /// sumar/quitar del conjunto; <paramref name="activa"/> queda por compatibilidad de firma.</summary>
    public static string Alternar(FiltrosCatalogo f, string clave, string valor, bool activa)
        => Toggle(f, clave, valor);

    /// <summary>Los ticks de sucursal de arriba de la grilla. Alias de Toggle sobre la clave "local".</summary>
    public static string AlternarLocal(FiltrosCatalogo f, string slug) => Toggle(f, "local", slug);

    // Valores actuales de una faceta multi como lista de strings (combo viaja como "cantidad-total").
    private static IReadOnlyList<string> ValoresDe(FiltrosCatalogo f, string clave) => clave switch
    {
        "tipo"    => f.Rubros,
        "gen"     => f.Generos,
        "familia" => f.Familias,
        "talle"   => f.Talles,
        "color"   => f.Colores,
        "local"   => f.Locales,
        "combo"   => f.ComboDetalles,
        _         => Array.Empty<string>(),
    };

    private static string? Csv(IReadOnlyList<string> xs) => xs.Count > 0 ? string.Join(",", xs) : null;

    /// <summary>La ruta limpia, sin filtros: es la URL canónica e indexable de la sección.</summary>
    public static string Canonica(FiltrosCatalogo f)
    {
        var ruta = "/catalogo";
        if (!string.IsNullOrEmpty(f.RubroSlug))
        {
            ruta += "/" + f.RubroSlug;
            if (!string.IsNullOrEmpty(f.GeneroSlug)) ruta += "/" + f.GeneroSlug;
        }
        return ruta;
    }

    /// <summary>true si hay algún filtro de refinamiento aplicado. Cuando lo hay, la página va con
    /// <c>noindex,follow</c> y canonical a la ruta limpia: las combinaciones de filtros son miles de
    /// URLs con contenido casi duplicado y no deben competir entre sí en Google.</summary>
    public static bool TieneRefinamientos(FiltrosCatalogo f)
        => f.Rubros.Count > 0 || f.Generos.Count > 0 || f.Familias.Count > 0 || f.Talles.Count > 0 || f.Colores.Count > 0
           || f.Locales.Count > 0 || f.ComboDetalles.Count > 0
           || f.PrecioMin is not null || f.PrecioMax is not null
           || !string.IsNullOrWhiteSpace(f.Texto) || f.Pagina > 1 || f.Orden != "destacados";

    /// <summary>Los chips de "filtros activos", con la URL que quita cada uno.</summary>
    public static IEnumerable<(string Etiqueta, string UrlQuitar)> ChipsActivos(FiltrosCatalogo f)
    {
        // Un chip por cada valor activo de cada faceta; la × de cada uno lo quita del conjunto.
        foreach (var v in f.Rubros)   yield return (Lindo(v), Toggle(f, "tipo", v));
        foreach (var v in f.Generos)  yield return (Lindo(v), Toggle(f, "gen", v));
        foreach (var v in f.Familias) yield return (Lindo(v), Toggle(f, "familia", v));
        foreach (var v in f.Talles)   yield return ($"Talle {v}", Toggle(f, "talle", v));
        foreach (var v in f.Colores)  yield return (Lindo(v), Toggle(f, "color", v));
        foreach (var v in f.Locales)  yield return (Lindo(v), Toggle(f, "local", v));
        foreach (var v in f.ComboDetalles) yield return (EtiquetaCombo(v), Toggle(f, "combo", v));
        if (f.PrecioMin is not null || f.PrecioMax is not null)
        {
            var etiqueta = (f.PrecioMin, f.PrecioMax) switch
            {
                (not null, not null) => $"${f.PrecioMin:N0} a ${f.PrecioMax:N0}",
                (not null, null) => $"desde ${f.PrecioMin:N0}",
                (null, not null) => $"hasta ${f.PrecioMax:N0}",
                _ => "precio",
            };
            yield return (etiqueta, Construir(Construir2(f), "precioMin", null));
        }
        if (!string.IsNullOrWhiteSpace(f.Texto)) yield return ($"“{f.Texto}”", Construir(f, "q", null));
    }

    // Quitar el rango de precio son dos claves a la vez, así que primero se limpia una.
    private static FiltrosCatalogo Construir2(FiltrosCatalogo f) => f with { PrecioMax = null };

    // El chip arma su etiqueta a partir del valor crudo del filtro ("2-15000" → "2 x $15.000"): acá no
    // hay acceso a PaginaCatalogoDto.Combos (con la etiqueta ya armada), sólo a FiltrosCatalogo.
    private static string EtiquetaCombo(string valor)
    {
        var partes = valor.Split('-', 2);
        if (partes.Length == 2 && int.TryParse(partes[0], out var cantidad)
            && decimal.TryParse(partes[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
            return $"{cantidad} x ${total:N0}";
        return $"Combo {valor}";
    }

    private static string Lindo(string slug)
    {
        var t = slug.Replace('-', ' ');
        return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
