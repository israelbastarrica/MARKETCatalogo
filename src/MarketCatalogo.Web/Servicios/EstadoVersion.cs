using System.Text.Json;
using MarketCatalogo.Web.Endpoints;

namespace MarketCatalogo.Web.Servicios;

/// <summary>
/// Si la versión publicada es la última de <c>main</c> o quedó atrasada, para pintarlo en el pie del sitio.
///
/// El repo es PÚBLICO, así que se le pregunta a la API de GitHub sin credenciales (no hay ningún token en el
/// sitio público, a propósito). La respuesta se cachea 10 minutos: el límite sin autenticar es 60 consultas
/// por hora y así se usan 6.
///
/// NUNCA bloquea el render: se devuelve lo último que se sabe y el refresco va en segundo plano. Si GitHub no
/// contesta, queda en Desconocido y el pie muestra el commit sin adorno — jamás se muestra "atrasado" por no
/// haber podido consultar.
/// </summary>
public sealed class EstadoVersion
{
    public enum Situacion { Desconocido, AlDia, Atrasada }

    private const string Repo = "israelbastarrica/MARKETCatalogo";
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<EstadoVersion> _log;
    private readonly SemaphoreSlim _uno = new(1, 1);
    private DateTime _consultado = DateTime.MinValue;
    private volatile int _refrescando;

    public EstadoVersion(IWebHostEnvironment env, ILogger<EstadoVersion> log)
    {
        _env = env;
        _log = log;
    }

    /// <summary>Commit corto del build que está corriendo ("" si el build no lo pudo escribir).</summary>
    public string Sha => VersionEndpoint.Sha(_env);

    public Situacion Estado { get; private set; } = Situacion.Desconocido;

    /// <summary>Cuántos commits de main quedaron sin publicar (0 si está al día o no se sabe).</summary>
    public int Detras { get; private set; }

    /// <summary>
    /// Lo llama el pie en cada render: si el dato venció, dispara el refresco EN SEGUNDO PLANO y devuelve
    /// enseguida. El primer visitante después de arrancar ve el commit sin adorno; el siguiente ya con estado.
    /// </summary>
    public void AsegurarFresco()
    {
        if (Sha.Length == 0) return;                               // sin buildinfo no hay nada que comparar
        if (DateTime.UtcNow - _consultado < Vigencia) return;
        if (Interlocked.Exchange(ref _refrescando, 1) == 1) return; // ya hay uno en curso
        _ = Task.Run(async () =>
        {
            try { await RefrescarAsync(); }
            finally { Interlocked.Exchange(ref _refrescando, 0); }
        });
    }

    private async Task RefrescarAsync()
    {
        await _uno.WaitAsync();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("User-Agent", "MarketCatalogo");
            // compare dice de una si el commit vivo está detrás de main y por cuánto.
            var url = $"https://api.github.com/repos/{Repo}/compare/{Sha}...main";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var atras = doc.RootElement.TryGetProperty("behind_by", out var b) ? b.GetInt32() : 0;
            var adelante = doc.RootElement.TryGetProperty("ahead_by", out var a) ? a.GetInt32() : 0;
            // "ahead_by" cuenta lo que main tiene y el vivo no: eso es lo que falta publicar.
            Detras = Math.Max(atras, adelante);
            Estado = Detras > 0 ? Situacion.Atrasada : Situacion.AlDia;
            _consultado = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No se pudo comparar la version publicada contra main; queda en Desconocido.");
            // Se reintenta al proximo render vencido; el estado anterior se mantiene.
            _consultado = DateTime.UtcNow.Subtract(Vigencia).AddMinutes(2);   // no machacar: reintento en 2'
        }
        finally { _uno.Release(); }
    }
}
