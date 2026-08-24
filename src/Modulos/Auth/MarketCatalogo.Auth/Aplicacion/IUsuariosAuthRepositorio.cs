namespace MarketCatalogo.Auth.Aplicacion;

/// <summary>Lo que la lógica de auth necesita de <c>UsuariosPC</c>. La interfaz vive en la capa de
/// aplicación (carpeta) y la implementa la capa de datos (carpeta Datos): inversión de dependencias.
/// SÓLO LECTURA: el catálogo no da de alta ni aprueba usuarios (eso lo hace MARKETweb).</summary>
public interface IUsuariosAuthRepositorio
{
    /// <summary>La fila de acceso de una persona por su mail (login Google). null si no existe.</summary>
    Task<UsuarioAuthRow?> BuscarPorMailAsync(string mail, CancellationToken ct = default);

    /// <summary>La fila de acceso por nombre de usuario (login local). null si no existe.</summary>
    Task<UsuarioAuthRow?> BuscarPorUsuarioAsync(string usuario, CancellationToken ct = default);
}

/// <summary>Fila mínima de UsuariosPC para autenticar: perfil, PC, hash y si el mail está aprobado.</summary>
public sealed record UsuarioAuthRow(string? Perfil, string? Pc, string? PasswordHash, bool MailAprobado, int? Area);
