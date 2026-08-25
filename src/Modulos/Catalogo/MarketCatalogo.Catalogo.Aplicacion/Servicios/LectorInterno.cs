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

    public async Task<ArticuloInternoDto?> PorCodigoAsync(string? codigo, CancellationToken ct = default)
    {
        var cod = (codigo ?? "").Trim();
        if (cod.Length == 0) return null;
        var filas = await _repo.LeerBaseAsync(soloPublicados: false, ct);
        var fila = filas.FirstOrDefault(f => f.Codigo.Equals(cod, StringComparison.OrdinalIgnoreCase));
        if (fila is null) return null;
        // Stock (por local) y ventas realizadas de la ventana, a demanda, sólo para la ficha. Una consulta
        // por réplica (stock + ventas juntos); si falla, la ficha se muestra sin esos datos.
        var datos = await TraerSeguro(() => _repo.TraerFichaStockVentasAsync(cod, _semanasVentas * 7, ct));
        return Mapear(fila, datos?.Stock, datos?.Ventas);
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

    public Task RefrescarAsync(CancellationToken ct = default) => _store.ReconstruirBaseAsync(ct);

    public async Task<PaginaInternaDto> BuscarAsync(FiltrosInterno f, CancellationToken ct = default)
    {
        _store.AsegurarBaseFresca();

        var todos = (await _repo.LeerBaseAsync(soloPublicados: false, ct)).Select(fila => Mapear(fila)).ToList();

        var textoNorm = string.IsNullOrWhiteSpace(f.Texto) ? null : Texto.SinAcentos(f.Texto);

        bool PasaUbicacion(ArticuloInternoDto a)
        {
            if (f.Ubicaciones.Count == 0) return true;
            return (f.Ubicaciones.Contains("luro", StringComparer.OrdinalIgnoreCase) && a.EnLuro)
                || (f.Ubicaciones.Contains("peralta", StringComparer.OrdinalIgnoreCase) && a.EnPeralta)
                || (f.Ubicaciones.Contains("deposito", StringComparer.OrdinalIgnoreCase) && a.EnDeposito);
        }
        bool PasaCruce(ArticuloInternoDto a) => f.CruceDepoLocal switch
        {
            "solo-deposito" => a.EnDeposito && !a.EnAlgunLocal,
            "en-local" => a.EnAlgunLocal,
            _ => true,
        };
        bool PasaRubro(ArticuloInternoDto a) => f.Rubros.Count == 0 || f.Rubros.Contains(a.Rubro, StringComparer.OrdinalIgnoreCase);
        bool PasaGenero(ArticuloInternoDto a) => f.Generos.Count == 0 || f.Generos.Contains(Texto.Slug(a.Genero), StringComparer.OrdinalIgnoreCase);
        bool PasaPrenda(ArticuloInternoDto a) => f.Prendas.Count == 0 || (a.Prenda is not null && f.Prendas.Contains(a.Prenda, StringComparer.OrdinalIgnoreCase));
        bool PasaProveedor(ArticuloInternoDto a) => f.Proveedores.Count == 0 || (a.Proveedor is not null && f.Proveedores.Contains(a.Proveedor, StringComparer.OrdinalIgnoreCase));
        bool PasaMarca(ArticuloInternoDto a) => f.Marcas.Count == 0 || (a.Marca is not null && f.Marcas.Contains(a.Marca, StringComparer.OrdinalIgnoreCase));
        bool PasaTemporada(ArticuloInternoDto a) => f.Temporadas.Count == 0 || (a.Temporada is not null && f.Temporadas.Contains(a.Temporada, StringComparer.OrdinalIgnoreCase));
        bool PasaTalle(ArticuloInternoDto a) => f.Talles.Count == 0 || a.Talles.Any(t => f.Talles.Contains(t, StringComparer.OrdinalIgnoreCase));
        bool PasaColor(ArticuloInternoDto a) => f.Colores.Count == 0 || a.Colores.Any(c => f.Colores.Contains(c, StringComparer.OrdinalIgnoreCase));
        bool PasaPublicado(ArticuloInternoDto a) => f.Publicado is null || a.Publicado == f.Publicado.Value;
        bool PasaMargen(ArticuloInternoDto a) => f.MargenMax is null || (a.MargenTeorico is not null && a.MargenTeorico <= f.MargenMax);
        bool PasaTexto(ArticuloInternoDto a) => textoNorm is null
            || Texto.SinAcentos($"{a.Descripcion} {a.Codigo} {a.Prenda} {a.Proveedor} {a.Marca}").Contains(textoNorm, StringComparison.Ordinal);

        // "excepto" deja fuera una faceta para poder contarla sin encerrar al usuario (igual que el público).
        IEnumerable<ArticuloInternoDto> Aplicar(string? excepto) => todos.Where(a =>
            PasaUbicacion(a) && PasaCruce(a) && PasaGenero(a) &&
            (excepto == "rubro" || PasaRubro(a)) &&
            (excepto == "prenda" || PasaPrenda(a)) &&
            (excepto == "proveedor" || PasaProveedor(a)) &&
            (excepto == "marca" || PasaMarca(a)) &&
            (excepto == "temporada" || PasaTemporada(a)) &&
            PasaTalle(a) && PasaColor(a) && PasaPublicado(a) && PasaMargen(a) && PasaTexto(a));

        var filtrados = Aplicar(null).ToList();

        var ordenados = f.Orden switch
        {
            "precio-asc" => filtrados.OrderBy(a => a.PrecioUnidadCombo ?? a.PrecioVenta ?? decimal.MaxValue).ThenBy(a => a.Codigo),
            "precio-desc" => filtrados.OrderByDescending(a => a.PrecioUnidadCombo ?? a.PrecioVenta ?? decimal.MinValue).ThenBy(a => a.Codigo),
            "margen" => filtrados.OrderBy(a => a.MargenTeorico ?? decimal.MaxValue).ThenBy(a => a.Codigo),
            "nombre" => filtrados.OrderBy(a => a.Descripcion, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Codigo),
            _ => filtrados.OrderBy(a => a.Codigo, StringComparer.OrdinalIgnoreCase),
        };

        var pagina = Math.Max(1, f.Pagina);
        var items = ordenados.Skip((pagina - 1) * FiltrosInterno.PorPagina).Take(FiltrosInterno.PorPagina).ToList();

        return new PaginaInternaDto
        {
            Items = items,
            Total = filtrados.Count,
            Pagina = pagina,
            TotalUniverso = todos.Count,
            EnDeposito = todos.Count(a => a.EnDeposito),
            SoloDeposito = todos.Count(a => a.EnDeposito && !a.EnAlgunLocal),
            Publicados = todos.Count(a => a.Publicado),
            BaseActualizada = _store.BaseActualizada,
            Rubros = Faceta(Aplicar("rubro"), a => a.Rubro, f.Rubros),
            Prendas = Faceta(Aplicar("prenda"), a => a.Prenda, f.Prendas),
            Proveedores = Faceta(Aplicar("proveedor"), a => a.Proveedor, f.Proveedores),
            Marcas = Faceta(Aplicar("marca"), a => a.Marca, f.Marcas),
            Temporadas = Faceta(Aplicar("temporada"), a => a.Temporada, f.Temporadas),
        };
    }

    private static IReadOnlyList<OpcionFacetaInterna> Faceta(
        IEnumerable<ArticuloInternoDto> conj, Func<ArticuloInternoDto, string?> sel, IReadOnlyList<string> activos)
        => conj.Select(sel).Where(v => !string.IsNullOrWhiteSpace(v))
               .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
               .Select(g => new OpcionFacetaInterna(g.Key, g.Key, g.Count(),
                   activos.Contains(g.Key, StringComparer.OrdinalIgnoreCase)))
               .OrderByDescending(o => o.Cantidad).ThenBy(o => o.Etiqueta, StringComparer.CurrentCultureIgnoreCase)
               .ToList();

    private static ArticuloInternoDto Mapear(CatalogoFilaLeida f, StockDetalleRow? stock = null, VentasPeriodoRow? ventas = null)
    {
        var combo = Combo.Parsear(f.Combo);
        var precioUnidad = combo?.PrecioUnidad ?? (f.PrecioVenta > 0 ? f.PrecioVenta : null);
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
            ComboTexto = combo is null ? null : Combo.Mostrar(combo.Cantidad, combo.Total),
            ComboCantidad = combo?.Cantidad,
            ComboTotal = combo?.Total,
            PrecioUnidadCombo = combo?.PrecioUnidad,
            MargenTeorico = margen,
            EnLuro = f.EnLuro,
            EnPeralta = f.EnPeralta,
            EnDeposito = f.EnDeposito,
            Publicado = f.Publicado,
            Talles = PartirCsv(f.TallesCsv),
            Colores = PartirCsv(f.ColoresCsv),
            Proveedor = string.IsNullOrWhiteSpace(f.Proveedor) ? null : f.Proveedor,
            Temporada = string.IsNullOrWhiteSpace(f.Temporada) ? null : f.Temporada,
            Marca = string.IsNullOrWhiteSpace(f.Marca) ? null : f.Marca,
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
            Vendido = ventas?.Vendido,
            VendidoLuro = ventas?.VendidoLuro,
            VendidoPeralta = ventas?.VendidoPeralta,
            Facturado = ventas?.Facturado,
            CostoPeriodo = ventas?.Costo,
            MargenRealPesos = ventas?.MargenPesos,
            MargenRealPct = ventas?.MargenPct,
            UltimaVenta = ventas?.UltimaVenta,
        };
    }

    private static IReadOnlyList<string> PartirCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
