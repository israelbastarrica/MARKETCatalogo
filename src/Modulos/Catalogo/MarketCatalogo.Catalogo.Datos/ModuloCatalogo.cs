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

        // Modelo tabla-como-caché (read-through), sin snapshot RAM:
        //  * CatalogoStore   reconstruye la tabla dbo.Catalogo (todo el universo) con TTL + single-flight.
        //  * LectorCatalogo  lee el subset publicado y arma el CatalogoSnapshot en cada request.
        //  * CatalogoService filtra/facetea/pagina sobre eso; FotosService resuelve la ruta desde la tabla.
        services.AddSingleton<CatalogoStore>();
        services.AddSingleton<LectorCatalogo>();
        services.AddSingleton<ICatalogoConsulta, CatalogoService>();
        services.AddSingleton<IFotosCatalogo, FotosService>();

        // Precalienta la tabla al arrancar (cold start del read-through). No hay refresh periódico: la
        // base se revalida on-read cuando vence el TTL.
        services.AddHostedService<CatalogoBaseWarmup>();

        return services;
    }
}
