# Qué se publica y qué se descarta

Reglas que deciden si un artículo aparece en el catálogo público, y la herramienta de **diagnóstico** para
ver, artículo por artículo, por qué aparece o no (incluida la foto).

Todo esto vive en `src/Modulos/Catalogo/MarketCatalogo.Catalogo.Aplicacion/Servicios/CatalogoCache.cs`
(`ConstruirAsync`), que arma el snapshot que sirve **todo** el sitio (listado, ficha, búsqueda, facetas,
mega-menú, home). Ver [CONSULTAS.md](CONSULTAS.md) para el porqué del caché.

---

## 1. Filtros de publicación (en orden)

Un artículo se **descarta** (no aparece) si cae en alguno de estos, evaluados en este orden:

1. **Taxonomía inválida** — rubro o género vacío o `"No aplica"`. Descarta pseudo-artículos de promoción
   (ej. `"2X15000"`) y datos mal cargados.
2. **Rubro ≠ Indumentaria** — *filtro temporal*, ver §2.
3. **Oculto manual** — el override editorial (`CatalogoArticulo.OcultarManual`) lo marca para ocultar.
4. **Sin variantes** — no tiene ninguna fila de color/talle en `PRECOMPRA` ni `REMCOMPRA`. Mejor no
   mostrarlo que mostrarlo sin talles. (Excepción: Lencería, que no usa esa cascada.)

Que un artículo **no tenga foto NO lo descarta**: se publica igual, con un placeholder. Ver
[FOTOS.md](FOTOS.md) §2.

## 2. Filtro temporal: sólo Indumentaria

> **POR AHORA el sitio publica únicamente el rubro `Indumentaria`.** El resto (Accesorios, Lencería,
> Calzado…) queda fuera hasta que se decida sumarlos.

Está implementado como un único bloque en `ConstruirAsync`, marcado con `// POR AHORA:`:

```csharp
if (Texto.SinAcentos(a.Rubro) != "indumentaria") { descartados++; continue; }
```

- Se compara sin acentos y en minúsculas (mismo criterio que el de Lencería), para no depender de
  mayúsculas/tildes que vienen del ERP.
- Como afecta el snapshot, **todo** el sitio queda consistente de una: la faceta "Tipo" del catálogo, al
  quedar un solo rubro, se **auto-oculta**; el mega-menú muestra sólo géneros; etc.
- **Para revertir** (volver a publicar todos los rubros): borrar ese bloque. Nada más.

## 3. Diagnóstico: por qué aparece / no aparece cada artículo

Herramienta de depuración **sólo para local**: en cada armado del catálogo (al arrancar y en cada refresh)
se escribe un `.txt` con **los códigos publicados y los descartados, con su motivo y el estado de la foto**.

### Cómo activarlo

En `appsettings.Development.json`:

```json
"Catalogo": {
  "DiagnosticoPath": "C:\\MARKETCatalogo\\_catalogo-diagnostico.txt"
}
```

- Si la clave está vacía o no existe, **no se escribe nada** (por eso en producción, que no la define, no
  corre). El `.txt` está en `.gitignore`.
- Nunca hace fallar el refresh: si no puede escribir, sólo deja un warning.
- Para que el chequeo de "archivo en disco" (ver abajo) sea correcto en la máquina de las fotos, hay que
  correr ahí en Development o agregar esa clave en esa máquina.

### Qué contiene

Dos secciones:

```
===== PUBLICADOS (n — X con foto OK, Y con link pero SIN archivo, Z sin link) =====
# código   estado foto   tipo / género   título[   archivo esperado si falta]

===== DESCARTADOS (n) =====
# código   motivo   [rubro / género / familia]   descripción
```

**Estado de foto** de cada publicado (chequea el archivo en la MISMA ruta que resolvería el endpoint,
respetando `Fotos:DirOriginales`):

| Estado | Significa |
|---|---|
| `OK (IA)` / `OK (drive)` | Hay link y el archivo existe en disco. |
| `FALTA ARCHIVO` | Hay link en la DB pero el `.jpg` **no está en disco** → se ve en blanco. Incluye la ruta esperada. |
| `SIN LINK` | No hay ni IA ni Drive en la DB. |

**Motivos de descarte** (los de §1): `taxonomía inválida (...)`, `rubro no indumentaria ('...')`,
`oculto manual (override)`, `sin variantes en PRECOMPRA ni REMCOMPRA`.

### Ejemplo de uso

"El artículo `IU109.140` no muestra foto." → buscar `IU109.140` en el `.txt`:

- Si sale `FALTA ARCHIVO` → el link está en la DB pero el `.jpg` no está en la carpeta (o tiene otro
  nombre). Se arregla poniendo el archivo o corrigiendo el link.
- Si no aparece en PUBLICADOS y sí en DESCARTADOS → lo sacó un filtro (ej. no es Indumentaria, o sin
  variantes).
