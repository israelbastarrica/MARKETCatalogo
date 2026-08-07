# Sistema de diseño — MARKET arg

Derivado del **Manual de Marca** oficial (35 páginas). Este documento traduce esas normas a tokens
y reglas aplicables a la web. **Ante cualquier duda, manda el manual.**

---

## 1. Paleta: la marca es estrictamente monocromática

El manual define **dos colores y nada más**:

| Color | Hex | RGB | CMYK |
|---|---|---|---|
| Negro pleno | `#000000` | 0, 0, 0 | C91 M79 Y62 K97 |
| Blanco pleno | `#FFFFFF` | 255, 255, 255 | C0 M0 Y0 K0 |

Y una **escala de opacidad permitida**: `100%` · `70%` · `40%` · `20%`. Son los únicos grises válidos,
y se obtienen por opacidad del negro (o del blanco, sobre fondo negro) — no son colores nuevos.

> ### ⚠️ El rosa y el verde menta están PROHIBIDOS
> La página "Usos prohibidos" del manual muestra explícitamente el logo en **rosa/magenta** y en
> **verde menta** como usos **no permitidos**, igual que el logo sobre fondo de color. Si alguien ve
> esos colores en material viejo de MARKET, **no son colores de marca**. La web no los usa en ningún
> lado — ni para acentos, ni para botones, ni para estados de error.

Esto es una ventaja para un catálogo de indumentaria, no una limitación: la interfaz en blanco y
negro deja que **la única fuente de color sea la foto del producto**. Es el mismo criterio editorial
que usan las marcas de moda.

### Consecuencia de accesibilidad, importante

La escala de opacidad es una herramienta gráfica, **no una escala de texto**. Contraste real sobre
blanco:

| Opacidad | Equivale a | Contraste | Uso permitido en la web |
|---|---|---|---|
| 100% | `#000000` | 21:1 | Títulos, texto principal |
| 70% | `#4D4D4D` | 8,4:1 ✅ AAA | Texto secundario, metadatos, descripciones |
| 40% | `#999999` | 2,9:1 ❌ | **Solo** bordes, divisores, iconos decorativos, placeholders |
| 20% | `#CCCCCC` | 1,6:1 ❌ | **Solo** fondos sutiles y separadores |

**Regla: ningún texto por debajo del 70%.** Una descripción de producto en 40% es ilegible en el
celular de un cliente al sol.

---

## 2. Tipografía

| Rol | Fuente | Uso |
|---|---|---|
| **Principal** | **Poppins** | Logo, títulos, encabezados, números grandes (`30% OFF`) |
| **Secundaria** | **Open Sans** | Tagline del logo y **toda la comunicación general**: texto corrido, fichas, navegación, formularios |

Pesos disponibles según el manual — Poppins: Light, Regular, Italic, Medium, Medium Italic, SemiBold,
Bold, ExtraBold, Black Italic. Open Sans: Light, Light Italic, Regular, Italic, Semibold, Semibold
Italic, Bold, Bold Italic, Extrabold, Extrabold Italic.

**Las dos son de Google Fonts (licencia SIL Open Font) y se sirven self-hosted** ✅ **ya instaladas**,
no desde el CDN de Google:
- No hay dependencia de un tercero para que el sitio se vea bien.
- No se filtra la IP del visitante a Google (relevante para privacidad).
- Es más rápido: mismo origen, misma conexión, sin un DNS + TLS extra.

Están en `wwwroot/fonts/`: solo los pesos que se usan (Poppins 400/500/600/700 y Open Sans 400/600/700),
en `woff2`, subsets **latin y latin-ext** — los dos, porque latin-ext es el que trae los acentos y la ñ.
14 archivos, 297 KB en total.

Las `@font-face` están en `wwwroot/css/fonts.css`, **archivo generado**: sale del CSS de Google con las
`url()` reescritas a rutas locales, conservando sus `unicode-range`. Eso último importa: son los que
hacen que el browser baje latin-ext **solo si la página lo necesita**. No editarlo a mano.

---

## 3. Logotipo

- Wordmark **`MARKET.ARG`**: "MARKET" con **tracking muy abierto** y ".ARG" en tamaño chico.
- Tagline opcional debajo: **`VIVI LA EXPERIENCIA`**, en Open Sans, también con tracking abierto.
- Dos versiones únicas: **positivo** (negro sobre blanco) y **negativo** (blanco sobre negro).
- **Reducción mínima en digital: 183 px de ancho.** Por debajo de eso no se usa el wordmark.
- Área de seguridad definida en el manual con una grilla propia; en la práctica: dejar como mínimo
  el ancho de una "M" de aire alrededor del logo.

**Prohibido** (del manual):
- El logo en cualquier color que no sea negro o blanco.
- El logo sobre fondo de color.
- **Alterar el tracking** — el logo condensado o con las letras juntas no es el logo.
- Usar la **"M" sola** como isotipo. MARKET no tiene isotipo separado.

Implicancia para la web: el favicon y el ícono de la PWA **no pueden ser una "M"**. Hay que resolverlos
con el wordmark completo (que a 32×32 no se lee) o consultar a diseño. **Es un punto abierto real**,
no algo que deba resolver por mi cuenta.

---

## 4. Tokens CSS

```css
:root {
  /* Paleta — lo único que existe */
  --mk-negro: #000;
  --mk-blanco: #fff;

  /* Escala de opacidad del manual, sobre fondo claro */
  --mk-tinta-100: #000;
  --mk-tinta-70:  rgba(0, 0, 0, .70);   /* texto secundario  */
  --mk-tinta-40:  rgba(0, 0, 0, .40);   /* bordes, iconos    */
  --mk-tinta-20:  rgba(0, 0, 0, .20);   /* divisores, fondos */

  /* La misma escala en negativo, sobre fondo negro */
  --mk-tinta-inv-70: rgba(255, 255, 255, .70);
  --mk-tinta-inv-40: rgba(255, 255, 255, .40);
  --mk-tinta-inv-20: rgba(255, 255, 255, .20);

  /* Tipografía */
  --mk-font-titulo: 'Poppins', system-ui, sans-serif;
  --mk-font-texto:  'Open Sans', system-ui, sans-serif;

  /* Tracking del wordmark y de los rótulos tipo señalética */
  --mk-tracking-logo: .38em;
  --mk-tracking-rotulo: .18em;
}
```

**Sin dark mode.** La marca ya es blanco y negro: un "modo oscuro" sería simplemente la versión
negativa del logo, que el manual ya contempla. Invertir la interfaz completa no aporta y arriesga
romper las normas de contraste del wordmark.

---

## 5. Cómo se traduce a las pantallas del catálogo

- **Grilla de productos:** fondo blanco, foto sobre fondo blanco, sin bordes de tarjeta o con un
  divisor en `--mk-tinta-20`. Descripción en Open Sans; el código de artículo en `--mk-tinta-70`
  con tracking de rótulo. La foto es lo único con color en la pantalla.
- **Ficha de producto:** foto grande a la izquierda, datos a la derecha. Título en Poppins SemiBold.
  Talles y colores como *chips* con borde `--mk-tinta-40`, texto en negro.
- **Header:** wordmark en positivo sobre blanco, respetando los 183 px mínimos. En mobile, si no
  entran 183 px, va el logo en una fila propia — **no se reduce**.
- **Bloques de sección / hero:** acá vive la versión negativa, con fondo `#000` y logo blanco, tal
  como las portadas del manual.
- **Ofertas y descuentos:** el manual ya tiene un patrón resuelto para esto (cartelería negra con
  `30% OFF` en Poppins muy grande y blanco). Se replica tal cual cuando haya promociones.
- **Señalética:** el manual trae un set de iconos propios (flechas, carrito, bolsa, envío, cambios,
  reciclaje, prendas falladas, ofertas). **Se usan esos**, no una librería de iconos genérica.
  Hay que pedirle a diseño los archivos vectoriales.

---

## 6. Lo que hay que pedirle a diseño

1. **Logo en SVG**, positivo y negativo, con y sin tagline. Vectorizarlo del PDF pierde precisión.
2. **Favicon / ícono de app**, dado que la "M" sola está prohibida.
3. **Los iconos de señalética en SVG** (página "Señalética y Gráficos" del manual).
4. ~~Los `.woff2`~~ ✅ resuelto: se bajaron de Google Fonts y están auto-hospedados.
5. Fotos de marca / ambiente para el home institucional.
