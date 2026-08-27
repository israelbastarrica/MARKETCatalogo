using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Catalogo.Contratos.Interno;
using MarketCatalogo.Compartido;
using Microsoft.Extensions.Configuration;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Implementa <see cref="ICatalogoInternoConsulta"/>: lee TODO el universo de <c>dbo.Catalogo</c>
/// (incluido depósito y lo no publicado), mapea a los DTOs internos (con costo y margen teórico) y
/// filtra/facetea/pagina en memoria. Espeja a <see cref="LectorCatalogo"/>/<c>CatalogoService</c> pero
/// para el staff: mismo modelo tabla-como-caché (dispara la revalidación de la base al leer), datos
/// completos. Sólo lo invocan páginas gateadas por la política "Interno".
/// </summary>
public sealed class LectorInterno : ICatalogoInternoConsulta
{
    // Ventana de ventas realizadas de la ficha, en semanas. Config: Catalogo:SemanasVentas (default 8),
    // acotada a 1..52. El día que se quiera cambiar, se toca appsettings sin recompilar.
    private readonly int _semanasVentas;

    private readonly ICatalogoRepositorio _repo;
    private readonly CatalogoStore _store;

    public LectorInterno(ICatalogoRepositorio repo, CatalogoStore store, IConfiguration cfg)
    {
        _repo = repo;
        _store = store;
        var semanas = int.TryParse(cfg["Catalogo:SemanasVentas"], out var s) ? s : 8;
        _semanasVentas = Math.Clamp(semanas, 1, 52);
    }

    // Corre una consulta a demanda de la ficha sin dejar que su fallo tumbe la página: null si falla.
    private static async Task<T?> TraerSeguro<T>(Func<Task<T>> consulta) where T : class
    {
        try { return await consulta(); }
        catch { return null; }
    }

    // Igual que TraerSeguro pero para bool (no admite null): false si falla.
    private static async Task<bool> TraerBoolSeguro(Func<Task<bool>> consulta)
    {
        try { return await consulta(); }
        catch { return false; }
    }

    public async Task<ArticuloInternoDto?> PorCodigoAsync(string? codigo, CancellationToken ct = default)
    {
        var cod = (codigo ?? "").Trim();
        if (cod.Length == 0) return null;
        // Lookup por PK: no hace falta traer todo el universo para mostrar un solo artículo.
        var fila = await _repo.LeerFilaAsync(cod, ct);
        if (fila is null) return null;
        // Datos de ficha a demanda, TODOS en paralelo (cada fuente su conexión; ninguno tumba la ficha):
        //   · stock + ventas realizadas de la ventana (una consulta por réplica),
        //   · características extendidas (Dragon central),
        //   · ubicaciones actuales con detalle de posición (MARKET).
        var datosT = TraerSeguro(() => _repo.TraerFichaStockVentasAsync(cod, _semanasVentas * 7, ct));
        // El '!' es porque TraerCaracteristicasAsync puede devolver null (código no está en Dragon) y
        // TraerSeguro<T> exige T no-anulable; el null real lo maneja Mapear (carac?.). Evita CS8634/CS8621.
        var caracT = TraerSeguro(async () => (await _repo.TraerCaracteristicasAsync(cod, ct))!);
        var ubicT = TraerSeguro(() => _repo.TraerUbicacionesDetalleAsync(cod, ct));
        var ordT = TraerSeguro(() => _repo.TraerOrdenesPedidoAsync(cod, ct));
        var bloqT = TraerBoolSeguro(() => _repo.EstaBloqueadoAsync(cod, ct));

        // Benchmark de familia (Prenda): facturado promedio por artículo de la misma familia. Los códigos
        // de la familia salen por SQL (por Prenda), no de traer todo a memoria; la suma de facturado va a
        // Dragon (tolerante a fallo).
        var codigosFamilia = string.IsNullOrWhiteSpace(fila.Prenda)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : await _repo.LeerCodigosPorPrendaAsync(fila.Prenda!, ct);
        var famT = codigosFamilia.Count > 0
            ? _repo.TraerFacturadoTotalAsync(codigosFamilia, _semanasVentas * 7, ct)
            : Task.FromResult(0m);

        await Task.WhenAll(datosT, caracT, ubicT, ordT, famT, bloqT);

        decimal? famProm = codigosFamilia.Count > 0 ? famT.Result / codigosFamilia.Count : null;
        int? famArt = codigosFamilia.Count > 0 ? codigosFamilia.Count : null;

        return Mapear(fila, datosT.Result?.Stock, datosT.Result?.Ventas, caracT.Result, ubicT.Result,
            famProm, famArt, ordT.Result, bloqT.Result);
    }

    public async Task<IReadOnlyList<RubroMenu>> MenuAsync(CancellationToken ct = default)
    {
        _store.AsegurarBaseFresca();
        var todos = (await _repo.LeerBaseAsync(soloPublicados: false, ct)).Select(f => Mapear(f)).ToList();
        // Rubro → géneros con conteos, sobre TODO el universo (no sólo Indumentaria publicada). Nombre del
        // rubro = VALOR (así el header filtra por ?rubro={valor}); slug para la ruta indexable del público
        // no aplica acá (todos los links van a /interno). Se descartan rubro/género vacíos.
        return todos
            .Where(a => !string.IsNullOrWhiteSpace(a.Rubro) && !string.IsNullOrWhiteSpace(a.Genero))
            .GroupBy(a => a.Rubro)
            .Select(r => new RubroMenu(
                Texto.Slug(r.Key), r.Key, r.Count(),
                r.GroupBy(a => a.Genero)
                 .Select(g => new GeneroMenu(Texto.Slug(g.Key), g.Key, g.Count()))
                 .OrderByDescending(g => g.Cantidad).ToList()))
            .OrderByDescending(r => r.Cantidad).ToList();
    }

    public async Task CambiarVisibilidadAsync(string codigo, bool ocultar, string origen, CancellationToken ct = default)
    {
        var art = await PorCodigoAsync(codigo, ct);
        if (art is null) return;
        // Si se muestra: ¿cumpliría las condiciones de publicación? (mismo criterio que el rebuild:
        // Indumentaria + en algún local + tiene talles/colores). El rebuild lo recomputa definitivamente.
        var publicadoSiVisible =
            Texto.SinAcentos(art.Rubro) == "indumentaria"
            && art.EnAlgunLocal
            && (art.Talles.Count > 0 || art.Colores.Count > 0);
        await _repo.CambiarVisibilidadAsync(codigo, ocultar, publicadoSiVisible, origen, ct);
    }

    public Task CambiarBloqueoAsync(string codigo, bool bloquear, string origen, CancellationToken ct = default)
        => _repo.CambiarBloqueoAsync(codigo, bloquear, origen, ct);

    public Task RefrescarAsync(CancellationToken ct = default) => _store.ReconstruirBaseAsync(ct);

    /// <summary>Grilla interna resuelta EN SQL: traduce el slug de género a valor con el mapa de taxonomía,
    /// le pide al repo la página + facetas + totales del universo (un solo viaje) y arma el DTO. Rubro/prenda
    /// ya viajan por valor en el interno; talle/color son filtros (no facetas). No trae toda la tabla.</summary>
    public async Task<PaginaInternaDto> BuscarAsync(FiltrosInterno f, CancellationToken ct = default)
    {
        _store.AsegurarBaseFresca();

        var consulta = new ConsultaInterna(
            Ubicaciones: f.Ubicaciones, CruceDepoLocal: f.CruceDepoLocal,
            RubrosValor: f.Rubros,                          // interno: rubro por valor
            GenerosValor: _store.Taxonomia.Generos(f.Generos),  // género slug→valor
            PrendasValor: f.Prendas,                        // interno: prenda por valor
            Proveedores: f.Proveedores, Marcas: f.Marcas, Temporadas: f.Temporadas,
            Anios: f.Anios, Talles: f.Talles, Colores: f.Colores, ComboDetalles: f.ComboDetalles,
            Publicado: f.Publicado, MargenMax: f.MargenMax, Texto: f.Texto,
            Orden: f.Orden, Pagina: f.Pagina);

        var r = await _repo.BuscarInternoAsync(consulta, ct);
        var comboTiers = await _repo.TraerComboTiersAsync(ct);

        return new PaginaInternaDto
        {
            Items = r.Items.Select(fila => Mapear(fila)).ToList(),
            Total = r.Total,
            Pagina = Math.Max(1, f.Pagina),
            TotalUniverso = r.TotalUniverso,
            EnDeposito = r.EnDeposito,
            SoloDeposito = r.SoloDeposito,
            Publicados = r.Publicados,
            BaseActualizada = _store.BaseActualizada,
            Generos = FacetaGenero(r.Generos, f.Generos),
            Rubros = FacetaValor(r.Rubros, f.Rubros),
            Prendas = FacetaValor(r.Prendas, f.Prendas),
            Proveedores = FacetaValor(r.Proveedores, f.Proveedores),
            Marcas = FacetaValor(r.Marcas, f.Marcas),
            Temporadas = FacetaValor(r.Temporadas, f.Temporadas),
            Anios = FacetaValor(r.Anios, f.Anios),
            Combos = ArmarCombos(comboTiers, r.Combos, f.ComboDetalles),
        };
    }

    // Faceta cuyo Valor ES el valor (rubro/prenda/proveedor/marca/temporada/año en el interno).
    private static IReadOnlyList<OpcionFacetaInterna> FacetaValor(
        IReadOnlyList<FacetaConteo> conteos, IReadOnlyList<string> activos)
        => conteos.Select(x => new OpcionFacetaInterna(x.Valor, x.Etiqueta, x.Cantidad,
                activos.Contains(x.Valor, StringComparer.OrdinalIgnoreCase)))
               .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta, StringComparer.CurrentCultureIgnoreCase)
               .ToList();

    // Faceta de género: el repo agrupó por valor; el Valor de la opción es el SLUG (lo que viaja en la URL).
    private static IReadOnlyList<OpcionFacetaInterna> FacetaGenero(
        IReadOnlyList<FacetaConteo> conteos, IReadOnlyList<string> activos)
        => conteos.Select(x =>
            {
                var slug = Texto.Slug(x.Valor);
                return new OpcionFacetaInterna(slug, x.Etiqueta, x.Cantidad,
                    activos.Contains(slug, StringComparer.OrdinalIgnoreCase));
            })
               .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta, StringComparer.CurrentCultureIgnoreCase)
               .ToList();

    // Faceta de combo de dos niveles: tramos de la grilla oficial (PruebaCombos), conteo del resultado SQL.
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
            .Where(gc => gc.Detalles.Count > 0).ToList();
    }

    private static ArticuloInternoDto Mapear(CatalogoFilaLeida f, StockDetalleRow? stock = null,
        VentasPeriodoRow? ventas = null, CaracteristicasRow? carac = null,
        IReadOnlyList<UbicacionDetalleRow>? ubicaciones = null,
        decimal? famFacturadoProm = null, int? famArticulos = null,
        IReadOnlyList<OrdenPedidoRow>? ordenes = null, bool bloqueado = false)
    {
        // Combo ya viene parseado en columnas (ComboCantidad/ComboTotal); el precio unitario se deriva.
        decimal? precioUnidadCombo = (f.ComboCantidad is int cc && cc > 0 && f.ComboTotal is int ct)
            ? (decimal)ct / cc : null;
        var precioUnidad = precioUnidadCombo ?? (f.PrecioVenta > 0 ? f.PrecioVenta : null);
        // Margen teórico = como "Cambiar Precios": sobre el precio unitario del combo (o el suelto si no
        // hay combo), NO el LISTA1 con recargo. null si falta algún dato o el precio es 0.
        decimal? margen = (precioUnidad is > 0 && f.PrecioCompra is > 0)
            ? Math.Round((precioUnidad.Value - f.PrecioCompra.Value) / precioUnidad.Value * 100, 1)
            : null;

        return new ArticuloInternoDto
        {
            Codigo = f.Codigo,
            Descripcion = f.Descripcion ?? f.Codigo,
            Slug = f.Slug ?? "",
            Rubro = f.Rubro ?? "",
            Genero = f.Genero ?? "",
            Prenda = string.IsNullOrWhiteSpace(f.Prenda) ? null : f.Prenda,
            PrecioVenta = f.PrecioVenta > 0 ? f.PrecioVenta : null,
            PrecioCompra = f.PrecioCompra > 0 ? f.PrecioCompra : null,
            ComboTexto = (f.ComboCantidad is int mc && f.ComboTotal is int mt) ? Combo.Mostrar(mc, mt) : null,
            ComboCantidad = f.ComboCantidad,
            ComboTotal = f.ComboTotal,
            PrecioUnidadCombo = precioUnidadCombo,
            MargenTeorico = margen,
            EnLuro = f.EnLuro,
            EnPeralta = f.EnPeralta,
            EnDeposito = f.EnDeposito,
            Publicado = f.Publicado,
            Bloqueado = bloqueado,
            Talles = PartirCsv(f.TallesCsv),
            Colores = PartirCsv(f.ColoresCsv),
            Proveedor = string.IsNullOrWhiteSpace(f.Proveedor) ? null : f.Proveedor,
            Temporada = string.IsNullOrWhiteSpace(f.Temporada) ? null : f.Temporada,
            Marca = string.IsNullOrWhiteSpace(f.Marca) ? null : f.Marca,
            Anio = f.Anio,
            Tratamiento = NuloSiVacio(carac?.Tratamiento),
            Linea = NuloSiVacio(carac?.Linea),
            Subfamilia = NuloSiVacio(carac?.Subfamilia),
            Material = NuloSiVacio(carac?.Material),
            Paleta = NuloSiVacio(carac?.Paleta),
            CurvaTalles = NuloSiVacio(carac?.CurvaTalles),
            Caracteristica = NuloSiVacio(carac?.Caracteristica),
            DescEcommerce = NuloSiVacio(carac?.DescEcommerce),
            PubEcommerce = carac?.PubEcommerce,
            Ubicaciones = ubicaciones is null ? []
                : ubicaciones.Select(u => new UbicacionInternaDto(
                    u.Local, u.Tipo, u.Mobiliario, u.Modulo, u.Pasillo, u.Fila, u.Posicion)).ToList(),
            Ordenes = ordenes is null ? []
                : ordenes.Select(o => new OrdenPedidoDto(o.NroOrden, o.Tipo, o.Estado, o.Finalizada, o.FechaMod)).ToList(),
            TieneFoto = f.TieneFoto,
            FotoVersion = f.FotoPrincipalVersion,
            StockTotal = stock?.Total,
            EnTransito = stock?.TransitoTotal,
            StockLuro = stock?.Luro,
            TransitoLuro = stock?.TransitoLuro,
            StockPeralta = stock?.Peralta,
            TransitoPeralta = stock?.TransitoPeralta,
            StockCentral = stock?.Central,
            TransitoCentral = stock?.TransitoCentral,
            VentasDias = ventas?.Dias,
            VentasSemanales = ventas?.SemanasUnidades ?? [],
            Vendido = ventas?.Vendido,
            VendidoLuro = ventas?.VendidoLuro,
            VendidoPeralta = ventas?.VendidoPeralta,
            Facturado = ventas?.Facturado,
            CostoPeriodo = ventas?.Costo,
            MargenRealPesos = ventas?.MargenPesos,
            MargenRealPct = ventas?.MargenPct,
            FamiliaFacturadoProm = famFacturadoProm,
            FamiliaArticulos = famArticulos,
            UltimaVenta = ventas?.UltimaVenta,
        };
    }

    private static string? NuloSiVacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string> PartirCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
