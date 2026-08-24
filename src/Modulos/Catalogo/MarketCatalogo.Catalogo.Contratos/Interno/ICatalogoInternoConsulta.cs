namespace MarketCatalogo.Catalogo.Contratos.Interno;

/// <summary>
/// Puerto de lectura del catálogo INTERNO (staff). Separado de <see cref="ICatalogoConsulta"/> a
/// propósito: DTOs y datos distintos (el interno trae costo/margen/depósito). Que sean tipos distintos es
/// una barrera de compilación — el público no puede devolver datos internos por accidente.
///
/// Lo consumen sólo las páginas gateadas por la política "Interno". El universo es TODO lo mapeado
/// (incluido depósito y lo no publicado), no el subset del público.
/// </summary>
public interface ICatalogoInternoConsulta
{
    /// <summary>Una página de la grilla interna con sus facetas y totales del universo.</summary>
    Task<PaginaInternaDto> BuscarAsync(FiltrosInterno filtros, CancellationToken ct = default);

    /// <summary>Un artículo interno por su código (para la ficha). null si no está en el universo.</summary>
    Task<ArticuloInternoDto?> PorCodigoAsync(string? codigo, CancellationToken ct = default);
}
