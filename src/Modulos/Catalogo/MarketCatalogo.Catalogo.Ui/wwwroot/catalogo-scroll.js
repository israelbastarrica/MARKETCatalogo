// Scroll infinito del catálogo. Mejora progresiva: sin esto la paginación por links (Anterior/
// Siguiente, cada uno con su URL real) funciona igual. Con esto, la nav de paginación pasa a ser
// el sensor que dispara la carga de la página siguiente al entrar en pantalla; se pide la página
// completa (misma URL que el link "Siguiente"), se sacan de ahí los artículos y la nav nueva, y
// se van agregando al final de la grilla actual. No hay endpoint aparte: es la misma página SSR.
(function () {
    function initInfinita() {
        var grilla = document.querySelector('.mk-catalogo .mk-grilla');
        var paginacion = document.querySelector('.mk-catalogo .mk-paginacion');
        if (!grilla || !paginacion) return;
        // Sin esto no hay forma de disparar la carga al llegar al final: se deja la paginación
        // por links tal cual, en vez de ocultarla y dejar al usuario sin cómo pasar de página.
        if (!('IntersectionObserver' in window)) return;

        document.documentElement.classList.add('js-infinita');

        var cargando = false;
        var io = null;

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
            if (!url) { terminar(); return; }
            if (cargando) return;
            cargando = true;
            paginacion.querySelector('.mk-cargando-mas').textContent = 'Cargando más…';

            fetch(url)
                .then(function (r) { return r.text(); })
                .then(function (html) {
                    var doc = new DOMParser().parseFromString(html, 'text/html');
                    var nuevaGrilla = doc.querySelector('.mk-catalogo .mk-grilla');
                    var nuevaPaginacion = doc.querySelector('.mk-catalogo .mk-paginacion');

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

                    if (nuevaPaginacion) {
                        paginacion.replaceWith(nuevaPaginacion);
                        paginacion = nuevaPaginacion;
                        if (io) { io.disconnect(); io.observe(paginacion); }
                    } else {
                        terminar();
                    }
                })
                .catch(function () {
                    // Si falla (red caída, etc.) se deja el link "Siguiente" real como red de
                    // contención: display:none es de .js-infinita, no de la nav en sí.
                    cargando = false;
                    terminar();
                    document.documentElement.classList.remove('js-infinita');
                });
        }

        if (!siguienteUrl()) return;

        io = new IntersectionObserver(function (entries) {
            entries.forEach(function (e) { if (e.isIntersecting) cargarMas(); });
        }, { rootMargin: '800px 0px' });
        io.observe(paginacion);
    }

    if (document.readyState !== 'loading') initInfinita();
    else document.addEventListener('DOMContentLoaded', initInfinita);
    if (window.Blazor && Blazor.addEventListener) {
        Blazor.addEventListener('enhancedload', initInfinita);
    }
})();
