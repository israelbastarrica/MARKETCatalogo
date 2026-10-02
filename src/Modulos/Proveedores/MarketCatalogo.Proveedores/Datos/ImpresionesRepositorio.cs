using Dapper;
using MarketCatalogo.Compartido.Datos;
using MarketCatalogo.Proveedores.Contratos;

namespace MarketCatalogo.Proveedores.Datos;

/// <summary>
/// El registro de impresiones DEL PORTAL (dbo.CatalogoProveedorImpresion) y la impresora elegida.
/// Es lo que hace cumplir el tope por combinación: MarketWeb registra por ARTCOD, sin color ni talle,
/// así que sin esta tabla no se puede saber si ya imprimió los negro XL.
/// </summary>
public sealed class ImpresionesRepositorio
{
    private readonly ISqlConnectionFactory _db;
    public ImpresionesRepositorio(ISqlConnectionFactory db) => _db = db;

    public sealed record ImpresoRow(string ArtCod, string CodColor, string Talle, int Cantidad);

    /// <summary>Lo ya impreso por ese proveedor en esa orden, por combinación.</summary>
    public async Task<IReadOnlyList<ImpresoRow>> ImpresoAsync(string codProveedor, int nroOrden, CancellationToken ct)
    {
        const string sql = """
            SELECT ArtCod, CodColor, Talle, Cantidad = SUM(Cantidad)
            FROM dbo.CatalogoProveedorImpresion WITH (NOLOCK)
            WHERE Eliminado = 0 AND CodProveedor = @prov AND NroOrden = @nro
            GROUP BY ArtCod, CodColor, Talle;
            """;
        using var cn = _db.CrearMarket();
        var filas = await cn.QueryAsync<ImpresoRow>(
            new CommandDefinition(sql, new { prov = codProveedor, nro = nroOrden }, cancellationToken: ct));
        return filas.AsList();
    }

    /// <summary>Deja asentado lo que se generó. Se llama DESPUÉS de que MarketWeb devolvió el ZPL: si falla
    /// allá no se registra nada, y si el proveedor reintenta, cuenta de nuevo — igual que lo que ellos
    /// asientan en EtiquetasImpresas, así los dos registros dicen lo mismo.</summary>
    public async Task RegistrarAsync(string codProveedor, int nroOrden, string? usuario,
                                     IReadOnlyList<PortalItemPedido> items, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.CatalogoProveedorImpresion (CodProveedor, NroOrden, ArtCod, CodColor, Talle, Cantidad, Usuario)
            VALUES (@prov, @nro, @art, @color, @talle, @cant, @usuario);
            """;
        using var cn = _db.CrearMarket();
        foreach (var i in items)
            await cn.ExecuteAsync(new CommandDefinition(sql, new
            {
                prov = codProveedor, nro = nroOrden, art = i.ArtCod, color = i.CodColor,
                talle = i.Talle, cant = i.Cantidad, usuario
            }, cancellationToken: ct));
    }

    public async Task<PortalImpresora?> ImpresoraAsync(string codProveedor, CancellationToken ct)
    {
        const string sql = """
            SELECT TOP 1 Impresora, Lenguaje, Dpi
            FROM dbo.CatalogoProveedorConfig WITH (NOLOCK)
            WHERE CodProveedor = @prov;
            """;
        using var cn = _db.CrearMarket();
        var row = await cn.QuerySingleOrDefaultAsync<(string Impresora, string Lenguaje, int Dpi)?>(
            new CommandDefinition(sql, new { prov = codProveedor }, cancellationToken: ct));
        if (row is null) return null;
        // La etiqueta mostrable sale del catálogo de modelos; si el guardado quedó de una versión anterior
        // y ya no está en la lista, se muestra la clave cruda antes que romper la pantalla.
        var conocida = IPortalProveedores.Impresoras.FirstOrDefault(i => i.Clave == row.Value.Impresora);
        return conocida ?? new PortalImpresora(row.Value.Impresora, row.Value.Impresora, row.Value.Lenguaje, row.Value.Dpi);
    }

    public async Task GuardarImpresoraAsync(string codProveedor, PortalImpresora imp, string? usuario, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.CatalogoProveedorConfig
               SET Impresora = @clave, Lenguaje = @leng, Dpi = @dpi, Usuario = @usuario, Actualizado = GETDATE()
             WHERE CodProveedor = @prov;
            IF @@ROWCOUNT = 0
            INSERT INTO dbo.CatalogoProveedorConfig (CodProveedor, Impresora, Lenguaje, Dpi, Usuario)
            VALUES (@prov, @clave, @leng, @dpi, @usuario);
            """;
        using var cn = _db.CrearMarket();
        await cn.ExecuteAsync(new CommandDefinition(sql, new
        {
            prov = codProveedor, clave = imp.Clave, leng = imp.Lenguaje, dpi = imp.Dpi, usuario
        }, cancellationToken: ct));
    }
}
