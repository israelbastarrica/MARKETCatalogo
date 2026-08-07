using MarketCatalogo.Catalogo.Datos;
using MarketCatalogo.Web.Components;
using MarketCatalogo.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();

// Cada módulo se registra con UNA línea. El host no ve nada de adentro de Catalogo.Datos ni
// Catalogo.Aplicacion más allá de esta llamada — sólo lo que Catalogo.Contratos expone.
builder.Services.AgregarModuloCatalogo();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

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
        typeof(MarketCatalogo.Institucional.Ui.Paginas.Nosotros).Assembly);

// Thumbnails: /fotos/{codigo}_{ancho}.webp, generados bajo demanda.
app.MapFotos();

// robots.txt y sitemap.xml, armados en vivo desde el caché.
app.MapSeo();

app.Run();
