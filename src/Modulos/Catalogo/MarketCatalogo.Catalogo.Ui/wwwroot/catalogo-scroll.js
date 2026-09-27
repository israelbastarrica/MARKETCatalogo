// Scroll infinito del catálogo. Mejora progresiva: sin esto la paginación por links (Anterior/
// Siguiente, cada uno con su URL real) funciona igual. Con esto, la nav de paginación pasa a ser
// el sensor que dispara la carga de la página siguiente al entrar en pantalla; se pide la página
// completa (misma URL que el link "Siguiente"), se sacan de ahí los artículos y la nav nueva, y
// se van agregando al final de la grilla actual. No hay endpoint aparte: es la misma página SSR.
(function () {
    // Sirve al catálogo PÚBLICO (.mk-catalogo .mk-grilla) y al INTERNO (.mk-int .mk-int-grilla): sólo
    // uno existe por página, así que el selector combinado devuelve el que corresponde.
    var SEL_GRILLA = '.mk-catalogo .mk-grilla, .mk-int .mk-int-grilla';
    var SEL_PAGINACION = '.mk-catalogo .mk-paginacion, .mk-int .mk-paginacion';

    function initInfinita() {
        var grilla = document.querySelector(SEL_GRILLA);
        var paginacion = document.querySelector(SEL_PAGINACION);
        if (!grilla || !paginacion) return;
        // Sin esto no hay forma de disparar la carga al llegar al final: se deja la paginación
        // por links tal cual, en vez de ocultarla y dejar al usuario sin cómo pasar de página.
        if (!('IntersectionObserver' in window)) return;

        document.documentElement.classList.add('js-infinita');

        var cargando = false;
        var io = null;
        // Cuántas páginas EXTRA se agregaron por scroll (la 1 la trae el SSR). Es lo que hay que
        // reponer al volver de una ficha para dejar la grilla como estaba.
        var paginasExtra = 0;

        function siguienteUrl() {
            var link = paginacion.querySelector('a[rel="next"]');
            return link ? link.getAttribute('href') : null;
        }

        function terminar(mensaje) {
            paginacion.querySelector('.mk-cargando-mas').textContent = mensaje || '';
            if (io) io.disconnect();
        }

        function cargarMas() {
            var url = siguienteUrl();
            if (!url) { terminar(); return Promise.resolve(false); }
            if (cargando) return Promise.resolve(false);
            cargando = true;
            paginacion.querySelector('.mk-cargando-mas').textContent = 'Cargando más…';

            return fetch(url)
                .then(function (r) { return r.text(); })
                .then(function (html) {
                    var doc = new DOMParser().parseFromString(html, 'text/html');
                    var nuevaGrilla = doc.querySelector(SEL_GRILLA);
                    var nuevaPaginacion = doc.querySelector(SEL_PAGINACION);

                    if (nuevaGrilla) {
                        while (nuevaGrilla.firstElementChild) {
                            grilla.appendChild(nuevaGrilla.firstElementChild);
                        }
                    }

                    // NO se toca la URL de la barra. Antes se hacía history.replaceState a ?pag=N y
                    // eso rompía el "volver atrás": al entrar a un artículo y volver, el navegador
                    // restauraba ?pag=5 y el SSR renderizaba SÓLO esa página (unos pocos artículos),
                    // no todo lo scrolleado — y recargar tampoco lo arreglaba. Dejando la URL en la
                    // página inicial, "atrás" vuelve a la grilla completa (página 1). La carga de más
                    // páginas no depende de la URL: usa el link "Siguiente" del DOM (siguienteUrl()).
                    cargando = false;
                    paginasExtra++;

                    if (nuevaPaginacion) {
                        paginacion.replaceWith(nuevaPaginacion);
                        paginacion = nuevaPaginacion;
                        if (io) { io.disconnect(); io.observe(paginacion); }
                    } else {
                        terminar();
                    }
                    return true;
                })
                .catch(function () {
                    // Si falla (red caída, etc.) se deja el link "Siguiente" real como red de
                    // contención: display:none es de .js-infinita, no de la nav en sí.
                    cargando = false;
                    terminar();
                    document.documentElement.classList.remove('js-infinita');
                    return false;
                });
        }

        // ===== Volver a la ficha y regresar: dejar la grilla donde estaba =====
        // La URL NO cambia con el scroll (ver arriba), así que al volver el SSR entrega la página 1 y
        // se pierde todo lo scrolleado. Se guarda cuántas páginas había y a qué altura estaba, y al
        // regresar a la MISMA url se reponen y se baja al mismo punto.
        var CLAVE = 'mk-grilla:' + location.pathname + location.search;
        var TOPE_PAGINAS = 12;          // techo: reponer 12 páginas es ~1 seg; más que eso no vale la pena
        var VIGENCIA = 30 * 60 * 1000;  // media hora: más viejo que eso, arrancar de cero

        function guardarEstado() {
            try {
                if (paginasExtra === 0 && window.scrollY < 200) { sessionStorage.removeItem(CLAVE); return; }
                sessionStorage.setItem(CLAVE, JSON.stringify({
                    paginas: paginasExtra, y: window.scrollY, t: Date.now()
                }));
            } catch (e) { /* modo privado o storage lleno: se pierde el lugar, no se rompe nada */ }
        }

        function leerEstado() {
            try {
                var crudo = sessionStorage.getItem(CLAVE);
                if (!crudo) return null;
                var e = JSON.parse(crudo);
                sessionStorage.removeItem(CLAVE);   // de un solo uso: recargar a mano arranca limpio
                return (Date.now() - e.t) < VIGENCIA ? e : null;
            } catch (e) { return null; }
        }

        // pagehide cubre navegar a la ficha, cerrar y el bfcache; en iOS es el único confiable.
        window.addEventListener('pagehide', guardarEstado);

        function restaurar() {
            var e = leerEstado();
            if (!e) return;
            if (io) io.disconnect();                        // que el observer no cargue en paralelo
            var faltan = Math.min(e.paginas, TOPE_PAGINAS);

            function paso() {
                if (faltan <= 0) {
                    window.scrollTo(0, e.y);
                    // De nuevo en el frame siguiente: con las fotos recién agregadas el alto todavía se
                    // está asentando y el primer scroll puede quedarse corto.
                    requestAnimationFrame(function () { window.scrollTo(0, e.y); });
                    if (io) io.observe(paginacion);          // a partir de acá sigue el scroll normal
                    return;
                }
                faltan--;
                // Siga o falle una página, se avanza igual: mejor restaurar de menos que colgarse.
                cargarMas().then(paso);
            }
            paso();
        }

        // Si el navegador lo trae del bfcache, el DOM y el scroll vuelven intactos: no se repone nada
        // (si no, se duplicarían las tarjetas).
        window.addEventListener('pageshow', function (ev) { if (ev.persisted) leerEstado(); });

        if (!siguienteUrl()) { restaurar(); return; }

        io = new IntersectionObserver(function (entries) {
            entries.forEach(function (e) { if (e.isIntersecting) cargarMas(); });
        }, { rootMargin: '800px 0px' });
        io.observe(paginacion);
        restaurar();
    }

    if (document.readyState !== 'loading') initInfinita();
    else document.addEventListener('DOMContentLoaded', initInfinita);
    if (window.Blazor && Blazor.addEventListener) {
        Blazor.addEventListener('enhancedload', initInfinita);
    }
})();
