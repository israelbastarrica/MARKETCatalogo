using System.Text;
using Dapper;
using MarketCatalogo.Catalogo.Aplicacion;
using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Catalogo.Contratos.Interno;

namespace MarketCatalogo.Catalogo.Datos;

/// <summary>
/// Resolución de la grilla EN SQL (pública e interna): en vez de traer toda la tabla y filtrar en C#, la
/// base hace el trabajo — WHERE con los filtros, OFFSET/FETCH para la página, COUNT para el total y un
/// GROUP BY por faceta (cada una excluyendo su propio filtro, para no encerrar al usuario). Todo en UN
/// viaje por catálogo (QueryMultiple). Talle/color se filtran/facetean por las tablas hijas
/// (CatalogoTalle/CatalogoColor); el combo, por las columnas ComboCantidad/ComboTotal. La taxonomía llega
/// ya resuelta a VALORES (el servicio tradujo el slug de la URL con su mapa): se filtra por Rubro/Genero/
/// Prenda y las facetas agrupan por valor (el servicio les calcula el slug).
///
/// Sin NOLOCK a propósito: el rebuild reconstruye las tablas hijas dentro de una transacción, así que
/// leer confirmado evita ver talles/colores a medio reconstruir.
/// </summary>
public sealed partial class CatalogoRepositorio
{
    // Precio UNITARIO por el que se ordena/filtra. Público: sólo el del combo (un artículo sin combo no
    // entra al filtro de precio ni tiene precio de grilla). Interno: combo, y si no hay, el suelto (LISTA1).
    private const string UnitPublico =
        "CASE WHEN c.ComboCantidad > 0 AND c.ComboTotal IS NOT NULL " +
        "THEN CAST(c.ComboTotal AS decimal(18,4)) / c.ComboCantidad ELSE NULL END";
    private const string UnitInterno =
        "CASE WHEN c.ComboCantidad > 0 AND c.ComboTotal IS NOT NULL " +
        "THEN CAST(c.ComboTotal AS decimal(18,4)) / c.ComboCantidad " +
        "WHEN c.PrecioVenta > 0 THEN c.PrecioVenta ELSE NULL END";
    // Margen teórico (%) igual que "Cambiar Precios": sobre el precio unitario, no el LISTA1 con recargo.
    private static readonly string MargenInterno =
        $"CASE WHEN ({UnitInterno}) > 0 AND c.PrecioCompra > 0 " +
        $"THEN ROUND((({UnitInterno}) - c.PrecioCompra) / ({UnitInterno}) * 100, 1) ELSE NULL END";

    /// <inheritdoc/>
    public async Task<PaginaPublicaCruda> BuscarPublicoAsync(ConsultaPublica q, CancellationToken ct = default)
    {
        var p = new DynamicParameters();

        // Base: siempre aplicado (subset público + filtros de la RUTA + géneros multi). No se excluye en facetas.
        var baseParts = new List<string> { "c.Eliminado = 0", "c.Publicado = 1" };
        if (!string.IsNullOrWhiteSpace(q.RubroValor)) { p.Add("rutaRubro", q.RubroValor); baseParts.Add("c.Rubro = @rutaRubro"); }
        if (!string.IsNullOrWhiteSpace(q.GeneroValor)) { p.Add("rutaGenero", q.GeneroValor); baseParts.Add("c.Genero = @rutaGenero"); }
        if (q.GenerosValor.Count > 0) { p.Add("generos", q.GenerosValor); baseParts.Add("c.Genero IN @generos"); }
        var baseSql = string.Join(" AND ", baseParts);

        // Refinamiento: cada uno con su clave, para poder excluirlo al contar su propia faceta.
        var preds = new List<(string Key, string Sql)>();
        if (q.RubrosValor.Count > 0) { p.Add("tipos", q.RubrosValor); preds.Add(("tipo", "c.Rubro IN @tipos")); }
        if (q.FamiliasValor.Count > 0) { p.Add("familias", q.FamiliasValor); preds.Add(("familia", "c.Prenda IN @familias")); }
        if (q.Talles.Count > 0) { p.Add("talles", q.Talles); preds.Add(("talle", "EXISTS (SELECT 1 FROM dbo.CatalogoTalle et WHERE et.Codigo = c.Codigo AND et.Talle IN @talles)")); }
        if (q.Colores.Count > 0) { p.Add("colores", q.Colores); preds.Add(("color", "EXISTS (SELECT 1 FROM dbo.CatalogoColor ec WHERE ec.Codigo = c.Codigo AND ec.Color IN @colores)")); }
        AgregarLocales(q.Locales, preds);
        AgregarCombo(q.ComboDetalles, p, preds);
        // Precio (por unidad de combo) y texto: siempre aplicados, no son facetas.
        if (q.PrecioMin is decimal min) { p.Add("precioMin", min); preds.Add(("precio-min", $"({UnitPublico}) >= @precioMin")); }
        if (q.PrecioMax is decimal max) { p.Add("precioMax", max); preds.Add(("precio-max", $"({UnitPublico}) <= @precioMax")); }
        if (!string.IsNullOrWhiteSpace(q.TextoNorm)) { p.Add("q", "%" + q.TextoNorm + "%"); preds.Add(("texto", "c.TextoBusqueda LIKE @q")); }

        string W(string? excepto) => Combinar(baseSql, preds, excepto);

        var orden = q.Orden switch
        {
            "precio-asc" => $"CASE WHEN ({UnitPublico}) IS NULL THEN 1 ELSE 0 END, ({UnitPublico}) ASC, c.Descripcion",
            "precio-desc" => $"CASE WHEN ({UnitPublico}) IS NULL THEN 1 ELSE 0 END, ({UnitPublico}) DESC, c.Descripcion",
            "nombre" => "c.Descripcion, c.Codigo",
            _ => "c.TieneFoto DESC, c.Codigo",   // destacados: Destacado siempre 0, así que foto primero
        };

        var pagina = Math.Max(1, q.Pagina);
        p.Add("skip", (pagina - 1) * FiltrosCatalogo.PorPagina);
        p.Add("take", FiltrosCatalogo.PorPagina);

        var sql = $"""
            SELECT {ColumnasFila} FROM dbo.Catalogo c WHERE {W(null)}
            ORDER BY {orden} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;

            SELECT COUNT(*) FROM dbo.Catalogo c WHERE {W(null)};

            SELECT Valor = c.Rubro, Etiqueta = c.Rubro, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("tipo")} AND c.Rubro IS NOT NULL AND c.Rubro <> ''
            GROUP BY c.Rubro;

            SELECT Valor = c.Prenda, Etiqueta = c.Prenda, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("familia")} AND c.Prenda IS NOT NULL AND c.Prenda <> ''
            GROUP BY c.Prenda;

            SELECT Talle = t.Talle, Cantidad = COUNT(*), Orden = MIN(t.Orden)
            FROM dbo.Catalogo c JOIN dbo.CatalogoTalle t ON t.Codigo = c.Codigo
            WHERE {W("talle")} GROUP BY t.Talle;

            SELECT Valor = cc.Color, Etiqueta = cc.Color, Cantidad = COUNT(*)
            FROM dbo.Catalogo c JOIN dbo.CatalogoColor cc ON cc.Codigo = c.Codigo
            WHERE {W("color")} GROUP BY cc.Color;

            SELECT Valor = 'luro', Etiqueta = 'LURO', Cantidad = SUM(CASE WHEN c.EnLuro = 1 THEN 1 ELSE 0 END)
            FROM dbo.Catalogo c WHERE {W("local")}
            UNION ALL
            SELECT Valor = 'peralta', Etiqueta = 'PERALTA', Cantidad = SUM(CASE WHEN c.EnPeralta = 1 THEN 1 ELSE 0 END)
            FROM dbo.Catalogo c WHERE {W("local")};

            SELECT Cantidad = c.ComboCantidad, Total = c.ComboTotal, Conteo = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("combo")} AND c.ComboCantidad > 0 AND c.ComboTotal > 0
            GROUP BY c.ComboCantidad, c.ComboTotal;
            """;

        using var cn = _db.CrearMarket();
        using var multi = await cn.QueryMultipleAsync(new CommandDefinition(sql, p, commandTimeout: 60, cancellationToken: ct));
        var items = (await multi.ReadAsync<CatalogoFilaLeida>()).ToList();
        var total = await multi.ReadSingleAsync<int>();
        var rubros = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var familias = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var talles = (await multi.ReadAsync<TalleConteo>()).ToList();
        var colores = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var locales = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var combos = (await multi.ReadAsync<ComboConteo>()).ToList();
        return new PaginaPublicaCruda(items, total, rubros, familias, talles, colores, locales, combos);
    }

    /// <inheritdoc/>
    public async Task<PaginaInternaCruda> BuscarInternoAsync(ConsultaInterna q, CancellationToken ct = default)
    {
        var p = new DynamicParameters();
        var preds = new List<(string Key, string Sql)>();

        // Ubicación (multi) y cruce depo/local: filtros directos, no facetas.
        if (q.Ubicaciones.Count > 0)
        {
            var ors = new List<string>();
            if (q.Ubicaciones.Contains("luro", StringComparer.OrdinalIgnoreCase)) ors.Add("c.EnLuro = 1");
            if (q.Ubicaciones.Contains("peralta", StringComparer.OrdinalIgnoreCase)) ors.Add("c.EnPeralta = 1");
            if (q.Ubicaciones.Contains("deposito", StringComparer.OrdinalIgnoreCase)) ors.Add("c.EnDeposito = 1");
            if (ors.Count > 0) preds.Add(("ubic", "(" + string.Join(" OR ", ors) + ")"));
        }
        var cruce = q.CruceDepoLocal switch
        {
            "deposito" => "c.EnDeposito = 1",
            "solo-deposito" => "c.EnDeposito = 1 AND c.EnLuro = 0 AND c.EnPeralta = 0",
            "deposito-luro" => "c.EnDeposito = 1 AND c.EnLuro = 1",
            "deposito-peralta" => "c.EnDeposito = 1 AND c.EnPeralta = 1",
            "en-local" => "(c.EnLuro = 1 OR c.EnPeralta = 1)",
            _ => null,
        };
        if (cruce is not null) preds.Add(("cruce", cruce));

        // Facetas: todo por VALOR (el servicio ya tradujo el slug de género a valor; rubro/prenda ya eran valor).
        if (q.RubrosValor.Count > 0) { p.Add("rubros", q.RubrosValor); preds.Add(("rubro", "c.Rubro IN @rubros")); }
        if (q.GenerosValor.Count > 0) { p.Add("generos", q.GenerosValor); preds.Add(("genero", "c.Genero IN @generos")); }
        if (q.PrendasValor.Count > 0) { p.Add("prendas", q.PrendasValor); preds.Add(("prenda", "c.Prenda IN @prendas")); }
        if (q.Proveedores.Count > 0) { p.Add("proveedores", q.Proveedores); preds.Add(("proveedor", "c.Proveedor IN @proveedores")); }
        if (q.Marcas.Count > 0) { p.Add("marcas", q.Marcas); preds.Add(("marca", "c.Marca IN @marcas")); }
        if (q.Temporadas.Count > 0) { p.Add("temporadas", q.Temporadas); preds.Add(("temporada", "c.Temporada IN @temporadas")); }
        var anios = q.Anios.Select(a => int.TryParse(a, out var n) ? (int?)n : null).Where(n => n is not null).Select(n => n!.Value).ToList();
        if (anios.Count > 0) { p.Add("anios", anios); preds.Add(("anio", "c.Anio IN @anios")); }
        AgregarCombo(q.ComboDetalles, p, preds);

        // Talle/color: filtros (no facetas en el interno).
        if (q.Talles.Count > 0) { p.Add("talles", q.Talles); preds.Add(("talle", "EXISTS (SELECT 1 FROM dbo.CatalogoTalle et WHERE et.Codigo = c.Codigo AND et.Talle IN @talles)")); }
        if (q.Colores.Count > 0) { p.Add("colores", q.Colores); preds.Add(("color", "EXISTS (SELECT 1 FROM dbo.CatalogoColor ec WHERE ec.Codigo = c.Codigo AND ec.Color IN @colores)")); }

        if (q.Publicado is bool pub) { p.Add("pub", pub ? 1 : 0); preds.Add(("pub", "c.Publicado = @pub")); }
        if (q.MargenMax is decimal mm) { p.Add("margenMax", mm); preds.Add(("margenmax", $"({MargenInterno}) IS NOT NULL AND ({MargenInterno}) <= @margenMax")); }
        // Búsqueda interna: Descripción + Código + Prenda + Proveedor + Marca, insensible a mayúsculas/acentos
        // (COLLATE _CI_AI espeja el SinAcentos en memoria; incluye proveedor/marca, que el público no busca).
        if (!string.IsNullOrWhiteSpace(q.Texto))
        {
            p.Add("q", "%" + q.Texto.Trim() + "%");
            preds.Add(("texto",
                "(ISNULL(c.Descripcion,'') + ' ' + c.Codigo + ' ' + ISNULL(c.Prenda,'') + ' ' + ISNULL(c.Proveedor,'') + ' ' + ISNULL(c.Marca,'')) " +
                "COLLATE Latin1_General_CI_AI LIKE @q COLLATE Latin1_General_CI_AI"));
        }

        string W(string? excepto) => Combinar("c.Eliminado = 0", preds, excepto);

        var orden = q.Orden switch
        {
            "precio-asc" => $"CASE WHEN ({UnitInterno}) IS NULL THEN 1 ELSE 0 END, ({UnitInterno}) ASC, c.Codigo",
            "precio-desc" => $"CASE WHEN ({UnitInterno}) IS NULL THEN 1 ELSE 0 END, ({UnitInterno}) DESC, c.Codigo",
            "margen" => $"CASE WHEN ({MargenInterno}) IS NULL THEN 1 ELSE 0 END, ({MargenInterno}) ASC, c.Codigo",
            "margen-desc" => $"CASE WHEN ({MargenInterno}) IS NULL THEN 1 ELSE 0 END, ({MargenInterno}) DESC, c.Codigo",
            "nombre" => "c.Descripcion, c.Codigo",
            _ => "c.Codigo",
        };

        var pagina = Math.Max(1, q.Pagina);
        p.Add("skip", (pagina - 1) * FiltrosInterno.PorPagina);
        p.Add("take", FiltrosInterno.PorPagina);

        var sql = $"""
            SELECT {ColumnasFila} FROM dbo.Catalogo c WHERE {W(null)}
            ORDER BY {orden} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;

            SELECT COUNT(*) FROM dbo.Catalogo c WHERE {W(null)};

            SELECT TotalUniverso = COUNT(*),
                   EnDeposito   = SUM(CASE WHEN c.EnDeposito = 1 THEN 1 ELSE 0 END),
                   SoloDeposito = SUM(CASE WHEN c.EnDeposito = 1 AND c.EnLuro = 0 AND c.EnPeralta = 0 THEN 1 ELSE 0 END),
                   Publicados   = SUM(CASE WHEN c.Publicado = 1 THEN 1 ELSE 0 END)
            FROM dbo.Catalogo c WHERE c.Eliminado = 0;

            SELECT Valor = c.Genero, Etiqueta = c.Genero, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("genero")} AND c.Genero IS NOT NULL AND c.Genero <> ''
            GROUP BY c.Genero;

            SELECT Valor = c.Rubro, Etiqueta = c.Rubro, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("rubro")} AND c.Rubro IS NOT NULL AND c.Rubro <> ''
            GROUP BY c.Rubro;

            SELECT Valor = c.Prenda, Etiqueta = c.Prenda, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("prenda")} AND c.Prenda IS NOT NULL AND c.Prenda <> ''
            GROUP BY c.Prenda;

            SELECT Valor = c.Proveedor, Etiqueta = c.Proveedor, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("proveedor")} AND c.Proveedor IS NOT NULL AND c.Proveedor <> ''
            GROUP BY c.Proveedor;

            SELECT Valor = c.Marca, Etiqueta = c.Marca, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("marca")} AND c.Marca IS NOT NULL AND c.Marca <> ''
            GROUP BY c.Marca;

            SELECT Valor = c.Temporada, Etiqueta = c.Temporada, Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("temporada")} AND c.Temporada IS NOT NULL AND c.Temporada <> ''
            GROUP BY c.Temporada;

            SELECT Valor = CAST(c.Anio AS varchar(4)), Etiqueta = CAST(c.Anio AS varchar(4)), Cantidad = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("anio")} AND c.Anio IS NOT NULL
            GROUP BY c.Anio;

            SELECT Cantidad = c.ComboCantidad, Total = c.ComboTotal, Conteo = COUNT(*)
            FROM dbo.Catalogo c WHERE {W("combo")} AND c.ComboCantidad > 0 AND c.ComboTotal > 0
            GROUP BY c.ComboCantidad, c.ComboTotal;
            """;

        using var cn = _db.CrearMarket();
        using var multi = await cn.QueryMultipleAsync(new CommandDefinition(sql, p, commandTimeout: 60, cancellationToken: ct));
        var items = (await multi.ReadAsync<CatalogoFilaLeida>()).ToList();
        var total = await multi.ReadSingleAsync<int>();
        var univ = await multi.ReadSingleAsync<UniversoRow>();
        var generos = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var rubros = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var prendas = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var proveedores = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var marcas = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var temporadas = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var anios2 = (await multi.ReadAsync<FacetaConteo>()).ToList();
        var combos = (await multi.ReadAsync<ComboConteo>()).ToList();
        return new PaginaInternaCruda(items, total, univ.TotalUniverso, univ.EnDeposito, univ.SoloDeposito,
            univ.Publicados, generos, rubros, prendas, proveedores, marcas, temporadas, anios2, combos);
    }

    // Fila de los totales del universo interno (una sola fila).
    private sealed record UniversoRow(int TotalUniverso, int EnDeposito, int SoloDeposito, int Publicados);

    // Filtro de locales del público: OR de los bits según los slugs elegidos (luro/peralta).
    private static void AgregarLocales(IReadOnlyList<string> locales, List<(string, string)> preds)
    {
        if (locales.Count == 0) return;
        var ors = new List<string>();
        if (locales.Contains("luro", StringComparer.OrdinalIgnoreCase)) ors.Add("c.EnLuro = 1");
        if (locales.Contains("peralta", StringComparer.OrdinalIgnoreCase)) ors.Add("c.EnPeralta = 1");
        if (ors.Count > 0) preds.Add(("local", "(" + string.Join(" OR ", ors) + ")"));
    }

    // Filtro de combo: OR de pares (cantidad, total) parseados de "{cantidad}-{total}".
    private static void AgregarCombo(IReadOnlyList<string> detalles, DynamicParameters p, List<(string, string)> preds)
    {
        if (detalles.Count == 0) return;
        var ors = new List<string>();
        var i = 0;
        foreach (var d in detalles)
        {
            var partes = (d ?? "").Split('-');
            if (partes.Length == 2 && int.TryParse(partes[0], out var cc) && int.TryParse(partes[1], out var tt))
            {
                p.Add($"cc{i}", cc);
                p.Add($"ct{i}", tt);
                ors.Add($"(c.ComboCantidad = @cc{i} AND c.ComboTotal = @ct{i})");
                i++;
            }
        }
        if (ors.Count > 0) preds.Add(("combo", "(" + string.Join(" OR ", ors) + ")"));
    }

    // Arma el WHERE: base + todos los predicados salvo el excluido (para contar su propia faceta).
    private static string Combinar(string baseSql, List<(string Key, string Sql)> preds, string? excepto)
    {
        var sb = new StringBuilder(baseSql);
        foreach (var (key, sql) in preds)
            if (key != excepto) sb.Append(" AND ").Append(sql);
        return sb.ToString();
    }
}
