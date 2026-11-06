// Data panel: a table's rows (Browse Data) or a SQL query's results. Every value is written with textContent.
(function () {
    const vscode = acquireVsCodeApi();
    if (document.body.dataset.xppState) {
        vscode.setState({ xpp: JSON.parse(document.body.dataset.xppState) });
    }
    const $ = id => document.getElementById(id);
    const el = (tag, props = {}, children = []) => {
        const node = document.createElement(tag);
        for (const [key, value] of Object.entries(props)) {
            if (key === 'text') { node.textContent = value; }
            else if (key === 'on') { for (const [event, handler] of Object.entries(value)) { node.addEventListener(event, handler); } }
            else if (value !== undefined && value !== false) { node.setAttribute(key, value === true ? '' : value); }
        }
        for (const child of children) { if (child) { node.append(child); } }
        return node;
    };

    const state = { mode: undefined, shape: undefined, filters: [], company: '', asOf: '', orderBy: '', descending: false, maxRows: 200, canWrite: false, running: false, relations: [], inspect: null };
    const RowChoices = [100, 200, 500, 1000, 5000];
    const NumericTypes = /^(int|bigint|smallint|tinyint|decimal|numeric|float|real|money)$/i;

    function send(message) { vscode.postMessage(message); }

    function rowsSelect() {
        const select = el('select', { 'aria-label': 'Maximum rows', on: { change: () => { state.maxRows = Number(select.value); } } },
            RowChoices.map(n => el('option', { value: String(n), selected: n === state.maxRows, text: `${n} rows` })));
        return select;
    }

    function renderToolbar() {
        const bar = $('toolbar');
        bar.replaceChildren();
        const cancel = el('button', { id: 'cancel', disabled: !state.running, text: 'Cancel', on: { click: () => send({ type: 'cancel' }) } });
        if (state.mode.kind === 'table') {
            const shape = state.shape;
            bar.append(el('span', { class: 'title', text: shape?.label && shape.label !== state.mode.table ? `${state.mode.table} (${shape.label})` : state.mode.table }));
            if (!shape || shape.perCompany) {
                bar.append(el('label', { text: 'Company ' }, [el('input', {
                    class: 'company', value: state.company, placeholder: 'all', 'aria-label': 'Company (DataAreaId)',
                    on: { change: e => { state.company = e.target.value.trim(); browse(); } },
                })]));
            }
            if (shape && shape.validTimeState && shape.validTimeState !== 'None') {
                bar.append(el('label', { text: 'As of ', title: 'Date-effective table: only the rows valid on this date (empty: all rows)' }, [el('input', {
                    type: 'date', value: state.asOf, 'aria-label': 'As-of date',
                    on: { change: e => { state.asOf = e.target.value; browse(); } },
                })]));
            }
            bar.append(rowsSelect(), filterEditor(), el('button', { class: 'primary', text: 'Refresh', on: { click: browse } }), cancel,
                el('button', { text: 'Open as SQL', title: 'Open this view as an editable SQL query', on: { click: () => send({ type: 'openAsQuery', sql: browseSql() }) } }));
        } else {
            const write = el('input', { type: 'checkbox', id: 'allowWrite', disabled: !state.canWrite });
            bar.append(el('button', { class: 'primary', text: 'Run (Ctrl+Enter)', on: { click: runQuery } }), cancel, rowsSelect(),
                el('button', { text: 'Explain Plan', title: 'Estimated plan of the SELECT (not run): costly steps, missing indexes, warnings', on: { click: () => send({ type: 'plan', sql: $('sql').value }) } }),
                el('label', { class: 'write', title: state.canWrite ? 'Commit changes made by the batch (asks every time)' : 'Needs Writer credentials or a local database' }, [write, document.createTextNode(' Allow changes')]));
        }
        bar.append(el('span', { class: 'spacer' }), el('span', { class: 'target', id: 'target', text: state.targetText || '' }));
    }

    function filterEditor() {
        const fields = (state.shape?.fields || []).map(f => f.name);
        const column = el('select', { 'aria-label': 'Filter column' }, fields.map(name => el('option', { value: name, text: name })));
        const operator = el('select', { 'aria-label': 'Filter operator' }, ['=', '<>', '<', '<=', '>', '>=', 'contains', 'startsWith'].map(op => el('option', { value: op, text: op })));
        const value = el('input', { placeholder: 'value', 'aria-label': 'Filter value' });
        const add = () => {
            if (!column.value) { return; }
            state.filters.push({ column: column.value, operator: operator.value, value: value.value });
            value.value = '';
            renderFilters();
            browse();
        };
        value.addEventListener('keydown', e => { if (e.key === 'Enter') { add(); } });
        return el('span', {}, [column, operator, value, el('button', { text: 'Add filter', on: { click: add } })]);
    }

    function renderFilters() {
        const box = $('filters');
        box.replaceChildren(...state.filters.map((f, i) => el('span', { class: 'chip' }, [
            document.createTextNode(`${f.column} ${f.operator} ${f.value}`),
            el('button', { title: 'Remove filter', 'aria-label': `Remove filter ${f.column}`, text: '×', on: { click: () => { state.filters.splice(i, 1); renderFilters(); browse(); } } }),
        ])));
        box.hidden = state.filters.length === 0;
    }

    function browse() {
        send({ type: 'browse', request: {
            table: state.mode.table, filters: state.filters, company: state.company || undefined, asOf: state.asOf || undefined,
            orderBy: state.orderBy || undefined, descending: state.descending, maxRows: state.maxRows,
        } });
    }

    function runQuery() {
        send({ type: 'query', sql: $('sql').value, maxRows: state.maxRows, allowWrite: !!$('allowWrite')?.checked });
    }

    function sqlText(value) { return `'${String(value).replace(/'/g, "''")}'`; }
    function sqlName(name) { return `[${name.replace(/]/g, ']]')}]`; }

    function browseSql() {
        const where = [];
        if (state.company) { where.push(`DATAAREAID = ${sqlText(state.company.toLowerCase())}`); }
        if (state.asOf) { where.push(`VALIDFROM <= ${sqlText(state.asOf)} AND VALIDTO >= ${sqlText(state.asOf)}`); }
        for (const f of state.filters) {
            if (f.operator === 'contains') { where.push(`${sqlName(f.column)} LIKE ${sqlText('%' + f.value + '%')}`); }
            else if (f.operator === 'startsWith') { where.push(`${sqlName(f.column)} LIKE ${sqlText(f.value + '%')}`); }
            else { where.push(`${sqlName(f.column)} ${f.operator} ${sqlText(f.value)}`); }
        }
        return `SELECT TOP (${state.maxRows}) *\nFROM ${sqlName(state.mode.table)}`
            + (where.length ? `\nWHERE ${where.join('\n  AND ')}` : '')
            + (state.orderBy ? `\nORDER BY ${sqlName(state.orderBy)}${state.descending ? ' DESC' : ''}` : '');
    }

    /** The metadata field behind a SQL column: same name, or an array element (FIELD2_). */
    function fieldOf(column) {
        const fields = state.shape?.fields || [];
        const lower = column.toLowerCase();
        return fields.find(f => f.name.toLowerCase() === lower)
            || fields.find(f => f.name.toLowerCase() === lower.replace(/\d+_$/, ''));
    }

    function renderResult(result) {
        const box = $('results');
        box.replaceChildren();
        result.resultSets.forEach((set, index) => box.append(renderSet(set, index)));
        const parts = [`${result.elapsedMs} ms`];
        if (result.committed && result.rowsAffected >= 0) { parts.push(`${result.rowsAffected} row(s) changed and committed`); }
        if (!result.resultSets.length && !result.committed) { parts.push('no rows returned'); }
        $('status').textContent = parts.join(' · ');
        if (result.messages?.length) { box.append(el('pre', { class: 'messages', text: result.messages.join('\n') })); }
    }

    function renderSet(set, index) {
        const browsing = state.mode.kind === 'table';
        const header = el('div', { class: 'set-header' }, [
            el('span', { text: `${set.rows.length}${set.truncated ? '+' : ''} row(s)${set.truncated ? ' (limit reached)' : ''}` }),
            el('button', { text: 'Export CSV', on: { click: () => send({ type: 'export', index }) } }),
        ]);
        const fields = set.columns.map(c => browsing ? fieldOf(c.name) : undefined);
        const head = el('tr', {}, set.columns.map((c, i) => {
            const field = fields[i];
            const sorted = browsing && state.orderBy.toLowerCase() === c.name.toLowerCase();
            const th = el('th', {
                class: browsing ? 'sortable' : undefined, scope: 'col',
                title: [field?.label, field?.enumType ? `Enum ${field.enumType}` : c.type].filter(Boolean).join(' · '),
                'aria-sort': sorted ? (state.descending ? 'descending' : 'ascending') : undefined,
            }, [document.createTextNode(c.name), sorted ? el('span', { class: 'sort', text: state.descending ? '▼' : '▲' }) : null]);
            if (browsing) {
                th.tabIndex = 0;
                const sort = () => {
                    state.descending = sorted ? !state.descending : false;
                    state.orderBy = c.name;
                    browse();
                };
                th.addEventListener('click', sort);
                th.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); sort(); } });
            }
            return th;
        }));
        const body = el('tbody');
        set.rows.forEach((row, rowIndex) => {
            const tr = el('tr');
            row.forEach((value, i) => tr.append(renderCell(value, set.columns[i], fields[i])));
            if (browsing) {
                tr.tabIndex = 0;
                tr.dataset.row = String(rowIndex);
                tr.addEventListener('click', () => inspect(set, rowIndex, fields));
                tr.addEventListener('keydown', e => {
                    const next = e.key === 'ArrowDown' ? tr.nextElementSibling : e.key === 'ArrowUp' ? tr.previousElementSibling : null;
                    if (next) {
                        e.preventDefault();
                        next.focus();
                        next.click();
                    } else if (e.key === 'Enter') {
                        e.preventDefault();
                        inspect(set, rowIndex, fields);
                        $('inspectorFilter')?.focus();
                    }
                });
            }
            body.append(tr);
        });
        const grid = el('div', { class: 'grid' }, [el('table', {}, [el('thead', {}, [head]), body])]);
        if (!browsing) {
            return el('div', { class: 'set' }, [header, grid]);
        }
        const inspector = el('aside', { class: 'inspector', id: 'inspector', 'aria-label': 'Selected row' }, [el('div', { class: 'muted', text: 'Select a row to see all of its fields.' })]);
        return el('div', { class: 'set' }, [header, el('div', { class: 'browse' }, [grid, inspector])]);
    }

    /** The selected row: every field with its label, enum names and links to related rows. */
    function inspect(set, rowIndex, fields) {
        state.inspect = { set, rowIndex, fields };
        document.querySelectorAll('tr.selected').forEach(r => r.classList.remove('selected'));
        document.querySelector(`tr[data-row="${rowIndex}"]`)?.classList.add('selected');
        renderInspector();
    }

    function renderInspector() {
        const box = $('inspector');
        const current = state.inspect;
        if (!box || !current) { return; }
        const { set, rowIndex, fields } = current;
        const row = set.rows[rowIndex];
        const valueOf = name => {
            const i = set.columns.findIndex(c => c.name.toLowerCase() === name.toLowerCase());
            return i < 0 ? undefined : row[i];
        };
        const filter = el('input', { type: 'search', id: 'inspectorFilter', placeholder: 'Field', 'aria-label': 'Filter fields', value: current.filter || '' });
        filter.addEventListener('input', () => { current.filter = filter.value; renderList(); filter.focus(); });
        const move = delta => {
            const target = rowIndex + delta;
            if (target >= 0 && target < set.rows.length) {
                inspect(set, target, fields);
                document.querySelector(`tr[data-row="${target}"]`)?.scrollIntoView({ block: 'nearest' });
            }
        };
        const company = valueOf('DATAAREAID');
        const copyJson = () => {
            const obj = {};
            set.columns.forEach((c, i) => { obj[c.name] = row[i]; });
            send({ type: 'copy', text: JSON.stringify(obj, null, 2) });
        };
        const copyWhere = () => {
            const keys = ['RECID', 'DATAAREAID'].filter(k => valueOf(k) !== undefined && valueOf(k) !== null);
            send({ type: 'copy', text: `WHERE ${keys.map(k => `${k} = ${sqlText(valueOf(k))}`).join(' AND ')}` });
        };
        const list = el('dl', { class: 'fields' });
        const renderList = () => {
            list.replaceChildren();
            const needle = (current.filter || '').toLowerCase();
            set.columns.forEach((c, i) => {
                const field = fields[i];
                if (needle && !c.name.toLowerCase().includes(needle) && !(field?.label || '').toLowerCase().includes(needle)) { return; }
                const value = row[i];
                const text = value === null || value === undefined ? 'NULL' : String(value);
                const enumInfo = field?.enumType ? state.shape.enums[field.enumType] : undefined;
                const enumName = enumInfo?.values?.[text];
                const dd = el('dd', {}, [el('span', { class: value === null ? 'null' : undefined, text: enumName !== undefined ? `${text} (${enumName})` : text })]);
                for (const relation of relationsOf(field ? field.name : c.name)) {
                    const filters = relation.constraints.map(k => ({ column: k.relatedField, operator: '=', value: String(valueOf(k.field) ?? '') }));
                    if (filters.some(f => f.value === '' || f.value === '0')) { continue; }
                    const label = `Open related ${relation.relatedTable} where ${filters.map(f => `${f.column} = ${f.value}`).join(' and ')}`;
                    dd.append(el('button', { class: 'link', title: label, 'aria-label': label, text: `\u2192 ${relation.relatedTable}`,
                        on: { click: () => send({ type: 'openRelated', table: relation.relatedTable, filters, company: company || undefined }) } }));
                }
                list.append(el('dt', { title: field?.label || undefined, text: c.name }), dd);
            });
        };
        renderList();
        box.replaceChildren(
            el('div', { class: 'inspector-bar' }, [
                el('span', { text: `Row ${rowIndex + 1} of ${set.rows.length}` }),
                el('span', { class: 'spacer' }),
                el('button', { title: 'Previous row', 'aria-label': 'Previous row', text: '\u25C0', disabled: rowIndex === 0, on: { click: () => move(-1) } }),
                el('button', { title: 'Next row', 'aria-label': 'Next row', text: '\u25B6', disabled: rowIndex === set.rows.length - 1, on: { click: () => move(1) } }),
            ]),
            filter, list,
            el('div', { class: 'inspector-actions' }, [
                el('button', { text: 'Copy as JSON', on: { click: copyJson } }),
                el('button', { text: 'Copy WHERE clause', on: { click: copyWhere } }),
            ]));
    }

    /** Relations that use this field, matched by name. */
    function relationsOf(fieldName) {
        const lower = fieldName.toLowerCase();
        return state.relations.filter(r => r.constraints.some(k => k.field.toLowerCase() === lower));
    }

    function renderCell(value, column, field) {
        if (value === null || value === undefined) { return el('td', { class: 'null', text: 'NULL' }); }
        const text = String(value);
        const enumInfo = field?.enumType ? state.shape.enums[field.enumType] : undefined;
        const name = enumInfo?.values?.[text];
        const td = name !== undefined
            ? el('td', { class: 'enum', title: `${field.enumType}::${name}` }, [document.createTextNode(name), el('span', { class: 'raw', text: `(${text})` })])
            : el('td', {
                class: NumericTypes.test(column.type) ? 'num' : undefined,
                title: enumInfo?.extensible ? `${field.enumType}: a value added by an enum extension (its number is assigned per environment)` : text.length > 60 ? text : undefined,
                text,
            });
        td.addEventListener('dblclick', () => send({ type: 'copy', text }));
        return td;
    }

    function setRunning(running) {
        state.running = running;
        const cancel = $('cancel');
        if (cancel) { cancel.disabled = !running; }
        if (running) {
            $('status').textContent = 'Running…';
            $('error').hidden = true;
        }
    }

    window.addEventListener('message', event => {
        const msg = event.data;
        switch (msg.type) {
            case 'init':
                state.mode = msg.mode;
                if (msg.mode.kind === 'table') {
                    state.filters = (msg.mode.filters || []).slice();
                    state.company = msg.mode.company || '';
                    renderFilters();
                }
                $('editor').hidden = msg.mode.kind !== 'query';
                if (msg.mode.kind === 'query') {
                    $('sql').value = msg.mode.sql || '';
                    $('sql').addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); runQuery(); } });
                    $('sql').focus();
                }
                renderToolbar();
                break;
            case 'shape':
                state.shape = msg.shape;
                renderToolbar();
                break;
            case 'relations':
                state.relations = msg.relations || [];
                renderInspector();
                break;
            case 'target':
                state.targetText = msg.text;
                if (state.canWrite !== msg.canWrite) {
                    state.canWrite = msg.canWrite;
                    renderToolbar();
                }
                $('target').textContent = msg.text;
                break;
            case 'running':
                setRunning(true);
                break;
            case 'result':
                setRunning(false);
                renderResult(msg.result);
                break;
            case 'error':
                setRunning(false);
                $('status').textContent = '';
                $('error').textContent = msg.message;
                $('error').hidden = false;
                break;
        }
    });

    send({ type: 'ready' });
}());
