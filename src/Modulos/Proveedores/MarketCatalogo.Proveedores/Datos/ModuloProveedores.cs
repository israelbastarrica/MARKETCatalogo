using MarketCatalogo.Proveedores.Aplicacion;
using MarketCatalogo.Proveedores.Contratos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketCatalogo.Proveedores.Datos;

/// <summary>Registro del módulo. Una línea en el host, como el resto.</summary>
public static class ModuloProveedores
{
    public static IServiceCollection AgregarModuloProveedores(this IServiceCollection services, IConfiguration cfg)
    {
        // MarketWeb vive en la MISMA máquina y escucha solo en loopback; la clave es la protección real.
        var baseUrl = (cfg["Portal:MarketWebUrl"] ?? "http://127.0.0.1:8000").TrimEnd('/') + "/";
        var clave = cfg["Portal:ServiceKey"] ?? "";

        services.AddHttpClient<ClienteMarketWeb>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            c.Timeout = TimeSpan.FromSeconds(30);   // generar miles de etiquetas no es instantáneo
            if (!string.IsNullOrWhiteSpace(clave)) c.DefaultRequestHeaders.Add("X-Service-Key", clave);
        });

        services.AddScoped<ImpresionesRepositorio>();
        services.AddScoped<IPortalProveedores, ServicioPortal>();
        return services;
    }
}
