// Resizable table columns. Columns auto-size to their content on load (the browser's
// auto table layout); drag the right edge of a header to resize, double-click it to
// auto-fit again. Widths persist per page + column title in localStorage.
(function () {
    const KEY = 'fl-col-widths';
    const SELECTOR = '.mud-table-root, .mud-simple-table table';
    let store = {};
    try { store = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { store = {}; }
    const save = () => { try { localStorage.setItem(KEY, JSON.stringify(store)); } catch { } };

    const tableKey = (table, idx) => location.pathname + '|' + (table.getAttribute('aria-label') || idx);
    const colKey = (th, i) => (th.textContent || '').trim().replace(/\d+$/, '') || ('col' + i);

    function setWidth(th, w) {
        th.style.width = w + 'px';
        th.style.minWidth = w + 'px';
        th.style.maxWidth = w + 'px';
    }

    function clearWidth(th) {
        th.style.width = th.style.minWidth = th.style.maxWidth = '';
    }

    function autoFit(table, th) {
        const i = th.cellIndex;
        clearWidth(th);
        let max = th.scrollWidth;
        table.querySelectorAll('tbody tr').forEach(tr => {
            const td = tr.cells[i];
            if (!td) return;
            const prev = td.style.cssText;
            td.style.cssText += ';max-width:none;overflow:visible;white-space:nowrap';
            max = Math.max(max, td.scrollWidth);
            td.style.cssText = prev;
        });
        return Math.min(Math.ceil(max) + 2, 700);
    }

    function enhance(table, tIdx) {
        const head = table.tHead && table.tHead.rows[0];
        if (!head) return;
        const tk = tableKey(table, tIdx);
        const saved = store[tk] || (store[tk] = {});
        table.classList.add('fl-resizable');
        [...head.cells].forEach((th, i) => {
            if (th.querySelector(':scope > .fl-col-handle')) return;
            if (th.classList.contains('mud-table-cell-hidden') || th.offsetWidth === 0) return;
            const ck = colKey(th, i);
            if (saved[ck] > 20 && !th.dataset.flApplied) {
                setWidth(th, saved[ck]);
            }
            th.dataset.flApplied = '1';
            const h = document.createElement('span');
            h.className = 'fl-col-handle';
            h.setAttribute('role', 'separator');
            h.setAttribute('aria-label', 'Resize column');
            h.addEventListener('click', e => e.stopPropagation());
            h.addEventListener('dblclick', e => {
                e.stopPropagation();
                const w = autoFit(table, th);
                setWidth(th, w);
                if (w > 20) { saved[ck] = w; save(); }
            });
            h.addEventListener('pointerdown', e => {
                e.preventDefault(); e.stopPropagation();
                h.setPointerCapture(e.pointerId);
                const startX = e.clientX, startW = th.getBoundingClientRect().width;
                document.body.classList.add('fl-col-resizing');
                const move = ev => setWidth(th, Math.max(40, Math.round(startW + ev.clientX - startX)));
                const up = () => {
                    h.removeEventListener('pointermove', move);
                    h.removeEventListener('pointerup', up);
                    document.body.classList.remove('fl-col-resizing');
                    const nw = Math.round(th.getBoundingClientRect().width); if (nw > 20) { saved[ck] = nw; save(); }
                };
                h.addEventListener('pointermove', move);
                h.addEventListener('pointerup', up);
            });
            th.appendChild(h);
        });
    }

    let scheduled = false;
    function scan() {
        scheduled = false;
        document.querySelectorAll(SELECTOR).forEach((t, i) => { try { enhance(t, i); } catch { } });
    }
    const schedule = () => { if (!scheduled) { scheduled = true; requestAnimationFrame(scan); } };

    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    document.addEventListener('DOMContentLoaded', schedule);
    window.addEventListener('resize', schedule);
    schedule();
})();
