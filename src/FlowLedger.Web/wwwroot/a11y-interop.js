// MudBlazor's data-grid column-filter row renders icon-only buttons (filter operator, clear) with no accessible name,
// which axe reports as button-name (critical). This is third-party markup we cannot template, so name those buttons
// after their column once they appear. Only buttons that have no text and no label already are touched.
(function () {
    function columnTitle(cell) {
        const row = cell.parentElement;
        const table = cell.closest('table');
        if (!row || !table) return 'Column';
        const index = Array.prototype.indexOf.call(row.children, cell);
        const headerRow = table.querySelector('thead tr');
        const header = headerRow && headerRow.children[index];
        const text = header && (header.innerText || header.textContent || '').trim();
        return text || 'Column';
    }

    function label() {
        document.querySelectorAll('.mud-table-head button, thead button').forEach((b) => {
            if (b.hasAttribute('aria-label') || b.hasAttribute('aria-labelledby') || (b.textContent || '').trim()) return;
            const cell = b.closest('th, td');
            if (!cell) return;
            const buttons = Array.from(cell.querySelectorAll('button'));
            const title = columnTitle(cell);
            b.setAttribute('aria-label', buttons.indexOf(b) === 0 ? `${title}: filter options` : `${title}: clear filter`);
        });
    }

    let pending = false;
    function schedule() {
        if (pending) return;
        pending = true;
        requestAnimationFrame(() => { pending = false; label(); });
    }

    function start() {
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
