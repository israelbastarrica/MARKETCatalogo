// Visor con zoom de la foto de la ficha. Mejora progresiva: sin esto la foto se ve normal (no pasa
// nada al tocarla). Con esto, al hacer click en la foto se abre un overlay a pantalla completa donde
// se puede hacer zoom con la rueda del mouse, doble click, o pellizco en celular, y arrastrar para
// desplazarse cuando está ampliada. Se cierra con Esc, tocando el fondo o el botón ×.
//
// Vive en el módulo Catalogo.Ui (no en el host), misma frontera que catalogo.css / catalogo-scroll.js.
(function () {
    var overlay = null, img = null;
    var escala = 1, tx = 0, ty = 0;
    var ESC_MIN = 1, ESC_MAX = 5;
    var punteros = {};          // pointerId -> {x, y}  (pan con 1 dedo, pinch con 2)
    var pinchDist = 0;

    function crear() {
        overlay = document.createElement('div');
        overlay.className = 'mk-zoom-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.innerHTML =
            '<button type="button" class="mk-zoom-cerrar" aria-label="Cerrar">×</button>' +
            '<img class="mk-zoom-img" alt="" draggable="false" />';
        img = overlay.querySelector('.mk-zoom-img');

        // Cerrar al tocar el fondo (no la imagen) o el botón.
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay || e.target.classList.contains('mk-zoom-cerrar')) cerrar();
        });

        // Rueda del mouse: zoom centrado en el cursor.
        overlay.addEventListener('wheel', function (e) {
            e.preventDefault();
            zoomA(escala * (e.deltaY < 0 ? 1.2 : 1 / 1.2), e.clientX, e.clientY);
        }, { passive: false });

        // Doble click / doble tap: alterna entre ampliado y original.
        img.addEventListener('dblclick', function (e) {
            e.preventDefault();
            if (escala > 1) reset(); else zoomA(2.5, e.clientX, e.clientY);
        });

        // Pan (1 puntero) y pinch (2 punteros) con Pointer Events: cubre mouse y touch.
        img.addEventListener('pointerdown', function (e) {
            punteros[e.pointerId] = { x: e.clientX, y: e.clientY };
            img.setPointerCapture(e.pointerId);
            // Arrastre/pinch en tiempo real: sin la transición del zoom deslizado (si no, laguea).
            img.classList.add('mk-arrastrando');
            var ids = Object.keys(punteros);
            if (ids.length === 2) pinchDist = distancia(ids);
        });
        img.addEventListener('pointermove', function (e) {
            if (!punteros[e.pointerId]) return;
            var ids = Object.keys(punteros);
            if (ids.length === 1) {
                if (escala > 1) {
                    tx += e.clientX - punteros[e.pointerId].x;
                    ty += e.clientY - punteros[e.pointerId].y;
                    aplicar();
                }
            } else if (ids.length === 2) {
                punteros[e.pointerId] = { x: e.clientX, y: e.clientY };
                var d = distancia(ids);
                if (pinchDist > 0) {
                    var centro = medio(ids);
                    zoomA(escala * (d / pinchDist), centro.x, centro.y);
                }
                pinchDist = d;
                return; // ya guardó la posición arriba
            }
            punteros[e.pointerId] = { x: e.clientX, y: e.clientY };
        });
        function soltar(e) {
            delete punteros[e.pointerId];
            if (Object.keys(punteros).length < 2) pinchDist = 0;
            // Ya no hay dedos: vuelve la transición para el próximo zoom de rueda / doble click.
            if (Object.keys(punteros).length === 0) img.classList.remove('mk-arrastrando');
        }
        img.addEventListener('pointerup', soltar);
        img.addEventListener('pointercancel', soltar);

        document.body.appendChild(overlay);
    }

    function distancia(ids) {
        var a = punteros[ids[0]], b = punteros[ids[1]];
        return Math.hypot(a.x - b.x, a.y - b.y);
    }
    function medio(ids) {
        var a = punteros[ids[0]], b = punteros[ids[1]];
        return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    }

    // Zoom a un nivel dado manteniendo fijo el punto (cx, cy) de la pantalla.
    // El centro se calcula de forma lógica (centro del viewport + la traslación actual), NO con
    // getBoundingClientRect: con la transición de zoom deslizado, el rect devuelve la posición a
    // mitad de animación y el centrado se desviaría al hacer scroll rápido. La imagen está centrada
    // por flex, así que sin transformar su centro coincide con el centro del viewport.
    function zoomA(nuevo, cx, cy) {
        nuevo = Math.min(ESC_MAX, Math.max(ESC_MIN, nuevo));
        var centroX = window.innerWidth / 2 + tx, centroY = window.innerHeight / 2 + ty;
        var ox = cx - centroX, oy = cy - centroY;
        var f = nuevo / escala;
        tx += ox * (1 - f);
        ty += oy * (1 - f);
        escala = nuevo;
        if (escala <= 1) { tx = 0; ty = 0; }   // vuelto al original: recentrar
        aplicar();
    }

    function reset() { escala = 1; tx = 0; ty = 0; aplicar(); }
    function aplicar() {
        img.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + escala + ')';
        img.classList.toggle('ampliada', escala > 1);
    }

    function abrir(src, alt) {
        // Recrear si nunca se creó O si quedó DESCONECTADO del DOM: la navegación mejorada de Blazor
        // reescribe el <body> y se lleva el overlay (que no es parte del HTML del servidor), pero deja
        // la variable apuntando al viejo. Sin este chequeo, la 2ª foto agregaba el bloqueo de scroll
        // pero el overlay no estaba en pantalla → quedaba todo trabado hasta recargar.
        if (!overlay || !overlay.isConnected) crear();
        img.src = src;
        img.alt = alt || '';
        reset();
        document.documentElement.classList.add('mk-zoom-bloqueado');
        overlay.classList.add('activa');
    }
    function cerrar() {
        // El bloqueo vive en <html> (sobrevive a la navegación): se saca siempre, aunque el overlay
        // ya no esté, para no dejar el scroll trabado.
        document.documentElement.classList.remove('mk-zoom-bloqueado');
        if (overlay) overlay.classList.remove('activa');
        punteros = {}; pinchDist = 0;
    }

    // Delegación: sirve para la foto que ya está y para las que llegan por navegación mejorada,
    // sin re-enganchar nada en cada carga.
    document.addEventListener('click', function (e) {
        var foto = e.target.closest ? e.target.closest('.mk-producto-foto .mk-zoomable') : null;
        if (!foto) return;
        e.preventDefault();
        // Prefiere la versión grande (1200) para que el zoom tenga resolución.
        abrir(foto.getAttribute('data-zoom') || foto.getAttribute('src'), foto.getAttribute('alt'));
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') cerrar();
    });

    // Al navegar (navegación mejorada de Blazor) se cierra el visor y se saca el bloqueo de scroll,
    // así cambiar de página nunca deja el scroll trabado ni un overlay huérfano.
    if (window.Blazor && Blazor.addEventListener) {
        Blazor.addEventListener('enhancedload', cerrar);
    }

    // Marca que el visor está disponible: el CSS recién ahí muestra el cursor de lupa en la foto.
    document.documentElement.classList.add('js-zoom');
})();
