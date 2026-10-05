using MarketCatalogo.Proveedores.Contratos;
using MarketCatalogo.Proveedores.Datos;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Proveedores.Aplicacion;

/// <summary>
/// La lógica del portal: cruza lo que trae MarketWeb (órdenes y curva) con lo que ese proveedor ya
/// imprimió, y pide las etiquetas.
///
/// SIN TOPE (decisión de Israel, 05/10/2026): el proveedor imprime las que le falten, sin límite ni control
/// a la vista. Lo que imprime se REGISTRA, y eso lo revisamos nosotros internamente. Lo único que se valida
/// es que la combinación exista en la OP: una etiqueta de un color o talle que la orden no tiene no es un
/// límite, es una etiqueta que no sirve.
/// </summary>
public sealed class ServicioPortal : IPortalProveedores
{
    private readonly ClienteMarketWeb _api;
    private readonly ImpresionesRepositorio _repo;
    private readonly ILogger<ServicioPortal> _log;

    public ServicioPortal(ClienteMarketWeb api, ImpresionesRepositorio repo, ILogger<ServicioPortal> log)
    {
        _api = api;
        _repo = repo;
        _log = log;
    }

    public async Task<PortalListaOrdenes> OrdenesAsync(string codProveedor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codProveedor)) return new(true, []);
        var crudas = await _api.OrdenesAsync(codProveedor.Trim(), ct);
        if (crudas is null) return new(false, []);   // no pudimos preguntar; no es que no tenga órdenes
        // Fuera las que no tienen NADA para imprimir: en el taller esa lista era casi toda ruido (órdenes
        // recién abiertas, sin curva todavía). Si MarketWeb no manda el dato (versión vieja), CantidadCurva
        // viene null y no se filtra nada — mejor mostrar de más que esconder una orden real por un campo
        // que no llegó. Se cuenta cuántas se escondieron: si del otro lado falla el cálculo vuelven TODAS
        // en cero, y sin este número la pantalla diría "no tenés órdenes" a alguien que tiene 109.
        //
        // Y fuera también los renglones sin ARTCOD: sin nuestro código no hay etiqueta posible (el código de
        // barras ES el ARTCOD), así que una OP que sólo tenga renglones así tampoco se lista.
        var conCurva = crudas
            .Where(o => o.CantidadCurva is null || o.CantidadCurva > 0)
            .Select(o => o with { Renglones = (o.Renglones ?? []).Where(r => !string.IsNullOrWhiteSpace(r.ArtCod)).ToList() })
            .Where(o => o.Renglones!.Count > 0)
            .ToList();
        var escondidas = crudas.Count - conCurva.Count;

        var ordenes = conCurva
            .Select(o => new PortalOrden(
            o.NroOrden, o.FechaOrden, o.Estado, o.Tipo,
            (o.Renglones ?? []).Select(r => new PortalRenglon(
                r.IdRenglon, (r.ArtCod ?? "").Trim(), r.Descripcion, r.CantidadPedida,
                r.EtiquetasImpresas, r.EtiquetasImpresasPortal, r.EtiquetasEnviadas, r.FechaEnvioEtiquetas,
                Limpio(r.CodigoProveedor))).ToList(),
            o.CantidadCurva
        )).ToList();
        return new(true, ordenes, escondidas);
    }

    public async Task<PortalCurva?> CurvaAsync(string codProveedor, int nroOrden, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codProveedor)) return null;
        var prov = codProveedor.Trim();
        var curva = await _api.CurvaAsync(prov, nroOrden, ct);
        if (curva is null) return null;                       // 404: es de otro proveedor, o no existe

        var impreso = await _repo.ImpresoAsync(prov, nroOrden, ct);
        var yaImpreso = impreso.ToDictionary(i => Clave(i.ArtCod, i.CodColor, i.Talle), i => i.Cantidad);

        var articulos = (curva.Articulos ?? []).Select(a =>
        {
            var art = (a.ArtCod ?? "").Trim();
            var combos = (a.Detalle ?? []).Select(d =>
            {
                var color = (d.CodColor ?? "").Trim();
                var talle = (d.Talle ?? "").Trim();
                yaImpreso.TryGetValue(Clave(art, color, talle), out var hechas);
                return new PortalCombinacion(color, (d.Color ?? "").Trim(), talle, d.Cantidad, hechas);
            }).ToList();
            return new PortalArticulo(art, a.Descripcion, combos, Limpio(a.CodigoProveedor));
        }).ToList();

        return new PortalCurva(curva.NroOrden, curva.PorPack, articulos);
    }

    // Vacío y null son lo mismo acá: el campo se carga a mano y a veces queda en blanco.
    private static string? Limpio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task<IReadOnlySet<string>> ConFotoAsync(IReadOnlyCollection<string> artCods, CancellationToken ct = default)
        => _repo.ConFotoAsync(artCods, ct);

    // Clave de combinación: sin distinguir mayúsculas y con trim. El talle viaja tal cual la fuente y el
    // color con dos dígitos, pero un espacio de más no puede dejar el tope sin descontar.
    private static string Clave(string art, string color, string talle)
        => art.Trim().ToUpperInvariant() + "|" + color.Trim().ToUpperInvariant() + "|" + talle.Trim().ToUpperInvariant();

    public async Task<PortalImpresionResultado> ImprimirAsync(string codProveedor, string usuario, int nroOrden,
        IReadOnlyList<PortalItemPedido> items, CancellationToken ct = default)
    {
        var prov = (codProveedor ?? "").Trim();
        if (prov.Length == 0) return new(false, "No se pudo identificar el proveedor.", 0, null);

        var pedidos = items.Where(i => i.Cantidad > 0).ToList();
        if (pedidos.Count == 0) return new(false, "No cargaste ninguna cantidad.", 0, null);

        // Con qué imprime: sin impresora elegida no se puede armar el ZPL (el DPI cambia las coordenadas).
        var impresora = await ImpresoraAsync(prov, ct);
        if (impresora is null) return new(false, "Antes elegí tu impresora.", 0, null);

        // Las combinaciones se validan contra la curva FRESCA, no contra lo que vino en el formulario.
        var curva = await CurvaAsync(prov, nroOrden, ct);
        if (curva is null) return new(false, "Esa orden ya no está disponible.", 0, null);

        var problemas = new List<string>();
        foreach (var p in pedidos)
        {
            var combo = curva.Articulos
                .FirstOrDefault(a => string.Equals(a.ArtCod, p.ArtCod, StringComparison.OrdinalIgnoreCase))?
                .Combinaciones.FirstOrDefault(c =>
                    string.Equals(c.CodColor, p.CodColor, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Talle, p.Talle, StringComparison.OrdinalIgnoreCase));

            if (combo is null)
                problemas.Add($"{p.ArtCod} {p.CodColor}/{p.Talle}: no está en esta orden.");
        }
        if (problemas.Count > 0) return new(false, string.Join(" ", problemas), 0, null);

        // MarketWeb acepta hasta 3000 etiquetas por pedido: es protección del server (cada etiqueta lleva el
        // gráfico inline y un ZPL gigante lo traba), no un límite para el proveedor. Así que si pide más, se
        // parte en tandas, se piden de a una y se juntan en UN archivo: él nunca se entera del corte.
        var zpl = new System.Text.StringBuilder();
        var etiquetas = 0;
        foreach (var tanda in Tandas(pedidos, MaxPorPedidoMarketWeb))
        {
            var (datos, error) = await _api.EtiquetasAsync(new
            {
                prov,
                nroOrden,
                lenguaje = impresora.Lenguaje,
                dpi = impresora.Dpi,
                usuario,
                items = tanda.Select(p => new { artCod = p.ArtCod, codColor = p.CodColor, talle = p.Talle, cantidad = p.Cantidad })
            }, ct);

            // Si una tanda falla no se entrega un archivo a medias: le faltarían etiquetas sin que lo sepa.
            // (Las tandas anteriores ya quedaron asentadas en EtiquetasImpresas del lado de MarketWeb; acá no
            // se registra nada, así que si reintenta, cuenta una sola vez de este lado.)
            if (datos is null || string.IsNullOrEmpty(datos.Zpl))
                return new(false, error ?? "No se pudo generar la etiqueta.", 0, null);

            zpl.Append(datos.Zpl);
            etiquetas += datos.Etiquetas;
        }

        // Recién cuando hay etiquetas de verdad se registra: es lo que después revisamos internamente.
        await _repo.RegistrarAsync(prov, nroOrden, usuario, pedidos, ct);
        _log.LogInformation("Portal: proveedor {Prov} imprimió {N} etiquetas de la OP {Orden}", prov, etiquetas, nroOrden);
        return new(true, null, etiquetas, zpl.ToString());
    }

    // El tope técnico por pedido de MarketWeb (MaxEtiquetasPorPedido en ProveedorPortalService).
    private const int MaxPorPedidoMarketWeb = 3000;

    /// <summary>Parte el pedido en tandas de hasta <paramref name="max"/> etiquetas. Un ítem que solo ya pasa
    /// el máximo se divide entre tandas: lo que importa es el total por llamada, no cuántos ítems lleva.</summary>
    private static IEnumerable<List<PortalItemPedido>> Tandas(IEnumerable<PortalItemPedido> items, int max)
    {
        var tanda = new List<PortalItemPedido>();
        var enTanda = 0;
        foreach (var item in items)
        {
            var resto = item.Cantidad;
            while (resto > 0)
            {
                var entra = Math.Min(resto, max - enTanda);
                tanda.Add(item with { Cantidad = entra });
                enTanda += entra;
                resto -= entra;
                if (enTanda == max)
                {
                    yield return tanda;
                    tanda = new List<PortalItemPedido>();
                    enTanda = 0;
                }
            }
        }
        if (tanda.Count > 0) yield return tanda;
    }

    public Task<PortalImpresora?> ImpresoraAsync(string codProveedor, CancellationToken ct = default)
        => _repo.ImpresoraAsync((codProveedor ?? "").Trim(), ct);

    public Task GuardarImpresoraAsync(string codProveedor, string clave, string usuario, CancellationToken ct = default)
    {
        var elegida = IPortalProveedores.Impresoras.FirstOrDefault(i => i.Clave == clave)
                      ?? throw new ArgumentException("Modelo de impresora desconocido", nameof(clave));
        return _repo.GuardarImpresoraAsync((codProveedor ?? "").Trim(), elegida, usuario, ct);
    }
}
