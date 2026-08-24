using Dapper;
using MarketCatalogo.Auth.Aplicacion;
using MarketCatalogo.Compartido.Datos;

namespace MarketCatalogo.Auth.Datos;

/// <summary>
/// Implementa <see cref="IUsuariosAuthRepositorio"/> leyendo <c>MARKET.dbo.UsuariosPC</c> con Dapper.
/// SÓLO LECTURA: las columnas Usuario/PasswordHash/Area ya existen (las creó MARKETweb); el catálogo no
/// las modifica ni da de alta usuarios. Una persona puede tener varias filas (una por PC): se elige por
/// mismo criterio que MARKETweb (área cargada primero, después ID).
/// </summary>
public sealed class UsuariosAuthRepositorio : IUsuariosAuthRepositorio
{
    private readonly ISqlConnectionFactory _db;
    public UsuariosAuthRepositorio(ISqlConnectionFactory db) => _db = db;

    public async Task<UsuarioAuthRow?> BuscarPorMailAsync(string mail, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1 Perfil = PERFIL, Pc = PC, PasswordHash, MailAprobado, Area
            FROM MARKET.dbo.UsuariosPC WITH (NOLOCK)
            WHERE Eliminado = 0 AND Mail = @mail
            ORDER BY CASE WHEN Area IS NULL THEN 1 ELSE 0 END, ID;
            """;
        using var cn = _db.CrearMarket();
        return await cn.QuerySingleOrDefaultAsync<UsuarioAuthRow>(
            new CommandDefinition(sql, new { mail = (mail ?? "").Trim().ToLowerInvariant() }, cancellationToken: ct));
    }

    public async Task<UsuarioAuthRow?> BuscarPorUsuarioAsync(string usuario, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1 Perfil = PERFIL, Pc = PC, PasswordHash, MailAprobado, Area
            FROM MARKET.dbo.UsuariosPC WITH (NOLOCK)
            WHERE Eliminado = 0 AND Usuario = @u
            ORDER BY ID;
            """;
        using var cn = _db.CrearMarket();
        return await cn.QuerySingleOrDefaultAsync<UsuarioAuthRow>(
            new CommandDefinition(sql, new { u = (usuario ?? "").Trim() }, cancellationToken: ct));
    }
}
