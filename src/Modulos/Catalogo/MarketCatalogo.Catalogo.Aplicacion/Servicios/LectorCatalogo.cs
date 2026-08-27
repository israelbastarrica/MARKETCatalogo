using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Compartido;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Arma el catálogo PÚBLICO leyendo la tabla materializada <c>dbo.Catalogo</c> (subset Publicado = 1) y
/// mapeando cada fila a <see cref="ArticuloDto"/>. Reemplaza al viejo <c>CatalogoCache</c> como fuente de
/// datos: ya no hay snapshot en RAM — cada lectura trae las ~569 filas publicadas de una tabla local e
/// indexada (el modelo tabla-como-caché: la tabla ES el caché).
///
/// Al leer, dispara <see cref="CatalogoStore.AsegurarBaseFresca"/> (stale-while-revalidate): si la base
/// venció el TTL, se reconstruye EN BACKGROUND y esta lectura igual devuelve lo último persistido.
///
/// Los derivados (slugs, combo parseado, locales desde los bits, talles/colores desde el CSV) se calculan
/// acá, no se guardan en la tabla.
/// </summary>
public sealed class LectorCatalogo
{
    private readonly ICatalogoRepositorio _repo;
    private readonly CatalogoStore _store;

    public LectorCatalogo(ICatalogoRepositorio repo, CatalogoStore store)
    {
        _repo = repo;
        _store = store;
    }

    /// <summary>Lee todo el catálogo público de la tabla y arma el snapshot (lista + menú + combos).
    /// Dispara la revalidación en background si la base venció.</summary>
    public async Task<CatalogoSnapshot> LeerAsync(CancellationToken ct = default)
    {
        _store.AsegurarBaseFresca();

        var filas = await _repo.LeerBaseAsync(soloPublicados: true, ct);
        var comboTiers = await _repo.TraerComboTiersAsync(ct);

        var articulos = filas.Select(Mapear).ToList();

        return new CatalogoSnapshot
        {
            Articulos = articulos,
            PorSlug = articulos.GroupBy(x => x.Slug, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            PorCodigo = articulos.GroupBy(x => x.ArtCod, StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            Menu = ArmarMenu(articulos),
            ComboTiers = comboTiers.Select(t => new ComboTier(t.Cantidad, t.Total)).Distinct()
                                   .OrderBy(t => t.Cantidad).ThenBy(t => t.Total).ToList(),
            // La ruta de la foto ya NO viaja en el snapshot: el endpoint de fotos la resuelve por código
            // con repo.LeerRutaFotoAsync (evita parsear 569 JSON por request). Se deja vacío a propósito.
            RutaFotoPorCodigo = new Dictionary<string, string>(),
            Generado = DateTimeOffset.UtcNow,
            TotalArmados = articulos.Count,
            DescartadosPorTaxonomia = 0,
            SinFoto = articulos.Count(a => !a.TieneFoto),
            SinVariantes = 0,
            TallesDesconocidos = Array.Empty<string>(),
        };
    }

    /// <summary>Resuelve una página de la grilla PÚBLICA EN SQL: traduce los slugs de la URL a valores con
    /// el mapa de taxonomía (rearmado con la base), le pide al repo la página + los conteos de facetas
    /// (WHERE/OFFSET-FETCH/GROUP BY, un solo viaje) y arma el DTO. No trae toda la tabla a memoria.</summary>
    public async Task<PaginaCatalogoDto> BuscarAsync(FiltrosCatalogo f, CancellationToken ct = default)
    {
        _store.AsegurarBaseFresca();
        var tax = _store.Taxonomia;

        // Slugs de la ruta no resueltos (URL vieja/rota o base aún fría): se pasa el propio slug como valor,
        // que nunca matchea un valor capitalizado ("indumentaria" ≠ "Indumentaria") → página vacía, igual
        // que cuando el filtro en memoria no encontraba nada.
        var consulta = new ConsultaPublica(
            RubroValor: f.RubroSlug is null ? null : (tax.RubroUno(f.RubroSlug) ?? f.RubroSlug),
            GeneroValor: f.GeneroSlug is null ? null : (tax.GeneroUno(f.GeneroSlug) ?? f.GeneroSlug),
            GenerosValor: tax.Generos(f.Generos),
            RubrosValor: tax.Rubros(f.Rubros),
            FamiliasValor: tax.Prendas(f.Familias),
            Talles: f.Talles, Colores: f.Colores, Locales: f.Locales, ComboDetalles: f.ComboDetalles,
            PrecioMin: f.PrecioMin, PrecioMax: f.PrecioMax,
            TextoNorm: string.IsNullOrWhiteSpace(f.Texto) ? null : Texto.SinAcentos(f.Texto),
            Orden: f.Orden, Pagina: f.Pagina);

        var r = await _repo.BuscarPublicoAsync(consulta, ct);
        var comboTiers = await _repo.TraerComboTiersAsync(ct);

        return new PaginaCatalogoDto
        {
            Items = r.Items.Select(Mapear).ToList(),
            Total = r.Total,
            Pagina = Math.Max(1, f.Pagina),
            // Facetas de taxonomía: el repo agrupó por VALOR; acá se calcula el slug (lo que viaja en la URL).
            Rubros = FacetaPorSlug(r.Rubros, f.Rubros),
            Familias = FacetaPorSlug(r.Familias, f.Familias),
            Talles = r.Talles.OrderBy(t => t.Orden)
                .Select(t => new OpcionFaceta(t.Talle, t.Talle, t.Cantidad,
                    f.Talles.Contains(t.Talle, StringComparer.OrdinalIgnoreCase))).ToList(),
            Colores = FacetaPorValor(r.Colores, f.Colores),
            Locales = r.Locales.Where(l => l.Cantidad > 0)
                .Select(l => new OpcionFaceta(l.Valor, l.Etiqueta, l.Cantidad,
                    f.Locales.Contains(l.Valor, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(o => o.Etiqueta).ToList(),
            Combos = ArmarCombos(comboTiers, r.Combos, f.ComboDetalles),
        };
    }

    // Faceta cuyo Valor es el SLUG del valor agrupado (rubro/familia): el repo devolvió el valor mostrable
    // y acá se deriva el slug para la URL. Orden: más artículos primero, después alfabético.
    private static IReadOnlyList<OpcionFaceta> FacetaPorSlug(IReadOnlyList<FacetaConteo> conteos, IReadOnlyList<string> activos)
        => conteos.Select(x =>
            {
                var slug = Texto.Slug(x.Valor);
                return new OpcionFaceta(slug, x.Etiqueta, x.Cantidad, activos.Contains(slug, StringComparer.OrdinalIgnoreCase));
            })
            .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta).ToList();

    // Faceta cuyo Valor ES el valor (color): sin traducción.
    private static IReadOnlyList<OpcionFaceta> FacetaPorValor(IReadOnlyList<FacetaConteo> conteos, IReadOnlyList<string> activos)
        => conteos.Select(x => new OpcionFaceta(x.Valor, x.Etiqueta, x.Cantidad,
                activos.Contains(x.Valor, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta).ToList();

    // Faceta de combo de dos niveles: los tramos salen de la grilla oficial (PruebaCombos); el conteo de
    // cada tramo, de lo que devolvió la query. Un tramo sin artículos no se ofrece.
    private static IReadOnlyList<OpcionFacetaCombo> ArmarCombos(
        IReadOnlyList<ComboTierRow> tiers, IReadOnlyList<ComboConteo> conteos, IReadOnlyList<string> activos)
    {
        var porTramo = conteos.ToDictionary(x => (x.Cantidad, x.Total), x => x.Conteo);
        return tiers.GroupBy(t => t.Cantidad).OrderBy(g => g.Key)
            .Select(g =>
            {
                var detalles = g.OrderBy(t => t.Total)
                    .Select(t =>
                    {
                        var valor = $"{g.Key}-{t.Total}";
                        var cant = porTramo.GetValueOrDefault((g.Key, t.Total));
                        return new OpcionFaceta(valor, Combo.Mostrar(g.Key, t.Total), cant, activos.Contains(valor));
                    })
                    .Where(d => d.Cantidad > 0).ToList();
                return new OpcionFacetaCombo(g.Key, $"Combo de {g.Key}",
                    detalles.Sum(d => d.Cantidad), detalles.Any(d => d.Activa), detalles);
            })
            .Where(o => o.Detalles.Count > 0).ToList();
    }

    /// <summary>Mapea una fila de la tabla al DTO público. Espeja lo que armaba <c>CatalogoCache</c>, pero
    /// desde columnas ya calculadas: talles/colores salen del CSV, locales de los bits, el combo se
    /// re-parsea. Variantes queda vacío a propósito (nadie las consume; el orden de la faceta de talles
    /// usa <see cref="Talles.OrdenEtiqueta"/>).</summary>
    private static ArticuloDto Mapear(CatalogoFilaLeida f)
    {
        var rubro = f.Rubro ?? "";
        var genero = f.Genero ?? "";
        var familia = string.IsNullOrWhiteSpace(f.Prenda) ? null : f.Prenda;
        // Combo ya viene parseado en columnas (ComboCantidad/ComboTotal); el precio unitario se deriva.
        decimal? precioUnidadCombo = (f.ComboCantidad is int cc && cc > 0 && f.ComboTotal is int ct)
            ? (decimal)ct / cc : null;

        var locales = new List<string>(2);
        if (f.EnLuro) locales.Add("LURO");
        if (f.EnPeralta) locales.Add("PERALTA");

        return new ArticuloDto
        {
            ArtCod = f.Codigo,
            Descripcion = f.Descripcion ?? f.Codigo,
            Marketing = null,
            Slug = f.Slug ?? Texto.SlugProducto(f.Descripcion ?? f.Codigo, f.Codigo),
            Rubro = rubro,
            RubroSlug = Texto.Slug(rubro),
            Genero = genero,
            GeneroSlug = Texto.Slug(genero),
            Familia = familia,
            FamiliaSlug = familia is null ? null : Texto.Slug(familia),
            ComboTexto = (f.ComboCantidad is int mc && f.ComboTotal is int mt) ? Combo.Mostrar(mc, mt) : null,
            ComboCantidad = f.ComboCantidad,
            ComboTotal = f.ComboTotal,
            PrecioUnidadCombo = precioUnidadCombo,
            PrecioUnidadSuelta = f.PrecioVenta > 0 ? f.PrecioVenta : null,
            PrecioSueltaTexto = f.PrecioVenta > 0 ? Combo.Plata(f.PrecioVenta.Value) : null,
            TieneFoto = f.TieneFoto,
            FotoVersion = f.FotoPrincipalVersion,
            Destacado = 0,
            Locales = locales,
            Variantes = Array.Empty<VarianteDto>(),
            Talles = PartirCsv(f.TallesCsv),
            Colores = PartirCsv(f.ColoresCsv),
            TextoBusqueda = f.TextoBusqueda ?? "",
        };
    }

    private static IReadOnlyList<string> PartirCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Mismo armado de menú que tenía CatalogoCache: rubro → géneros con sus conteos.
    private static IReadOnlyList<RubroMenu> ArmarMenu(List<ArticuloDto> lista) =>
        lista.GroupBy(x => (x.RubroSlug, x.Rubro))
             .Select(r => new RubroMenu(
                 r.Key.RubroSlug, r.Key.Rubro, r.Count(),
                 r.GroupBy(x => (x.GeneroSlug, x.Genero))
                  .Select(g => new GeneroMenu(g.Key.GeneroSlug, g.Key.Genero, g.Count()))
                  .OrderByDescending(g => g.Cantidad).ToList()))
             .OrderByDescending(r => r.Cantidad)
             .ToList();
}
