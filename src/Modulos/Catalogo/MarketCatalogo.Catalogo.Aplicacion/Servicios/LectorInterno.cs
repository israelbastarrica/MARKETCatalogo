using MarketCatalogo.Catalogo.Contratos.Interno;
using MarketCatalogo.Compartido;

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
    private readonly ICatalogoRepositorio _repo;
    private readonly CatalogoStore _store;

    public LectorInterno(ICatalogoRepositorio repo, CatalogoStore store)
    {
        _repo = repo;
        _store = store;
    }

    public async Task<ArticuloInternoDto?> PorCodigoAsync(string? codigo, CancellationToken ct = default)
    {
        var cod = (codigo ?? "").Trim();
        if (cod.Length == 0) return null;
        var filas = await _repo.LeerBaseAsync(soloPublicados: false, ct);
        var fila = filas.FirstOrDefault(f => f.Codigo.Equals(cod, StringComparison.OrdinalIgnoreCase));
        return fila is null ? null : Mapear(fila);
    }

    public async Task<PaginaInternaDto> BuscarAsync(FiltrosInterno f, CancellationToken ct = default)
    {
        _store.AsegurarBaseFresca();

        var todos = (await _repo.LeerBaseAsync(soloPublicados: false, ct)).Select(Mapear).ToList();

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
        bool PasaPrenda(ArticuloInternoDto a) => f.Prendas.Count == 0 || (a.Prenda is not null && f.Prendas.Contains(a.Prenda, StringComparer.OrdinalIgnoreCase));
        bool PasaProveedor(ArticuloInternoDto a) => f.Proveedores.Count == 0 || (a.Proveedor is not null && f.Proveedores.Contains(a.Proveedor, StringComparer.OrdinalIgnoreCase));
        bool PasaMarca(ArticuloInternoDto a) => f.Marcas.Count == 0 || (a.Marca is not null && f.Marcas.Contains(a.Marca, StringComparer.OrdinalIgnoreCase));
        bool PasaTemporada(ArticuloInternoDto a) => f.Temporadas.Count == 0 || (a.Temporada is not null && f.Temporadas.Contains(a.Temporada, StringComparer.OrdinalIgnoreCase));
        bool PasaTalle(ArticuloInternoDto a) => f.Talles.Count == 0 || a.Talles.Any(t => f.Talles.Contains(t, StringComparer.OrdinalIgnoreCase));
        bool PasaColor(ArticuloInternoDto a) => f.Colores.Count == 0 || a.Colores.Any(c => f.Colores.Contains(c, StringComparer.OrdinalIgnoreCase));
        bool PasaPublicado(ArticuloInternoDto a) => f.Publicado is null || a.Publicado == f.Publicado.Value;
        bool PasaMargen(ArticuloInternoDto a) => f.MargenMax is null || (a.MargenTeorico is not null && a.MargenTeorico <= f.MargenMax);
        bool PasaTexto(ArticuloInternoDto a) => textoNorm is null
            || Texto.SinAcentos($"{a.Titulo} {a.Descripcion} {a.Codigo} {a.Prenda} {a.Proveedor} {a.Marca}").Contains(textoNorm, StringComparison.Ordinal);

        // "excepto" deja fuera una faceta para poder contarla sin encerrar al usuario (igual que el público).
        IEnumerable<ArticuloInternoDto> Aplicar(string? excepto) => todos.Where(a =>
            PasaUbicacion(a) && PasaCruce(a) &&
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
            "nombre" => filtrados.OrderBy(a => a.Titulo, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Codigo),
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

    private static ArticuloInternoDto Mapear(CatalogoFilaLeida f)
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
            Titulo = f.Titulo ?? f.Codigo,
            Descripcion = f.Descripcion ?? "",
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
        };
    }

    private static IReadOnlyList<string> PartirCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
