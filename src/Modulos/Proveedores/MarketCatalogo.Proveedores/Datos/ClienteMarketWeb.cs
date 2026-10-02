using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Proveedores.Datos;

/// <summary>
/// Cliente de los endpoints del portal en MarketWeb (<c>/api/proveedor/*</c>). Server-to-server por
/// <c>127.0.0.1:8000</c> —los dos servicios viven en la misma máquina— con el header <c>X-Service-Key</c>.
///
/// Por qué no leemos la base directamente, teniendo acceso: la curva de una OP sale de los packs si existen
/// y si no del presupuesto de Dragon, y la etiqueta la arma EtiquetasService con un layout que se ajustó
/// muchas veces. Copiar esa lógica acá se desfasa sola y el día que no coincida, el escaneo en depósito
/// falla. MarketWeb es el dueño; el portal consume.
/// </summary>
public sealed class ClienteMarketWeb
{
    private readonly HttpClient _http;
    private readonly ILogger<ClienteMarketWeb> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ClienteMarketWeb(HttpClient http, ILogger<ClienteMarketWeb> log)
    {
        _http = http;
        _log = log;
    }

    // ---- Respuestas crudas de MarketWeb (camelCase; los null NO vienen en el JSON) ----
    // CodigoProveedor: el código del proveedor para esa prenda. Si MarketWeb todavía no lo manda, queda
    // null y la pantalla simplemente no lo muestra — no hace falta deployar los dos a la vez.
    public sealed record RenglonDto(int IdRenglon, string? ArtCod, string? Descripcion, decimal? CantidadPedida,
        int EtiquetasImpresas, int EtiquetasImpresasPortal, int? EtiquetasEnviadas, DateTime? FechaEnvioEtiquetas,
        string? CodigoProveedor);
    public sealed record OrdenDto(int NroOrden, DateTime? FechaOrden, string? Estado, string? Tipo,
        List<RenglonDto>? Renglones, int? CantidadCurva);
    public sealed record DetalleDto(string? CodColor, string? Color, string? Talle, int Cantidad);
    public sealed record ArticuloDto(string? ArtCod, string? Descripcion, int Cantidad, List<DetalleDto>? Detalle,
        string? CodigoProveedor);
    public sealed record CurvaDto(int NroOrden, bool PorPack, List<ArticuloDto>? Articulos);
    public sealed record EtiquetasDto(string? Lenguaje, int Dpi, int Etiquetas, string? Zpl);

    public async Task<List<OrdenDto>?> OrdenesAsync(string prov, CancellationToken ct)
        => await LeerAsync<List<OrdenDto>>($"api/proveedor/ordenes?prov={Uri.EscapeDataString(prov)}", ct);

    public async Task<CurvaDto?> CurvaAsync(string prov, int nroOrden, CancellationToken ct)
        => await LeerAsync<CurvaDto>($"api/proveedor/orden/{nroOrden}/articulos?prov={Uri.EscapeDataString(prov)}", ct);

    /// <summary>Genera las etiquetas. Devuelve (resultado, mensaje de error): MarketWeb responde 400 con
    /// TODOS los motivos juntos, y ese texto es el que le sirve ver al proveedor.</summary>
    public async Task<(EtiquetasDto? Datos, string? Error)> EtiquetasAsync(object body, CancellationToken ct)
    {
        try
        {
            var r = await _http.PostAsJsonAsync("api/proveedor/etiquetas", body, Json, ct);
            if (r.IsSuccessStatusCode)
                return (await r.Content.ReadFromJsonAsync<EtiquetasDto>(Json, ct), null);

            var crudo = await r.Content.ReadAsStringAsync(ct);
            _log.LogWarning("MarketWeb rechazo la impresion ({Codigo}): {Cuerpo}", (int)r.StatusCode, crudo);
            return (null, MensajeDeError(r.StatusCode, crudo));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "No se pudo pedir las etiquetas a MarketWeb");
            return (null, "No se pudo generar la etiqueta ahora. Probá de nuevo en un minuto.");
        }
    }

    private async Task<T?> LeerAsync<T>(string ruta, CancellationToken ct) where T : class
    {
        try
        {
            var r = await _http.GetAsync(ruta, ct);
            if (r.StatusCode == HttpStatusCode.NotFound) return null;   // OP ajena o inexistente: no existe, y punto
            if (!r.IsSuccessStatusCode)
            {
                _log.LogWarning("MarketWeb respondio {Codigo} en {Ruta}", (int)r.StatusCode, ruta);
                return null;
            }
            return await r.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "No se pudo consultar {Ruta} en MarketWeb", ruta);
            return null;
        }
    }

    // El 400 trae { mensaje } con el detalle: eso SÍ se le muestra al proveedor (le dice qué corregir).
    // El 401/503 son problemas NUESTROS de configuración: no se le explican al de afuera.
    private static string MensajeDeError(HttpStatusCode codigo, string cuerpo)
    {
        if (codigo == HttpStatusCode.BadRequest)
        {
            try
            {
                using var doc = JsonDocument.Parse(cuerpo);
                if (doc.RootElement.TryGetProperty("mensaje", out var m) && m.GetString() is { Length: > 0 } txt)
                    return txt;
            }
            catch { }
            return "La selección no es válida. Revisá las cantidades.";
        }
        if (codigo == HttpStatusCode.NotFound) return "Esa orden ya no está disponible.";
        return "El servicio de etiquetas no está disponible. Avisale a MARKET.";
    }
}
