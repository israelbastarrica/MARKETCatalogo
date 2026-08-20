using System.Diagnostics;
using MarketCatalogo.Catalogo.Contratos;
using MarketCatalogo.Compartido;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarketCatalogo.Catalogo.Aplicacion;

/// <summary>
/// Mantiene TODO el catálogo en memoria y lo refresca cada pocos minutos. El catálogo son ~981 artículos
/// y ~14.225 variantes: unos 2 MB. Así el sitio hace <b>una consulta cada 5 minutos</b> en vez de una
/// por request, y filtrar/ordenar/paginar pasa a ser LINQ en memoria (microsegundos).
///
/// Es un patrón estándar (<i>cached read model</i>), no un atajo — el razonamiento y los cinco
/// guardarraíles que lo hacen correcto están en docs/CONSULTAS.md §2.ter. De esos guardarraíles, acá
/// viven tres: se precalienta al arranque, se loguea cada corrida, y si un refresh falla se sigue
/// sirviendo la copia anterior en vez de dejar el sitio sin catálogo.
/// </summary>
public sealed class CatalogoCache
{
    // Depende de la INTERFAZ, no de Catalogo.Datos: Aplicacion no referencia a Datos (Datos -> Aplicacion,
    // nunca al revés). El registro de qué implementación usar es responsabilidad de Catalogo.Datos.
    private readonly ICatalogoRepositorio _repo;
    private readonly ILogger<CatalogoCache> _log;
    private readonly TimeSpan _ttl;
    // Ruta opcional (SOLO local) donde volcar el detalle de qué se publicó y qué se descartó, para
    // depurar el catálogo. Si está vacía no se escribe nada. Se setea en appsettings.Development.json.
    private readonly string? _diagPath;
    // Mismo override de carpeta que usa FotosService: para que el diagnóstico chequee la existencia del
    // archivo en la MISMA ruta que resolvería el endpoint de fotos. Vacío = ruta de la DB tal cual.
    private readonly string? _dirOriginales;

    private readonly SemaphoreSlim _candado = new(1, 1);
    private volatile CatalogoSnapshot _actual = CatalogoSnapshot.Vacio();

    public CatalogoCache(ICatalogoRepositorio repo, ILogger<CatalogoCache> log, IConfiguration cfg)
    {
        _repo = repo;
        _log = log;
        // Sin GetValue<T> para no arrastrar Configuration.Binder sólo por un entero.
        var minutos = int.TryParse(cfg["Catalogo:MinutosCache"], out var m) ? m : 5;
        _ttl = TimeSpan.FromMinutes(Math.Clamp(minutos, 1, 120));
        var diag = cfg["Catalogo:DiagnosticoPath"];
        _diagPath = string.IsNullOrWhiteSpace(diag) ? null : diag.Trim();
        _dirOriginales = cfg["Fotos:DirOriginales"];
    }

    public CatalogoSnapshot Actual => _actual;
    public TimeSpan Ttl => _ttl;

    /// <summary>Devuelve el catálogo, refrescándolo si venció. Los requests concurrentes que llegan
    /// durante un refresh NO se apilan: el primero refresca y el resto sigue con la copia anterior, que
    /// para un catálogo es preferible a hacerlos esperar.</summary>
    public async Task<CatalogoSnapshot> ObtenerAsync(CancellationToken ct = default)
    {
        var snap = _actual;
        if (snap.Total > 0 && snap.Antiguedad < _ttl) return snap;

        // Si ya hay datos y alguien está refrescando, servimos lo que hay.
        if (snap.Total > 0 && !await _candado.WaitAsync(0, ct)) return snap;
        // Si NO hay datos (arranque en frío), sí hay que esperar el refresh.
        if (snap.Total == 0) await _candado.WaitAsync(ct);

        try
        {
            if (_actual.Total > 0 && _actual.Antiguedad < _ttl) return _actual;
            await RefrescarInternoAsync(ct);
            return _actual;
        }
        finally { _candado.Release(); }
    }

    /// <summary>Fuerza un refresh (arranque y tarea de fondo).</summary>
    public async Task RefrescarAsync(CancellationToken ct = default)
    {
        await _candado.WaitAsync(ct);
        try { await RefrescarInternoAsync(ct); }
        finally { _candado.Release(); }
    }

    private async Task RefrescarInternoAsync(CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        try
        {
            var nuevo = await ConstruirAsync(ct);
            _actual = nuevo;
            reloj.Stop();

            _log.LogInformation(
                "Catálogo actualizado en {Ms} ms: {Total} publicados de {Armados} armados " +
                "({SinFoto} sin foto, {Descartados} descartados por taxonomía, {SinVariantes} " +
                "descartados por no tener color/talle en PRECOMPRA ni REMCOMPRA).",
                reloj.ElapsedMilliseconds, nuevo.Total, nuevo.TotalArmados, nuevo.SinFoto,
                nuevo.DescartadosPorTaxonomia, nuevo.SinVariantes);

            if (nuevo.TallesDesconocidos.Count > 0)
                _log.LogWarning("Talles no registrados en Talles.cs: {Talles}. Agregarlos con su grupo y orden.",
                    string.Join(", ", nuevo.TallesDesconocidos));
        }
        catch (Exception ex)
        {
            // Guardarraíl: no se pisa lo que había. Servir datos viejos y avisar es mejor que
            // quedarse sin catálogo — pero NUNCA en silencio: por eso es LogError.
            //
            // Y NUNCA se relanza, ni siquiera en el arranque en frío sin copia todavía: si esto tirara,
            // OBLIGARÍA a todas las páginas del catálogo — hoy y las que se agreguen — a un try/catch
            // repetido. Ya pasó una vez (Producto.razor no lo tenía y tiraba 500). El contrato del
            // caché es "ObtenerAsync nunca lanza"; en frío, la UI simplemente ve un snapshot vacío
            // (0 resultados), que ya sabe manejar sin caso especial.
            _log.LogError(ex,
                "Falló el refresh del catálogo. Se sigue sirviendo la copia de hace {Antiguedad}.",
                _actual.Total > 0 ? _actual.Antiguedad.ToString(@"hh\:mm\:ss") : "(no hay copia)");
        }
    }

    private async Task<CatalogoSnapshot> ConstruirAsync(CancellationToken ct)
    {
        // 1) MARKET: qué está armado y dónde. Define el universo.
        var armados = await _repo.TraerArmadosAsync(ct);
        var localesPorCodigo = armados
            .GroupBy(a => a.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Local).Distinct(StringComparer.OrdinalIgnoreCase)
                                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var codigos = localesPorCodigo.Keys.ToArray();
        if (codigos.Length == 0) return CatalogoSnapshot.Vacio();

        // 2) Las otras fuentes. Las de Dragon van por su propia conexión (D8c).
        var articulos = await _repo.TraerArticulosAsync(codigos, ct);
        var fotos = await _repo.TraerRutasFotoAsync(ct);
        var overrides = await _repo.TraerOverridesAsync(ct);
        var comboTiers = await _repo.TraerComboTiersAsync(ct);

        // Color/talle: cascada de DOS fuentes, por artículo — PRECOMPRA primero (color como texto
        // directo del remito, sin el problema de matcheo de COMB contra DPCOLOR), después REMCOMPRA
        // (mismo criterio, cubre lo que no tuvo orden de compra). A propósito NO se cae a COMB: sus
        // datos vienen sucios, así que un artículo sin nada en PRECOMPRA ni REMCOMPRA queda sin
        // colores/talles antes que mostrar algo incorrecto (excepto Lencería, que no usa esta
        // cascada — ver el "Único" fijo más abajo).
        var variantesPrecompra = await _repo.TraerVariantesPrecompraAsync(codigos, ct);
        var codigosSinPrecompra = codigos.Except(
            variantesPrecompra.Select(v => v.ArtCod), StringComparer.OrdinalIgnoreCase).ToArray();

        var variantesRemcompra = codigosSinPrecompra.Length == 0
            ? Array.Empty<VarianteRow>()
            : await _repo.TraerVariantesRemcompraAsync(codigosSinPrecompra, ct);

        var variantes = variantesPrecompra.Concat(variantesRemcompra).ToList();

        // Curva de talles DEFINIDA (ART.CURTALL → CTALLE/DCTALLE). No es lo que se compró: es el
        // fallback para los artículos que las compras dejaron sin talle real (todo ST/U/X/vacío).
        // En ese caso se muestra la curva definida en vez de "Talle único" — así un artículo cargado
        // en la compra como un solo renglón sin talle igual muestra su 2XL/3XL/4XL/5XL. Los colores
        // siguen saliendo de las compras (que es donde están bien): esto sólo aporta talles.
        var curvasPorCodigo = (await _repo.TraerCurvasTalleAsync(codigos, ct))
            .GroupBy(c => c.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Orden).ToList(), StringComparer.OrdinalIgnoreCase);

        var fotoPorCodigo = fotos
            .GroupBy(f => f.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Ruta, StringComparer.OrdinalIgnoreCase);

        var overridePorCodigo = overrides
            .GroupBy(o => o.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var variantesPorCodigo = variantes
            .GroupBy(v => v.ArtCod, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 3) Cruce en C#.
        var lista = new List<ArticuloDto>(articulos.Count);
        var tallesDesconocidos = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var descartados = 0;
        var sinFoto = 0;
        var sinVariantes = 0;
        // Detalle de descartes para el volcado de diagnóstico (sólo si _diagPath está configurado).
        var descartadosDetalle = _diagPath is null ? null : new List<string>();

        foreach (var a in articulos)
        {
            // Filtro de basura. OBLIGATORIO ahora que se publican los artículos sin foto: antes la
            // descartaba sola el requisito de tener foto. Sin esto saldría publicado el pseudo-artículo
            // de promoción "2X15000" (rubro y género = "No aplica") como si fuera un producto.
            if (!TaxonomiaValida(a.Rubro) || !TaxonomiaValida(a.Genero))
            {
                descartados++;
                descartadosDetalle?.Add(LineaDiag(a, $"taxonomía inválida (rubro='{a.Rubro}', género='{a.Genero}')"));
                continue;
            }

            // POR AHORA: se publica ÚNICAMENTE el rubro Indumentaria. El resto (Accesorios, Lencería,
            // Calzado…) queda fuera del catálogo hasta que se decida sumarlos. Para volver a mostrar
            // todo, borrar este bloque. Mismo criterio de comparación (sin acentos, minúsculas) que el
            // de Lencería de más abajo, para no depender de mayúsculas/tildes que vienen del ERP.
            if (Texto.SinAcentos(a.Rubro) != "indumentaria")
            {
                descartados++;
                descartadosDetalle?.Add(LineaDiag(a, $"rubro no indumentaria ('{a.Rubro}')"));
                continue;
            }

            overridePorCodigo.TryGetValue(a.ArtCod, out var ovr);
            if (ovr?.OcultarManual == true)
            {
                descartadosDetalle?.Add(LineaDiag(a, "oculto manual (override)"));
                continue;
            }

            // Sin ninguna fila en PRECOMPRA ni REMCOMPRA no se publica: mejor no mostrar el artículo
            // que mostrarlo sin colores/talles. Lencería es la excepción — no usa esta cascada (ver
            // más abajo), así que no se descarta por esto.
            var esLenceria = Texto.SinAcentos(a.Rubro) == "lenceria";
            if (!esLenceria && !variantesPorCodigo.ContainsKey(a.ArtCod))
            {
                sinVariantes++;
                descartadosDetalle?.Add(LineaDiag(a, "sin variantes en PRECOMPRA ni REMCOMPRA"));
                continue;
            }

            // El ERP guardó parte del texto con la 'ñ' perdida como '?' (dato mal cargado upstream; no se
            // puede corregir en Dragonfish desde acá, que sólo lee). Se repara al leer, en un solo lugar,
            // para todo el texto de presentación que viene del ERP. Ver Texto.RepararEnie.
            var artDes  = Texto.RepararEnie(a.ArtDes);
            var rubro   = Texto.RepararEnie(a.Rubro);
            var genero  = Texto.RepararEnie(a.Genero);
            var familia = Texto.RepararEnie(a.Familia);

            var titulo = string.IsNullOrWhiteSpace(ovr?.NombreComercial)
                ? TituloArticulo.Derivar(artDes, familia)
                : ovr!.NombreComercial!.Trim();

            var combo = Combo.Parsear(a.Combo);
            var ruta = fotoPorCodigo.GetValueOrDefault(a.ArtCod);
            var tieneFoto = !string.IsNullOrWhiteSpace(ruta);
            if (!tieneFoto) sinFoto++;
            var fotoVersion = tieneFoto ? VersionFoto(ruta!) : null;

            // Lencería no usa la cascada PRECOMPRA/REMCOMPRA para color/talle: es la categoría con
            // los datos más inconsistentes en las tres fuentes (corpiños, conjuntos, medias con
            // curvas de talle que no se corresponden entre sí), así que en vez de mostrar algo
            // potencialmente incorrecto se muestra un talle y un color únicos, fijo.
            List<VarianteDto> variantesDto;
            List<string> talles;
            List<string> colores;

            if (esLenceria)
            {
                variantesDto = [];
                talles = ["Único"];
                colores = ["Único"];
            }
            else
            {
                var vs = variantesPorCodigo.GetValueOrDefault(a.ArtCod) ?? new();
                foreach (var v in vs)
                    if (Talles.EsDesconocido(v.Talle) && v.Talle.Length > 0) tallesDesconocidos.Add(v.Talle);

                variantesDto = vs
                    .Select(v => new VarianteDto(v.ColorCod, LimpiarColor(Texto.RepararEnie(v.Color), v.ColorCod), v.Talle,
                                                 Talles.Mostrar(v.Talle), Talles.Resolver(v.Talle).Orden))
                    .OrderBy(v => v.TalleOrden).ThenBy(v => v.Color, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                talles = variantesDto
                    .Where(v => !Talles.EsSinTalle(v.Talle))
                    .GroupBy(v => v.TalleMostrar)
                    .OrderBy(g => g.Min(v => v.TalleOrden))
                    .Select(g => g.Key).ToList();

                // Fallback: si las compras dejaron el artículo sin talle real (todo ST/U/X/vacío) pero
                // tiene una curva de talles definida, se muestra esa curva. Respeta el ORDEN de fábrica
                // de DCTALLE (2XL, 3XL, 4XL, 5XL) y descarta cualquier talle "sin talle" que pudiera
                // tener la curva. No toca colores ni Variantes: sólo rellena la lista de talles.
                if (talles.Count == 0 && curvasPorCodigo.TryGetValue(a.ArtCod, out var curva))
                {
                    foreach (var ct2 in curva)
                        if (Talles.EsDesconocido(ct2.Talle) && ct2.Talle.Length > 0) tallesDesconocidos.Add(ct2.Talle);

                    talles = curva
                        .Where(c => !Talles.EsSinTalle(c.Talle))
                        .Select(c => Talles.Mostrar(c.Talle))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                colores = variantesDto
                    .Where(v => v.Color.Length > 0)
                    .Select(v => v.Color).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            }

            lista.Add(new ArticuloDto
            {
                ArtCod = a.ArtCod,
                Titulo = titulo,
                Descripcion = artDes,
                Marketing = string.IsNullOrWhiteSpace(ovr?.Marketing) ? null : ovr!.Marketing!.Trim(),
                Slug = Texto.SlugProducto(titulo, a.ArtCod),
                Rubro = rubro,
                RubroSlug = Texto.Slug(rubro),
                Genero = genero,
                GeneroSlug = Texto.Slug(genero),
                Familia = string.IsNullOrWhiteSpace(familia) ? null : familia,
                FamiliaSlug = string.IsNullOrWhiteSpace(familia) ? null : Texto.Slug(familia),
                ComboTexto = combo is null ? null : Combo.Mostrar(combo.Cantidad, combo.Total),
                ComboCantidad = combo?.Cantidad,
                ComboTotal = combo?.Total,
                PrecioUnidadCombo = combo?.PrecioUnidad,
                PrecioUnidadSuelta = a.PrecioSuelta > 0 ? a.PrecioSuelta : null,
                PrecioSueltaTexto = a.PrecioSuelta > 0 ? Combo.Plata(a.PrecioSuelta.Value) : null,
                TieneFoto = tieneFoto,
                FotoVersion = fotoVersion,
                Destacado = ovr?.Destacado ?? 0,
                Locales = localesPorCodigo.GetValueOrDefault(a.ArtCod) ?? Array.Empty<string>(),
                Variantes = variantesDto,
                Talles = talles,
                Colores = colores,
                TextoBusqueda = Texto.SinAcentos($"{titulo} {artDes} {a.ArtCod} {familia}"),
            });
        }

        if (_diagPath is not null)
            await EscribirDiagnosticoAsync(lista, descartadosDetalle!, fotoPorCodigo, ct);

        return new CatalogoSnapshot
        {
            Articulos = lista,
            PorSlug = lista.GroupBy(x => x.Slug, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            PorCodigo = lista.GroupBy(x => x.ArtCod, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            Menu = ArmarMenu(lista),
            ComboTiers = comboTiers.Select(t => new ComboTier(t.Cantidad, t.Total)).Distinct()
                                   .OrderBy(t => t.Cantidad).ThenBy(t => t.Total).ToList(),
            // Sólo las rutas de artículos que quedaron publicados: el endpoint de fotos no puede
            // servir la imagen de algo que el catálogo no muestra.
            RutaFotoPorCodigo = lista.Where(a => a.TieneFoto)
                                     .ToDictionary(a => a.ArtCod,
                                                   a => fotoPorCodigo[a.ArtCod],
                                                   StringComparer.OrdinalIgnoreCase),
            Generado = DateTimeOffset.UtcNow,
            TotalArmados = codigos.Length,
            DescartadosPorTaxonomia = descartados,
            SinFoto = sinFoto,
            SinVariantes = sinVariantes,
            TallesDesconocidos = tallesDesconocidos.ToList(),
        };
    }

    /// <summary>Rubro/género válidos: no vacíos y distintos de "No aplica". Es el filtro que descarta
    /// los pseudo-artículos de promoción y los mal cargados.</summary>
    private static bool TaxonomiaValida(string? valor)
        => !string.IsNullOrWhiteSpace(valor)
           && !valor.Trim().Equals("No aplica", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre del color. Si DPCOLOR ya lo resolvió, se usa. Si no, sólo se cae al código cuando
    /// éste NO es numérico: un código numérico sin resolver es un id de color no mapeado (p. ej. "50", que
    /// no está en DPCOLOR), y mostrarlo como si fuera un color ensucia la faceta — mejor tratarlo como
    /// "sin color". Tras el match global por DPCOLOR esto afecta a un puñado de variantes.</summary>
    private static string LimpiarColor(string? desc, string? cod)
    {
        var d = (desc ?? "").Trim();
        if (d.Length > 0) return d;
        var c = (cod ?? "").Trim();
        if (c.Length == 0 || int.TryParse(c, out _)) return "";
        return c;
    }

    /// <summary>Token de versión para la URL de la foto (<c>?v=</c>). Es la fecha de modificación del
    /// archivo original: cambia cuando la foto se reemplaza o se le genera la IA, así el navegador baja la
    /// nueva sin esperar a que venza su caché. Si el archivo no está accesible en esta máquina, se cae al
    /// hash de la ruta — que igual cubre el caso disco→IA, porque ahí cambia el nombre del archivo.</summary>
    private string VersionFoto(string rutaEnBase)
    {
        try
        {
            var origen = RutasFoto.Resolver(rutaEnBase, _dirOriginales);
            if (origen is not null && File.Exists(origen))
                return File.GetLastWriteTimeUtc(origen).Ticks.ToString("x");
        }
        catch { /* sin acceso al disco: se usa el fallback */ }
        return ((uint)StringComparer.Ordinal.GetHashCode(rutaEnBase)).ToString("x");
    }

    // Una línea de diagnóstico para un artículo descartado: código, motivo y su taxonomía cruda del ERP.
    private static string LineaDiag(ArticuloRow a, string motivo)
        => $"{a.ArtCod}\t{motivo}\t[{a.Rubro} / {a.Genero} / {a.Familia}]\t{a.ArtDes}";

    /// <summary>Vuelca a un .txt qué artículos se publicaron y cuáles se descartaron (con motivo), para
    /// depurar el catálogo en local. Nunca hace fallar el refresh: si no puede escribir, sólo avisa.</summary>
    private async Task EscribirDiagnosticoAsync(
        List<ArticuloDto> publicados, List<string> descartados,
        Dictionary<string, string> fotoPorCodigo, CancellationToken ct)
    {
        try
        {
            // Estado de foto por artículo, resolviendo el archivo en la MISMA ruta que el endpoint:
            //   SIN LINK      -> no hay LinkIADisco ni LinkDriveDisco en la DB.
            //   FALTA ARCHIVO -> hay link en la DB, pero el archivo no está en disco (por eso sale en blanco).
            //   OK            -> hay link y el archivo existe.
            string EstadoFoto(ArticuloDto a, out string detalle)
            {
                detalle = "";
                if (!a.TieneFoto) return "SIN LINK";
                var ruta = fotoPorCodigo.GetValueOrDefault(a.ArtCod);
                var origen = RutasFoto.Resolver(ruta, _dirOriginales);
                var esIA = ruta is not null && ruta.Contains("_ia.", StringComparison.OrdinalIgnoreCase);
                if (origen is not null && File.Exists(origen)) return esIA ? "OK (IA)" : "OK (drive)";
                detalle = $"\tarchivo esperado: {origen}";
                return "FALTA ARCHIVO";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Catálogo local — {publicados.Count} publicados, {descartados.Count} descartados");
            sb.AppendLine("# (POR AHORA sólo se publica el rubro Indumentaria — ver CatalogoCache.cs)");
            sb.AppendLine();

            // Se calcula una vez por artículo, en orden, contando de paso.
            var lineas = new List<string>(publicados.Count);
            int ok = 0, falta = 0, sinLink = 0;
            foreach (var a in publicados.OrderBy(x => x.ArtCod, StringComparer.OrdinalIgnoreCase))
            {
                var estado = EstadoFoto(a, out var detalle);
                if (estado.StartsWith("OK")) ok++;
                else if (estado == "FALTA ARCHIVO") falta++;
                else sinLink++;
                lineas.Add($"{a.ArtCod}\t{estado}\t[{a.Rubro} / {a.Genero}]\t{a.Titulo}{detalle}");
            }

            sb.AppendLine($"===== PUBLICADOS ({publicados.Count} — {ok} con foto OK, {falta} con link pero SIN archivo, {sinLink} sin link) =====");
            sb.AppendLine("# código\testado foto\ttipo / género\ttítulo[\tarchivo esperado si falta]");
            foreach (var linea in lineas) sb.AppendLine(linea);

            sb.AppendLine();
            sb.AppendLine($"===== DESCARTADOS ({descartados.Count}) =====");
            sb.AppendLine("# código\tmotivo\t[rubro / género / familia]\tdescripción");
            foreach (var linea in descartados.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(linea);

            var dir = Path.GetDirectoryName(_diagPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_diagPath!, sb.ToString(), ct);
            _log.LogInformation("Diagnóstico del catálogo escrito en {Path}.", _diagPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No se pudo escribir el diagnóstico del catálogo en {Path}.", _diagPath);
        }
    }

    private static IReadOnlyList<RubroMenu> ArmarMenu(List<ArticuloDto> lista) =>
        lista.GroupBy(x => (x.RubroSlug, x.Rubro))
             .Select(r => new RubroMenu(
                 r.Key.RubroSlug, r.Key.Rubro, r.Count(),
                 r.GroupBy(x => (x.GeneroSlug, x.Genero))
                  .Select(g => new GeneroMenu(g.Key.GeneroSlug, g.Key.Genero, g.Count()))
                  .OrderByDescending(g => g.Cantidad).ToList()))
             .OrderByDescending(r => r.Cantidad)
             .ToList();
}
