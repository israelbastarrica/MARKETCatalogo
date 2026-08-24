using MarketCatalogo.Auth.Contratos;

namespace MarketCatalogo.Auth.Aplicacion;

/// <summary>
/// Implementa <see cref="IAutenticacion"/>: resuelve el acceso de una persona contra <c>UsuariosPC</c>
/// (vía <see cref="IUsuariosAuthRepositorio"/>) y verifica la contraseña del login local. Es la lógica
/// del login del catálogo, portada de MARKETweb pero SIN escrituras (no aprueba ni da de alta: el catálogo
/// sólo lee, el ABM de usuarios sigue en MARKETweb).
/// </summary>
public sealed class ServicioAutenticacion : IAutenticacion
{
    private readonly IUsuariosAuthRepositorio _repo;
    public ServicioAutenticacion(IUsuariosAuthRepositorio repo) => _repo = repo;

    public async Task<AccesoResultado?> ValidarLoginLocalAsync(string usuario, string password, CancellationToken ct = default)
    {
        var u = (usuario ?? "").Trim();
        if (u.Length == 0 || string.IsNullOrEmpty(password)) return null;
        var row = await _repo.BuscarPorUsuarioAsync(u, ct);
        // No aprobado, sin contraseña cargada o contraseña incorrecta → mismo resultado (no filtrar el motivo).
        if (row is null || !row.MailAprobado || !PasswordHasher.Verify(password, row.PasswordHash)) return null;
        return new AccesoResultado("ok", row.Perfil, row.Pc, row.Area);
    }

    public async Task<AccesoResultado> ResolverAccesoAsync(string mail, CancellationToken ct = default)
    {
        var row = await _repo.BuscarPorMailAsync((mail ?? "").Trim().ToLowerInvariant(), ct);
        if (row is null) return new AccesoResultado("onboarding", null, null);
        return row.MailAprobado
            ? new AccesoResultado("ok", row.Perfil, row.Pc, row.Area)
            : new AccesoResultado("pendiente", null, row.Pc);
    }

    public async Task<AccesoResultado> ResolverAccesoPorUsuarioAsync(string usuario, CancellationToken ct = default)
    {
        var u = (usuario ?? "").Trim();
        if (u.Length == 0) return new AccesoResultado("onboarding", null, null);
        var row = await _repo.BuscarPorUsuarioAsync(u, ct);
        if (row is null) return new AccesoResultado("onboarding", null, null);
        return row.MailAprobado
            ? new AccesoResultado("ok", row.Perfil, row.Pc, row.Area)
            : new AccesoResultado("pendiente", null, row.Pc);
    }
}
