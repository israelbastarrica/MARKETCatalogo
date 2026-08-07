using MarketCatalogo.Catalogo.Aplicacion;
using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Compartido.Datos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketCatalogo.Catalogo.Datos;

/// <summary>
/// Punto de registro del módulo Catálogo. El host (Web) llama a
/// <see cref="AgregarModuloCatalogo"/> y no ve nada más: ni <c>CatalogoCache</c>, ni
/// <c>CatalogoRepositorio</c>, ni el esquema de Dragon. Sólo <see cref="ICatalogoConsulta"/> y
/// <see cref="IFotosCatalogo"/> (en Catalogo.Contratos) quedan expuestos por DI.
///
/// Vive en Catalogo.Datos porque es la única capa del módulo que referencia a las otras dos
/// (Aplicacion y, transitivamente, Contratos) y a la vez es la capa "de borde" que el host conecta.
/// </summary>
public static class ModuloCatalogo
{
    public static IServiceCollection AgregarModuloCatalogo(this IServiceCollection services)
    {
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<ICatalogoRepositorio, CatalogoRepositorio>();

        // Singleton a propósito: CatalogoCache es el catálogo entero en memoria, compartido por todos
        // los requests. Un scoped tiraría el trabajo a la basura en cada request.
        services.AddSingleton<CatalogoCache>();
        services.AddSingleton<ICatalogoConsulta, CatalogoService>();
        services.AddSingleton<IFotosCatalogo, FotosService>();

        // Precalienta al arrancar y refresca en segundo plano (guardarraíl del patrón, ver
        // docs/CONSULTAS.md §2.ter).
        services.AddHostedService<CatalogoWarmup>();

        return services;
    }
}
