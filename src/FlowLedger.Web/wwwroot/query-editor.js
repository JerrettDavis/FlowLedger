// query-editor.js
// Binds the query language service (query-intellisense.js) to a plain <textarea> with a syntax-highlight
// overlay, an autocomplete popup, hover tooltips and a signature/status bar. No dependencies.
//
// Markup (rendered by QueryPage.razor):
//   <div class="fl-editor"> <pre class="fl-editor-overlay"></pre> <textarea class="fl-editor-input"></textarea> </div>
//   <div class="fl-editor-status"></div>

import { complete, hover, signature, highlight } from './query-intellisense.js';

const KIND_ICON = { dataset: 'DS', operator: '|', keyword: 'kw', function: 'fx', aggregate: 'Σ', column: 'col', value: '“”', punct: '|' };

export function create(host, dotnet, schema, initial) {
    const ta = host.querySelector('textarea');
    const overlay = host.querySelector('.fl-editor-overlay');
    const status = host.parentElement.querySelector('.fl-editor-status');
    const valuesCache = {};
    const pending = new Set();
    let errorAt = -1;
    let popup = null;
    let current = null; // { res, index, navigated, prefix }
    let tip = null;
    let tipTimer = 0;
    let notifyTimer = 0;

    ta.value = initial ?? '';

    // ── Rendering ──────────────────────────────────────────────────────────
    function render() {
        overlay.innerHTML = highlight(ta.value, schema, errorAt);
        ta.style.height = 'auto';
        ta.style.height = `${Math.max(ta.scrollHeight + 2, 190)}px`;
    }

    function notify() {
        clearTimeout(notifyTimer);
        notifyTimer = setTimeout(() => dotnet.invokeMethodAsync('OnQueryChanged', ta.value), 150);
    }

    // ── Caret geometry (mirror-div technique) ──────────────────────────────
    function caretRect() {
        const cs = getComputedStyle(ta);
        const mirror = document.createElement('div');
        for (const p of ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'padding', 'borderWidth', 'boxSizing', 'whiteSpace', 'overflowWrap', 'tabSize']) {
            mirror.style[p] = cs[p];
        }
        mirror.style.position = 'absolute';
        mirror.style.visibility = 'hidden';
        mirror.style.width = `${ta.clientWidth}px`;
        mirror.style.whiteSpace = 'pre-wrap';
        mirror.textContent = ta.value.slice(0, ta.selectionStart);
        const marker = document.createElement('span');
        marker.textContent = '​';
        mirror.appendChild(marker);
        host.appendChild(mirror);
        const box = ta.getBoundingClientRect();
        const lineHeight = parseFloat(cs.lineHeight) || 20;
        const rect = { x: box.left + marker.offsetLeft, y: box.top + marker.offsetTop, h: lineHeight };
        host.removeChild(mirror);
        return rect;
    }

    // ── Completion popup ───────────────────────────────────────────────────
    function closePopup() {
        popup?.remove();
        popup = null;
        current = null;
    }

    function showPopup(res) {
        const wasIndex = current && current.res.items[0]?.label === res.items[0]?.label ? current.index : 0;
        current = { res, index: Math.min(wasIndex, res.items.length - 1), navigated: current?.navigated ?? false };
        if (!popup) {
            popup = document.createElement('div');
            popup.className = 'fl-ac-popup';
            popup.setAttribute('role', 'listbox');
            popup.addEventListener('mousedown', (e) => e.preventDefault());
            document.body.appendChild(popup);
        }
        paintPopup();
        const r = caretRect();
        const width = 420;
        popup.style.left = `${Math.max(8, Math.min(r.x, window.innerWidth - width - 12))}px`;
        const below = r.y + r.h + 4;
        popup.style.top = `${below}px`;
        const ph = popup.getBoundingClientRect().height;
        if (below + ph > window.innerHeight - 8 && r.y - ph - 4 > 8) { popup.style.top = `${r.y - ph - 4}px`; }
    }

    function paintPopup() {
        const { res, index } = current;
        popup.replaceChildren();
        const list = document.createElement('div');
        list.className = 'fl-ac-list';
        res.items.forEach((item, i) => {
            const row = document.createElement('div');
            row.className = `fl-ac-item${i === index ? ' sel' : ''}`;
            row.setAttribute('role', 'option');
            row.setAttribute('aria-selected', i === index ? 'true' : 'false');
            const icon = document.createElement('span');
            icon.className = `fl-ac-icon k-${item.kind}`;
            icon.textContent = KIND_ICON[item.kind] ?? '';
            const label = document.createElement('span');
            label.className = 'fl-ac-label';
            label.textContent = item.label;
            const detail = document.createElement('span');
            detail.className = 'fl-ac-detail';
            detail.textContent = item.detail ?? '';
            row.append(icon, label, detail);
            row.addEventListener('click', () => accept(i));
            list.appendChild(row);
        });
        popup.appendChild(list);
        const doc = document.createElement('div');
        doc.className = 'fl-ac-doc';
        doc.textContent = res.items[index]?.doc ?? '';
        popup.appendChild(doc);
        list.querySelector('.sel')?.scrollIntoView({ block: 'nearest' });
    }

    function move(delta) {
        const n = current.res.items.length;
        current.index = (current.index + delta + n) % n;
        current.navigated = true;
        paintPopup();
    }

    function replaceRange(from, to, text, caretBack = 0) {
        ta.focus();
        ta.setSelectionRange(from, to);
        // execCommand keeps the browser's undo stack intact; fall back where unsupported.
        if (!document.execCommand('insertText', false, text)) { ta.setRangeText(text, from, to, 'end'); }
        const caret = from + text.length - caretBack;
        ta.setSelectionRange(caret, caret);
        afterEdit();
    }

    function accept(i) {
        if (!current) { return; }
        const { res } = current;
        const item = res.items[i ?? current.index];
        let { from, to } = res;
        let text = item.insert;
        let back = 0;
        if (item.kind === 'function' || item.kind === 'aggregate') {
            const hasParen = ta.value[to] === '(';
            if (hasParen) { text = item.label; to = to; }
            else if (item.paramless) { text = `${item.label}()`; }
            else { text = `${item.label}()`; back = 1; }
        }
        let chain = /[ (]$/.test(item.insert) || item.kind === 'operator' && /[=<>]/.test(item.label);
        if (item.kind === 'value') {
            const closing = ta.value[to];
            if (closing === '"' || closing === "'") { to += 1; text += closing; }
            else { text += '"'; }
            chain = false;
        }
        closePopup();
        replaceRange(from, to, text, back);
        if (chain && item.kind !== 'function' && item.kind !== 'aggregate') { setTimeout(() => refresh(true), 0); }
        else if (back === 1) { setTimeout(() => refresh(true), 0); }
    }

    async function refresh(explicit = false) {
        const caret = ta.selectionStart;
        if (ta.selectionEnd !== caret) { closePopup(); return; }
        const res = complete(ta.value, caret, schema, valuesCache, explicit);
        if (!res) { closePopup(); return; }
        if (res.wantValues) {
            const { key, dataset, column } = res.wantValues;
            if (!pending.has(key)) {
                pending.add(key);
                try {
                    valuesCache[key] = (await dotnet.invokeMethodAsync('GetValues', dataset, column)) ?? [];
                } catch {
                    valuesCache[key] = [];
                } finally {
                    pending.delete(key);
                }
                if (document.activeElement === ta && ta.selectionStart === caret) { refresh(explicit); }
            }
            return;
        }
        if (res.items.length === 0) { closePopup(); return; }
        // Nothing left to complete: the one suggestion is exactly what has been typed.
        const typed = ta.value.slice(res.from, caret);
        if (res.items.length === 1 && res.items[0].label === typed) { closePopup(); return; }
        if (!popup) { current = null; }
        showPopup(res);
        current.prefix = typed;
    }

    // ── Status / signature bar ─────────────────────────────────────────────
    function paintStatus() {
        if (!status) { return; }
        const sig = signature(ta.value, ta.selectionStart, schema);
        status.replaceChildren();
        if (!sig) {
            status.textContent = 'Ctrl+Space for suggestions · Ctrl+Enter to run · hover any word for help';
            return;
        }
        const label = document.createElement('span');
        label.className = 'fl-sig-label';
        if (sig.params.length > 0) {
            label.append(`${sig.name}(`);
            sig.params.forEach((p, i) => {
                const b = document.createElement('span');
                b.textContent = p;
                if (i === sig.active) { b.className = 'fl-sig-active'; }
                label.append(b);
                if (i < sig.params.length - 1) { label.append(', '); }
            });
            label.append(')');
        } else {
            label.textContent = sig.label;
        }
        const doc = document.createElement('span');
        doc.className = 'fl-sig-doc';
        doc.textContent = ` — ${sig.doc}`;
        status.append(label, doc);
    }

    // ── Hover tooltips ─────────────────────────────────────────────────────
    function hideTip() {
        clearTimeout(tipTimer);
        tip?.remove();
        tip = null;
    }

    function onMouseMove(e) {
        clearTimeout(tipTimer);
        const { clientX: x, clientY: y } = e;
        tipTimer = setTimeout(() => {
            // The textarea sits on top of the overlay; peek through it to find the token under the pointer.
            ta.style.pointerEvents = 'none';
            overlay.style.pointerEvents = 'auto';
            const el = document.elementFromPoint(x, y);
            ta.style.pointerEvents = '';
            overlay.style.pointerEvents = '';
            const start = el?.dataset?.s;
            if (start === undefined) { hideTip(); return; }
            const info = hover(ta.value, Number(start), schema);
            if (!info) { hideTip(); return; }
            hideTip();
            tip = document.createElement('div');
            tip.className = `fl-tip k-${info.kind}`;
            const title = document.createElement('div');
            title.className = 'fl-tip-title';
            title.textContent = info.title;
            const body = document.createElement('div');
            body.className = 'fl-tip-body';
            body.textContent = info.body;
            tip.append(title, body);
            document.body.appendChild(tip);
            const w = tip.getBoundingClientRect();
            tip.style.left = `${Math.min(x + 12, window.innerWidth - w.width - 12)}px`;
            tip.style.top = `${y + 18 + w.height > window.innerHeight ? y - w.height - 10 : y + 18}px`;
        }, 280);
    }

    // ── Events ─────────────────────────────────────────────────────────────
    function afterEdit() {
        render();
        notify();
        paintStatus();
    }

    ta.addEventListener('input', (e) => {
        hideTip();
        afterEdit();
        const t = e.inputType ?? '';
        if (t.startsWith('insert') || (t.startsWith('delete') && popup)) { refresh(false); } else { closePopup(); }
    });

    ta.addEventListener('keydown', (e) => {
        if (e.ctrlKey && e.key === 'Enter') {
            e.preventDefault();
            closePopup();
            clearTimeout(notifyTimer);
            dotnet.invokeMethodAsync('RunRequested', ta.value); // send the text with the request; the debounced sync may not have fired
            return;
        }
        if (e.ctrlKey && e.key === ' ') {
            e.preventDefault();
            refresh(true);
            return;
        }
        if (!popup) { return; }
        switch (e.key) {
            case 'ArrowDown': e.preventDefault(); move(1); break;
            case 'ArrowUp': e.preventDefault(); move(-1); break;
            case 'Enter':
                // With nothing typed and no arrow-key choice, Enter is just a newline.
                if (!current.navigated && !current.prefix) { closePopup(); break; }
                e.preventDefault();
                accept();
                break;
            case 'Tab': e.preventDefault(); accept(); break;
            case 'Escape': e.preventDefault(); closePopup(); break;
            default: break;
        }
    });

    ta.addEventListener('keyup', (e) => {
        if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) { closePopup(); }
        paintStatus();
    });
    ta.addEventListener('click', () => { closePopup(); paintStatus(); });
    ta.addEventListener('blur', () => {
        clearTimeout(notifyTimer);
        dotnet.invokeMethodAsync('OnQueryChanged', ta.value);
        setTimeout(closePopup, 120);
    });
    ta.addEventListener('mousemove', onMouseMove);
    ta.addEventListener('mouseleave', hideTip);
    ta.addEventListener('scroll', hideTip);
    window.addEventListener('resize', render);

    render();
    paintStatus();

    return {
        setValue(text) {
            ta.value = text ?? '';
            errorAt = -1;
            closePopup();
            afterEdit();
        },
        insertAtCaret(text) {
            const pos = ta.selectionStart ?? ta.value.length;
            const needsSpace = pos > 0 && !/\s$/.test(ta.value.slice(0, pos));
            replaceRange(pos, ta.selectionEnd ?? pos, (needsSpace ? ' ' : '') + text);
        },
        setError(position) {
            errorAt = position;
            render();
        },
        focus() { ta.focus(); },
        dispose() {
            clearTimeout(notifyTimer);
            hideTip();
            closePopup();
            window.removeEventListener('resize', render);
        },
    };
}
