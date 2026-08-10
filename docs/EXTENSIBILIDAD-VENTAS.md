# Extensibilidad a ventas (descuento de stock) — y por qué el caché es correcto por ahora

Este documento responde una pregunta concreta que va a volver: **si más adelante el sitio vende y hay
que descontar stock, ¿la arquitectura actual sirve, o hay que arrancar de cero?** Y de paso deja
escrito **por qué hoy el catálogo se sirve desde un caché en memoria** y qué cambia el día que eso deje
de alcanzar.

> ## 📦 Documento de ESCALAMIENTO — no es el diseño vigente
> **Hoy NO se venden productos ni se descuenta stock.** El sitio es de **solo lectura**: navegación,
> filtros, ficha, precio publicado. No hay carrito, checkout, pagos ni pedidos.
>
> Este documento se escribe ahora, con el razonamiento fresco, para que el día que se decida vender la
> discusión arranque desde acá y no desde cero. El detalle profundo del caché vive en
> [CONSULTAS.md §2.ter](CONSULTAS.md); acá se lo referencia, no se lo duplica.

---

## 1. La respuesta corta

**Sí, es rediseñable de forma aditiva. No se arranca de cero.** El lado de ventas se **agrega al lado**
como un módulo transaccional nuevo; el catálogo actual se conserva casi entero como el **lado de
lectura**. La estructura de monolito modular (Contratos / Aplicación / Datos / Ui + host, con la
frontera por interfaces) es justamente lo que permite enchufar `Ventas`/`Pedidos` **sin tocar
`Catálogo`**.

Lo que **no** se traslada es el *patrón*: el caché es una técnica de **lectura**; una venta es
**escritura transaccional**, con requisitos opuestos. Conviven — no se pisan.

---

## 2. Por qué el caché es correcto HOY (y por qué es "por ahora")

El razonamiento completo está en [CONSULTAS.md §2.ter](CONSULTAS.md). En una línea: es un patrón con
nombre —*cached read model*— apropiado para datos **acotados, muy leídos y que cambian poco** (~981
artículos, ~2 MB, sitio de solo lectura, atraso de minutos tolerable). La alternativa "tabla
materializada + job" **también es un caché**, solo que con más maquinaria y más formas de estar mal
(ver la tabla comparativa en §2.ter).

El "por ahora" es literal: el caché es correcto **mientras browsear sea la única operación**. En cuanto
haya una operación que **decida sobre el stock** (una venta), esa operación puntual deja de leer del
caché — no porque el caché esté mal, sino porque **no es la herramienta para el momento de la
decisión**. El caché sigue sirviendo la navegación; la venta va a la fuente de verdad.

---

## 3. Lectura vs. escritura: por qué el caché no se traslada a ventas

Es el reparto clásico de **CQRS**: lecturas por un lado (rápidas, cacheadas), escrituras por otro
(consistentes, transaccionales). El código actual es, sin haberlo buscado, "el lado de lectura" de ese
split.

| | Catálogo (hoy) | Venta / descuento de stock |
|---|---|---|
| Naturaleza | lectura | escritura transaccional |
| Consistencia | 5 min de atraso está bien | **al instante, o se sobrevende** |
| Operación | leer | leer + **reservar/descontar** atómico |
| Dónde vive el dato | caché en RAM | **tablas ACID** (pedidos, reservas, pagos) |
| Fuente de verdad del stock | Dragonfish (ERP) | Dragonfish (ERP) |
| Si se pierde el proceso | se reconstruye del origen | **no puede perderse una venta confirmada** |

---

## 4. Es aditivo, no un rewrite

**Lo que se reutiliza tal cual:**

- **Toda la navegación** (catálogo, filtros, facetas, ficha). Browsear no necesita stock en vivo;
  necesita ser rápido, y es el ~90% del tráfico. El caché sigue siendo lo correcto para eso.
- **La estructura modular** y sus fronteras por interfaces (`ICatalogoConsulta` y compañía).
- **El acceso a Dragonfish** (repos Dapper), el modelo de artículo/variante y la UI.

**Lo que se agrega (módulo nuevo, patrón transaccional):**

- Un módulo `Ventas`/`Pedidos` con **su propio almacenamiento real** (tablas ACID: pedidos, líneas,
  reservas, pagos). Nada cacheado ahí.
- Un flujo de carrito → checkout que, en el momento de decidir, **no confía en el número cacheado**.
- Su propio puerto (interfaz) hacia el resto, igual que hace hoy `Catálogo`.

---

## 5. El único punto crítico: la autoridad del stock

Regla de oro: **el caché NUNCA descuenta stock.** En el checkout se hace un **check-and-reserve
atómico** contra la fuente de verdad, dentro de una transacción con bloqueo / `rowversion`. Así, si dos
personas van por la última unidad, **una gana y la otra ve "sin stock"** — la sobreventa se evita ahí,
no en el caché.

Dos caminos posibles (a decidir cuando llegue el momento):

1. **Reservar/descontar directo en Dragonfish** en la transacción del checkout, si el ERP expone una
   API/transacción segura. Máxima consistencia; te acoplás al camino de escritura del ERP y a su
   concurrencia.
2. **Tabla de reservas propia** en la DB de MARKET, administrada por la web y **reconciliada** con
   Dragonfish. Más desacoplado, más piezas móviles (job de reconciliación, resolución de conflictos).

En ambos casos, el precio y el stock se **reconfirman contra el origen** al confirmar el pedido, no se
toman del caché.

---

## 6. Lo que no se puede cachear (y hay que sumar sí o sí)

- **Carrito → checkout:** reconfirmar precio y stock real.
- **Descuento de stock:** transaccional contra la fuente de verdad.
- **Pedidos y pagos:** tablas ACID, integración de pago, **idempotencia** (doble submit, reintentos de
  pago) y **concurrencia** (dos ventas por la última unidad).
- **Facturación / AFIP:** en Argentina es un mundo aparte (comprobantes electrónicos, CAE). Inherente a
  vender, no al diseño.

---

## 7. Camino por etapas (cuando se decida)

1. **Ficha con stock/precio en vivo** consultando la DB por artículo (híbrido: listado desde caché,
   ficha puntual al origen). Es un cambio **chico y acotado**, y es el primer ladrillo del lado
   transaccional sin romper nada.
2. **Carrito.**
3. **Reserva de stock + checkout transaccional** (la sección §5).
4. **Pago.**
5. **Facturación.**

---

## 8. Veredicto

- **Arquitectónicamente:** no se pelea con lo que hay. Los *seams* (módulos + puertos) están puestos
  para esto. Se extiende, no se reescribe.
- **En esfuerzo:** es igual un salto real de complejidad — pagos, ciclo de vida del pedido,
  devoluciones, reconciliación con el ERP, facturación. Eso es propio de vender, no deuda del diseño
  actual.
- **El caché de hoy no es un obstáculo:** es el lado de lectura de una arquitectura que, cuando sume
  ventas, queda naturalmente partida en lectura (cacheada) y escritura (transaccional).
