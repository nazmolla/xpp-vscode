// Trace analysis panel: timeline, requests with call trees, hot methods, SQL and pauses. All report text goes through textContent.
(function () {
    'use strict';
    const vscode = acquireVsCodeApi();
    const saved = (vscode.getState && vscode.getState()) || {};
    const S = {
        report: null,
        tab: saved.tab || 'requests',
        sel: -1,
        showBatch: !!saved.showBatch,
        includeWaits: !!saved.includeWaits,
        timelineOpen: saved.timelineOpen !== false,
        reqSort: saved.reqSort || 'slow',
        filter: '',
        sort: { methods: { key: 'excl', dir: -1 }, sql: { key: 'ms', dir: -1 }, gaps: { key: 'ms', dir: -1 } },
        expanded: new Map(),
        focusNode: new Map(),
        sqlOpen: new Set(),
    };
    const waits = new Set();
    const searchText = new WeakMap();
    const flat = new WeakMap();
    const $ = id => document.getElementById(id);
    const persist = () => vscode.setState && vscode.setState({ tab: S.tab, showBatch: S.showBatch, includeWaits: S.includeWaits, timelineOpen: S.timelineOpen, reqSort: S.reqSort });

    // ---------- helpers ----------
    function h(tag, props, ...children) {
        const node = document.createElement(tag);
        if (props) {
            for (const [k, v] of Object.entries(props)) {
                if (v === undefined || v === null || v === false) continue;
                if (k === 'class') node.className = v;
                else if (k === 'text') node.textContent = v;
                else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
                else node.setAttribute(k, v === true ? '' : String(v));
            }
        }
        for (const c of children.flat(Infinity)) if (c !== undefined && c !== null && c !== false) node.append(c instanceof Node ? c : String(c));
        return node;
    }
    const SVGNS = 'http://www.w3.org/2000/svg';
    function s(tag, attrs, ...children) {
        const node = document.createElementNS(SVGNS, tag);
        for (const [k, v] of Object.entries(attrs || {})) if (v !== undefined && v !== null) node.setAttribute(k, String(v));
        for (const c of children) if (c) node.append(c instanceof Node ? c : document.createTextNode(String(c)));
        return node;
    }
    const icon = (name, extra) => h('span', { class: `codicon codicon-${name}${extra ? ' ' + extra : ''}`, 'aria-hidden': 'true' });
    function dur(v) {
        if (v === undefined || v === null) return '';
        if (v <= 0) return '0 ms';
        if (v < 1) return '<1 ms';
        if (v < 10) return `${v.toFixed(1)} ms`;
        if (v < 1000) return `${Math.round(v)} ms`;
        if (v < 10000) return `${(v / 1000).toFixed(2)} s`;
        return `${(v / 1000).toFixed(1)} s`;
    }
    const at = v => `${(v / 1000).toFixed(v < 10000 ? 2 : 1)} s`;
    const num = v => Number(v || 0).toLocaleString('en-US');
    const pct = (a, b) => b > 0 ? `${Math.round((a / b) * 100)}%` : '';
    const lower = v => (v || '').toLowerCase();
    const matches = text => !S.filter || lower(text).includes(S.filter);
    function split(m) {
        const i = m.lastIndexOf('::');
        return i < 0 ? ['', m] : [m.slice(0, i), m.slice(i + 2)];
    }
    /** A method name: class part dimmed, member emphasized; a link that opens the code unless it is a placeholder. */
    function method(m, opts) {
        const o = opts || {};
        const [cls, mem] = split(m || '');
        const parts = [h('span', { class: 'cls', text: cls }), cls ? h('span', { class: 'sep', text: '::' }) : null, h('span', { class: 'mem', text: mem })];
        if (!m || m.startsWith('(')) return h('span', { class: 'method none', title: m, text: m || '' });
        if (o.plain) return h('span', { class: 'method plain', title: m }, parts);
        return h('a', { class: 'method', href: '#', 'data-m': m, title: `Open ${m}`, tabindex: o.tabindex }, parts);
    }
    function bar(fraction, cls, extra) {
        const track = h('span', { class: `bar ${cls || ''}`, 'aria-hidden': 'true' });
        const fill = h('span', { class: 'fill' });
        fill.style.width = `${Math.max(0, Math.min(1, fraction)) * 100}%`;
        track.append(fill);
        if (extra !== undefined) {
            const self = h('span', { class: 'self' });
            self.style.width = `${Math.max(0, Math.min(1, extra)) * 100}%`;
            track.append(self);
        }
        return track;
    }
    function empty(iconName, title, text) {
        return h('div', { class: 'empty' }, icon(iconName, 'empty-icon'), h('div', { class: 'empty-title', text: title }), text ? h('div', { class: 'empty-text', text }) : null);
    }
    function badge(text, cls, title) {
        return h('span', { class: `badge ${cls || ''}`, text, title });
    }
    const isWait = m => waits.has(m);
    const visibleRequests = () => S.report.requests.map((q, i) => ({ q, i })).filter(({ q }) => S.showBatch || !q.batch);

    /** Flattened view of a request tree: parent and depth per node, computed once. */
    function flatten(q) {
        let f = flat.get(q);
        if (f) return f;
        f = { parent: new Map(), depth: new Map(), all: [] };
        const walk = (n, d, p) => {
            f.parent.set(n, p);
            f.depth.set(n, d);
            f.all.push(n);
            if (n.c) for (const c of n.c) walk(c, d + 1, n);
        };
        walk(q.tree, 0, null);
        flat.set(q, f);
        return f;
    }
    function requestText(q) {
        let t = searchText.get(q);
        if (t === undefined) {
            t = lower(flatten(q).all.map(n => n.m).join('\n'));
            searchText.set(q, t);
        }
        return t;
    }

    // ---------- header ----------
    function renderHeader() {
        const r = S.report;
        const name = r.file.split(/[\\/]/).pop();
        $('file').textContent = name;
        $('file').title = r.file;
        const batch = r.requests.filter(q => q.batch).length;
        const slowest = [...r.requests].filter(q => !q.batch).sort((a, b) => b.ms - a.ms)[0] || [...r.requests].sort((a, b) => b.ms - a.ms)[0];
        const longestGap = r.gaps.reduce((m, g) => Math.max(m, g.ms), 0);
        const tile = (label, value, sub, opts) => h(opts && opts.onclick ? 'button' : 'div', { class: `tile ${(opts && opts.cls) || ''}`, onclick: opts && opts.onclick, title: opts && opts.title },
            h('span', { class: 'tile-label', text: label }), h('span', { class: 'tile-value', text: value }), sub ? h('span', { class: 'tile-sub', text: sub }) : null);
        $('stats').replaceChildren(
            tile('Duration', dur(r.durationMs), `${num(r.events)} events`),
            tile('Requests', num(r.requests.length - batch), batch ? `+ ${num(batch)} batch tasks` : 'over 50 ms'),
            slowest ? tile(slowest.batch ? 'Slowest task' : 'Slowest request', dur(slowest.ms), split(slowest.method)[1] + (slowest.unfinished ? ' · unfinished' : ''),
                { cls: 'tile-action', title: `Show ${slowest.method}`, onclick: () => { setTab('requests'); select(r.requests.indexOf(slowest), { scroll: true }); } }) : null,
            tile('X++ calls', num(r.xppCalls), `${num(r.methods.length)} hot methods listed`),
            tile('SQL', num(r.sqlTotal.n), `${dur(r.sqlTotal.ms)} in the database`),
            tile('Pauses', num(r.gaps.length), r.gaps.length ? `longest ${dur(longestGap)}` : 'none over 500 ms'));
    }

    // ---------- timeline ----------
    let timelineFrame = 0;
    function scheduleTimeline() {
        cancelAnimationFrame(timelineFrame);
        timelineFrame = requestAnimationFrame(renderTimeline);
    }
    function renderTimeline() {
        const r = S.report;
        const host = $('timeline');
        $('overview').classList.toggle('collapsed', !S.timelineOpen);
        $('tlToggle').setAttribute('aria-expanded', String(S.timelineOpen));
        $('tlBatch').checked = S.showBatch;
        if (!S.timelineOpen) { host.replaceChildren(); return; }
        const list = visibleRequests();
        const lanes = [];
        for (const { q } of [...list].sort((a, b) => a.q.startMs - b.q.startMs)) if (!lanes.includes(q.thread)) lanes.push(q.thread);
        const width = Math.max(320, host.clientWidth);
        const labelW = 64, padR = 12, axisH = 18, laneH = 16, gap = 3;
        const plotW = width - labelW - padR;
        const height = axisH + Math.max(1, lanes.length) * (laneH + gap) + 2;
        const total = r.durationMs || 1;
        const x = t => labelW + (t / total) * plotW;
        const svg = s('svg', { width, height, viewBox: `0 0 ${width} ${height}`, role: 'img', 'aria-label': `Timeline: ${list.length} requests on ${lanes.length} threads over ${dur(total)}` });
        const defs = s('defs');
        const hatch = s('pattern', { id: 'hatch', width: 4, height: 4, patternUnits: 'userSpaceOnUse', patternTransform: 'rotate(45)' });
        hatch.append(s('rect', { class: 'hatch-bg', width: 4, height: 4 }), s('line', { class: 'hatch-line', x1: 0, y1: 0, x2: 0, y2: 4 }));
        defs.append(hatch);
        svg.append(defs);
        // Axis with "nice" ticks.
        const steps = [100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000];
        const step = steps.find(st => total / st <= Math.max(2, plotW / 90)) || 600000;
        for (let t = 0; t <= total; t += step) {
            const px = x(t);
            svg.append(s('line', { class: 'grid', x1: px, y1: axisH - 4, x2: px, y2: height }));
            svg.append(s('text', { class: 'tick', x: px + 3, y: 11 }, step < 1000 ? `${(t / 1000).toFixed(1)} s` : `${Math.round(t / 1000)} s`));
        }
        lanes.forEach((thread, li) => {
            const y = axisH + li * (laneH + gap);
            svg.append(s('rect', { class: 'lane', x: labelW, y, width: plotW, height: laneH }));
            const kinds = list.filter(e => e.q.thread === thread);
            const label = s('text', { class: 'lane-label', x: labelW - 8, y: y + laneH - 4, 'text-anchor': 'end' }, `T${thread}`);
            label.append(s('title', {}, `Thread ${thread}${kinds.every(e => e.q.batch) ? ' (batch)' : ''}`));
            svg.append(label);
        });
        for (const g of r.gaps.filter(g => !g.thread)) {
            const rect = s('rect', { class: 'gap-all', x: x(g.startMs), y: axisH - 2, width: Math.max(2, x(g.startMs + g.ms) - x(g.startMs)), height: height - axisH + 2, fill: 'url(#hatch)' });
            rect.append(s('title', {}, `Whole process silent for ${dur(g.ms)} at ${at(g.startMs)}`));
            svg.append(rect);
        }
        const sel = r.requests[S.sel];
        if (sel && (S.showBatch || !sel.batch)) {
            svg.append(s('rect', { class: 'sel-span', x: x(sel.startMs), y: axisH - 4, width: Math.max(2, x(sel.startMs + sel.ms) - x(sel.startMs)), height: height - axisH + 4 }));
        }
        for (const { q, i } of list) {
            const li = lanes.indexOf(q.thread);
            const y = axisH + li * (laneH + gap);
            const w = Math.max(3, x(q.startMs + q.ms) - x(q.startMs));
            const cls = ['req', q.batch ? 'batch' : 'ui', q.unfinished ? 'unfinished' : '', i === S.sel ? 'selected' : '', S.filter && !requestText(q).includes(S.filter) ? 'dimmed' : ''].join(' ');
            const rect = s('rect', { class: cls, x: x(q.startMs), y, width: w, height: laneH, rx: 2, 'data-i': i });
            rect.append(s('title', {}, `${q.method}\n${dur(q.ms)}${q.unfinished ? ' (unfinished)' : ''} at ${at(q.startMs)} · thread ${q.thread}${q.batch ? ' · batch' : ''}\n${num(q.sqlN)} SQL · ${dur(q.sqlMs)}`));
            svg.append(rect);
            if (w > 48) {
                const name = split(q.method)[1];
                const fit = Math.floor((w - 8) / 6);
                const text = s('text', { class: 'req-label', x: x(q.startMs) + 4, y: y + laneH - 5 }, name.length > fit ? name.slice(0, Math.max(1, fit - 1)) + '…' : name);
                svg.append(text);
            }
        }
        for (const g of r.gaps.filter(g => g.thread && lanes.includes(g.thread))) {
            const li = lanes.indexOf(g.thread);
            const y = axisH + li * (laneH + gap);
            const rect = s('rect', { class: 'gap', x: x(g.startMs), y: y + laneH - 3, width: Math.max(2, x(g.startMs + g.ms) - x(g.startMs)), height: 3 });
            rect.append(s('title', {}, `Pause of ${dur(g.ms)} at ${at(g.startMs)} in ${g.method || 'thread ' + g.thread}`));
            svg.append(rect);
        }
        if (!lanes.length) svg.append(s('text', { class: 'tick', x: labelW, y: axisH + 11 }, 'No requests to show. Turn on batch tasks to see background work.'));
        host.replaceChildren(svg);
    }

    // ---------- tabs ----------
    const TABS = [
        ['requests', 'Requests', 'pulse'],
        ['methods', 'Hot methods', 'flame'],
        ['sql', 'SQL', 'database'],
        ['gaps', 'Pauses', 'debug-pause'],
    ];
    function renderTabs() {
        const r = S.report;
        const counts = { requests: visibleRequests().length, methods: r.methods.length, sql: r.sql.length, gaps: r.gaps.length };
        $('tabs').replaceChildren(...TABS.map(([id, label, ic]) => h('button', {
            role: 'tab', id: `tab-${id}`, class: 'tab', 'aria-selected': String(S.tab === id), 'aria-controls': 'panel', tabindex: S.tab === id ? '0' : '-1',
            onclick: () => setTab(id),
        }, icon(ic), h('span', { text: label }), h('span', { class: 'count', text: num(counts[id]) }))));
        $('panel').setAttribute('aria-labelledby', `tab-${S.tab}`);
    }
    function setTab(id, focus) {
        if (S.tab !== id) {
            S.tab = id;
            persist();
            renderTabs();
            renderPanel();
        }
        if (focus) $(`tab-${id}`).focus();
    }

    function renderPanel() {
        const panel = $('panel');
        panel.className = `panel panel-${S.tab}`;
        panel.replaceChildren(({ requests: requestsView, methods: methodsView, sql: sqlView, gaps: gapsView })[S.tab]());
        if (S.tab === 'requests') renderDetail();
    }

    // ---------- requests ----------
    function listedRequests() {
        const list = visibleRequests().filter(({ q }) => !S.filter || requestText(q).includes(S.filter));
        if (S.reqSort === 'slow') list.sort((a, b) => b.q.ms - a.q.ms);
        return list;
    }
    function requestsView() {
        const r = S.report;
        const list = listedRequests();
        if (!list.some(e => e.i === S.sel) && list.length) S.sel = [...list].sort((a, b) => b.q.ms - a.q.ms)[0].i;
        const max = list.reduce((m, e) => Math.max(m, e.q.ms), 0);
        const hiddenBatch = S.showBatch ? 0 : r.requests.filter(q => q.batch).length;
        const items = list.map(({ q, i }) => {
            const item = h('div', { role: 'option', id: `req-${i}`, class: `req-item${q.batch ? ' is-batch' : ''}${i === S.sel ? ' selected' : ''}`, 'aria-selected': String(i === S.sel), 'data-i': i },
                h('div', { class: 'req-line' }, icon(q.batch ? 'server-process' : 'symbol-event', 'req-icon'), method(q.method, { plain: true }), h('span', { class: 'req-ms num', text: dur(q.ms) })),
                bar(max ? q.ms / max : 0, q.batch ? 'batch' : 'ui'),
                h('div', { class: 'req-meta' },
                    h('span', { text: `T${q.thread} · ${at(q.startMs)}${q.sqlN ? ` · ${num(q.sqlN)} SQL` : ''}` }),
                    q.unfinished ? badge('unfinished', 'warn', 'Still running when the trace stopped') : null,
                    q.batch ? badge('batch', 'muted') : null));
            item.title = `${q.method}\n${dur(q.ms)} on thread ${q.thread}`;
            return item;
        });
        const listbox = h('div', {
            role: 'listbox', id: 'reqList', class: 'req-list', tabindex: '0', 'aria-label': 'Requests', 'aria-activedescendant': S.sel >= 0 && list.some(e => e.i === S.sel) ? `req-${S.sel}` : null,
            onclick: e => { const it = e.target.closest('.req-item'); if (it) select(Number(it.dataset.i)); },
            onkeydown: e => {
                const ids = list.map(x => x.i);
                let k = ids.indexOf(S.sel);
                if (e.key === 'ArrowDown') k = Math.min(ids.length - 1, k + 1);
                else if (e.key === 'ArrowUp') k = Math.max(0, k - 1);
                else if (e.key === 'Home') k = 0;
                else if (e.key === 'End') k = ids.length - 1;
                else if (e.key === 'PageDown') k = Math.min(ids.length - 1, k + 10);
                else if (e.key === 'PageUp') k = Math.max(0, k - 10);
                else if (e.key === 'Enter' || e.key === 'ArrowRight') { const row = document.querySelector('#tree .trow[tabindex="0"]'); if (row) { row.focus(); e.preventDefault(); } return; }
                else return;
                e.preventDefault();
                if (ids[k] !== undefined) select(ids[k], { scroll: true });
            },
        }, items.length ? items : empty('search', 'No matching requests', S.filter ? 'Nothing matches the filter.' : 'No requests over 50 ms.'));
        const sort = h('select', {
            class: 'dropdown', 'aria-label': 'Order requests', onchange: e => { S.reqSort = e.target.value; persist(); renderPanel(); },
        }, h('option', { value: 'slow', selected: S.reqSort === 'slow', text: 'Slowest first' }), h('option', { value: 'time', selected: S.reqSort === 'time', text: 'In time order' }));
        return h('div', { class: 'req-layout' },
            h('aside', { class: 'req-side', 'aria-label': 'Requests' },
                h('div', { class: 'pane-head' }, h('span', { class: 'pane-title', text: `${list.length} ${list.length === 1 ? 'request' : 'requests'}` }), sort),
                listbox,
                hiddenBatch ? h('div', { class: 'pane-foot' }, h('span', { text: `${hiddenBatch} batch tasks hidden` }),
                    h('button', { class: 'link', onclick: () => setBatch(true), text: 'Show' })) : null),
            h('section', { class: 'req-detail', id: 'detail', 'aria-label': 'Request details' }));
    }

    function select(i, opts) {
        if (i === S.sel && $('detail') && $('detail').childElementCount) return;
        S.sel = i;
        const list = $('reqList');
        if (list) {
            for (const it of list.querySelectorAll('.req-item.selected')) { it.classList.remove('selected'); it.setAttribute('aria-selected', 'false'); }
            const it = $(`req-${i}`);
            if (it) {
                it.classList.add('selected');
                it.setAttribute('aria-selected', 'true');
                list.setAttribute('aria-activedescendant', it.id);
                if (opts && opts.scroll) it.scrollIntoView({ block: 'nearest' });
            }
        }
        renderDetail();
        scheduleTimeline();
    }

    function renderDetail() {
        const detail = $('detail');
        if (!detail) return;
        const q = S.report.requests[S.sel];
        if (!q || !listedRequests().some(e => e.i === S.sel)) {
            detail.replaceChildren(empty('pulse', 'Select a request', 'Pick a request on the left or in the timeline to see where its time went.'));
            return;
        }
        const waitMs = (q.topExclusive || []).filter(t => t.wait).reduce((a, t) => a + t.ms, 0);
        const fact = (label, value, sub, cls) => h('div', { class: `fact ${cls || ''}` }, h('dt', { text: label }), h('dd', {}, h('span', { class: 'fact-value', text: value }), sub ? h('span', { class: 'fact-sub', text: sub }) : null));
        const head = h('div', { class: 'detail-head' },
            h('div', { class: 'detail-title' }, icon(q.batch ? 'server-process' : 'symbol-event'), h('h2', {}, method(q.method)),
                q.unfinished ? badge('unfinished', 'warn', 'Still running when the trace stopped; the duration is a minimum') : null, q.batch ? badge('batch task', 'muted') : null),
            h('dl', { class: 'facts' },
                fact('Duration', (q.unfinished ? '≥ ' : '') + dur(q.ms), q.unfinished ? 'still running at trace end' : null),
                fact('Started', at(q.startMs), `thread ${q.thread}`),
                fact('SQL', dur(q.sqlMs), `${num(q.sqlN)} statements · ${pct(q.sqlMs, q.ms)}`, 'f-sql'),
                waitMs ? fact('Waiting', dur(waitMs), `${pct(waitMs, q.ms)} not working`, 'f-wait') : fact('Busy', dur(q.ms - q.sqlMs), 'X++ and kernel')));
        const top = (q.topExclusive || []).slice(0, 8);
        const maxTop = top.reduce((m, t) => Math.max(m, t.ms), 0);
        const spots = h('section', { class: 'hotspots', 'aria-label': 'Hot spots' },
            h('h3', { class: 'section-title' }, 'Hot spots', h('span', { class: 'section-sub', text: 'methods with the most self time in this request' })),
            top.length ? h('ol', { class: 'spot-list' }, top.map(t => h('li', { class: `spot${t.wait ? ' wait' : ''}` },
                method(t.m),
                t.wait ? badge('waiting', 'muted', 'Waits for another session; this is not work done here') : null,
                bar(maxTop ? t.ms / maxTop : 0, t.wait ? 'wait' : 'hot'),
                h('span', { class: 'num spot-ms', text: dur(t.ms) }),
                h('span', { class: 'num spot-pct', text: pct(t.ms, q.ms) })))) : h('p', { class: 'dim', text: 'No self time recorded.' }));
        const tools = h('div', { class: 'tree-tools' },
            h('h3', { class: 'section-title' }, 'Call tree'),
            h('button', { class: 'tool', title: 'Expand the heaviest path', onclick: () => expandHotPath(q) }, icon('flame'), h('span', { text: 'Hot path' })),
            h('button', { class: 'tool', title: 'Collapse all', onclick: () => { S.expanded.set(q, new Set([q.tree])); S.focusNode.set(q, q.tree); renderTree(q); } }, icon('collapse-all'), h('span', { text: 'Collapse' })));
        const grid = h('div', { role: 'treegrid', id: 'tree', class: 'tree', 'aria-label': `Call tree of ${q.method}`, 'aria-readonly': 'true' },
            h('div', { role: 'row', class: 'thead' },
                h('div', { role: 'columnheader', class: 'c-name', text: 'Method' }),
                h('div', { role: 'columnheader', class: 'c-total num', text: 'Total', title: 'Inclusive time: the method and everything it called' }),
                h('div', { role: 'columnheader', class: 'c-self num', text: 'Self', title: 'Exclusive time: spent in the method itself' }),
                h('div', { role: 'columnheader', class: 'c-calls num', text: 'Calls' }),
                h('div', { role: 'columnheader', class: 'c-sql num', text: 'SQL' })),
            h('div', { role: 'rowgroup', id: 'treeBody', class: 'tbody' }));
        grid.addEventListener('keydown', e => treeKey(e, q));
        grid.addEventListener('click', e => {
            if (e.target.closest('a.method')) return;
            const row = e.target.closest('.trow');
            if (!row || !row.node) return;
            S.focusNode.set(q, row.node);
            if (e.target.closest('.twisty')) toggle(q, row.node);
            else { setRovingRow(row); row.focus(); }
        });
        grid.addEventListener('dblclick', e => { const row = e.target.closest('.trow'); if (row && row.node && !e.target.closest('a')) toggle(q, row.node); });
        detail.replaceChildren(head, spots, h('section', { class: 'tree-wrap', 'aria-label': 'Call tree' }, tools, grid));
        if (!S.expanded.has(q)) defaultExpand(q);
        if (S.filter) {
            expandMatches(q);
            const first = flatten(q).all.find(n => lower(n.m).includes(S.filter));
            if (first) S.focusNode.set(q, first);
            renderTree(q, { center: !!first });
            return;
        }
        renderTree(q);
    }

    // ---------- call tree ----------
    function heaviest(n) { return n.c ? n.c.reduce((a, b) => (b.incl > a.incl ? b : a)) : null; }
    function defaultExpand(q) {
        const set = new Set([q.tree]);
        const root = q.tree.incl || 1;
        const walk = n => { if (n.c && n.incl >= root * 0.15) { set.add(n); n.c.forEach(walk); } };
        walk(q.tree);
        S.expanded.set(q, set);
        S.focusNode.set(q, q.tree);
    }
    function expandHotPath(q) {
        const set = S.expanded.get(q) || new Set();
        let n = q.tree, last = n;
        const root = q.tree.incl || 1;
        while (n && n.c) {
            set.add(n);
            const next = heaviest(n);
            if (!next || next.incl < root * 0.02) break;
            last = next;
            n = next;
        }
        S.expanded.set(q, set);
        S.focusNode.set(q, last);
        renderTree(q, { focus: true, center: true });
    }
    function expandMatches(q) {
        const set = S.expanded.get(q);
        const f = flatten(q);
        for (const n of f.all) {
            if (!lower(n.m).includes(S.filter)) continue;
            for (let p = f.parent.get(n); p; p = f.parent.get(p)) set.add(p);
        }
    }
    function toggle(q, node) {
        if (!node.c) return;
        const set = S.expanded.get(q);
        if (set.has(node)) set.delete(node); else set.add(node);
        S.focusNode.set(q, node);
        renderTree(q, { focus: true });
    }
    function indentWidth(depth) {
        // Deep stacks (40+ levels) compress after level 16 so names stay readable.
        return depth <= 16 ? depth * 14 : 16 * 14 + (depth - 16) * 4;
    }
    function renderTree(q, opts) {
        const body = $('treeBody');
        if (!body) return;
        const set = S.expanded.get(q);
        const root = q.tree.incl || 1;
        const frag = document.createDocumentFragment();
        const focusNode = S.focusNode.get(q) || q.tree;
        let focusRow = null;
        const walk = (n, depth, pos, size) => {
            const open = set.has(n) && !!n.c;
            const wait = isWait(n.m);
            const selfShare = n.excl / root;
            const row = h('div', {
                role: 'row', class: `trow${wait ? ' wait' : ''}${S.filter && lower(n.m).includes(S.filter) ? ' match' : ''}`, tabindex: '-1',
                'aria-level': depth + 1, 'aria-posinset': pos, 'aria-setsize': size, 'aria-expanded': n.c ? String(open) : null,
                title: `${n.m}\nTotal ${dur(n.incl)} (${pct(n.incl, root)}) · self ${dur(n.excl)} (${pct(n.excl, root)}) · ${num(n.n)} calls`,
            });
            row.node = n;
            const indent = h('span', { class: 'indent' });
            indent.style.width = `${indentWidth(depth)}px`;
            const twisty = n.c ? h('span', { class: `twisty codicon codicon-chevron-${open ? 'down' : 'right'}`, 'aria-hidden': 'true' }) : h('span', { class: 'twisty leaf', 'aria-hidden': 'true' });
            row.append(
                h('div', { role: 'gridcell', class: 'c-name' }, indent, twisty, method(n.m, { tabindex: '-1' }), wait ? badge('waiting', 'muted') : null),
                h('div', { role: 'gridcell', class: 'c-total' }, bar(n.incl / root, wait ? 'wait' : 'incl', wait ? 0 : selfShare), h('span', { class: 'num', text: dur(n.incl) })),
                h('div', { role: 'gridcell', class: `c-self num${!wait && selfShare >= 0.2 ? ' heat2' : !wait && selfShare >= 0.05 ? ' heat1' : ''}`, text: n.excl >= 0.05 ? dur(n.excl) : '' }),
                h('div', { role: 'gridcell', class: 'c-calls num', text: num(n.n) }),
                h('div', { role: 'gridcell', class: 'c-sql num', text: n.sqlN ? `${num(n.sqlN)} · ${dur(n.sqlMs)}` : '' }));
            if (n === focusNode) focusRow = row;
            frag.append(row);
            if (open) {
                const size2 = n.c.length + (n.hidden ? 1 : 0);
                n.c.forEach((c, k) => walk(c, depth + 1, k + 1, size2));
                if (n.hidden) {
                    const moreIndent = h('span', { class: 'indent' });
                    moreIndent.style.width = `${indentWidth(depth + 1)}px`;
                    frag.append(h('div', { role: 'row', class: 'trow more', 'aria-level': depth + 2, 'aria-posinset': size2, 'aria-setsize': size2, tabindex: '-1' },
                        h('div', { role: 'gridcell', class: 'c-name' }, moreIndent, h('span', { class: 'twisty leaf' }),
                            h('span', { class: 'dim', text: `${n.hidden} smaller ${n.hidden === 1 ? 'call' : 'calls'} not shown` }))));
                }
            }
        };
        walk(q.tree, 0, 1, 1);
        body.replaceChildren(frag);
        const target = focusRow || body.querySelector('.trow');
        if (target) {
            target.tabIndex = 0;
            if (opts && opts.focus) target.focus({ preventScroll: true });
            if (opts && (opts.focus || opts.center)) target.scrollIntoView({ block: opts.center ? 'center' : 'nearest' });
        }
    }
    function setRovingRow(row) {
        const body = $('treeBody');
        for (const r of body.querySelectorAll('.trow[tabindex="0"]')) r.tabIndex = -1;
        row.tabIndex = 0;
    }
    function treeKey(e, q) {
        const row = e.target.closest('.trow');
        if (!row) return;
        const rows = [...$('treeBody').querySelectorAll('.trow')];
        const k = rows.indexOf(row);
        const node = row.node;
        const set = S.expanded.get(q);
        const go = target => {
            if (!target) return;
            e.preventDefault();
            setRovingRow(target);
            if (target.node) S.focusNode.set(q, target.node);
            target.focus();
            target.scrollIntoView({ block: 'nearest' });
        };
        switch (e.key) {
            case 'ArrowDown': go(rows[k + 1]); break;
            case 'ArrowUp': go(rows[k - 1]); break;
            case 'Home': go(rows[0]); break;
            case 'End': go(rows[rows.length - 1]); break;
            case 'PageDown': go(rows[Math.min(rows.length - 1, k + 15)]); break;
            case 'PageUp': go(rows[Math.max(0, k - 15)]); break;
            case 'ArrowRight':
                if (!node || !node.c) return;
                e.preventDefault();
                if (!set.has(node)) toggle(q, node); else go(rows[k + 1]);
                break;
            case 'ArrowLeft': {
                e.preventDefault();
                if (node && node.c && set.has(node)) { toggle(q, node); break; }
                const level = Number(row.getAttribute('aria-level'));
                for (let j = k - 1; j >= 0; j--) if (Number(rows[j].getAttribute('aria-level')) < level) { go(rows[j]); break; }
                break;
            }
            case 'Enter':
                if (node) { e.preventDefault(); open(node.m); }
                break;
            case ' ':
                if (node) { e.preventDefault(); toggle(q, node); }
                break;
        }
    }

    // ---------- sortable tables ----------
    function table(key, columns, rows, renderRow, label) {
        const sort = S.sort[key];
        const head = h('tr', {}, columns.map(([id, text, cls, title]) => {
            const sorted = id && sort.key === id;
            const th = h('th', { class: cls || '', scope: 'col', 'aria-sort': sorted ? (sort.dir < 0 ? 'descending' : 'ascending') : (id ? 'none' : null), title });
            if (!id) { th.textContent = text; return th; }
            th.append(h('button', {
                class: `sort${sorted ? ' sorted' : ''}`, onclick: () => {
                    S.sort[key] = { key: id, dir: sort.key === id ? -sort.dir : -1 };
                    renderPanel();
                    const again = document.querySelector(`th button[data-k="${id}"]`);
                    if (again) again.focus();
                }, 'data-k': id,
            }, h('span', { text }), icon(sorted ? (sort.dir < 0 ? 'arrow-down' : 'arrow-up') : 'blank', 'sort-icon')));
            return th;
        }));
        const body = h('tbody', {}, rows.map((row, k) => { const tr = renderRow(row, k); tr.tabIndex = k === 0 ? 0 : -1; return tr; }));
        const t = h('table', { class: `grid grid-${key}`, 'aria-label': label }, h('thead', {}, head), body);
        body.addEventListener('keydown', e => {
            const tr = e.target.closest('tr');
            if (!tr || e.target !== tr) return;
            const all = [...body.rows];
            const k = all.indexOf(tr);
            const go = n => { if (!n) return; e.preventDefault(); tr.tabIndex = -1; n.tabIndex = 0; n.focus(); };
            if (e.key === 'ArrowDown') go(all[k + 1]);
            else if (e.key === 'ArrowUp') go(all[k - 1]);
            else if (e.key === 'Home') go(all[0]);
            else if (e.key === 'End') go(all[all.length - 1]);
            else if (e.key === 'PageDown') go(all[Math.min(all.length - 1, k + 15)]);
            else if (e.key === 'PageUp') go(all[Math.max(0, k - 15)]);
            else if (e.key === 'Enter' && tr.onEnter) { e.preventDefault(); tr.onEnter(); }
        });
        body.addEventListener('click', e => {
            const tr = e.target.closest('tr');
            if (!tr) return;
            for (const r of body.querySelectorAll('tr[tabindex="0"]')) r.tabIndex = -1;
            tr.tabIndex = 0;
        });
        return t;
    }
    const sortRows = (rows, key) => rows.sort((a, b) => (a[S.sort[key].key] - b[S.sort[key].key]) * S.sort[key].dir);
    const viewHead = (title, sub, ...tools) => h('div', { class: 'view-head' }, h('span', { class: 'pane-title', text: title }), sub ? h('span', { class: 'dim', text: sub }) : null, h('span', { class: 'spacer' }), tools);
    const check = (label, checked, onchange) => h('label', { class: 'check' }, h('input', { type: 'checkbox', checked, onchange: e => onchange(e.target.checked) }), h('span', { text: label }));
    const cellBar = (v, max, cls) => h('td', { class: 'num barcell' }, bar(max ? v / max : 0, cls), h('span', { text: dur(v) }));

    // ---------- hot methods ----------
    function methodsView() {
        const all = S.report.methods;
        const waitCount = all.filter(m => m.wait).length;
        const rows = sortRows(all.filter(m => (S.includeWaits || !m.wait) && matches(m.m)).map(m => Object.assign({ avg: m.n ? m.incl / m.n : 0 }, m)), 'methods');
        const maxExcl = rows.reduce((a, m) => Math.max(a, m.excl), 0);
        const maxIncl = rows.reduce((a, m) => Math.max(a, m.incl), 0);
        const head = viewHead('Hot methods', `${num(rows.length)} of ${num(all.length)} · time summed over the whole trace`,
            waitCount ? check(`Include waits (${waitCount})`, S.includeWaits, v => { S.includeWaits = v; persist(); renderPanel(); }) : null);
        if (!rows.length) return h('div', { class: 'view' }, head, empty('search', 'No matching methods', S.filter ? 'Nothing matches the filter.' : null));
        return h('div', { class: 'view' }, head, h('div', { class: 'scroll' }, table('methods', [
            [null, 'Method', 'c-method'], ['excl', 'Self', 'num', 'Exclusive time'], ['incl', 'Total', 'num', 'Inclusive time'], ['n', 'Calls', 'num'],
            ['avg', 'Average', 'num', 'Total time per call'], ['max', 'Longest call', 'num'], ['sqlN', 'SQL', 'num', 'SQL statements run by the method itself'], ['sqlMs', 'SQL time', 'num'],
        ], rows, m => {
            const tr = h('tr', { class: m.wait ? 'wait' : '' },
                h('td', { class: 'c-method' }, method(m.m, { tabindex: '-1' }), m.wait ? badge('waiting', 'muted', 'Waits for another session; not work') : null),
                cellBar(m.excl, maxExcl, m.wait ? 'wait' : 'hot'),
                cellBar(m.incl, maxIncl, 'incl'),
                h('td', { class: 'num', text: num(m.n) }),
                h('td', { class: 'num', text: dur(m.avg) }),
                h('td', { class: 'num', text: dur(m.max) }),
                h('td', { class: 'num', text: m.sqlN ? num(m.sqlN) : '' }),
                h('td', { class: 'num', text: m.sqlN ? dur(m.sqlMs) : '' }));
            tr.onEnter = () => open(m.m);
            return tr;
        }, 'Hot methods')));
    }

    // ---------- SQL ----------
    const KW = /\b(SELECT|FROM|WHERE|AND|OR|NOT|IN|EXISTS|INNER|LEFT|RIGHT|OUTER|CROSS|APPLY|JOIN|ON|ORDER|GROUP|BY|HAVING|UPDATE|SET|INSERT|INTO|VALUES|DELETE|MERGE|USING|OUTPUT|TOP|DISTINCT|UNION|ALL|AS|WITH|NOLOCK|UPDLOCK|READPAST|REPEATABLEREAD|OPTION|IS|NULL|CASE|WHEN|THEN|ELSE|END|BETWEEN|LIKE|ASC|DESC|CALL|EXEC|DECLARE)\b|\?/gi;
    function highlight(text) {
        const frag = document.createDocumentFragment();
        let last = 0;
        for (const m of text.matchAll(KW)) {
            if (m.index > last) frag.append(document.createTextNode(text.slice(last, m.index)));
            frag.append(h('span', { class: m[0] === '?' ? 'sql-p' : 'sql-k', text: m[0] }));
            last = m.index + m[0].length;
        }
        if (last < text.length) frag.append(document.createTextNode(text.slice(last)));
        return frag;
    }
    const pretty = sql => sql
        .replace(/\s+(FROM|WHERE|INNER JOIN|LEFT OUTER JOIN|LEFT JOIN|RIGHT JOIN|CROSS JOIN|CROSS APPLY|OUTER APPLY|JOIN|ORDER BY|GROUP BY|HAVING|SET|VALUES|OPTION|UNION ALL|UNION)\b/gi, '\n$1')
        .replace(/\s+(AND|OR)\s+/g, '\n    $1 ')
        .replace(/,(?=\S)/g, ', ');
    function sqlView() {
        const r = S.report;
        const rows = r.sql.filter(q => matches(q.sql) || q.callers.some(c => matches(c.m)));
        const max = rows.reduce((a, q) => Math.max(a, q.ms), 0);
        const head = viewHead('SQL', `${num(r.sqlTotal.n)} statements · ${dur(r.sqlTotal.ms)} in the database · ${num(rows.length)} distinct shown`);
        if (!rows.length) return h('div', { class: 'view' }, head, empty('database', 'No matching SQL', S.filter ? 'Nothing matches the filter.' : 'The trace has no SQL statements.'));
        return h('div', { class: 'view' }, head, h('div', { class: 'scroll' }, table('sql', [
            ['ms', 'Total', 'num'], ['n', 'Count', 'num'], ['max', 'Longest', 'num'], [null, 'Statement', 'c-stmt'], [null, 'Run by', 'c-callers'],
        ], sortRows(rows, 'sql'), q => {
            const openNow = S.sqlOpen.has(q);
            const scrubbed = /^<.*>$/.test(q.sql.trim());
            const stmtCell = h('td', { class: 'c-stmt' });
            const callersCell = h('td', { class: 'c-callers' });
            const tr = h('tr', { class: openNow ? 'open' : '', 'aria-expanded': String(openNow) }, cellBar(q.ms, max, 'sql'), h('td', { class: 'num', text: num(q.n) }), h('td', { class: 'num', text: dur(q.max) }), stmtCell, callersCell);
            const fill = () => {
                const isOpen = S.sqlOpen.has(q);
                tr.classList.toggle('open', isOpen);
                tr.setAttribute('aria-expanded', String(isOpen));
                const twisty = h('button', { class: 'icon-btn twisty-btn', 'aria-label': isOpen ? 'Collapse statement' : 'Expand statement', title: isOpen ? 'Collapse' : 'Expand', tabindex: '-1', onclick: () => flip() }, icon(isOpen ? 'chevron-down' : 'chevron-right'));
                const text = scrubbed ? h('span', { class: 'sql-text scrubbed', text: 'Direct SQL (the text was scrubbed from the trace)' })
                    : isOpen ? h('pre', { class: 'sql-text' }, highlight(pretty(q.sql.trim()))) : h('code', { class: 'sql-text one-line' }, highlight(q.sql.length > 400 ? q.sql.slice(0, 400) : q.sql));
                const copy = scrubbed ? null : h('button', {
                    class: 'icon-btn copy', title: 'Copy SQL', 'aria-label': 'Copy SQL', tabindex: '-1',
                    onclick: e => { vscode.postMessage({ type: 'copy', text: q.sql }); const b = e.currentTarget; b.replaceChildren(icon('check')); setTimeout(() => b.replaceChildren(icon('copy')), 1200); },
                }, icon('copy'));
                stmtCell.replaceChildren(h('div', { class: 'stmt' }, scrubbed ? h('span', { class: 'twisty-btn spacer-btn' }) : twisty, text, copy));
                const shown = isOpen ? q.callers : q.callers.slice(0, 2);
                callersCell.replaceChildren(h('ul', { class: 'callers' }, shown.map(c => h('li', {}, method(c.m, { tabindex: '-1' }), h('span', { class: 'times num', text: `×${num(c.n)}` }))),
                    !isOpen && q.callers.length > 2 ? h('li', {}, h('button', { class: 'link', tabindex: '-1', onclick: () => flip(), text: `+${q.callers.length - 2} more` })) : null));
            };
            const flip = () => { if (scrubbed) return; if (S.sqlOpen.has(q)) S.sqlOpen.delete(q); else S.sqlOpen.add(q); fill(); };
            fill();
            tr.onEnter = flip;
            stmtCell.addEventListener('dblclick', flip);
            return tr;
        }, 'SQL statements')));
    }

    // ---------- pauses ----------
    function requestAt(g) {
        const r = S.report;
        return r.requests.findIndex(q => q.thread === g.thread && g.startMs >= q.startMs - 1 && g.startMs <= q.startMs + q.ms + 1);
    }
    function gapsView() {
        const r = S.report;
        const rows = r.gaps.filter(g => !S.filter || (g.method && matches(g.method)) || matches(g.thread ? `T${g.thread}` : 'whole process'));
        const max = rows.reduce((a, g) => Math.max(a, g.ms), 0);
        const total = rows.reduce((a, g) => a + g.ms, 0);
        const head = viewHead('Pauses', `${num(rows.length)} periods of 500 ms or more where nothing was logged · ${dur(total)} in total`);
        const note = h('p', { class: 'note' }, icon('info'), h('span', { text: 'A pause on a thread means the running request logged nothing: it waited (locks, another session, the client) or the runtime was busy (JIT compilation). A pause of the whole process usually means garbage collection or a busy machine.' }));
        if (!rows.length) return h('div', { class: 'view' }, head, empty('debug-pause', 'No pauses', S.filter ? 'Nothing matches the filter.' : 'Nothing stopped logging for 500 ms or more.'));
        return h('div', { class: 'view' }, head, note, h('div', { class: 'scroll' }, table('gaps', [
            ['startMs', 'At', 'num'], ['ms', 'Duration', 'num'], [null, 'Where', 'c-where'], [null, 'Last method', 'c-method'], [null, '', 'c-action'],
        ], sortRows(rows, 'gaps'), g => {
            const ri = g.thread ? requestAt(g) : -1;
            const show = () => { setBatch(S.showBatch || (ri >= 0 && r.requests[ri].batch), true); setTab('requests'); select(ri, { scroll: true }); };
            const tr = h('tr', {},
                h('td', { class: 'num', text: at(g.startMs) }),
                cellBar(g.ms, max, 'pause'),
                h('td', { class: 'c-where' }, g.thread ? h('span', { text: `Thread ${g.thread}` }) : h('span', { class: 'whole' }, icon('warning'), h('span', { text: 'Whole process' }))),
                h('td', { class: 'c-method' }, g.method ? method(g.method, { tabindex: '-1' }) : h('span', { class: 'dim', text: '—' })),
                h('td', { class: 'c-action' }, ri >= 0 ? h('button', { class: 'link', tabindex: '-1', onclick: show, text: 'Show request' }) : null));
            tr.onEnter = ri >= 0 ? show : g.method ? () => open(g.method) : null;
            return tr;
        }, 'Pauses')));
    }

    // ---------- global actions ----------
    function open(m) {
        if (m && !m.startsWith('(')) vscode.postMessage({ type: 'open', method: m });
    }
    function setBatch(v, quiet) {
        if (S.showBatch === v) return;
        S.showBatch = v;
        persist();
        renderTabs();
        scheduleTimeline();
        if (!quiet || S.tab === 'requests') renderPanel();
    }
    document.addEventListener('click', e => {
        const a = e.target.closest('a.method[data-m]');
        if (!a) return;
        e.preventDefault();
        open(a.dataset.m);
    });
    $('timeline').addEventListener('click', e => {
        const rect = e.target.closest('rect.req');
        if (!rect) return;
        setTab('requests');
        select(Number(rect.getAttribute('data-i')), { scroll: true });
    });
    $('tlToggle').addEventListener('click', () => { S.timelineOpen = !S.timelineOpen; persist(); renderTimeline(); });
    $('tlBatch').addEventListener('change', e => setBatch(e.target.checked));
    $('tabs').addEventListener('keydown', e => {
        const ids = TABS.map(t => t[0]);
        let k = ids.indexOf(S.tab);
        if (e.key === 'ArrowRight') k = (k + 1) % ids.length;
        else if (e.key === 'ArrowLeft') k = (k + ids.length - 1) % ids.length;
        else if (e.key === 'Home') k = 0;
        else if (e.key === 'End') k = ids.length - 1;
        else return;
        e.preventDefault();
        setTab(ids[k], true);
    });
    let filterTimer = 0;
    const filter = $('filter');
    filter.addEventListener('input', () => {
        clearTimeout(filterTimer);
        filterTimer = setTimeout(() => {
            S.filter = filter.value.trim().toLowerCase();
            $('filterClear').hidden = !filter.value;
            if (S.report) { renderPanel(); scheduleTimeline(); }
        }, 120);
    });
    filter.addEventListener('keydown', e => { if (e.key === 'Escape' && filter.value) { e.preventDefault(); filter.value = ''; filter.dispatchEvent(new Event('input')); } });
    $('filterClear').addEventListener('click', () => { filter.value = ''; filter.dispatchEvent(new Event('input')); filter.focus(); });
    document.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') { e.preventDefault(); filter.focus(); filter.select(); }
    });
    if (window.ResizeObserver) new ResizeObserver(() => { if (S.report) scheduleTimeline(); }).observe($('timeline'));

    window.addEventListener('message', e => {
        if (!e.data || e.data.type !== 'report') return;
        S.report = e.data.report;
        waits.clear();
        for (const m of S.report.methods) if (m.wait) waits.add(m.m);
        for (const q of S.report.requests) for (const t of q.topExclusive || []) if (t.wait) waits.add(t.m);
        S.sel = -1;
        document.body.classList.remove('loading');
        renderHeader();
        renderTabs();
        renderPanel();
        renderTimeline();
    });
    vscode.postMessage({ type: 'ready' });
})();
