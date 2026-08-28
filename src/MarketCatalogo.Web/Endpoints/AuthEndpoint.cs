using System.Security.Claims;
using MarketCatalogo.Auth.Contratos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;

namespace MarketCatalogo.Web.Endpoints;

/// <summary>
/// Endpoints del login del catálogo: <c>/auth/login</c> (Google), <c>/auth/login-local</c> (usuario+clave),
/// <c>/auth/logout</c> y <c>/auth/dev-login</c> (sólo Development). Son navegaciones/POST de página
/// completa (no fetch): así el navegador guarda la cookie de sesión sin depender de JS. La pantalla que
/// los invoca es <c>/login</c> (módulo Auth.Ui). Portado de MARKETweb (AuthController) a minimal API.
/// </summary>
public static class AuthEndpoint
{
    public static void MapAuth(this WebApplication app)
    {
        // Inicia el login con Google (challenge de página completa). Vuelve a `volver` (o /interno).
        app.MapGet("/auth/login", (string? volver, OpcionesDeIngreso opciones) =>
        {
            // Sin credenciales cargadas el esquema de Google NI SE REGISTRA, y desafiarlo revienta con 500.
            // Antes eso le pasaba a cualquiera que tocara "Continuar con Google" en un server sin configurar.
            if (!opciones.GoogleHabilitado) return Results.Redirect("/login?error=google");

            var destino = DestinoSeguro(volver);
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = destino },
                new[] { GoogleDefaults.AuthenticationScheme });
        });

        // Login LOCAL: usuario + contraseña (para quien no tiene cuenta @marketarg.com). POST de formulario.
        app.MapPost("/auth/login-local", async (HttpContext ctx, IAutenticacion auth) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var usuario = (form["usuario"].ToString() ?? "").Trim();
            var password = form["password"].ToString() ?? "";
            var volver = DestinoSeguro(form["volver"].ToString());

            var acceso = await auth.ValidarLoginLocalAsync(usuario, password);
            if (acceso is null || acceso.Estado != PoliticasAuth.EstadoOk)
                return Results.Redirect("/login?error=login");

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "local:" + usuario),
                new(ClaimTypes.Name, usuario),
                new("usuario", usuario), // lo usa ClaimsDeUsuario para resolver perfil/estado/pc
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect(volver);
        }).RequireRateLimiting(MarketCatalogo.Web.Servicios.LimiteIntentos.Politica);

        app.MapGet("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });

        // SÓLO DESARROLLO: loguea con cookie sin pasar por Google (para probar en localhost). Los claims
        // reales (perfil/estado) los resuelve ClaimsDeUsuario desde UsuariosPC.
        app.MapGet("/auth/dev-login", async (HttpContext ctx, IWebHostEnvironment env,
            string? email, string? volver) =>
        {
            if (!env.IsDevelopment()) return Results.NotFound();
            var mail = string.IsNullOrWhiteSpace(email) ? "federicopetersen@marketarg.com" : email.Trim();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, mail),
                new Claim(ClaimTypes.Name, mail),
                new Claim(ClaimTypes.Email, mail),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect(DestinoSeguro(volver));
        });
    }

    // Sólo se acepta un returnUrl LOCAL (arranca con "/" y no con "//"): evita open-redirect a un sitio externo.
    private static string DestinoSeguro(string? volver)
        => !string.IsNullOrWhiteSpace(volver) && volver.StartsWith('/') && !volver.StartsWith("//")
            ? volver : "/interno";
}
