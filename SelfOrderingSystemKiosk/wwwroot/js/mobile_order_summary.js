(function () {
    function isMobile() {
        return window.matchMedia('(max-width: 768px)').matches;
    }

    function setupSheet() {
        const summary = document.querySelector('.order-summary');
        if (!summary || summary.dataset.mobileSheetReady === 'true') return;

        summary.dataset.mobileSheetReady = 'true';
        summary.classList.add('mobile-sheet');

        const handle = document.createElement('button');
        handle.type = 'button';
        handle.className = 'order-sheet-handle';
        handle.setAttribute('aria-label', 'Open order summary');
        summary.prepend(handle);

        let startY = 0;
        let moved = false;

        function setExpanded(expanded) {
            summary.classList.toggle('collapsed', !expanded);
            handle.setAttribute('aria-label', expanded ? 'Collapse order summary' : 'Open order summary');
        }

        function syncMode() {
            if (isMobile()) {
                summary.classList.add('mobile-sheet');
                if (!summary.classList.contains('collapsed') && !summary.classList.contains('expanded-once')) {
                    setExpanded(false);
                }
            } else {
                summary.classList.remove('mobile-sheet', 'collapsed', 'expanded-once');
            }
        }

        handle.addEventListener('pointerdown', event => {
            startY = event.clientY;
            moved = false;
            handle.setPointerCapture(event.pointerId);
        });

        handle.addEventListener('pointermove', event => {
            if (!startY) return;
            if (Math.abs(event.clientY - startY) > 10) moved = true;
        });

        handle.addEventListener('pointerup', event => {
            const delta = event.clientY - startY;
            startY = 0;

            if (!moved) {
                const expanded = summary.classList.contains('collapsed');
                summary.classList.add('expanded-once');
                setExpanded(expanded);
                return;
            }

            summary.classList.add('expanded-once');
            if (delta < -24) setExpanded(true);
            if (delta > 24) setExpanded(false);
        });

        window.addEventListener('resize', syncMode);
        syncMode();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', setupSheet);
    } else {
        setupSheet();
    }
})();
