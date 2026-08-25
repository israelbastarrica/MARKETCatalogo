using Microsoft.Data.SqlClient;

namespace MarketCatalogo.Compartido.Datos;

/// <summary>
/// Crea conexiones a las DOS bases que usa el catálogo, por separado y a propósito
/// (ver docs/CONSULTAS.md §2.bis — decisión D8c del plan).
///
/// Hoy las dos apuntan al mismo servidor SQL, así que un JOIN cruzado entre MARKET y
/// DRAGONFISH_CENTRAL <em>funcionaría</em>. Igual NO se hace: si algún día las bases se suben a la
/// nube separadas, el join cross-database deja de existir (Azure SQL Database no lo soporta) y un
/// diseño que dependa de él habría que reescribirlo entero. Con conexiones separadas y el cruce en
/// C#, esa mudanza es cambiar dos valores de configuración.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Base MARKET: mapeos, fotos y las tablas propias del catálogo.</summary>
    SqlConnection CrearMarket();

    /// <summary>Base DRAGONFISH_CENTRAL (Zoologic): artículos, categorías, precios y combinaciones.</summary>
    SqlConnection CrearDragon();

    /// <summary>Réplica DRAGONFISH_LURO (Zoologic): el Dragonfish del local Luro, replicado por CDC en el
    /// mismo servidor que MARKET. Se usa para el stock por tienda de la ficha interna. Se deriva de la misma
    /// cadena que las otras cambiando la base — sin OPENQUERY ni linked servers, para que mudar una tienda a
    /// la nube sea sólo cambiar config.</summary>
    SqlConnection CrearLuro();

    /// <summary>Réplica DRAGONFISH_PERALTA (Zoologic): ídem <see cref="CrearLuro"/> para el local Peralta.</summary>
    SqlConnection CrearPeralta();
}
