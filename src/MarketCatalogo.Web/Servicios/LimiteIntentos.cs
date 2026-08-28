using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MarketCatalogo.Web.Servicios;

/// <summary>
/// Límite de intentos del login. El sitio es PÚBLICO y accesible desde internet, así que el formulario de
/// usuario+clave es un blanco para probar contraseñas de a miles; sin esto, nada lo frena.
///
/// Dos ventanas, por IP:
///  - <b>Rápida</b>: 5 intentos por minuto. Corta el goteo automático sin molestar a una persona que se
///    equivocó dos veces.
///  - <b>Lenta</b>: 30 por hora. Corta al que espera entre intento e intento para esquivar la primera.
///
/// Solo aplica al login LOCAL: el de Google lo resuelve Google, y limitar ahí solo molestaría al staff.
/// Al pasarse devuelve 429 y la pantalla lo explica; no se cuenta el login exitoso porque el ciclo normal
/// (entrar, equivocarse, entrar bien) no tiene por qué gastar cupo.
/// </summary>
public static class LimiteIntentos
{
    public const string Politica = "login";

    public static IServiceCollection AgregarLimiteDeIntentos(this IServiceCollection services)
        => services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.AddPolicy(Politica, ctx => RateLimitPartition.GetFixedWindowLimiter(
                // Detrás de Caddy la IP real llega por X-Forwarded-For; UseForwardedHeaders ya la puso en
                // RemoteIpAddress cuando esto corre. Sin IP (caso raro) todos caen en la misma partición:
                // preferimos limitar de más que dejar un agujero.
                partitionKey: "rapida|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "sin-ip"),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            // La ventana lenta va como limitador global encadenado sobre el mismo endpoint.
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                {
                    if (!EsLoginLocal(ctx)) return RateLimitPartition.GetNoLimiter("libre");
                    return RateLimitPartition.GetFixedWindowLimiter(
                        "lenta|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "sin-ip"),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 30,
                            Window = TimeSpan.FromHours(1),
                            QueueLimit = 0,
                        });
                }));

            // Al rechazar, se vuelve al login con el motivo (es una navegación de página, no un fetch).
            o.OnRejected = async (contexto, ct) =>
            {
                if (!contexto.HttpContext.Response.HasStarted)
                {
                    contexto.HttpContext.Response.Redirect("/login?error=intentos");
                    contexto.HttpContext.Response.StatusCode = StatusCodes.Status302Found;
                }
                await Task.CompletedTask;
            };
        });

    private static bool EsLoginLocal(HttpContext ctx)
        => HttpMethods.IsPost(ctx.Request.Method)
           && ctx.Request.Path.StartsWithSegments("/auth/login-local", StringComparison.OrdinalIgnoreCase);
}
