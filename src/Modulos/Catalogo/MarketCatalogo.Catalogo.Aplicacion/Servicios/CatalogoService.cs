using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Compartido;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Implementa <see cref="ICatalogoConsulta"/>. La grilla (filtrar/contar/ordenar/paginar) la resuelve
/// <b>en SQL</b> <see cref="LectorCatalogo.BuscarAsync"/>: se dejó de traer todo a memoria. Este servicio
/// es una fachada fina — delega en el lector, que habla con el repo y el store.
/// </summary>
public sealed class CatalogoService : ICatalogoConsulta
{
    private readonly LectorCatalogo _lector;
    public CatalogoService(LectorCatalogo lector) => _lector = lector;

    public Task<CatalogoSnapshot> SnapshotAsync(CancellationToken ct = default) => _lector.LeerAsync(ct);

    public async Task<ArticuloDto?> PorSlugAsync(string? slug, CancellationToken ct = default)
    {
        var snap = await _lector.LeerAsync(ct);
        if (string.IsNullOrWhiteSpace(slug)) return null;
        if (snap.PorSlug.TryGetValue(slug.Trim(), out var art)) return art;

        // El slug cambió (alguien editó el título) pero el link viejo sigue circulando: se resuelve por
        // el código, que va al final del slug. Quien llame hace 301 al slug canónico.
        var cola = slug.Trim().Split('-').LastOrDefault(s => s.Length > 0);
        if (cola is null) return null;
        return snap.Articulos.FirstOrDefault(a => Texto.Slug(a.ArtCod).EndsWith(cola, StringComparison.OrdinalIgnoreCase));
    }

    // Grilla resuelta EN SQL (WHERE/OFFSET-FETCH/GROUP BY): el lector traduce slugs, pide la página +
    // facetas al repo y arma el DTO. Ya no filtra en memoria sobre el snapshot completo.
    public Task<PaginaCatalogoDto> BuscarAsync(FiltrosCatalogo f, CancellationToken ct = default)
        => _lector.BuscarAsync(f, ct);
}
