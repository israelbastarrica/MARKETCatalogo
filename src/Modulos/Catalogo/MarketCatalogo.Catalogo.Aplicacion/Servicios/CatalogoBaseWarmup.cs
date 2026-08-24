using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Precalienta la tabla materializada <c>dbo.Catalogo</c> al arrancar: cubre el "arranque en frío" del
/// modelo read-through (sin esto, el primer visitante tras un deploy pagaría el rebuild completo).
///
/// A diferencia del viejo <c>CatalogoWarmup</c>, NO hay refresco periódico: el modelo es read-through, la
/// base se revalida on-read cuando vence el TTL (<see cref="CatalogoStore.AsegurarBaseFresca"/>). Esto es
/// sólo el disparo inicial, y como todo el resto, nunca tira la app abajo si la base no responde.
/// </summary>
public sealed class CatalogoBaseWarmup : BackgroundService
{
    private readonly CatalogoStore _store;
    private readonly ILogger<CatalogoBaseWarmup> _log;

    public CatalogoBaseWarmup(CatalogoStore store, ILogger<CatalogoBaseWarmup> log)
    {
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await _store.ReconstruirBaseAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "No se pudo precargar la base del catálogo al arrancar; se reintenta on-read.");
        }
    }
}
