using MarketCatalogo.Auth.Datos;
using MarketCatalogo.Catalogo.Datos;
using MarketCatalogo.Web.Components;
using MarketCatalogo.Web.Endpoints;
using MarketCatalogo.Web.Servicios;

var builder = WebApplication.CreateBuilder(args);

// El sitio corre como SERVICIO DE WINDOWS en el server (el panel MARKETServicios lo controla por nombre).
// Sin esto, un servicio creado con `sc.exe` no recibe el aviso de "arrancó bien" y Windows marca el arranque
// como fallido aunque el proceso quede vivo — y no haría falta NSSM de intermediario. Cuando se corre a mano
// (dotnet run, consola) esta línea no hace nada: detecta que no hay Service Control Manager detrás.
builder.Host.UseWindowsService();

// Detrás de Caddy la app solo escucha en 127.0.0.1 y ve HTTP: sin esto arma las URLs (y el redirect_uri
// de Google) en http://127.0.0.1 y el login falla con redirect_uri_mismatch. Se confía en Caddy, que es
// el único que le habla. Mismo criterio que MarketWeb.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                       | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                       | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorComponents();

// Estado de autenticación en cascada para SSR: alimenta CascadingAuthenticationState/AuthorizeView desde
// el HttpContext.User del request (lo llena la cookie + ClaimsDeUsuario).
builder.Services.AddCascadingAuthenticationState();

// Estado de la versión publicada (al día / atrasada respecto de main), para el pie del sitio.
builder.Services.AddSingleton<MarketCatalogo.Web.Servicios.EstadoVersion>();

// Cada módulo se registra con UNA línea. El host no ve nada de adentro de Catalogo.Datos ni
// Catalogo.Aplicacion más allá de esta llamada — sólo lo que Catalogo.Contratos expone.
builder.Services.AgregarModuloCatalogo();

// Módulo Auth: login (cookie + Google @marketarg.com) + política "Interno". El host sólo agrega el
// middleware más abajo. El público no necesita cuenta; esto habilita la vista interna del staff.
builder.Services.AgregarModuloAuth(builder.Configuration);

// Límite de intentos del login (sitio público expuesto a internet).
builder.Services.AgregarLimiteDeIntentos();   // MarketCatalogo.Web.Servicios

var app = builder.Build();

// PRIMERO en el pipeline: aplica X-Forwarded-* antes de auth y de cualquier redirección.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Autenticación/autorización ANTES de antiforgery y del ruteo de componentes: así los [Authorize] de
// las páginas internas y los AuthorizeView ven la identidad del request.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();

// AddAdditionalAssemblies es lo que hace que las páginas @page de cada módulo (que viven en sus
// propios Razor Class Library) se registren como endpoints HTTP reales. El AdditionalAssemblies de
// <Router> en Routes.razor es OTRA cosa: sólo afecta el routing del lado cliente en modo
// interactivo, no el SSR estático que usa este sitio — hace falta declarar los ensamblados en los
// DOS lugares.
app.MapRazorComponents<App>()
   .AddAdditionalAssemblies(
        typeof(MarketCatalogo.Catalogo.Ui.UrlCatalogo).Assembly,
        typeof(MarketCatalogo.Auth.Ui.Paginas.Login).Assembly,
        typeof(MarketCatalogo.Institucional.Ui.Paginas.Nosotros).Assembly);

// Endpoints de login (Google / usuario+clave / logout / dev-login).
app.MapAuth();

// Acciones del catálogo interno (ocultar/mostrar del público). Gateado por la política "Interno".
app.MapInterno();

// Thumbnails: /fotos/{codigo}_{ancho}.webp, generados bajo demanda.
app.MapFotos();

// robots.txt y sitemap.xml, armados en vivo desde el caché.
app.MapSeo();

// /version: de qué commit salió el build que está corriendo. Lo usa el deploy para verificar.
app.MapVersion();

app.Run();
