// Share mode: strips every dollar amount, account mask and large bare number from the rendered page
// while leaving charts, bar/segment proportions, categories, names of categories and dates intact, so a
// screenshot shows where money moves without revealing how much. It works on the rendered DOM (text
// nodes, SVG labels, tooltips, title/aria-label), so every page and later re-renders are covered, and
// toggling off restores the originals. Nothing is sent to the server; the setting is per-browser.
(function () {
    'use strict';

    var KEY = 'fl-share';
    var CLASS = 'fl-share';
    var MASK = '•••'; // •••
    var ATTRS = ['title', 'aria-label', 'placeholder'];

    // $1,234.56  -$12  +$3.5k  1.2M (with $)  — sign is kept (direction of flow), the amount is not.
    var MONEY = /\$\s?\d[\d,]*(?:\.\d+)?(?:[kKmMbB]\b)?/g;
    // ••••1234 / ****1234 / x1234 / ...1234 account masks
    var MASKED_ACCT = /(?:[•*]{2,}|\bx{2,}|\.{3}|…)\s?\d{3,4}\b/gi;
    // Whole-node numbers: "1,234", "12.5", "4500", "1.2k" — but not years or 1–2 digit counts/days.
    var BARE = /^\s*[+\-−]?\s?(\d[\d,]*(?:\.\d+)?)\s?[kKmMbB]?\s*$/;

    var originalText = new WeakMap();   // text node -> original data
    var originalAttr = new WeakMap();   // element -> { attr: original }
    var on = false;
    var observer = null;
    var applying = false;

    function isYear(s) { return /^(19|20)\d{2}$/.test(s); }

    function scrub(value) {
        var out = value.replace(MONEY, MASK).replace(MASKED_ACCT, '••••');
        var m = BARE.exec(out);
        if (m) {
            var digits = m[1];
            var plain = digits.replace(/,/g, '');
            var isSuffixed = /[kKmMbB]\s*$/.test(out);
            if (!isYear(plain) && (plain.length >= 3 || plain.indexOf('.') >= 0 || digits.indexOf(',') >= 0 || isSuffixed)) {
                out = out.replace(digits, MASK).replace(/[kKmMbB](\s*)$/, '$1');
            }
        }
        return out;
    }

    function skip(node) {
        var p = node.parentNode;
        return !p || p.nodeName === 'SCRIPT' || p.nodeName === 'STYLE' || p.nodeName === 'NOSCRIPT' || p.nodeName === 'TEXTAREA';
    }

    function scrubText(node) {
        if (skip(node)) return;
        var current = node.data;
        var orig = originalText.get(node);
        // If Blazor rewrote the node, its new data is the new "original".
        var source = (orig !== undefined && orig.scrubbed === current) ? orig.original : current;
        var next = scrub(source);
        if (next !== source || orig !== undefined) {
            originalText.set(node, { original: source, scrubbed: next });
            if (next !== current) node.data = next;
        }
    }

    function scrubAttrs(el) {
        for (var i = 0; i < ATTRS.length; i++) {
            var a = ATTRS[i];
            if (!el.hasAttribute || !el.hasAttribute(a)) continue;
            var cur = el.getAttribute(a);
            var saved = originalAttr.get(el) || {};
            var rec = saved[a];
            var source = (rec && rec.scrubbed === cur) ? rec.original : cur;
            var next = scrub(source);
            if (next !== source || rec) {
                saved[a] = { original: source, scrubbed: next };
                originalAttr.set(el, saved);
                if (next !== cur) el.setAttribute(a, next);
            }
        }
    }

    function walk(root) {
        if (!root) return;
        if (root.nodeType === 3) { scrubText(root); return; }
        if (root.nodeType !== 1) return;
        scrubAttrs(root);
        var w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
        var n;
        while ((n = w.nextNode())) {
            if (n.nodeType === 3) scrubText(n); else scrubAttrs(n);
        }
    }

    function restoreAll() {
        var w = document.createTreeWalker(document.body, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
        var n;
        while ((n = w.nextNode())) {
            if (n.nodeType === 3) {
                var o = originalText.get(n);
                if (o && n.data === o.scrubbed) n.data = o.original;
                originalText.delete(n);
            } else {
                var saved = originalAttr.get(n);
                if (saved) {
                    for (var a in saved) {
                        if (n.getAttribute(a) === saved[a].scrubbed) n.setAttribute(a, saved[a].original);
                    }
                    originalAttr.delete(n);
                }
            }
        }
    }

    function onMutations(muts) {
        if (!on || applying) return;
        applying = true;
        try {
            for (var i = 0; i < muts.length; i++) {
                var m = muts[i];
                if (m.type === 'characterData') scrubText(m.target);
                else if (m.type === 'attributes') scrubAttrs(m.target);
                else for (var j = 0; j < m.addedNodes.length; j++) walk(m.addedNodes[j]);
            }
        } finally { applying = false; }
    }

    function apply(enable) {
        on = !!enable;
        document.documentElement.classList.toggle(CLASS, on);
        if (on) {
            applying = true;
            try { walk(document.body); } finally { applying = false; }
            if (!observer) observer = new MutationObserver(onMutations);
            observer.observe(document.body, {
                childList: true, subtree: true, characterData: true,
                attributes: true, attributeFilter: ATTRS
            });
        } else {
            if (observer) observer.disconnect();
            applying = true;
            try { restoreAll(); } finally { applying = false; }
        }
    }

    function stored() {
        try { return localStorage.getItem(KEY) === '1'; } catch (e) { return false; }
    }

    function persist(v) {
        try { localStorage.setItem(KEY, v ? '1' : '0'); } catch (e) { /* private mode: still works for this page */ }
    }

    function init() {
        var q = new URLSearchParams(location.search).get('share');
        if (q === '1' || q === 'true') persist(true);
        else if (q === '0' || q === 'false') persist(false);
        apply(stored());
    }

    window.FlowLedgerShare = {
        isOn: function () { return on; },
        get: stored,
        set: function (v) { persist(!!v); apply(!!v); return on; }
    };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
