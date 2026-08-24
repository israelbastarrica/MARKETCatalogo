namespace MarketCatalogo.Auth.Contratos;

/// <summary>Resultado de resolver el acceso de una persona contra <c>UsuariosPC</c>.
/// <para><b>Estado</b>: <c>"ok"</c> (aprobado, es staff, entra al interno), <c>"pendiente"</c> (existe pero
/// sin aprobar) u <c>"onboarding"</c> (no hay fila para ese mail/usuario).</para>
/// Perfil/Pc/Area sólo son confiables cuando Estado = "ok".</summary>
public sealed record AccesoResultado(string Estado, string? Perfil, string? Pc, int? Area = null);

/// <summary>La superficie pública del módulo Auth (lo único que el host referencia además de
/// <see cref="PoliticasAuth"/>). Valida credenciales y resuelve el acceso contra <c>UsuariosPC</c>.</summary>
public interface IAutenticacion
{
    /// <summary>Login LOCAL (usuario + contraseña, para quien no tiene cuenta @marketarg.com). Devuelve el
    /// acceso "ok" sólo si el usuario existe, está aprobado y la contraseña verifica; si no, null.</summary>
    Task<AccesoResultado?> ValidarLoginLocalAsync(string usuario, string password, CancellationToken ct = default);

    /// <summary>Resuelve el acceso por mail (login Google).</summary>
    Task<AccesoResultado> ResolverAccesoAsync(string mail, CancellationToken ct = default);

    /// <summary>Resuelve el acceso por nombre de usuario (login local, para la ClaimsTransformation).</summary>
    Task<AccesoResultado> ResolverAccesoPorUsuarioAsync(string usuario, CancellationToken ct = default);
}

/// <summary>Autorización del catálogo. <b>Por ahora hay un solo nivel</b>: público (anónimo) ve lo
/// público; cualquier staff logueado y aprobado ve TODO el interno, sin distinguir perfil (diseño, admin,
/// logística, etc. ven lo mismo). Cuando haga falta diferenciar por área se agregan más políticas acá.</summary>
public static class PoliticasAuth
{
    /// <summary>Única política: ver el catálogo interno = estar logueado y aprobado (estado = ok).</summary>
    public const string Interno = "Interno";

    public const string ClaimEstado = "estado";
    public const string ClaimPerfil = "perfil";
    public const string ClaimPc = "pc";
    public const string ClaimArea = "area";
    public const string EstadoOk = "ok";
}
