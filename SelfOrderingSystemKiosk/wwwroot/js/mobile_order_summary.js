(function () {
    function setupSheet() {
        const summary = document.querySelector('.order-summary');
        if (!summary || summary.dataset.mobileSheetReady === 'true') return;

        summary.dataset.mobileSheetReady = 'true';
        summary.classList.add('cart-drawer', 'collapsed');

        const header = summary.querySelector('.summary-header');
        const list = summary.querySelector('.summary-list');
        const footer = summary.querySelector('.summary-footer');
        const mobileQuery = window.matchMedia('(max-width: 768px)');

        function collapsedTransform() {
            return 'translateX(100%)';
        }

        function applyDrawerLayout() {
            Object.assign(summary.style, {
                position: 'fixed',
                inset: '0',
                top: '0',
                right: '0',
                bottom: '0',
                left: '0',
                width: '100vw',
                maxWidth: '100vw',
                height: '100dvh',
                minWidth: '0',
                maxHeight: 'none',
                margin: '0',
                borderRadius: '0',
                boxSizing: 'border-box',
                overflow: 'hidden',
                zIndex: '5000',
                display: 'grid',
                gridTemplateRows: 'auto minmax(0, 1fr) auto',
                alignItems: 'stretch',
                justifyItems: 'stretch',
                gap: mobileQuery.matches ? '10px' : '16px',
                padding: mobileQuery.matches ? '14px' : '20px max(20px, calc((100vw - 1100px) / 2))',
                background: '#fff8f2',
                boxShadow: 'none'
            });

            if (summary.classList.contains('collapsed')) {
                summary.style.transform = collapsedTransform();
            }
        }

        applyDrawerLayout();

        if (header) {
            Object.assign(header.style, {
                width: '100%',
                minWidth: '0',
                maxWidth: '100%',
                justifySelf: 'stretch',
                alignSelf: 'stretch',
                boxSizing: 'border-box'
            });
        }
        if (list) {
            Object.assign(list.style, {
                width: '100%',
                minHeight: '0',
                maxHeight: 'none',
                justifySelf: 'stretch',
                alignSelf: 'stretch',
                overflowY: 'auto',
                overflowX: 'hidden',
                boxSizing: 'border-box'
            });
        }
        if (footer) {
            Object.assign(footer.style, {
                width: '100%',
                boxSizing: 'border-box',
                maxWidth: '100%',
                justifySelf: 'stretch',
                alignSelf: 'stretch',
                overflow: 'hidden'
            });

            const breakdown = footer.querySelector('.order-breakdown');
            const confirm = footer.querySelector('.confirm-btn');
            if (breakdown) {
                Object.assign(breakdown.style, {
                    width: '100%',
                    maxWidth: '100%',
                    alignSelf: 'stretch',
                    boxSizing: 'border-box'
                });
            }
            if (confirm) {
                Object.assign(confirm.style, {
                    width: '100%',
                    maxWidth: '100%',
                    alignSelf: 'stretch',
                    boxSizing: 'border-box'
                });
            }
        }

        const fab = document.createElement('button');
        fab.type = 'button';
        fab.className = 'cart-fab';
        fab.setAttribute('aria-label', 'View order summary');
        fab.innerHTML = '<i class="bi bi-cart-fill"></i><span class="cart-fab-count">0</span>';
        summary.insertAdjacentElement('afterend', fab);

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'cart-drawer-close';
        close.setAttribute('aria-label', 'Back to menu');
        close.innerHTML = '<i class="bi bi-arrow-left"></i>';
        summary.prepend(close);

        const backdrop = document.createElement('button');
        backdrop.type = 'button';
        backdrop.className = 'cart-drawer-backdrop';
        backdrop.setAttribute('aria-label', 'Back to menu');
        summary.insertAdjacentElement('afterend', backdrop);

        function setExpanded(expanded) {
            summary.classList.toggle('collapsed', !expanded);
            backdrop.classList.toggle('active', expanded);
            fab.setAttribute('aria-label', expanded ? 'Back to menu' : 'View order summary');
            summary.style.opacity = expanded ? '1' : '0';
            summary.style.visibility = expanded ? 'visible' : 'hidden';
            summary.style.pointerEvents = expanded ? 'auto' : 'none';
            summary.style.transform = expanded ? 'translate(0, 0)' : collapsedTransform();
            document.body.style.overflow = expanded ? 'hidden' : '';
            document.documentElement.style.overflow = expanded ? 'hidden' : '';
        }

        function syncCount() {
            const label = summary.querySelector('.item-count');
            const count = fab.querySelector('.cart-fab-count');
            const match = (label?.textContent || '').match(/\d+/);
            count.textContent = match ? match[0] : '0';
        }

        fab.addEventListener('click', () => setExpanded(summary.classList.contains('collapsed')));
        close.addEventListener('click', () => setExpanded(false));
        backdrop.addEventListener('click', () => setExpanded(false));
        document.addEventListener('keydown', event => {
            if (event.key === 'Escape') setExpanded(false);
        });
        window.addEventListener('resize', () => {
            applyDrawerLayout();
            if (summary.classList.contains('collapsed')) {
                document.body.style.overflow = '';
                document.documentElement.style.overflow = '';
            } else {
                summary.style.transform = 'translate(0, 0)';
            }
        });

        new MutationObserver(syncCount).observe(summary, {
            childList: true,
            subtree: true,
            characterData: true
        });
        syncCount();
        setExpanded(false);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', setupSheet);
    } else {
        setupSheet();
    }
})();
