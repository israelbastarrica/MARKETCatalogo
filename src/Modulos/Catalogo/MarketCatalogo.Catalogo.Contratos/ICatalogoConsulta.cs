namespace MarketCatalogo.Catalogo.Contratos;

/// <summary>
/// Puerto de lectura del módulo Catálogo. Es <b>la</b> forma de consultar el catálogo desde afuera:
/// ni la UI ni otros módulos ni el host tocan repositorios, caché ni SQL.
///
/// Regla del monolito modular: quien necesite el catálogo referencia este proyecto (Contratos) y pide
/// esta interfaz por DI. Nadie referencia Catalogo.Aplicacion ni Catalogo.Datos, salvo el host y sólo
/// para registrar la implementación en el arranque.
/// </summary>
public interface ICatalogoConsulta
{
    /// <summary>Foto completa del catálogo: sirve para el menú, los totales y la antigüedad de los datos.</summary>
    Task<CatalogoSnapshot> SnapshotAsync(CancellationToken ct = default);

    /// <summary>Una página de la grilla con sus facetas ya contadas.</summary>
    Task<PaginaCatalogoDto> BuscarAsync(FiltrosCatalogo filtros, CancellationToken ct = default);

    /// <summary>Un artículo por su slug. Si el slug no coincide exactamente pero el código sí, devuelve
    /// el artículo igual — quien llame debería redirigir (301) al slug canónico.</summary>
    Task<ArticuloDto?> PorSlugAsync(string? slug, CancellationToken ct = default);
}
