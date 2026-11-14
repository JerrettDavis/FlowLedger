// query-intellisense.js
// Pure (DOM-free) language service for the FlowLedger query language: tokenizer, context-aware
// completion, signature help, hover docs and highlighting. Kept separate from the editor so it can be
// unit tested with `node --test` (see tests/js/query-intellisense.test.mjs).
//
// `schema` shape: { datasets: [{ name, description, columns: [{ name, type, description }] }] }

export const OPERATORS = [
    { name: 'where', sig: 'where <condition>', doc: 'Keep only rows that match. Combine tests with and / or / not.' },
    { name: 'project', sig: 'project column [, name = expr …]', doc: 'Choose and compute the output columns; everything else is dropped.' },
    { name: 'extend', sig: 'extend name = expr [, …]', doc: 'Add or replace columns while keeping the rest.' },
    { name: 'summarize', sig: 'summarize [name =] aggregate(…) [, …] [by key [, …]]', doc: 'Group rows and aggregate. Output is ordered by the grouping keys, so trends read chronologically.' },
    { name: 'sort', insert: 'sort by ', sig: 'sort by expr [asc|desc] [, …]', doc: 'Order rows. Descending unless you say asc.' },
    { name: 'top', insert: 'top ', sig: 'top N by expr [asc|desc]', doc: 'Sort by one expression and keep the first N rows.' },
    { name: 'take', insert: 'take ', sig: 'take N', doc: 'Keep the first N rows.' },
    { name: 'distinct', sig: 'distinct column [, …]', doc: 'Unique combinations of the given columns.' },
    { name: 'count', sig: 'count', doc: 'Replace the rows with a single row holding the row count.' },
];

const OPERATOR_ALIASES = { filter: 'where', stats: 'summarize', order: 'sort', limit: 'take', head: 'take' };

export const AGGREGATES = [
    { name: 'count', params: [], doc: 'Number of rows in the group.' },
    { name: 'sum', params: ['x'], doc: 'Sum of x (numbers only; nulls ignored).' },
    { name: 'avg', params: ['x'], doc: 'Average of x.' },
    { name: 'min', params: ['x'], doc: 'Smallest value of x.' },
    { name: 'max', params: ['x'], doc: 'Largest value of x.' },
    { name: 'dcount', params: ['x'], doc: 'Number of distinct values of x.' },
    { name: 'countif', params: ['cond'], doc: 'Number of rows where cond is true.' },
    { name: 'sumif', params: ['x', 'cond'], doc: 'Sum of x over rows where cond is true.' },
];

export const FUNCTIONS = [
    { name: 'ago', params: ['duration'], doc: 'The date this long before today, e.g. ago(30d), ago(6mo).' },
    { name: 'today', params: [], doc: "Today's date." },
    { name: 'now', params: [], doc: "Today's date (alias of today())." },
    { name: 'month', params: ['date'], doc: 'Month label like 2026-03. Group by it to trend monthly.' },
    { name: 'quarter', params: ['date'], doc: 'Quarter label like 2026-Q1.' },
    { name: 'year', params: ['date'], doc: 'Year as a number.' },
    { name: 'week', params: ['date'], doc: 'Monday of the date\'s week.' },
    { name: 'startofweek', params: ['date'], doc: 'Monday of the date\'s week.' },
    { name: 'startofmonth', params: ['date'], doc: 'First day of the date\'s month.' },
    { name: 'startofyear', params: ['date'], doc: 'First day of the date\'s year.' },
    { name: 'dayofweek', params: ['date'], doc: 'Weekday label "1 Mon" … "7 Sun" (sorts Monday first).' },
    { name: 'dayofmonth', params: ['date'], doc: 'Day of the month as a number.' },
    { name: 'datediff', params: ['a', 'b'], doc: 'Days from b to a.' },
    { name: 'abs', params: ['x'], doc: 'Absolute value.' },
    { name: 'round', params: ['x', 'digits'], doc: 'Round x to the given decimals (default 0).' },
    { name: 'floor', params: ['x'], doc: 'Round down.' },
    { name: 'ceil', params: ['x'], doc: 'Round up.' },
    { name: 'sign', params: ['x'], doc: '-1, 0 or 1.' },
    { name: 'lower', params: ['s'], doc: 'Lower-case text.' },
    { name: 'upper', params: ['s'], doc: 'Upper-case text.' },
    { name: 'trim', params: ['s'], doc: 'Remove surrounding whitespace.' },
    { name: 'len', params: ['s'], doc: 'Length of text.' },
    { name: 'strcat', params: ['a', 'b', '…'], doc: 'Join values as text.' },
    { name: 'tostring', params: ['x'], doc: 'Convert to text.' },
    { name: 'tonumber', params: ['x'], doc: 'Convert text to a number.' },
    { name: 'coalesce', params: ['a', 'b', '…'], doc: 'First value that is not empty.' },
    { name: 'iif', params: ['cond', 'then', 'else'], doc: 'then if cond is true, otherwise else.' },
    { name: 'isnull', params: ['x'], doc: 'True when x has no value.' },
    { name: 'isnotnull', params: ['x'], doc: 'True when x has a value.' },
];

export const KEYWORDS = {
    by: 'Introduces grouping keys (summarize) or the sort expression (sort / top).',
    asc: 'Ascending order.',
    desc: 'Descending order (the default).',
    and: 'Both conditions must be true.',
    or: 'Either condition may be true.',
    not: 'Negates a condition; also `not in (…)`.',
    in: 'Matches any value in a list: category in ("Food", "Rent").',
    contains: 'Case-insensitive substring test: merchant contains "cafe".',
    startswith: 'Case-insensitive prefix test.',
    endswith: 'Case-insensitive suffix test.',
    has: 'Alias of contains.',
    between: 'between(low, high): inclusive range test.',
    true: 'Boolean true.',
    false: 'Boolean false.',
    null: 'No value.',
};

const COMPARATORS = ['==', '!=', '<', '<=', '>', '>='];
const OPERAND_WORDS = ['contains', 'startswith', 'endswith', 'in', 'not', 'and', 'or'];
const VALUE_AFTER_WORDS = new Set(['where', 'filter', 'project', 'extend', 'summarize', 'stats', 'distinct', 'by', 'and', 'or', 'not',
    'contains', 'startswith', 'endswith', 'has', 'in', 'between', 'asc', 'desc']);
const VALUE_AFTER_SYMBOLS = new Set(['(', ',', '=', '==', '!=', '<>', '<', '<=', '>', '>=', '+', '-', '*', '/', '%', '&&', '||']);
const DURATION_UNITS = { d: 'day(s)', w: 'week(s)', mo: 'month(s)', y: 'year(s)' };

// ── Tokenizer ──────────────────────────────────────────────────────────────

export function tokenize(text) {
    const tokens = [];
    let i = 0;
    while (i < text.length) {
        const c = text[i];
        const start = i;
        if (/\s/.test(c)) {
            while (i < text.length && /\s/.test(text[i])) { i++; }
            tokens.push({ type: 'ws', text: text.slice(start, i), start, end: i });
        } else if (c === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') { i++; }
            tokens.push({ type: 'comment', text: text.slice(start, i), start, end: i });
        } else if (/[0-9]/.test(c) || (c === '.' && /[0-9]/.test(text[i + 1] || ''))) {
            while (i < text.length && /[0-9.]/.test(text[i])) { i++; }
            const numEnd = i;
            let j = i;
            while (j < text.length && /[A-Za-z]/.test(text[j])) { j++; }
            const unit = text.slice(numEnd, j);
            if (unit && unit in DURATION_UNITS) {
                i = j;
                tokens.push({ type: 'duration', text: text.slice(start, i), start, end: i, amount: Number(text.slice(start, numEnd)), unit });
            } else {
                tokens.push({ type: 'number', text: text.slice(start, numEnd), start, end: numEnd });
            }
        } else if (/[A-Za-z_]/.test(c)) {
            while (i < text.length && /[A-Za-z0-9_.]/.test(text[i])) { i++; }
            tokens.push({ type: 'ident', text: text.slice(start, i), start, end: i });
        } else if (c === '"' || c === "'") {
            i++;
            let closed = false;
            while (i < text.length) {
                if (text[i] === '\\' && i + 1 < text.length) { i += 2; continue; }
                if (text[i] === c) { closed = true; i++; break; }
                i++;
            }
            tokens.push({ type: 'string', text: text.slice(start, i), start, end: i, quote: c, open: !closed });
        } else {
            const two = text.slice(i, i + 2);
            if (['==', '!=', '<>', '<=', '>=', '&&', '||'].includes(two)) {
                i += 2;
            } else {
                i += 1;
            }
            tokens.push({ type: 'symbol', text: text.slice(start, i), start, end: i });
        }
    }
    return tokens;
}

const significant = (t) => t.type !== 'ws' && t.type !== 'comment';
const lower = (t) => t.text.toLowerCase();
const canonicalOp = (name) => OPERATOR_ALIASES[name] ?? name;

/** Splits significant tokens into pipe-separated segments (pipes at paren depth 0). */
function segmentsOf(sigTokens) {
    const segments = [[]];
    let depth = 0;
    for (const t of sigTokens) {
        if (t.type === 'symbol') {
            if (t.text === '(') { depth++; }
            else if (t.text === ')') { depth = Math.max(0, depth - 1); }
            else if (t.text === '|' && depth === 0) { segments.push([]); continue; }
        }
        segments[segments.length - 1].push(t);
    }
    return segments;
}

function splitTopLevel(tokens, sep) {
    const parts = [[]];
    let depth = 0;
    for (const t of tokens) {
        if (t.type === 'symbol' && t.text === '(') { depth++; }
        else if (t.type === 'symbol' && t.text === ')') { depth = Math.max(0, depth - 1); }
        if (depth === 0 && t.type === 'symbol' && t.text === sep) { parts.push([]); continue; }
        parts[parts.length - 1].push(t);
    }
    return parts.filter((p) => p.length > 0);
}

/** Output column name of an item, mirroring the server's default naming. */
function itemName(item) {
    if (item.length >= 2 && item[0].type === 'ident' && item[1].type === 'symbol' && item[1].text === '=') {
        return { name: item[0].text, expr: item.slice(2) };
    }
    if (item.length === 1 && item[0].type === 'ident') { return { name: item[0].text, expr: item }; }
    if (item[0]?.type === 'ident' && item[1]?.text === '(') {
        const first = item[2];
        const after = item[3];
        const bare = first?.type === 'ident' && (after?.text === ',' || after?.text === ')');
        return { name: bare ? `${lower(item[0])}_${first.text}` : lower(item[0]), expr: item };
    }
    return { name: null, expr: item };
}

/** Columns visible to a segment given the segments before it. Returns Map(lowerName → {name,type,description}). */
function columnsBefore(segments, upTo, dataset) {
    let cols = new Map();
    for (const c of dataset?.columns ?? []) { cols.set(c.name.toLowerCase(), c); }
    for (let s = 1; s < upTo; s++) {
        const seg = segments[s];
        if (!seg?.length || seg[0].type !== 'ident') { continue; }
        const op = canonicalOp(lower(seg[0]));
        const body = seg.slice(1);
        const add = (m, name, type = 'any', description = 'Computed column') => {
            if (name) { m.set(name.toLowerCase(), { name, type, description }); }
        };
        if (op === 'extend') {
            for (const item of splitTopLevel(body, ',')) { add(cols, itemName(item).name); }
        } else if (op === 'project' || op === 'distinct') {
            const next = new Map();
            for (const item of splitTopLevel(body, ',')) {
                const { name } = itemName(item);
                const known = item.length === 1 ? cols.get(item[0].text.toLowerCase()) : null;
                if (name) { next.set(name.toLowerCase(), known ?? { name, type: 'any', description: 'Computed column' }); }
            }
            cols = next;
        } else if (op === 'summarize') {
            const byAt = body.findIndex((t) => t.type === 'ident' && lower(t) === 'by');
            const aggs = byAt < 0 ? body : body.slice(0, byAt);
            const keys = byAt < 0 ? [] : body.slice(byAt + 1);
            const next = new Map();
            for (const item of splitTopLevel(keys, ',')) {
                const { name } = itemName(item);
                const known = item.length === 1 ? cols.get(item[0].text.toLowerCase()) : null;
                if (name) { next.set(name.toLowerCase(), known ?? { name, type: 'any', description: 'Grouping key' }); }
            }
            for (const item of splitTopLevel(aggs, ',')) {
                const { name } = itemName(item);
                if (name) { next.set(name.toLowerCase(), { name, type: 'number', description: 'Aggregate result' }); }
            }
            cols = next;
        }
    }
    return cols;
}

// Columns for segment k need the segments *before* k, i.e. indexes 1..k-1 applied after the source.
function columnsForSegment(segments, k, dataset) {
    return columnsBefore(segments, k, dataset);
}

// ── Completion ─────────────────────────────────────────────────────────────

function fuzzy(prefix, label) {
    if (!prefix) { return 1; }
    const p = prefix.toLowerCase();
    const l = label.toLowerCase();
    if (l === p) { return 100; }
    if (l.startsWith(p)) { return 80; }
    if (l.includes(p)) { return 50; }
    if (l[0] !== p[0]) { return 0; }
    let i = 0;
    for (const ch of l) { if (ch === p[i]) { i++; } }
    return i === p.length ? 20 : 0;
}

const KIND_ORDER = { column: 0, value: 0, aggregate: 1, function: 2, operator: 3, keyword: 4, dataset: 0, punct: 5 };

export function complete(text, caret, schema, valuesCache = {}, explicit = false) {
    const tokens = tokenize(text);
    const hit = tokens.find((t) => t.start < caret && caret <= t.end && t.type !== 'ws');
    const inString = hit?.type === 'string' && (hit.open ? caret > hit.start : caret > hit.start && caret < hit.end);
    const inWord = hit?.type === 'ident';
    if (hit?.type === 'comment') { return null; }

    const from = inWord ? hit.start : inString ? hit.start + 1 : caret;
    const to = inWord ? hit.end : inString ? (hit.open ? hit.end : hit.end - 1) : caret;
    const prefix = inWord ? text.slice(hit.start, caret) : inString ? text.slice(hit.start + 1, caret) : '';

    const before = tokens.filter((t) => significant(t) && t.end <= (inWord || inString ? hit.start : caret));
    const segments = segmentsOf(before);
    const segIndex = segments.length - 1;
    const seg = segments[segIndex];
    const first = segments[0][0];
    const dataset = first?.type === 'ident' ? (schema.datasets || []).find((d) => d.name.toLowerCase() === lower(first)) : undefined;
    const columns = columnsForSegment(segments, segIndex, dataset);
    const prev = seg[seg.length - 1];
    const items = [];
    const push = (label, kind, detail, doc, extra = {}) => {
        // Punctuation like the pipe is only offered on request (Ctrl+Space), never while typing.
        if (kind === 'punct' && !explicit) { return; }
        const score = fuzzy(prefix, label);
        if (score > 0) { items.push({ label, kind, detail, doc, insert: label, score, ...extra }); }
    };

    // Inside a string literal: offer live values for the column being compared.
    if (inString) {
        const target = valueTarget(before, columns);
        if (!target || !dataset) { return null; }
        const key = `${dataset.name}.${target.name}`.toLowerCase();
        const cached = valuesCache[key];
        if (!cached) { return { from, to, items: [], wantValues: { dataset: dataset.name, column: target.name, key } }; }
        for (const v of cached) { push(v, 'value', target.name, `A ${target.name} value in your data`, { insert: v.replace(/"/g, '\\"') }); }
        return finish(from, to, items);
    }

    if (segIndex === 0 && seg.length === 0) {
        if (!prefix && !explicit && text.trim() !== '') { return null; }
        for (const d of schema.datasets || []) { push(d.name, 'dataset', 'dataset', d.description); }
        return finish(from, to, items);
    }
    if (segIndex === 0) {
        // Only the dataset has been typed so far.
        if (seg.length === 1 && prev.type === 'ident') {
            push('|', 'punct', 'pipe', 'Start the next operator', { insert: '\n| ' });
        }
        return finish(from, to, items);
    }
    if (seg.length === 0) {
        for (const o of OPERATORS) { push(o.name, 'operator', o.sig, o.doc, { insert: o.insert ?? `${o.name} ` }); }
        return finish(from, to, items);
    }

    const opName = canonicalOp(lower(seg[0]));
    const bodyDepth = depthOf(seg.slice(1));
    const hasBy = seg.some((t) => t.type === 'ident' && lower(t) === 'by');

    // Operators with fixed grammar.
    if (opName === 'sort' && seg.length === 1) { push('by', 'keyword', 'keyword', KEYWORDS.by); return finish(from, to, items); }
    if (opName === 'top' && seg.length === 2 && seg[1].type === 'number') { push('by', 'keyword', 'keyword', KEYWORDS.by); return finish(from, to, items); }
    if ((opName === 'take' || opName === 'count' || (opName === 'top' && seg.length === 1)) && bodyDepth === 0) {
        if (opName !== 'top' || seg.length > 1) { push('|', 'punct', 'pipe', 'Start the next operator', { insert: '\n| ' }); }
        return finish(from, to, items);
    }

    const isValuePos =
        (prev.type === 'symbol' && VALUE_AFTER_SYMBOLS.has(prev.text)) ||
        (prev.type === 'ident' && prev === seg[0]) ||
        (prev.type === 'ident' && VALUE_AFTER_WORDS.has(lower(prev)) && !(prev === seg[0] && opName === 'sort'));

    if (isValuePos) {
        const aggregatesHere = opName === 'summarize' && !hasBy && bodyDepth === 0;
        if (aggregatesHere) {
            for (const a of AGGREGATES) { push(a.name, 'aggregate', `${a.name}(${a.params.join(', ')})`, a.doc, { insert: a.name + '(', paramless: a.params.length === 0 }); }
        }
        if (!aggregatesHere || bodyDepth > 0) {
            for (const c of columns.values()) { push(c.name, 'column', c.type, c.description); }
            for (const f of FUNCTIONS) { push(f.name, 'function', `${f.name}(${f.params.join(', ')})`, f.doc, { insert: f.name + '(', paramless: f.params.length === 0 }); }
            if (opName === 'where' || bodyDepth > 0) {
                for (const w of ['true', 'false', 'null', 'not']) { push(w, 'keyword', 'keyword', KEYWORDS[w]); }
            }
        }
        return finish(from, to, items);
    }

    // After an operand: comparison words, logic words and clause keywords.
    if (bodyDepth === 0) {
        if (opName === 'where') {
            for (const s of COMPARATORS) { push(s, 'operator', 'comparison', 'Compare values', { insert: s + ' ' }); }
            for (const w of OPERAND_WORDS) { push(w, 'keyword', 'keyword', KEYWORDS[w]); }
        }
        if (opName === 'summarize' && !hasBy) { push('by', 'keyword', 'keyword', KEYWORDS.by); }
        if (opName === 'sort' || opName === 'top') {
            push('asc', 'keyword', 'keyword', KEYWORDS.asc);
            push('desc', 'keyword', 'keyword', KEYWORDS.desc);
        }
        push('|', 'punct', 'pipe', 'Start the next operator', { insert: '\n| ' });
    } else {
        for (const s of COMPARATORS) { push(s, 'operator', 'comparison', 'Compare values', { insert: s + ' ' }); }
        for (const w of OPERAND_WORDS) { push(w, 'keyword', 'keyword', KEYWORDS[w]); }
    }
    return finish(from, to, items);
}

function finish(from, to, items) {
    items.sort((a, b) => b.score - a.score || (KIND_ORDER[a.kind] ?? 9) - (KIND_ORDER[b.kind] ?? 9) || a.label.localeCompare(b.label));
    return items.length === 0 ? null : { from, to, items: items.slice(0, 60) };
}

function depthOf(tokens) {
    let d = 0;
    for (const t of tokens) {
        if (t.type === 'symbol' && t.text === '(') { d++; }
        else if (t.type === 'symbol' && t.text === ')') { d = Math.max(0, d - 1); }
    }
    return d;
}

/** For a string being typed, find the string column it is compared with (col == "…", col in ("a", "…"). */
function valueTarget(before, columns) {
    let i = before.length - 1;
    const t = before[i];
    if (!t) { return null; }
    if (t.type === 'symbol' && ['==', '!=', '=', '<>'].includes(t.text)) { return columnOf(before[i - 1], columns); }
    if (t.type === 'ident' && ['contains', 'startswith', 'endswith', 'has'].includes(lower(t))) { return columnOf(before[i - 1], columns); }
    if (t.type === 'symbol' && (t.text === '(' || t.text === ',')) {
        // Walk back to the `in (` that opened the list.
        for (let j = i; j >= 0; j--) {
            if (before[j].type === 'symbol' && before[j].text === '(' && before[j - 1]?.type === 'ident' && lower(before[j - 1]) === 'in') {
                const k = before[j - 2] && lower(before[j - 2]) === 'not' ? j - 3 : j - 2;
                return columnOf(before[k], columns);
            }
            if (before[j].type === 'symbol' && before[j].text === ')') { break; }
        }
    }
    return null;
}

function columnOf(token, columns) {
    if (token?.type !== 'ident') { return null; }
    const c = columns.get(token.text.toLowerCase());
    return c && c.type === 'string' ? c : null;
}

// ── Signature help ─────────────────────────────────────────────────────────

export function signature(text, caret, schema) {
    const tokens = tokenize(text).filter((t) => significant(t) && t.end <= caret);
    // Enclosing call: nearest unmatched '(' preceded by an identifier.
    let depth = 0;
    let commas = 0;
    for (let i = tokens.length - 1; i >= 0; i--) {
        const t = tokens[i];
        if (t.type === 'symbol' && t.text === ')') { depth++; }
        else if (t.type === 'symbol' && t.text === '(') {
            if (depth === 0) {
                const name = tokens[i - 1];
                if (name?.type === 'ident') {
                    const fn = [...FUNCTIONS, ...AGGREGATES].find((f) => f.name === lower(name));
                    if (fn) { return { label: `${fn.name}(${fn.params.join(', ')})`, name: fn.name, params: fn.params, active: Math.min(commas, Math.max(0, fn.params.length - 1)), doc: fn.doc }; }
                }
                return null;
            }
            depth--;
        } else if (t.type === 'symbol' && t.text === ',' && depth === 0) { commas++; }
    }
    // Otherwise show the grammar of the operator in the current segment.
    const segments = segmentsOf(tokenize(text).filter((t) => significant(t) && t.start < caret));
    const seg = segments[segments.length - 1];
    if (segments.length > 1 && seg[0]?.type === 'ident') {
        const op = OPERATORS.find((o) => o.name === canonicalOp(lower(seg[0])));
        if (op) { return { label: op.sig, name: op.name, params: [], active: -1, doc: op.doc }; }
    }
    return null;
}

// ── Hover ──────────────────────────────────────────────────────────────────

export function hover(text, pos, schema) {
    const tokens = tokenize(text);
    const idx = tokens.findIndex((t) => t.start <= pos && pos < t.end);
    const t = tokens[idx];
    if (!t || t.type === 'ws') { return null; }
    const sig = tokens.filter(significant);
    const sigIndex = sig.indexOf(t);
    const segments = segmentsOf(sig.slice(0, sigIndex + 1));
    const segIndex = segments.length - 1;
    const seg = segments[segIndex];
    const first = segments[0][0];
    const dataset = first?.type === 'ident' ? (schema.datasets || []).find((d) => d.name.toLowerCase() === lower(first)) : undefined;
    const columns = columnsForSegment(segments, segIndex, dataset);
    const base = { from: t.start, to: t.end };

    if (t.type === 'duration') { return { ...base, title: t.text, body: `${t.amount} ${DURATION_UNITS[t.unit]}`, kind: 'duration' }; }
    if (t.type === 'number') { return { ...base, title: t.text, body: 'Number', kind: 'number' }; }
    if (t.type !== 'ident') { return null; }

    const name = lower(t);
    const next = sig[sigIndex + 1];
    if (t === seg[0]) {
        if (segIndex === 0) {
            const d = (schema.datasets || []).find((x) => x.name.toLowerCase() === name);
            if (d) { return { ...base, title: d.name, body: `${d.description}\n\nColumns: ${d.columns.map((c) => c.name).join(', ')}`, kind: 'dataset' }; }
            return { ...base, title: t.text, body: `Unknown dataset. Available: ${(schema.datasets || []).map((x) => x.name).join(', ')}`, kind: 'error' };
        }
        const op = OPERATORS.find((o) => o.name === canonicalOp(name));
        if (op) { return { ...base, title: op.sig, body: op.doc + (canonicalOp(name) !== name ? ` (“${name}” is an alias of ${op.name})` : ''), kind: 'operator' }; }
        return { ...base, title: t.text, body: 'Unknown operator.', kind: 'error' };
    }
    if (next?.text === '(' && next.start === t.end) {
        const fn = [...AGGREGATES, ...FUNCTIONS].find((f) => f.name === name);
        const isAgg = AGGREGATES.some((f) => f.name === name);
        if (fn) { return { ...base, title: `${fn.name}(${fn.params.join(', ')})`, body: fn.doc, kind: isAgg ? 'aggregate' : 'function' }; }
        return { ...base, title: t.text, body: 'Unknown function.', kind: 'error' };
    }
    if (name in KEYWORDS) { return { ...base, title: t.text, body: KEYWORDS[name], kind: 'keyword' }; }
    const col = columns.get(name);
    if (col) { return { ...base, title: `${col.name} · ${col.type}`, body: col.description, kind: 'column' }; }
    if (next?.text === '=' ) { return { ...base, title: t.text, body: 'Name of the column being defined.', kind: 'alias' }; }
    return { ...base, title: t.text, body: `Unknown column. Available: ${[...columns.values()].map((c) => c.name).join(', ')}`, kind: 'error' };
}

// ── Highlighting ───────────────────────────────────────────────────────────

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** HTML for the overlay; every token carries data-s/data-e so the editor can map the pointer to text offsets. */
export function highlight(text, schema, errorAt = -1) {
    const tokens = tokenize(text);
    const sig = tokens.filter(significant);
    let html = '';
    let segIndex = 0;
    let segFirst = null;
    let segments = [[]];
    let columns = new Map();
    let dataset;
    let localAliases = new Set();
    const aggNames = new Set(AGGREGATES.map((f) => f.name));
    const fnNames = new Set(FUNCTIONS.map((f) => f.name));

    for (const t of tokens) {
        let cls = '';
        if (t.type === 'comment') { cls = 'tk-cmt'; }
        else if (t.type === 'string') { cls = 'tk-str'; }
        else if (t.type === 'number' || t.type === 'duration') { cls = 'tk-num'; }
        else if (t.type === 'symbol') {
            cls = 'tk-sym';
            if (t.text === '|') {
                segIndex++;
                segFirst = null;
                segments = segmentsOf(sig.filter((x) => x.end <= t.start));
                columns = columnsForSegment(segments, segIndex, dataset);
                localAliases = new Set();
            }
        } else if (t.type === 'ident') {
            const name = lower(t);
            const nextSig = sig[sig.indexOf(t) + 1];
            if (segIndex === 0 && segFirst === null) {
                segFirst = t;
                dataset = (schema.datasets || []).find((d) => d.name.toLowerCase() === name);
                cls = dataset ? 'tk-ds' : 'tk-err';
            } else if (segFirst === null) {
                segFirst = t;
                cls = OPERATORS.some((o) => o.name === canonicalOp(name)) ? 'tk-kw' : 'tk-err';
            } else if (nextSig?.text === '(' && nextSig.start === t.end) {
                cls = aggNames.has(name) ? 'tk-agg' : fnNames.has(name) ? 'tk-fn' : 'tk-err';
            } else if (name in KEYWORDS) { cls = 'tk-kw'; }
            else if (columns.has(name) || localAliases.has(name)) { cls = 'tk-col'; }
            else if (nextSig?.text === '=') { cls = 'tk-alias'; localAliases.add(name); }
            else { cls = 'tk-err'; }
        }
        const bad = errorAt >= t.start && errorAt < t.end ? ' tk-bad' : '';
        const body = esc(t.text);
        html += t.type === 'ws' ? body : `<span class="${cls}${bad}" data-s="${t.start}" data-e="${t.end}">${body}</span>`;
    }
    // A trailing newline needs a visible line box, or the overlay is one line short of the textarea.
    return html + (text.endsWith('\n') ? ' ' : '');
}
