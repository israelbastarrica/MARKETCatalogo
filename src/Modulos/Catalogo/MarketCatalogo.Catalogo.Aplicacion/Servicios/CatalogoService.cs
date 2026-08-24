using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Compartido;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Implementa <see cref="ICatalogoConsulta"/>: filtra, cuenta facetas, ordena y pagina — todo
/// <b>en memoria</b> sobre las filas que devuelve <see cref="LectorCatalogo"/> (leídas de la tabla
/// <c>dbo.Catalogo</c>). Son ~569 objetos: cada filtro es un predicado sobre menos de mil items, o sea
/// microsegundos. Por eso "qué filtros podemos ofrecer" dejó de ser una pregunta de performance y es
/// sólo de UX.
/// </summary>
public sealed class CatalogoService : ICatalogoConsulta
{
    private readonly LectorCatalogo _lector;
    public CatalogoService(LectorCatalogo lector) => _lector = lector;

    public Task<CatalogoSnapshot> SnapshotAsync(CancellationToken ct = default) => _lector.LeerAsync(ct);

    public async Task<ArticuloDto?> PorSlugAsync(string? slug, CancellationToken ct = default)
    {
        var snap = await _lector.LeerAsync(ct);
        if (string.IsNullOrWhiteSpace(slug)) return null;
        if (snap.PorSlug.TryGetValue(slug.Trim(), out var art)) return art;

        // El slug cambió (alguien editó el título) pero el link viejo sigue circulando: se resuelve por
        // el código, que va al final del slug. Quien llame hace 301 al slug canónico.
        var cola = slug.Trim().Split('-').LastOrDefault(s => s.Length > 0);
        if (cola is null) return null;
        return snap.Articulos.FirstOrDefault(a => Texto.Slug(a.ArtCod).EndsWith(cola, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PaginaCatalogoDto> BuscarAsync(FiltrosCatalogo f, CancellationToken ct = default)
    {
        var snap = await _lector.LeerAsync(ct);

        // Base = sólo los filtros que vienen en la RUTA (rubro/género). Los de query string son
        // refinamiento y se aplican después, porque cada faceta necesita contar sin su propio filtro.
        var baseSet = snap.Articulos.Where(a =>
            (f.RubroSlug is null  || string.Equals(a.RubroSlug,  f.RubroSlug,  StringComparison.OrdinalIgnoreCase)) &&
            (f.GeneroSlug is null || string.Equals(a.GeneroSlug, f.GeneroSlug, StringComparison.OrdinalIgnoreCase)) &&
            (f.Generos.Count == 0 || f.Generos.Contains(a.GeneroSlug, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var textoNorm = string.IsNullOrWhiteSpace(f.Texto) ? null : Texto.SinAcentos(f.Texto);

        // Multi-selección: sin filtro (lista vacía) pasa todo; con varias opciones, pasa si cumple
        // CUALQUIERA (unión). Es lo intuitivo de un panel de tildes: marcar Campera y Pantalón muestra
        // ambos, no la intersección (que daría vacío).
        bool PasaTipo(ArticuloDto a)    => f.Rubros.Count == 0 || f.Rubros.Contains(a.RubroSlug, StringComparer.OrdinalIgnoreCase);
        bool PasaFamilia(ArticuloDto a) => f.Familias.Count == 0 || (a.FamiliaSlug is not null && f.Familias.Contains(a.FamiliaSlug, StringComparer.OrdinalIgnoreCase));
        bool PasaTalle(ArticuloDto a)   => f.Talles.Count == 0 || a.Talles.Any(t => f.Talles.Contains(t, StringComparer.OrdinalIgnoreCase));
        bool PasaColor(ArticuloDto a)   => f.Colores.Count == 0 || a.Colores.Any(c => f.Colores.Contains(c, StringComparer.OrdinalIgnoreCase));
        bool PasaLocal(ArticuloDto a)   => f.Locales.Count == 0 || a.Locales.Any(l => f.Locales.Contains(Texto.Slug(l), StringComparer.OrdinalIgnoreCase));
        bool PasaCombo(ArticuloDto a)   => f.ComboDetalles.Count == 0
            || (a.ComboCantidad is not null && a.ComboTotal is not null
                && f.ComboDetalles.Contains($"{a.ComboCantidad}-{(int)a.ComboTotal.Value}"));
        bool PasaPrecio(ArticuloDto a)  => (f.PrecioMin is null || a.PrecioUnidadCombo >= f.PrecioMin)
                                        && (f.PrecioMax is null || a.PrecioUnidadCombo <= f.PrecioMax);
        bool PasaTexto(ArticuloDto a)   => textoNorm is null || a.TextoBusqueda.Contains(textoNorm, StringComparison.Ordinal);

        // "excepto" deja fuera un filtro para poder contar su propia faceta: si no, al elegir "Campera"
        // el panel de familia mostraría sólo "Campera (28)" y quedarías encerrado sin poder cambiar.
        IEnumerable<ArticuloDto> Aplicar(string? excepto) => baseSet.Where(a =>
            (excepto == "tipo"    || PasaTipo(a))     &&
            (excepto == "familia" || PasaFamilia(a)) &&
            (excepto == "talle"   || PasaTalle(a))   &&
            (excepto == "color"   || PasaColor(a))   &&
            (excepto == "local"   || PasaLocal(a))   &&
            (excepto == "combo"   || PasaCombo(a))   &&
            PasaPrecio(a) && PasaTexto(a));

        var filtrados = Aplicar(null).ToList();

        var ordenados = f.Orden switch
        {
            "precio-asc"  => filtrados.OrderBy(a => a.PrecioUnidadCombo ?? decimal.MaxValue).ThenBy(a => a.Titulo),
            "precio-desc" => filtrados.OrderByDescending(a => a.PrecioUnidadCombo ?? decimal.MinValue).ThenBy(a => a.Titulo),
            "nombre"      => filtrados.OrderBy(a => a.Titulo, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.ArtCod),
            // Destacados primero, después los que tienen foto (una grilla que arranca con placeholders
            // se ve peor), y al final por código para que el orden sea estable entre corridas.
            _ => filtrados.OrderByDescending(a => a.Destacado)
                          .ThenByDescending(a => a.TieneFoto)
                          .ThenBy(a => a.ArtCod, StringComparer.OrdinalIgnoreCase),
        };

        var pagina = Math.Max(1, f.Pagina);
        var items = ordenados.Skip((pagina - 1) * FiltrosCatalogo.PorPagina)
                             .Take(FiltrosCatalogo.PorPagina).ToList();

        // Combo es de dos niveles: los GRUPOS (cantidad) y sus TRAMOS (precio) salen de la grilla oficial
        // de márgenes (snap.ComboTiers), no de agrupar lo que hay armado — así el panel ofrece los tramos
        // reales del negocio aunque momentáneamente algún artículo tenga un CLASIFART fuera de tabla. El
        // conteo de cada tramo sí sale del catálogo armado, como el resto de las facetas.
        var conteoPorTramo = Aplicar("combo")
            .Where(a => a.ComboCantidad is > 0 && a.ComboTotal is > 0)
            .GroupBy(a => (Cantidad: a.ComboCantidad!.Value, Total: (int)a.ComboTotal!.Value))
            .ToDictionary(g => g.Key, g => g.Count());

        var combos = snap.ComboTiers
            .GroupBy(t => t.Cantidad)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var detalles = g.OrderBy(t => t.Total)
                    .Select(t =>
                    {
                        var valor = $"{g.Key}-{(int)t.Total}";
                        var cantidad = conteoPorTramo.GetValueOrDefault((g.Key, (int)t.Total));
                        return new OpcionFaceta(valor, Combo.Mostrar(g.Key, t.Total), cantidad,
                            f.ComboDetalles.Contains(valor));
                    })
                    // Igual que cualquier otra faceta: un tramo sin ningún artículo no se ofrece.
                    .Where(d => d.Cantidad > 0)
                    .ToList();
                return new OpcionFacetaCombo(g.Key, $"Combo de {g.Key}",
                    detalles.Sum(d => d.Cantidad), detalles.Any(d => d.Activa), detalles);
            })
            .Where(o => o.Detalles.Count > 0)
            .ToList();

        return new PaginaCatalogoDto
        {
            Items = items,
            Total = filtrados.Count,
            Pagina = pagina,
            // Faceta "Tipo": los rubros (Indumentaria, Accesorios, Lencería…). Sólo aparece en /catalogo
            // (sin rubro en la ruta); en una ruta de rubro el baseSet ya deja uno solo y Faceta se auto-oculta.
            Rubros = Aplicar("tipo")
                .GroupBy(a => (a.RubroSlug, a.Rubro))
                .Select(g => new OpcionFaceta(g.Key.RubroSlug, g.Key.Rubro, g.Count(),
                    f.Rubros.Contains(g.Key.RubroSlug, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta).ToList(),

            Familias = Aplicar("familia")
                .Where(a => a.FamiliaSlug is not null)
                .GroupBy(a => (a.FamiliaSlug!, a.Familia!))
                .Select(g => new OpcionFaceta(g.Key.Item1, g.Key.Item2, g.Count(),
                    f.Familias.Contains(g.Key.Item1, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta).ToList(),

            Talles = Aplicar("talle")
                .SelectMany(a => a.Talles.Select(t => (Talle: t, Orden: Talles.OrdenEtiqueta(t))))
                .GroupBy(x => x.Talle, StringComparer.OrdinalIgnoreCase)
                // El orden real del talle ya viene calculado por variante (TalleOrden, mapeado por el
                // CÓDIGO en Talles). Se ordena la faceta por ese orden —el mínimo del grupo— así los
                // talles quedan agrupados por curva (Letra, Niño, Adulto, Lencería) y en secuencia. Antes
                // se re-resolvía por la etiqueta mostrada, que fallaba: "1"/"S/M" no matchean el código
                // "01"/"SM" y caían todos al final, sueltos.
                .Select(g => (
                    Op: new OpcionFaceta(g.Key, g.Key, g.Count(),
                        f.Talles.Contains(g.Key, StringComparer.OrdinalIgnoreCase)),
                    Orden: g.Min(x => x.Orden)))
                .OrderBy(x => x.Orden).Select(x => x.Op).ToList(),

            Colores = Aplicar("color")
                .SelectMany(a => a.Colores)
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .Select(g => new OpcionFaceta(g.Key, g.Key, g.Count(),
                    f.Colores.Contains(g.Key, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta).ToList(),

            Locales = Aplicar("local")
                .SelectMany(a => a.Locales)
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .Select(g => new OpcionFaceta(Texto.Slug(g.Key), g.Key, g.Count(),
                    f.Locales.Contains(Texto.Slug(g.Key), StringComparer.OrdinalIgnoreCase)))
                .OrderBy(o => o.Etiqueta).ToList(),

            Combos = combos,
        };
    }
}
