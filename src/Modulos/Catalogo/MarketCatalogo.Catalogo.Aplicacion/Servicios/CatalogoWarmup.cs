using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Precalienta el caché al arrancar y lo refresca en segundo plano. Es uno de los guardarraíles del
/// patrón (docs/CONSULTAS.md §2.ter): sin esto, el primer visitante después de cada deploy pagaría los
/// ~300 ms del primer llenado.
///
/// Refrescar acá y no sólo por TTL en el request tiene otra ventaja: el usuario nunca espera un refresh,
/// porque cuando el TTL vence ya hay una copia nueva.
/// </summary>
public sealed class CatalogoWarmup : BackgroundService
{
    private readonly CatalogoCache _cache;
    private readonly ILogger<CatalogoWarmup> _log;

    public CatalogoWarmup(CatalogoCache cache, ILogger<CatalogoWarmup> log)
    {
        _cache = cache;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // El arranque no debe tirar la app abajo si la base no responde: el sitio institucional tiene
        // que poder servirse igual, y el catálogo se recupera en el próximo intento.
        try { await _cache.RefrescarAsync(ct); }
        catch (Exception ex) { _log.LogError(ex, "No se pudo precargar el catálogo al arrancar; se reintenta."); }

        // Refresca un poco antes de que venza, para que siempre haya copia fresca.
        var intervalo = _cache.Ttl > TimeSpan.FromMinutes(2)
            ? _cache.Ttl - TimeSpan.FromSeconds(30)
            : _cache.Ttl;

        using var timer = new PeriodicTimer(intervalo);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);
                await _cache.RefrescarAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Falló el refresh periódico del catálogo."); }
        }
    }
}
