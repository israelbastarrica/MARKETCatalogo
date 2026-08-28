using System.Security.Claims;
using MarketCatalogo.Auth.Aplicacion;
using MarketCatalogo.Auth.Contratos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketCatalogo.Auth.Datos;

/// <summary>
/// Punto de registro del módulo Auth. El host llama a <see cref="AgregarModuloAuth"/> y obtiene todo:
/// los servicios (validación + claims), y la autenticación configurada (cookie + Google restringido a
/// @marketarg.com) con la política <c>Interno</c>. El host sólo agrega el middleware
/// (<c>UseAuthentication/UseAuthorization</c>), que es responsabilidad del pipeline.
///
/// Login del catálogo, portado de MARKETweb pero adaptado a un sitio SSR: ante falta de auth se REDIRIGE
/// a <c>/login</c> (no se devuelve 401 como en la API de MARKETweb).
/// </summary>
public static class ModuloAuth
{
    public static IServiceCollection AgregarModuloAuth(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<IUsuariosAuthRepositorio, UsuariosAuthRepositorio>();
        services.AddSingleton<IAutenticacion, ServicioAutenticacion>();
        // Claims por request: scoped (dependen del usuario del request).
        services.AddScoped<IClaimsTransformation, ClaimsDeUsuario>();

        var googleClientId = cfg["Authentication:Google:ClientId"];
        var googleClientSecret = cfg["Authentication:Google:ClientSecret"];
        var googleHabilitado = !string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret);

        // Para que la pantalla de login no ofrezca un botón que no puede funcionar.
        services.AddSingleton(new OpcionesDeIngreso { GoogleHabilitado = googleHabilitado });

        var auth = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "MarketCatalogo.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                options.Cookie.MaxAge = TimeSpan.FromDays(30);
                // Sitio SSR: ante falta de auth se va a la pantalla de login (no 401).
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.ReturnUrlParameter = "volver";
            });

        // Google sólo si están las credenciales (así la app arranca sin ellas: el login local sigue andando).
        if (googleHabilitado)
        {
            auth.AddGoogle(options =>
            {
                options.ClientId = googleClientId!;
                options.ClientSecret = googleClientSecret!;

                // Restringe al Workspace (hd) y fuerza el selector de cuentas.
                options.Events.OnRedirectToAuthorizationEndpoint = context =>
                {
                    var extra = new Dictionary<string, string?>
                    {
                        ["hd"] = "marketarg.com",
                        ["prompt"] = "select_account",
                    };
                    context.Response.Redirect(QueryHelpers.AddQueryString(context.RedirectUri, extra));
                    return Task.CompletedTask;
                };

                // Validación dura del lado servidor: el mail DEBE ser @marketarg.com.
                options.Events.OnTicketReceived = context =>
                {
                    var email = context.Principal?.FindFirst(ClaimTypes.Email)?.Value;
                    if (string.IsNullOrEmpty(email) || !email.EndsWith("@marketarg.com", StringComparison.OrdinalIgnoreCase))
                    {
                        context.HandleResponse();
                        context.Response.Redirect("/login?error=dominio");
                    }
                    return Task.CompletedTask;
                };
            });
        }

        services.AddAuthorization(options =>
        {
            // Único nivel por ahora: cualquier staff aprobado ve el catálogo interno (sin distinguir perfil).
            options.AddPolicy(PoliticasAuth.Interno,
                p => p.RequireClaim(PoliticasAuth.ClaimEstado, PoliticasAuth.EstadoOk));
        });

        return services;
    }
}
