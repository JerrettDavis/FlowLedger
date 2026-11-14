// Run with: node --test tests/js
import test from 'node:test';
import assert from 'node:assert/strict';
import { complete, hover, signature, highlight } from '../../src/FlowLedger.Web/wwwroot/query-intellisense.js';

const schema = {
    datasets: [
        {
            name: 'transactions', description: 'All transactions.',
            columns: [
                { name: 'date', type: 'date', description: 'Posted date' },
                { name: 'amount', type: 'number', description: 'Absolute amount' },
                { name: 'category', type: 'string', description: 'Category path' },
                { name: 'direction', type: 'string', description: 'Credit or Debit' },
                { name: 'merchant', type: 'string', description: 'Merchant' },
            ],
        },
        { name: 'accounts', description: 'Accounts.', columns: [{ name: 'name', type: 'string', description: 'n' }] },
    ],
};

const at = (text, explicit = false, cache = {}) => complete(text, text.length, schema, cache, explicit);
const labels = (r) => (r ? r.items.map((i) => i.label) : []);

test('empty editor offers datasets when explicitly invoked', () => {
    assert.deepEqual(labels(at('', true)).sort(), ['accounts', 'transactions']);
});

test('typing a prefix filters datasets', () => {
    assert.deepEqual(labels(at('tra')), ['transactions']);
});

test('after the dataset a pipe is offered; after a pipe, operators', () => {
    assert.ok(labels(at('transactions ', true)).includes('|'));
    const ops = labels(at('transactions\n| ', true));
    for (const o of ['where', 'summarize', 'sort', 'top', 'take', 'project']) { assert.ok(ops.includes(o), o); }
});

test('operator prefix completes and inserts a trailing space (sort adds by)', () => {
    const r = at('transactions | so');
    assert.equal(r.items[0].label, 'sort');
    assert.equal(r.items[0].insert, 'sort by ');
});

test('where offers columns and functions, then comparison words after an operand', () => {
    const v = labels(at('transactions | where ', true));
    assert.ok(v.includes('amount') && v.includes('ago') && !v.includes('count'));
    const c = labels(at('transactions | where category ', true));
    assert.ok(c.includes('==') && c.includes('contains') && c.includes('and'));
});

test('summarize offers aggregates first, then by, then grouping columns', () => {
    assert.ok(labels(at('transactions | summarize ', true)).includes('sumif'));
    assert.ok(labels(at('transactions | summarize total = sum(amount) ', true)).includes('by'));
    const keys = labels(at('transactions | summarize total = sum(amount) by ', true));
    assert.ok(keys.includes('category') && keys.includes('month'));
});

test('columns created by earlier operators are suggested later, and dropped ones are not', () => {
    const r = labels(at('transactions | summarize total = sum(amount) by category | sort by ', true));
    assert.ok(r.includes('total') && r.includes('category'));
    assert.ok(!r.includes('merchant'), 'merchant is gone after summarize');

    const e = labels(at('transactions | extend net = amount * 2 | where ', true));
    assert.ok(e.includes('net') && e.includes('merchant'));
});

test('default names of unnamed aggregates and keys are known', () => {
    const r = labels(at('transactions | summarize sum(amount), count() by month(date) | sort by ', true));
    assert.ok(r.includes('sum_amount') && r.includes('count') && r.includes('month_date'));
});

test('top N is followed by "by"', () => {
    assert.deepEqual(labels(at('transactions | top 5 ', true)), ['by']);
});

test('string values are requested, then served from the cache', () => {
    const want = at('transactions | where category == "');
    assert.deepEqual(want.wantValues, { dataset: 'transactions', column: 'category', key: 'transactions.category' });
    const r = at('transactions | where category == "F', false, { 'transactions.category': ['Food', 'Rent', 'Fun'] });
    assert.deepEqual(labels(r), ['Food', 'Fun']);
    const list = at('transactions | where category in ("Rent", "', false, { 'transactions.category': ['Food'] });
    assert.deepEqual(labels(list), ['Food']);
    assert.equal(at('transactions | where amount > "'), null, 'no values for numeric columns');
});

test('no completions inside comments', () => {
    assert.equal(at('transactions // wh'), null);
});

test('signature help shows the function and the active parameter', () => {
    const s = signature('transactions | summarize sumif(amount, ', 'transactions | summarize sumif(amount, '.length, schema);
    assert.equal(s.name, 'sumif');
    assert.equal(s.active, 1);
    const nested = signature('x | where round(abs(', 'x | where round(abs('.length, schema);
    assert.equal(nested.name, 'abs');
    const op = signature('transactions | sort by ', 'transactions | sort by '.length, schema);
    assert.match(op.label, /^sort by/);
});

test('hover explains datasets, operators, functions, columns and unknowns', () => {
    const q = 'transactions | where amount > ago(30d) | bogus';
    assert.equal(hover(q, 2, schema).kind, 'dataset');
    assert.equal(hover(q, q.indexOf('where') + 1, schema).kind, 'operator');
    const col = hover(q, q.indexOf('amount') + 1, schema);
    assert.equal(col.kind, 'column');
    assert.match(col.title, /number/);
    assert.equal(hover(q, q.indexOf('ago') + 1, schema).kind, 'function');
    assert.match(hover(q, q.indexOf('30d') + 1, schema).body, /30 day/);
    assert.equal(hover(q, q.indexOf('bogus') + 1, schema).kind, 'error');
    assert.equal(hover('transactions | where nope == 1', 22, schema).kind, 'error');
});

test('highlight classifies tokens and escapes html', () => {
    const html = highlight('transactions | where merchant contains "<b>" and nope == 1', schema);
    assert.match(html, /tk-ds[^>]*>transactions/);
    assert.match(html, /tk-kw[^>]*>where/);
    assert.match(html, /tk-col[^>]*>merchant/);
    assert.match(html, /tk-str[^>]*>"&lt;b&gt;"/);
    assert.match(html, /tk-err[^>]*>nope/);
    assert.ok(!html.includes('<b>'));
});

test('highlight treats aliases as columns once defined', () => {
    const html = highlight('transactions | summarize total = sum(amount) by category | sort by total', schema);
    assert.match(html, /tk-alias[^>]*>total/);
    assert.match(html, /tk-col[^>]*>total<\/span>$/);
});
