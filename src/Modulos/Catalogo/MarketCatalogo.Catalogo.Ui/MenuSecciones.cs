using MarketCatalogo.Catalogo.Contratos;

namespace MarketCatalogo.Catalogo.Ui;

/// <summary>Un tipo (rubro) dentro de una sección de género, con su link al catálogo ya resuelto.</summary>
public sealed record TipoNav(string Slug, string Nombre, int Cantidad, string Href);

/// <summary>Una sección de la navegación por género para la barra/el drawer (ej. "Niños" = nene+nena+bebe),
/// con los tipos disponibles adentro.</summary>
public sealed record SeccionNav(string Slug, string Nombre, int Cantidad, IReadOnlyList<TipoNav> Tipos);

/// <summary>
/// Arma la navegación por sección (Género → Tipos) a partir del menú del catálogo, que viene al revés
/// (Rubro → Géneros). La usan el mega-menú del header (desktop) y el drawer de filtros (celular), así
/// que vive acá una sola vez.
/// </summary>
public static class MenuSecciones
{
    // Géneros que se muestran fusionados bajo un mismo rótulo. Clave = slug del género en la data.
    // Valor = (slug del grupo, rótulo). El resto de los géneros van solos.
    private static readonly Dictionary<string, (string Slug, string Nombre)> Fusion =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["nene"] = ("ninos", "Niños"),
            ["nena"] = ("ninos", "Niños"),
            ["bebe"] = ("ninos", "Niños"),
        };

    public static IReadOnlyList<SeccionNav> Construir(IReadOnlyList<RubroMenu> menu)
    {
        // Aplanar a filas (género, rubro, conteo). GeneroMenu.Cantidad ya es el conteo de ese género
        // DENTRO de ese rubro, así que sirve tal cual.
        var filas = menu.SelectMany(r => r.Generos.Select(g => new
        {
            GenSlug = g.Slug, GenNombre = g.Nombre,
            RubSlug = r.Slug, RubNombre = r.Nombre, Cant = g.Cantidad,
        }));

        return filas
            // Asignar cada fila a su SECCIÓN (fusionada o el propio género).
            .Select(x =>
            {
                var sec = Fusion.TryGetValue(x.GenSlug, out var f)
                    ? (f.Slug, f.Nombre)
                    : (Slug: x.GenSlug, Nombre: Titulo(x.GenNombre));
                return new { sec.Slug, sec.Nombre, x.GenSlug, x.RubSlug, x.RubNombre, x.Cant };
            })
            .GroupBy(x => (x.Slug, x.Nombre))
            .Select(grp =>
            {
                var miembros = grp.Select(x => x.GenSlug).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var esMulti = miembros.Count > 1;
                var tipos = grp
                    .GroupBy(x => (x.RubSlug, x.RubNombre))
                    .Select(rg => new TipoNav(
                        rg.Key.RubSlug,
                        Titulo(rg.Key.RubNombre),
                        rg.Sum(x => x.Cant),
                        // Un solo género → ruta indexable; sección fusionada → filtro multi-género.
                        esMulti
                            ? $"/catalogo/{rg.Key.RubSlug}?gen={string.Join(",", miembros)}"
                            : $"/catalogo/{rg.Key.RubSlug}/{miembros[0]}"))
                    .OrderByDescending(t => t.Cantidad).ThenBy(t => t.Nombre)
                    .ToList();
                return new SeccionNav(grp.Key.Slug, grp.Key.Nombre, grp.Sum(x => x.Cant), tipos);
            })
            .OrderByDescending(s => s.Cantidad).ThenBy(s => s.Nombre)
            .ToList();
    }

    // La taxonomía viene en mayúsculas de Dragon ("DAMA", "INDUMENTARIA"): se muestra en Title Case.
    private static string Titulo(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var palabras = s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', palabras.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
