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

    /// <summary>El menú del universo INTERNO (rubro → géneros con conteos), para el header cuando hay un
    /// staff logueado: a diferencia del público (sólo Indumentaria publicada), acá están TODOS los rubros
    /// (Accesorios, Lencería, Calzado…). El header lo invierte a Género → Tipos con MenuSecciones.</summary>
    Task<IReadOnlyList<RubroMenu>> MenuAsync(CancellationToken ct = default);

    /// <summary>Un artículo interno por su código (para la ficha). null si no está en el universo.</summary>
    Task<ArticuloInternoDto?> PorCodigoAsync(string? codigo, CancellationToken ct = default);

    /// <summary>Oculta o muestra un artículo del catálogo PÚBLICO (la única escritura de la app). Escribe
    /// el override <c>OcultarManual</c> y refleja el cambio al instante. <paramref name="origen"/> = quién
    /// lo hizo (para la auditoría).</summary>
    Task CambiarVisibilidadAsync(string codigo, bool ocultar, string origen, CancellationToken ct = default);

    /// <summary>Fuerza la reconstrucción de la base ahora (botón "Actualizar"). Espera a que termine
    /// (single-flight: si ya hay una en curso, espera esa).</summary>
    Task RefrescarAsync(CancellationToken ct = default);
}
