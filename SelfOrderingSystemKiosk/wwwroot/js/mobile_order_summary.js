(function () {
    function setupSheet() {
        const summary = document.querySelector('.order-summary');
        if (!summary || summary.dataset.mobileSheetReady === 'true') return;

        summary.dataset.mobileSheetReady = 'true';
        summary.classList.add('cart-drawer', 'collapsed');

        const fab = document.createElement('button');
        fab.type = 'button';
        fab.className = 'cart-fab';
        fab.setAttribute('aria-label', 'Open cart');
        fab.innerHTML = '<i class="bi bi-cart-fill"></i><span class="cart-fab-count">0</span>';
        summary.insertAdjacentElement('afterend', fab);

        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'cart-drawer-close';
        close.setAttribute('aria-label', 'Close cart');
        close.innerHTML = '<i class="bi bi-x-lg"></i>';
        summary.prepend(close);

        const backdrop = document.createElement('button');
        backdrop.type = 'button';
        backdrop.className = 'cart-drawer-backdrop';
        backdrop.setAttribute('aria-label', 'Close cart');
        summary.insertAdjacentElement('afterend', backdrop);

        function setExpanded(expanded) {
            summary.classList.toggle('collapsed', !expanded);
            backdrop.classList.toggle('active', expanded);
            fab.setAttribute('aria-label', expanded ? 'Close cart' : 'Open cart');
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

        new MutationObserver(syncCount).observe(summary, {
            childList: true,
            subtree: true,
            characterData: true
        });
        syncCount();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', setupSheet);
    } else {
        setupSheet();
    }
})();
