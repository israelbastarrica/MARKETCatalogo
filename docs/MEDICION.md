# Medición contra datos reales

> **Documento HISTÓRICO.** Es la medición inicial contra producción que fundamentó el diseño. Los
> **números son reales** (y siguen sirviendo como referencia de volúmenes/tiempos), pero algunas
> **conclusiones de la época quedaron atrás**: el diseño final NO usa `OutputCache` ni un caché en
> memoria — la tabla materializada `dbo.Catalogo` ES el caché y la grilla se resuelve en SQL. Ver
> [../README.md](../README.md) y [../CLAUDE.md](../CLAUDE.md) para el diseño vigente.

Corrida sobre producción (solo lecturas con `NOLOCK`). Fue la primera medición del proyecto, y **cambió
tres decisiones** del plan de ese momento.

---

## 1. Volumen

| Métrica | Valor |
|---|---|
| **Artículos armados en locales** (universo del catálogo) | **985** |
| Existen en `ZooLogic.ART` | 985 (100%) |
| Están en `GoogleDriveFotosArticulos` | 749 |
| **Con foto en disco** | **678 (69%)** |
| Con foto en blob | **0** |
| Con foto IA | 27 |
| **Variantes** (filas de `COMB`) | **14.225** en 981 artículos (~14,5 por artículo) |

**Tiempo de la query del universo: 0,1 s.** La de cobertura de fotos: 0,5 s.

### Consecuencias

**El catálogo publicable son ~678 artículos**, no 985 — si se esconden los que no tienen foto
(recomendación del PLAN §8.5). Los **307 sin foto son una tarea concreta para el equipo**, y algo que
se puede ir midiendo: cada foto que suben agranda el catálogo.

**Los blobs están en cero.** El comentario del código de MARKETweb decía que la migración a disco los
había vaciado; está confirmado. Así que **el fallback al blob es código muerto para este proyecto** y
no hay que implementarlo. Solo `LinkDriveDisco`.

**El volumen es chico.** Un refresh completo lee ~1.000 artículos y 14 mil filas de variantes. Corre
en segundos.

### El catálogo real, por rubro

| Rubro | Armados | **Con foto (lo que se vería)** | % con foto |
|---|---|---|---|
| Indumentaria | 587 | **409** | 70% |
| Lencería | 344 | **244** | 71% |
| Calzado | 18 | **17** | 94% |
| Accesorios | 9 | **4** | 44% |
| Casa blanquería | 21 | **4** | 19% |
| Juguetería | 3 | **0** | 0% |
| No aplica / sin rubro | 3 | **0** | 0% |
| **TOTAL** | **985** | **678** | **69%** |

Dato que valida el criterio de filtrado del §5: **toda la basura (`No aplica`, sin rubro, Juguetería)
tiene 0 fotos**, así que el filtro por foto la descarta sola. No hace falta ninguna lista negra.

### Por género

| Género | Armados | Con foto |
|---|---|---|
| Mujer | 384 | 284 |
| Hombre | 288 | 196 |
| Nene | 132 | 91 |
| Nena | 126 | 82 |
| Unisex | 45 | 20 |
| Bebé | 6 | 5 |

### Las secciones del menú (rubro × género, solo con foto)

| Sección | Artículos |
|---|---|
| Indumentaria / Mujer | **172** |
| Indumentaria / Hombre | **124** |
| Lencería / Mujer | **105** |
| Lencería / Hombre | **67** |
| Indumentaria / Nena | **59** |
| Indumentaria / Nene | **49** |
| Lencería / Nene | **42** |
| Lencería / Nena | **20** |
| Calzado / Mujer | 7 |
| Lencería / Bebé · Indumentaria/Unisex · Lencería/Unisex | 5 c/u |
| Calzado/Unisex · Casa blanquería/Unisex | 4 c/u |
| Calzado/Hombre · Calzado/Nena | 3 c/u |
| Accesorios/Hombre · Accesorios/Unisex | 2 c/u |

**Ocho secciones concentran 638 de 678 artículos (94%).** El resto son de un dígito.

Consecuencia de diseño concreta: **Calzado, Accesorios y Casa blanquería no deberían tener entrada
propia en el menú principal todavía** — serían secciones casi vacías, que es peor que no tenerlas. El
menú arranca con **Indumentaria** y **Lencería** × (Mujer / Hombre / Nena / Nene), y los rubros chicos
entran cuando tengan volumen. Es una decisión que hay que **recalcular cada tanto**, no clavar: depende
de cuántas fotos se vayan subiendo.

---

## 2. Corrección #1: el Manual de Marca documenta 3 rubros, pero hay 6

`ZooLogic.TIPOART`, cruzado contra la primera letra del `ARTCOD`:

| Letra | `TIPOART` | Artículos | ¿Está en el manual? |
|---|---|---|---|
| `I` | Indumentaria | 587 | ✅ |
| `L` | **Lencería** | **344** | ❌ **no** |
| `B` | **Casa / Blanquería** | 21 | ❌ **no** |
| `C` | Calzado | 18 | ✅ |
| `A` | Accesorios | 9 | ✅ |
| `J` | **Juguetería** | 3 | ❌ **no** |
| — | (vacío) | 2 | — |
| `2` | No aplica | 1 | código malformado, ver §5 |

**Lencería es el segundo rubro del catálogo con 344 artículos (35%) y no figura en el manual.** La
página "Composición código" del manual está incompleta o quedó vieja.

Consecuencia directa: **la estrategia T2 del PLAN (derivar la taxonomía del `ARTCOD` usando la tabla
del manual) habría clasificado mal 368 de 985 artículos — el 37% del catálogo.** Se habrían ido todos a
un rubro desconocido.

Las letras **sí** mapean bien (`L`→Lencería, `B`→Blanquería, `J`→Juguetería); lo que estaba incompleto
era la tabla de referencia del manual, no la convención.

---

## 3. Corrección #2: `TIPOART` **es** el "Tipo" del manual (no hay colisión de nombres)

En el PLAN advertí sobre una supuesta colisión entre el "Tipo" del manual y el `TIPOART` de Dragon.
**Estaba equivocado: son lo mismo.** Los datos lo muestran sin ambigüedad:

| Tabla de Dragon | Qué contiene realmente | Rol en el catálogo |
|---|---|---|
| `TIPOART` | Indumentaria, Lencería, Casa blanquería, Calzado, Accesorios, Juguetería | **Rubro** — el "Tipo" del manual, nivel 1 |
| `CATEGART` | Mujer, Hombre, Nena, Nene, Bebé, Unisex | **Género** — nivel 2 |
| `FAMILIA` | Campera, Pantalón, Remera, Media, Corpiño, Bombacha, Boxer, Toallón… | **Tipo de prenda** — el filtro fino |

O sea que la jerarquía del manual (Tipo → Género) está **literalmente en las tablas de Dragon**, y con
99,8% de cobertura: 983/985 con `TIPOARTI`, 982/985 con `CATEARTI` y con `FAMILIA`.

**Corrección #3: el género también tiene un valor más que el manual** — aparece **Bebé** (6 artículos),
que no está en el `H`/`M`/`U`/`NE`/`NA` documentado.

### Consecuencia para la taxonomía

**Se invierte la recomendación del PLAN §3.bis.** Ya no es "Dragon con el `ARTCOD` de red de
contención": es **Dragon como fuente única** (99,8% de cobertura y la lista completa y correcta), y el
`ARTCOD` sirve solo para dos cosas mucho más chicas:

1. Rellenar los 2–3 artículos sin `TIPOARTI`.
2. **Detectar códigos malformados** (§5).

Y aparece un hallazgo que no esperábamos: **`FAMILIA` es el filtro más valioso del catálogo.** Es lo
que una persona realmente busca —"camperas", "medias", "pantalones"— y tiene buena cobertura.
Distribución en Indumentaria: Campera 175, Pantalón 170, Remera 95, Buzo 34, Sweater 23, Polera 21,
Chaleco 19, Calza 16. En Lencería: Media 145, Corpiño 57, Bombacha 45, Boxer 40.

---

## 4. Los talles son un problema real

53 valores distintos, de familias de talles incompatibles entre sí:

| Grupo | Valores | Artículos |
|---|---|---|
| **Sin talle** | `ST` (835), `U` (180), vacío (228), `X` (1) | la mayoría |
| Letras | `XS`, `S` (287), `M` (315), `L` (323), `XL` (309), `2XL` (195), `3XL` (76) … hasta `7XL` | muchos |
| Combinados | `SM` (17), `LXL` (18) | pocos |
| Niños | `01`–`16` (`06`: 148, `08`: 147, `10`: 153, `12`: 153, `14`: 151, `16`: 98) | muchos |
| Adulto numérico | `36`–`56` | ~100 |
| Corpiño / lencería | `80`–`120` | ~30 |

Dos conclusiones:

1. **`ST`, `U`, `X` y el vacío significan "sin talle"** → en la ficha no va ningún chip de talle. Es la
   mayoría de los casos, así que hay que tratarlo bien y no mostrar un chip vacío.
2. **El orden no se puede resolver alfabéticamente.** `L` iría antes que `M`, y `10` antes que `2`.
   Hace falta una **tabla de orden de talles** que además sepa a qué grupo pertenece cada uno, porque
   un artículo no mezcla grupos (o es S/M/L, o es 36/38/40).

Esto valida el punto abierto del doc de sync, y ahora está dimensionado: son 53 valores, se ordenan a
mano una vez y queda resuelto para siempre.

---

## 5. Basura que hay que filtrar

Apareció `2X15000` — descripción `"2X15000"`, con `TIPOART`, `CATEGART` y `FAMILIA` todos en
**"No aplica"**. Es un pseudo-artículo de promoción ("2 por 15.000"), y **está armado en un local**, así
que la query del universo lo trae.

Sin filtro, **eso aparecería en el catálogo público como un producto**. El filtro correcto no es una
lista negra de códigos: es exigir que el artículo tenga rubro y género válidos y una foto. Los tres
criterios juntos lo descartan solo.

Es exactamente el tipo de cosa que justifica tener la tabla `CatalogoWeb` con un `Publicado` calculado
en vez de consultar los mapeos en vivo.

---

## 6. El precio: MARKET vende por combo, y hay una regla exacta

Al sumar el precio al alcance, apareció que el modelo de precios de MARKET **no es un precio por
artículo**. Hay dos números y viven en lugares distintos:

| Dato | Dónde | Qué es |
|---|---|---|
| **Combo** | `ART.CLASIFART` — texto, ej. `2X15000` | **La oferta**: 2 unidades por $15.000. Es lo que dice la etiqueta en el local, y lo que el colector y Consulta de Artículos muestran como "Precio". |
| **Precio individual** | `PRECIOAR.PDIRECTO` con `LISTAPRE='LISTA1'` | Lo que cuesta **una** unidad sola. |

Cobertura, sobre los 678 publicables:

| Métrica | Valor |
|---|---|
| Con precio `LISTA1` | **678 (100%)** |
| Con combo en `CLASIFART` | **678 (100%)** |
| Combos con formato raro (no `NxTOTAL`) | 0 |
| Precios con vigencia futura | 0 (hoy) |

### La regla, verificada en los 678

```
PRECIOAR.LISTA1  =  (combo_total / combo_cantidad)  +  $5.000
```

**Se cumple en 678 de 678 artículos, con diferencia de exactamente $5.000 y ni una excepción.**

Es decir: MARKET publica una oferta por combo y cobra **un recargo fijo de $5.000 por comprar de a una**.

| Combo | Unidad en el combo | Una sola unidad (`LISTA1`) |
|---|---|---|
| `2X10000` | $5.000 | $10.000 |
| `2X15000` | $7.500 | $12.500 |
| `2X20000` | $10.000 | $15.000 |
| `4X15000` | $3.750 | $8.750 |

Reparto de combos: **519 artículos son `2X`, 159 son `4X`.** Precio por unidad en combo: de **$1.500 a
$50.000**, promedio ~$12.000.

### Qué mostrar en la card, y por qué

**Los dos números, con el combo como titular.**

```
CAMPERA INFLABLE DAMA
2 x $15.000
$12.500 la unidad
```

Las alternativas son ambas malas:

- **Solo `LISTA1` ($12.500)** → el sitio se ve un 67% más caro que la oferta del local, y muestra un
  precio que casi nadie paga.
- **Solo la unidad del combo ($7.500)** → es un precio que **no podés obtener comprando uno**. Publicar
  eso es engañoso y, con la Ley de Defensa del Consumidor, un problema concreto.

### Y una consecuencia que sube la apuesta de todo el proyecto

**Publicar precios convierte al sitio en una oferta comercial.** En Argentina el precio exhibido tiene
que poder honrarse. Eso cambia tres cosas del plan:

1. **La frecuencia del job pasa de cómoda a importante.** Los precios cambian seguido (MARKETweb tiene
   un proceso nocturno de "Cambiar Precios" que los aplica en Dragon). Cada 15 minutos.
2. **El log de sincronización deja de ser una buena práctica y pasa a ser un resguardo.** Si el sync
   viene fallando 6 horas y nadie se enteró, el sitio está publicando precios viejos.
3. **Hay que filtrar por vigencia**: `FECHAVIG <= GETDATE()`. Hoy no hay precios con fecha futura, pero
   el proceso de cambio de precios los inserta, y sin ese filtro el sitio publicaría **un precio que
   todavía no entró en vigencia**. MARKETweb no filtra por esto (y para una pantalla interna de gestión
   está bien — ahí querés ver el precio pendiente), pero para el público es obligatorio.
4. Conviene un **"precios actualizados al …"** visible, alimentado por `FechaSync`.

---

## 7. Puntos abiertos que se cerraron

| Punto | Resultado |
|---|---|
| `MapeoRegistro.Historico` | **Todas las filas con `Eliminado=0` tienen `Historico=0`.** Hoy no discrimina nada; la query del universo no necesita filtrarlo. |
| ¿`FMODIFW` trae la hora? | **No.** 0 de 985 tienen componente horario. Confirmada la granularidad de día, como se inferió del código. |
| Volumen | 985 / 678 publicables (§1). |
| Cobertura de la taxonomía de Dragon | 99,8% (§3). |

---

## 8. La conclusión que más cambia el plan

**Todo el aparato de sincronización incremental sobra.** Con 985 artículos y una query de universo de
0,1 s, un **refresh completo** corre en segundos y puede ejecutarse cada 15 minutos sin que nadie lo
note.

Se eliminan del diseño: `SyncPendiente`, `HashOrigen`, las marcas de agua sobre `ID`, el filtro por
`FMODIFW` y la comparación de `LastWriteTime` para las fotos. Y con ellos se elimina su costo real, que
nunca fue el CPU sino **la clase de bug que deja un dato viejo en silencio durante meses**.

Y algo más incómodo pero honesto: **a este volumen, el argumento de performance para tener la tabla
`CatalogoWeb` desaparece.** La Alternativa A (query en vivo con `OutputCache`) andaría bien.

La tabla se sigue justificando, pero por otras tres razones —ninguna de las cuales es velocidad:

1. **Hay que generar los thumbnails de todos modos** (una grilla de 48 fotos originales no la banca
   ningún celular), y eso ya obliga a tener un job. Con el job andando, la tabla sale casi gratis.
2. **Los campos propios no tienen dónde vivir**: `Slug`, `NombreComercial`, `Destacado`,
   `OcultarManual`. En Dragon no se pueden guardar.
3. **Filtrar la basura y el 31% sin foto** requiere un lugar donde decidirlo (§5).

O sea: la tabla se queda, pero **el job es mucho más simple de lo que había diseñado**. Un solo modo:
recalcular todo.
