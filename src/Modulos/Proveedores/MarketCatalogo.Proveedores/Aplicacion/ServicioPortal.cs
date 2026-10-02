using MarketCatalogo.Proveedores.Contratos;
using MarketCatalogo.Proveedores.Datos;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Proveedores.Aplicacion;

/// <summary>
/// La lógica del portal: cruza lo que trae MarketWeb (órdenes y curva) con lo que ese proveedor ya
/// imprimió, y aplica el TOPE por combinación antes de pedir las etiquetas.
///
/// El tope se valida DOS veces a propósito: acá, para poder explicarle al proveedor qué se pasó en vez de
/// mandarlo a un error genérico; y en MarketWeb, que además chequea que la combinación exista en esa OP.
/// Si alguna vez discrepan, manda el de allá: es el que tiene la curva de verdad.
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
        var ordenes = crudas
            // Fuera las que no tienen NADA para imprimir: en el taller esa lista era casi toda ruido
            // (órdenes recién abiertas, sin curva todavía). Si MarketWeb no manda el dato (versión vieja),
            // CantidadCurva viene null y no se filtra nada — mejor mostrar de más que esconder una orden
            // real por un campo que no llegó.
            .Where(o => o.CantidadCurva is null || o.CantidadCurva > 0)
            .Select(o => new PortalOrden(
            o.NroOrden, o.FechaOrden, o.Estado, o.Tipo,
            (o.Renglones ?? []).Select(r => new PortalRenglon(
                r.IdRenglon, (r.ArtCod ?? "").Trim(), r.Descripcion, r.CantidadPedida,
                r.EtiquetasImpresas, r.EtiquetasImpresasPortal, r.EtiquetasEnviadas, r.FechaEnvioEtiquetas,
                Limpio(r.CodigoProveedor))).ToList(),
            o.CantidadCurva
        )).ToList();
        return new(true, ordenes);
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

        // El tope se evalúa contra la curva FRESCA, no contra lo que vino en el formulario.
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
            {
                problemas.Add($"{p.ArtCod} {p.CodColor}/{p.Talle}: no está en esta orden.");
                continue;
            }
            if (p.Cantidad > combo.Disponible)
                problemas.Add($"{p.ArtCod} {combo.Color} talle {p.Talle}: pediste {p.Cantidad} y quedan {combo.Disponible}.");
        }
        if (problemas.Count > 0) return new(false, string.Join(" ", problemas), 0, null);

        var (datos, error) = await _api.EtiquetasAsync(new
        {
            prov,
            nroOrden,
            lenguaje = impresora.Lenguaje,
            dpi = impresora.Dpi,
            usuario,
            items = pedidos.Select(p => new { artCod = p.ArtCod, codColor = p.CodColor, talle = p.Talle, cantidad = p.Cantidad })
        }, ct);

        if (datos is null || string.IsNullOrEmpty(datos.Zpl))
            return new(false, error ?? "No se pudo generar la etiqueta.", 0, null);

        // Recién cuando hay etiquetas de verdad se descuenta del tope.
        await _repo.RegistrarAsync(prov, nroOrden, usuario, pedidos, ct);
        _log.LogInformation("Portal: proveedor {Prov} imprimió {N} etiquetas de la OP {Orden}", prov, datos.Etiquetas, nroOrden);
        return new(true, null, datos.Etiquetas, datos.Zpl);
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
