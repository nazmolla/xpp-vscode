/* F&O Studio report designer: lays out an SSRS precision design (RDL). The RDL is edited as an XML DOM, so every element
   the designer doesn't touch is kept as it was; positions keep the unit they were written in. */
(function () {
    'use strict';
    const vscode = acquireVsCodeApi();
    const RD = 'http://schemas.microsoft.com/SQLServer/reporting/reportdesigner';
    const ITEM_TYPES = ['Textbox', 'Rectangle', 'Line', 'Image', 'Tablix', 'Subreport', 'Chart', 'GaugePanel', 'Map', 'CustomReportItem'];
    const EDITABLE_TYPES = ['Textbox', 'Rectangle', 'Line', 'Image', 'Tablix'];
    const PAPERS = { Letter: [215.9, 279.4], Legal: [215.9, 355.6], A4: [210, 297], A5: [148, 210], Executive: [184.15, 266.7] };
    const BUILT_INS = ['PageNumber', 'TotalPages', 'OverallPageNumber', 'OverallTotalPages', 'ExecutionTime', 'ReportName', 'RenderFormat.Name'];
    const app = document.getElementById('app');
    const saved = vscode.getState() || {};
    const bodyState = JSON.parse(document.body.dataset.xppState || '{}');

    const S = {
        info: null, design: null, doc: null, ns: '', header: '', unit: 'in', readOnly: false,
        zoom: saved.zoom || 1, grid: saved.grid || { size: 1, snap: true, show: true },
        selected: [], undo: [], redo: [], savedXml: '', mode: 'design', labels: {}, fonts: [], images: {},
        scan: null, preview: null, previewSettings: saved.previewSettings || { language: 'en-US', company: 'USMF', sampleRows: 1, pages: 3, dpi: 110, data: {} },
        serverWarnings: [], measure: null, addMode: null,
    };
    const itemOf = new WeakMap();        // div -> XML element
    const divOf = new WeakMap();         // XML element -> div
    const originOf = new WeakMap();      // ReportItems XML element -> absolute mm origin {x, y}
    let surface, sheet, props, statusBar, warningsBox;

    function persist() {
        vscode.setState({ xpp: bodyState, zoom: S.zoom, grid: S.grid, previewSettings: S.previewSettings, scans: saved.scans || {} });
    }

    // ── XML helpers ──
    const kids = (el, name) => el ? Array.from(el.children).filter(c => c.localName === name) : [];
    const kid = (el, name) => el ? Array.from(el.children).find(c => c.localName === name) : undefined;
    const txt = (el, name) => { const c = kid(el, name); return c ? c.textContent : null; };
    function create(name, value) {
        const e = S.doc.createElementNS(S.ns, name);
        if (value !== undefined && value !== null) e.textContent = value;
        return e;
    }
    function ensure(el, name) { return kid(el, name) || el.appendChild(create(name)); }
    function setTxt(el, name, value) {
        let c = kid(el, name);
        if (value === null || value === undefined || value === '') { if (c) c.remove(); return; }
        if (!c) { c = create(name); el.appendChild(c); }
        c.textContent = String(value);
    }
    function build(name, children, attrs) {
        const e = create(name);
        Object.entries(attrs || {}).forEach(([k, v]) => e.setAttribute(k, v));
        (children || []).forEach(c => e.appendChild(typeof c === 'string' ? S.doc.createTextNode(c) : c));
        return e;
    }
    function rd(name, value) { const e = S.doc.createElementNS(RD, 'rd:' + name); e.textContent = value; return e; }

    // ── sizes ──
    const UNIT_MM = { in: 25.4, cm: 10, mm: 1, pt: 25.4 / 72, pc: 25.4 / 6 };
    function toMm(size) {
        if (!size) return 0;
        const m = /^\s*(-?\d+(?:\.\d+)?)\s*(in|cm|mm|pt|pc)?\s*$/i.exec(size);
        return m ? parseFloat(m[1]) * UNIT_MM[(m[2] || 'mm').toLowerCase()] : 0;
    }
    function unitOf(size) { const m = /(in|cm|mm|pt|pc)\s*$/i.exec(size || ''); return m ? m[1].toLowerCase() : S.unit; }
    function fromMm(mm, unit) {
        const u = unit || S.unit;
        const digits = u === 'in' ? 5 : u === 'cm' ? 4 : u === 'pt' ? 2 : 3;
        return (+(mm / UNIT_MM[u]).toFixed(digits)) + u;
    }
    function setSize(el, name, mm) { setTxt(el, name, fromMm(Math.max(0, mm), unitOf(txt(el, name)))); }
    const pxPerMm = () => S.zoom * 96 / 25.4;
    const px = mm => mm * pxPerMm();
    const r2 = v => Math.round(v * 100) / 100;
    function snap(mm) { return S.grid.snap && S.grid.size > 0 ? Math.round(mm / S.grid.size) * S.grid.size : mm; }

    // ── document structure ──
    const section = () => kid(kid(S.doc.documentElement, 'ReportSections'), 'ReportSection') || S.doc.documentElement;
    const pageEl = () => kid(section(), 'Page') || S.doc.documentElement;
    const bodyEl = () => kid(section(), 'Body');
    function page() {
        const p = pageEl(), root = S.doc.documentElement, s = section();
        const v = (name) => toMm(txt(p, name) || txt(root, name));
        const header = kid(p, 'PageHeader'), footer = kid(p, 'PageFooter');
        return {
            width: v('PageWidth') || 215.9, height: v('PageHeight') || 279.4,
            left: v('LeftMargin'), right: v('RightMargin'), top: v('TopMargin'), bottom: v('BottomMargin'),
            bodyWidth: toMm(txt(s, 'Width') || txt(root, 'Width')), bodyHeight: toMm(txt(bodyEl(), 'Height')),
            header, footer, headerHeight: header ? toMm(txt(header, 'Height')) : 0, footerHeight: footer ? toMm(txt(footer, 'Height')) : 0,
        };
    }
    function allNames() {
        const names = new Set();
        S.doc.querySelectorAll('*').forEach(e => { const n = e.getAttribute && e.getAttribute('Name'); if (n) names.add(n.toLowerCase()); });
        return names;
    }
    function uniqueName(base) {
        const names = allNames();
        for (let i = 1; ; i++) if (!names.has((base + i).toLowerCase())) return base + i;
    }
    const isItem = el => el && ITEM_TYPES.includes(el.localName);
    const inTablix = el => { for (let p = el.parentElement; p; p = p.parentElement) { if (p.localName === 'CellContents') return true; if (p.localName === 'ReportItems') return false; } return false; };
    const tablixOf = el => { for (let p = el.parentElement; p; p = p.parentElement) if (p.localName === 'Tablix') return p; return null; };
    // The element that holds an item's ReportItems: Body, PageHeader, PageFooter or a Rectangle.
    const containerOf = el => el.parentElement && el.parentElement.localName === 'ReportItems' ? el.parentElement.parentElement : null;

    // ── serialization, undo, dirty ──
    function xml() { return S.header + new XMLSerializer().serializeToString(S.doc.documentElement); }
    let dirtyTimer;
    function changed() {
        clearTimeout(dirtyTimer);
        dirtyTimer = setTimeout(() => { const x = xml(); vscode.postMessage({ type: 'dirty', dirty: x !== S.savedXml, rdl: x }); }, 300);
    }
    function edit(fn) {
        if (S.readOnly) return;
        S.undo.push(xml());
        if (S.undo.length > 200) S.undo.shift();
        S.redo = [];
        const paths = S.selected.map(pathOf);
        fn();
        render(paths);
        changed();
    }
    function load(text, keepSelection) {
        const paths = keepSelection ? S.selected.map(pathOf) : [];
        S.doc = new DOMParser().parseFromString(text, 'application/xml');
        render(paths);
    }
    function undo() { if (!S.undo.length) return; S.redo.push(xml()); load(S.undo.pop(), true); changed(); }
    function redo() { if (!S.redo.length) return; S.undo.push(xml()); load(S.redo.pop(), true); changed(); }
    // An element's place in the document, so selections survive re-parsing.
    function pathOf(el) {
        const path = [];
        for (let e = el; e && e.parentElement; e = e.parentElement) path.unshift(Array.prototype.indexOf.call(e.parentElement.children, e));
        return path;
    }
    function byPath(path) {
        let e = S.doc.documentElement;
        for (const i of path) { e = e && e.children[i]; }
        return e;
    }

    // ── text shown for a textbox ──
    function shown(value) {
        if (value === null || value === undefined) return '';
        if (!value.startsWith('=')) return value;
        let m;
        if ((m = /^=\s*(?:First\s*\(|Sum\s*\(|Last\s*\()?\s*Fields!(\w+)\.Value/i.exec(value))) return '«' + m[1] + '»';
        if ((m = /^=\s*(ucase|lcase)?\s*\(?\s*Labels!(@[A-Za-z]\w*(?::\w+)?)\s*\)?\s*$/i.exec(value))) {
            const t = S.labels[m[2]] || m[2];
            return m[1] && m[1].toLowerCase() === 'ucase' ? t.toUpperCase() : t;
        }
        if ((m = /^=\s*Parameters!(\w+)\.(Value|Label)\s*$/i.exec(value))) return '[@' + m[1] + ']';
        if ((m = /^=\s*Globals!(\w+)\s*$/i.exec(value))) return '[&' + m[1] + ']';
        if ((m = /^=\s*"((?:[^"]|"")*)"\s*$/.exec(value))) return m[1].replace(/""/g, '"');
        return '«Expr»';
    }
    function textboxText(tb) {
        return kids(kid(tb, 'Paragraphs'), 'Paragraph').map(p => kids(kid(p, 'TextRuns'), 'TextRun').map(r => shown(txt(r, 'Value'))).join('')).join('\n');
    }
    function firstRun(tb) { return kid(kid(kid(kid(tb, 'Paragraphs'), 'Paragraph'), 'TextRuns'), 'TextRun'); }
    function firstParagraph(tb) { return kid(kid(tb, 'Paragraphs'), 'Paragraph'); }
    const literal = v => v && !v.startsWith('=') ? v : null;

    // ── styles ──
    function border(style, side) {
        const b = kid(style, side) || kid(style, 'Border');
        const s = literal(txt(b, 'Style')) || (side === 'Border' ? 'None' : literal(txt(kid(style, 'Border'), 'Style')) || 'None');
        const w = literal(txt(b, 'Width')) || literal(txt(kid(style, 'Border'), 'Width')) || '1pt';
        const c = literal(txt(b, 'Color')) || literal(txt(kid(style, 'Border'), 'Color')) || 'Black';
        if (s === 'None') return 'none';
        return `${Math.max(1, px(toMm(w)))}px ${s.toLowerCase() === 'double' ? 'double' : s.toLowerCase() === 'dashed' ? 'dashed' : s.toLowerCase() === 'dotted' ? 'dotted' : 'solid'} ${color(c)}`;
    }
    function color(c) { return !c || c === 'Transparent' ? 'transparent' : c.startsWith('#') ? c : c.toLowerCase(); }
    function applyBox(div, style) {
        if (!style) return;
        div.style.borderTop = border(style, 'TopBorder');
        div.style.borderBottom = border(style, 'BottomBorder');
        div.style.borderLeft = border(style, 'LeftBorder');
        div.style.borderRight = border(style, 'RightBorder');
        const bg = literal(txt(style, 'BackgroundColor'));
        if (bg) div.style.backgroundColor = color(bg);
    }
    function applyText(div, tb) {
        const style = kid(tb, 'Style');
        const run = kid(firstRun(tb), 'Style');
        const para = kid(firstParagraph(tb), 'Style');
        const get = n => literal(txt(run, n)) || literal(txt(style, n));
        div.style.fontFamily = `"${get('FontFamily') || 'Arial'}", sans-serif`;
        div.style.fontSize = px(toMm(get('FontSize') || '10pt')) + 'px';
        div.style.fontWeight = /bold|semibold|heavy|extrabold/i.test(get('FontWeight') || '') ? '700' : '400';
        div.style.fontStyle = /italic/i.test(get('FontStyle') || '') ? 'italic' : 'normal';
        div.style.textDecoration = /underline/i.test(get('TextDecoration') || '') ? 'underline' : /linethrough/i.test(get('TextDecoration') || '') ? 'line-through' : 'none';
        div.style.color = color(get('Color') || 'Black');
        const align = (literal(txt(para, 'TextAlign')) || literal(txt(style, 'TextAlign')) || 'General').toLowerCase();
        div.style.textAlign = align === 'general' ? 'left' : align;
        const valign = (literal(txt(style, 'VerticalAlign')) || 'Top').toLowerCase();
        div.style.justifyContent = valign === 'middle' ? 'center' : valign === 'bottom' ? 'flex-end' : 'flex-start';
        ['Left', 'Right', 'Top', 'Bottom'].forEach(s => { div.style['padding' + s] = px(toMm(literal(txt(style, 'Padding' + s)) || '0pt')) + 'px'; });
    }

    // ── rendering the design ──
    function render(selectPaths) {
        if (!S.doc) return;
        const scroll = surface ? { x: surface.scrollLeft, y: surface.scrollTop } : null;
        app.textContent = '';
        app.appendChild(toolbar());
        const main = el('div', 'main');
        main.appendChild(dataPane());
        const center = el('div', 'center');
        if (S.mode === 'preview') center.appendChild(previewView());
        else center.appendChild(designView());
        main.appendChild(center);
        props = el('div', 'props');
        props.setAttribute('role', 'region');
        props.setAttribute('aria-label', 'Properties');
        main.appendChild(props);
        app.appendChild(main);
        statusBar = el('div', 'status');
        app.appendChild(statusBar);
        S.selected = (selectPaths || []).map(byPath).filter(e => e && isItem(e));
        if (S.mode === 'design') {
            drawSelection();
            if (scroll) { surface.scrollLeft = scroll.x; surface.scrollTop = scroll.y; }
        }
        renderProps();
        status();
    }

    function el(tag, cls, text) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text !== undefined) e.textContent = text;
        return e;
    }
    function button(icon, title, onClick, opts) {
        const b = el('button', 'tb' + (opts && opts.text ? ' tb-text' : ''));
        b.title = title;
        b.setAttribute('aria-label', title);
        if (icon) b.appendChild(el('span', 'codicon codicon-' + icon));
        if (opts && opts.text) b.appendChild(el('span', '', opts.text));
        if (opts && opts.pressed !== undefined) b.setAttribute('aria-pressed', String(!!opts.pressed));
        if (opts && opts.disabled) b.disabled = true;
        b.addEventListener('click', e => { e.preventDefault(); onClick(e); });
        return b;
    }

    function toolbar() {
        const bar = el('div', 'toolbar');
        bar.setAttribute('role', 'toolbar');
        const title = el('div', 'title');
        title.appendChild(el('span', 'codicon codicon-graph'));
        title.appendChild(el('strong', '', S.info.name));
        const designs = S.info.designs;
        const select = el('select', 'design-select');
        select.setAttribute('aria-label', 'Design');
        designs.forEach(d => {
            const o = el('option', '', d.name + (d.kind === 'precision' ? '' : ' (auto design: create a precision design…)'));
            o.value = d.name;
            o.dataset.kind = d.kind;
            o.disabled = d.kind !== 'precision' && S.readOnly;
            o.selected = S.design && d.name.toLowerCase() === S.designName.toLowerCase();
            select.appendChild(o);
        });
        select.addEventListener('change', () => {
            const chosen = select.selectedOptions[0];
            // The extension switches (or creates the precision design); until then the current design stays selected.
            vscode.postMessage(chosen && chosen.dataset.kind !== 'precision' ? { type: 'convertDesign', design: select.value } : { type: 'openDesign', design: select.value });
            select.value = S.designName;
        });
        title.appendChild(select);
        title.appendChild(el('span', 'model', S.info.model + (S.readOnly ? ' · read-only' : '')));
        bar.appendChild(title);

        const group = () => { const g = el('div', 'group'); bar.appendChild(g); return g; };
        let g = group();
        g.appendChild(button('save', 'Save (Ctrl+S)', save, { disabled: S.readOnly }));
        g.appendChild(button('discard', 'Undo (Ctrl+Z)', undo, { disabled: !S.undo.length }));
        g.appendChild(button('redo', 'Redo (Ctrl+Y)', redo, { disabled: !S.redo.length }));
        if (S.mode === 'design') {
            g = group();
            [['symbol-text', 'Textbox'], ['primitive-square', 'Rectangle'], ['dash', 'Line'], ['file-media', 'Image']].forEach(([icon, type]) =>
                g.appendChild(button(icon, `Add ${type}: click on the page`, () => { S.addMode = type; status(`Click on the page to place the ${type.toLowerCase()}. Escape cancels.`); }, { pressed: S.addMode === type, disabled: S.readOnly })));
            g = group();
            const multi = S.selected.filter(e => !inTablix(e)).length > 1;
            [['align-left', 'Align lefts', 'left'], ['align-center', 'Align centers', 'center'], ['align-right', 'Align rights', 'right'],
             ['arrow-up', 'Align tops', 'top'], ['symbol-constant', 'Align middles', 'middle'], ['arrow-down', 'Align bottoms', 'bottom']].forEach(([icon, label, how]) =>
                g.appendChild(button(icon, label + ' (to the first selected item)', () => align(how), { disabled: !multi })));
            g.appendChild(button('arrow-both', 'Make same width', () => sameSize(true, false), { disabled: !multi }));
            g.appendChild(button('fold', 'Make same height', () => sameSize(false, true), { disabled: !multi }));
            g.appendChild(button('split-horizontal', 'Distribute horizontally', () => distribute(true), { disabled: S.selected.length < 3 }));
            g.appendChild(button('split-vertical', 'Distribute vertically', () => distribute(false), { disabled: S.selected.length < 3 }));
            g.appendChild(button('layers-active', 'Bring to front', () => zOrder(true), { disabled: !S.selected.length }));
            g.appendChild(button('layers', 'Send to back', () => zOrder(false), { disabled: !S.selected.length }));
            g = group();
            g.appendChild(button('zoom-out', 'Zoom out', () => setZoom(S.zoom / 1.25)));
            g.appendChild(el('span', 'zoom', Math.round(S.zoom * 100) + '%'));
            g.appendChild(button('zoom-in', 'Zoom in', () => setZoom(S.zoom * 1.25)));
            g.appendChild(button('screen-normal', 'Actual size (100%)', () => setZoom(1)));
            g = group();
            g.appendChild(button('symbol-numeric', 'Show grid', () => { S.grid.show = !S.grid.show; persist(); render(S.selected.map(pathOf)); }, { pressed: S.grid.show }));
            g.appendChild(button('magnet', 'Snap to grid', () => { S.grid.snap = !S.grid.snap; persist(); render(S.selected.map(pathOf)); }, { pressed: S.grid.snap }));
            const gs = el('select', 'grid-size');
            gs.title = 'Grid size';
            gs.setAttribute('aria-label', 'Grid size');
            [['0.5', '0.5 mm'], ['1', '1 mm'], ['2', '2 mm'], ['2.54', '0.1 in'], ['3.175', '1/8 in'], ['5', '5 mm']].forEach(([v, t]) => {
                const o = el('option', '', t); o.value = v; o.selected = +v === S.grid.size; gs.appendChild(o);
            });
            gs.addEventListener('change', () => { S.grid.size = +gs.value; persist(); render(S.selected.map(pathOf)); });
            g.appendChild(gs);
            g.appendChild(button('device-camera', 'Scan overlay: lay a scanned form under the design (never saved)', scanDialog, { pressed: !!(S.scan && S.scan.show) }));
            g.appendChild(button('ruler', 'Measure: drag on the page (or hold Alt and drag)', () => { S.addMode = 'measure'; status('Drag on the page to measure. Escape cancels.'); }, { pressed: S.addMode === 'measure' }));
        }
        g = group();
        g.appendChild(button('edit', 'Design', () => { S.mode = 'design'; render(S.selected.map(pathOf)); }, { pressed: S.mode === 'design', text: 'Design' }));
        g.appendChild(button('play', 'Preview with Microsoft\'s report engine', () => { S.mode = 'preview'; render([]); if (!S.preview) runPreview(); }, { pressed: S.mode === 'preview', text: 'Preview' }));
        g = group();
        g.appendChild(button('code', 'View the RDL', () => vscode.postMessage({ type: 'viewRdl', rdl: xml() })));
        g.appendChild(button('list-tree', 'Report properties: datasets, parameters (element designer)', () => vscode.postMessage({ type: 'properties' })));
        return bar;
    }

    function setZoom(z) {
        S.zoom = Math.min(8, Math.max(0.25, z));
        persist();
        render(S.selected.map(pathOf));
    }

    function designView() {
        const wrap = el('div', 'design');
        if (S.readOnly) {
            const banner = el('div', 'banner');
            banner.appendChild(el('span', 'codicon codicon-lock'));
            banner.appendChild(el('span', '', `${S.info.name} belongs to ${S.info.model}, which you can't change. To change its layout, duplicate the report into your model (Microsoft's extension pattern), change the copy, and select it in Print management or redirect the controller to it.`));
            banner.appendChild(button('copy', 'Duplicate into my model', () => vscode.postMessage({ type: 'duplicate' }), { text: 'Duplicate into my model…' }));
            wrap.appendChild(banner);
        }
        surface = el('div', 'surface');
        surface.tabIndex = 0;
        surface.setAttribute('role', 'application');
        surface.setAttribute('aria-label', 'Design surface. Arrow keys move the selection, Shift+arrows resize, Ctrl+arrows move by 0.1 mm.');
        const p = page();
        const bodyTop = p.top + p.headerHeight;
        const sheetHeight = Math.max(p.height, bodyTop + p.bodyHeight + p.footerHeight + p.bottom);
        const holder = el('div', 'holder');
        holder.style.width = px(p.width) + 60 + 'px';
        holder.style.height = px(sheetHeight) + 60 + 'px';
        holder.appendChild(rulers(p, sheetHeight));
        sheet = el('div', 'sheet');
        sheet.style.width = px(p.width) + 'px';
        sheet.style.height = px(sheetHeight) + 'px';
        if (S.grid.show) {
            sheet.classList.add('grid');
            const step = px(S.grid.size < 1 ? 1 : S.grid.size);
            const major = px(10);
            sheet.style.backgroundSize = `${major}px ${major}px, ${major}px ${major}px, ${step}px ${step}px, ${step}px ${step}px`;
        }
        if (S.scan && S.scan.show) {
            const img = el('img', 'scan');
            img.src = `data:${S.scan.mimeType};base64,${S.scan.data}`;
            img.alt = '';
            img.style.left = px(S.scan.offsetX || 0) + 'px';
            img.style.top = px(S.scan.offsetY || 0) + 'px';
            img.style.width = px(S.scan.widthMm) + 'px';
            img.style.height = px(S.scan.heightMm) + 'px';
            img.style.opacity = String(S.scan.opacity);
            sheet.appendChild(img);
        }
        // Page beyond the paper (body taller than a page) is marked.
        if (sheetHeight > p.height) {
            const over = el('div', 'overflow-page');
            over.style.top = px(p.height) + 'px';
            over.style.height = px(sheetHeight - p.height) + 'px';
            over.title = 'Beyond the first page';
            sheet.appendChild(over);
        }
        const margin = el('div', 'margins');
        margin.style.left = px(p.left) + 'px';
        margin.style.top = px(p.top) + 'px';
        margin.style.width = px(Math.max(0, p.width - p.left - p.right)) + 'px';
        margin.style.height = px(Math.max(0, p.height - p.top - p.bottom)) + 'px';
        sheet.appendChild(margin);
        if (p.header) sheet.appendChild(sectionDiv('PageHeader', p.header, p.left, p.top, p.bodyWidth, p.headerHeight));
        sheet.appendChild(sectionDiv('Body', bodyEl(), p.left, bodyTop, p.bodyWidth, p.bodyHeight));
        if (p.footer) sheet.appendChild(sectionDiv('PageFooter', p.footer, p.left, p.height - p.bottom - p.footerHeight, p.bodyWidth, p.footerHeight));
        holder.appendChild(sheet);
        surface.appendChild(holder);
        wrap.appendChild(surface);
        warningsBox = el('div', 'warnings');
        warningsBox.setAttribute('role', 'log');
        warningsBox.setAttribute('aria-label', 'Layout warnings');
        wrap.appendChild(warningsBox);
        renderWarnings();
        wireSurface();
        return wrap;
    }

    function rulers(p, sheetHeight) {
        const frag = document.createDocumentFragment();
        const top = el('div', 'ruler ruler-h');
        top.style.width = px(p.width) + 'px';
        const left = el('div', 'ruler ruler-v');
        left.style.height = px(sheetHeight) + 'px';
        const inch = S.unit === 'in';
        const step = inch ? 25.4 / 16 : 1;
        const labelEvery = inch ? 16 : 10;
        const smallEvery = px(step) < 3 ? (inch ? 4 : 5) : 1;
        const tick = (ruler, horizontal, i) => {
            if (i % smallEvery) return;
            const pos = px(i * step);
            const big = i % labelEvery === 0, mid = i % (labelEvery / 2) === 0;
            const t = el('div', 'tick' + (big ? ' big' : mid ? ' mid' : ''));
            if (horizontal) t.style.left = pos + 'px'; else t.style.top = pos + 'px';
            ruler.appendChild(t);
            if (big) {
                const label = el('span', 'tick-label', String(i / labelEvery * (inch ? 1 : 10)));
                if (horizontal) label.style.left = pos + 2 + 'px'; else label.style.top = pos + 1 + 'px';
                ruler.appendChild(label);
            }
        };
        for (let i = 0; i * step <= p.width; i++) tick(top, true, i);
        for (let i = 0; i * step <= sheetHeight; i++) tick(left, false, i);
        top.appendChild(el('span', 'ruler-unit', inch ? 'in' : 'mm'));
        frag.appendChild(top);
        frag.appendChild(left);
        return frag;
    }

    function sectionDiv(kind, sectionEl, x, y, w, h) {
        const div = el('div', 'section section-' + kind);
        div.style.left = px(x) + 'px';
        div.style.top = px(y) + 'px';
        div.style.width = px(w) + 'px';
        div.style.height = px(h) + 'px';
        div.dataset.section = kind;
        div.appendChild(el('span', 'section-label', kind === 'PageHeader' ? 'Page header' : kind === 'PageFooter' ? 'Page footer' : 'Body'));
        originOf.set(sectionEl, { x, y });
        itemOf.set(div, sectionEl);
        const items = kid(sectionEl, 'ReportItems');
        if (items) drawItems(items, div, x, y);
        applyBox(div, kid(sectionEl, 'Style'));
        return div;
    }
    // The ReportItems of a section or rectangle, created only when an item is added (RDL doesn't allow an empty one).
    function ensureItemsHolder(holder) {
        return kid(holder, 'ReportItems') || holder.insertBefore(create('ReportItems'), holder.firstChild);
    }

    function drawItems(items, parent, ox, oy) {
        const list = Array.from(items.children).filter(isItem).sort((a, b) => (+txt(a, 'ZIndex') || 0) - (+txt(b, 'ZIndex') || 0));
        for (const item of list) {
            const x = toMm(txt(item, 'Left')), y = toMm(txt(item, 'Top')), w = toMm(txt(item, 'Width')), h = toMm(txt(item, 'Height'));
            const div = drawItem(item, w, h, ox + x, oy + y);
            div.style.left = px(x) + 'px';
            div.style.top = px(y) + 'px';
            parent.appendChild(div);
        }
    }

    function drawItem(item, w, h, absX, absY) {
        const type = item.localName;
        const div = el('div', 'it it-' + type);
        div.style.width = px(w) + 'px';
        div.style.height = px(h) + 'px';
        div.style.zIndex = String(+txt(item, 'ZIndex') || 0);
        itemOf.set(div, item);
        divOf.set(item, div);
        const hidden = txt(kid(item, 'Visibility'), 'Hidden');
        if (hidden === 'true') div.classList.add('hidden-item');
        else if (hidden && hidden.startsWith('=')) div.classList.add('conditional');
        div.title = `${type} ${item.getAttribute('Name') || ''}`;
        switch (type) {
            case 'Textbox': {
                applyBox(div, kid(item, 'Style'));
                const inner = el('div', 'tb-text-inner');
                applyText(inner, item);
                inner.textContent = textboxText(item);
                div.appendChild(inner);
                if (txt(item, 'CanGrow') === 'true') div.classList.add('can-grow');
                break;
            }
            case 'Rectangle': {
                applyBox(div, kid(item, 'Style'));
                originOf.set(item, { x: absX, y: absY });
                const items = kid(item, 'ReportItems');
                if (items) drawItems(items, div, absX, absY);
                break;
            }
            case 'Line': {
                const style = kid(item, 'Style');
                const svgNs = 'http://www.w3.org/2000/svg';
                const svg = document.createElementNS(svgNs, 'svg');
                svg.setAttribute('class', 'line-svg');
                svg.setAttribute('width', String(Math.max(1, px(w))));
                svg.setAttribute('height', String(Math.max(1, px(h))));
                const line = document.createElementNS(svgNs, 'line');
                line.setAttribute('x1', '0'); line.setAttribute('y1', '0');
                line.setAttribute('x2', String(px(w))); line.setAttribute('y2', String(px(h)));
                const b = kid(style, 'Border');
                line.setAttribute('stroke', color(literal(txt(b, 'Color')) || 'Black'));
                line.setAttribute('stroke-width', String(Math.max(1, px(toMm(literal(txt(b, 'Width')) || '1pt')))));
                if (/dash/i.test(txt(b, 'Style') || '')) line.setAttribute('stroke-dasharray', '6 3');
                if (/dot/i.test(txt(b, 'Style') || '')) line.setAttribute('stroke-dasharray', '2 2');
                svg.appendChild(line);
                div.appendChild(svg);
                break;
            }
            case 'Image': {
                applyBox(div, kid(item, 'Style'));
                const name = txt(item, 'Value');
                const image = txt(item, 'Source') === 'Embedded' && name && !name.startsWith('=') ? imageData(name) : null;
                if (image) {
                    const img = el('img', 'img-' + (txt(item, 'Sizing') || 'AutoSize').toLowerCase());
                    img.src = `data:${image.mimeType};base64,${image.data}`;
                    img.alt = name;
                    div.appendChild(img);
                } else {
                    div.classList.add('placeholder');
                    div.appendChild(el('span', 'codicon codicon-file-media'));
                    div.appendChild(el('span', 'ph-text', name || 'Image'));
                }
                break;
            }
            case 'Tablix':
                drawTablix(item, div, absX, absY);
                break;
            default:
                div.classList.add('placeholder');
                div.appendChild(el('span', 'ph-text', `${type} ${item.getAttribute('Name') || ''} (kept as is)`));
        }
        return div;
    }

    function imageData(name) {
        if (S.images[name]) return S.images[name];
        const e = kids(kid(S.doc.documentElement, 'EmbeddedImages'), 'EmbeddedImage').find(i => i.getAttribute('Name') === name);
        return e ? { mimeType: txt(e, 'MIMEType') || 'image/png', data: txt(e, 'ImageData') } : null;
    }

    function headerDepth(hierarchy) {
        let total = 0, members = kid(hierarchy, 'TablixMembers');
        while (members) {
            const m = kid(members, 'TablixMember');
            if (!m) break;
            total += toMm(txt(kid(m, 'TablixHeader'), 'Size'));
            members = kid(m, 'TablixMembers');
        }
        return total;
    }

    function drawTablix(tablix, div, absX, absY) {
        div.classList.add('tablix');
        const body = kid(tablix, 'TablixBody');
        const widths = kids(kid(body, 'TablixColumns'), 'TablixColumn').map(c => toMm(txt(c, 'Width')));
        const rows = kids(kid(body, 'TablixRows'), 'TablixRow');
        const rowHeader = headerDepth(kid(tablix, 'TablixRowHierarchy'));
        const colHeader = headerDepth(kid(tablix, 'TablixColumnHierarchy'));
        let y = colHeader;
        rows.forEach((row, r) => {
            const height = toMm(txt(row, 'Height'));
            let x = rowHeader, column = 0;
            kids(kid(row, 'TablixCells'), 'TablixCell').forEach(cell => {
                const contents = kid(cell, 'CellContents');
                const span = Math.max(1, +txt(contents, 'ColSpan') || 1);
                const width = widths.slice(column, column + span).reduce((a, b) => a + b, 0);
                const item = contents && Array.from(contents.children).find(isItem);
                if (item) {
                    const cellDiv = drawItem(item, width, height, absX + x, absY + y);
                    cellDiv.classList.add('cell');
                    cellDiv.dataset.row = String(r);
                    cellDiv.dataset.column = String(column);
                    cellDiv.style.left = px(x) + 'px';
                    cellDiv.style.top = px(y) + 'px';
                    if (item.localName === 'Rectangle') originOf.set(item, { x: absX + x, y: absY + y });
                    div.appendChild(cellDiv);
                }
                x += width;
                column += span;
            });
            y += height;
        });
        const grip = el('div', 'tablix-grip');
        grip.title = `Tablix ${tablix.getAttribute('Name')} (dataset ${txt(tablix, 'DataSetName') || '?'}): click to select it`;
        grip.appendChild(el('span', 'codicon codicon-table'));
        itemOf.set(grip, tablix);
        div.appendChild(grip);
    }

    // ── selection ──
    function absBox(item) {
        const holder = containerOf(item);
        const o = holder ? originOf.get(holder) : null;
        if (o) return { x: o.x + toMm(txt(item, 'Left')), y: o.y + toMm(txt(item, 'Top')), w: toMm(txt(item, 'Width')), h: toMm(txt(item, 'Height')) };
        const div = divOf.get(item);
        if (!div || !sheet) return null;
        const a = div.getBoundingClientRect(), s = sheet.getBoundingClientRect();
        return { x: (a.left - s.left) / pxPerMm(), y: (a.top - s.top) / pxPerMm(), w: a.width / pxPerMm(), h: a.height / pxPerMm() };
    }

    function drawSelection() {
        if (!sheet) return;
        sheet.querySelectorAll('.sel, .handle').forEach(n => n.remove());
        S.selected.forEach((item, i) => {
            const box = absBox(item);
            if (!box) return;
            const sel = el('div', 'sel' + (i === 0 ? ' primary' : ''));
            sel.style.left = px(box.x) + 'px';
            sel.style.top = px(box.y) + 'px';
            sel.style.width = px(box.w) + 'px';
            sel.style.height = px(box.h) + 'px';
            sheet.appendChild(sel);
            if (!inTablix(item) && S.selected.length === 1 && !S.readOnly) {
                ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'].forEach(h => {
                    const handle = el('div', 'handle handle-' + h);
                    handle.dataset.handle = h;
                    const hx = h.includes('w') ? box.x : h.includes('e') ? box.x + box.w : box.x + box.w / 2;
                    const hy = h.includes('n') ? box.y : h.includes('s') ? box.y + box.h : box.y + box.h / 2;
                    handle.style.left = px(hx) - 4 + 'px';
                    handle.style.top = px(hy) - 4 + 'px';
                    sheet.appendChild(handle);
                });
            }
        });
    }

    function select(items, add) {
        if (add) {
            items.forEach(i => { const at = S.selected.indexOf(i); if (at >= 0) S.selected.splice(at, 1); else S.selected.push(i); });
        } else S.selected = items;
        drawSelection();
        renderProps();
        refreshToolbar();
        status();
    }
    function refreshToolbar() {
        const old = app.querySelector('.toolbar');
        if (old) old.replaceWith(toolbar());
    }

    // ── mouse ──
    function mmAt(e) {
        const r = sheet.getBoundingClientRect();
        return { x: (e.clientX - r.left) / pxPerMm(), y: (e.clientY - r.top) / pxPerMm() };
    }
    // Where a click or drop lands: the rectangle or section under it.
    function containerAt(target) {
        for (let n = target; n && n !== sheet; n = n.parentElement) {
            const item = itemOf.get(n);
            if (item && ['Body', 'PageHeader', 'PageFooter'].includes(item.localName)) return item;
            if (item && item.localName === 'Rectangle' && !inTablix(item)) return item;
        }
        return bodyEl();
    }

    function wireSurface() {
        surface.addEventListener('mousedown', onMouseDown);
        surface.addEventListener('mousemove', e => { if (sheet) { const p = mmAt(e); status(null, p); } });
        surface.addEventListener('keydown', onKey);
        surface.addEventListener('dragover', e => { if (!S.readOnly) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
        surface.addEventListener('drop', onDrop);
        surface.addEventListener('dblclick', e => {
            const item = itemAt(e.target);
            if (item && item.localName === 'Textbox') { select([item]); const v = props.querySelector('textarea[data-prop="value"]'); if (v) v.focus(); }
        });
    }

    function itemAt(target) {
        for (let n = target; n && n !== sheet; n = n.parentElement) {
            const item = itemOf.get(n);
            if (item && isItem(item)) return item;
        }
        return null;
    }

    function onMouseDown(e) {
        if (e.button !== 0 || !sheet || !sheet.contains(e.target) && e.target !== surface) return;
        surface.focus();
        const start = mmAt(e);
        if (S.addMode === 'measure' || e.altKey) return startMeasure(e, start);
        if (S.addMode && S.addMode !== 'measure') {
            const type = S.addMode;
            S.addMode = null;
            const holder = containerAt(e.target);
            const origin = originOf.get(holder) || { x: 0, y: 0 };
            addItem(type, holder, snap(start.x - origin.x), snap(start.y - origin.y));
            e.preventDefault();
            return;
        }
        const handle = e.target.dataset && e.target.dataset.handle;
        if (handle && S.selected.length === 1) return startDrag(e, start, handle);
        const item = itemAt(e.target);
        if (!item) {
            if (!e.ctrlKey && !e.shiftKey) select([]);
            return startMarquee(e, start);
        }
        if (!S.selected.includes(item) || e.ctrlKey || e.shiftKey) select([item], e.ctrlKey || e.shiftKey);
        if (S.selected.includes(item) && !inTablix(item)) startDrag(e, start, null);
        e.preventDefault();
    }

    function startDrag(e, start, handle) {
        if (S.readOnly) return;
        const movable = S.selected.filter(i => !inTablix(i));
        if (!movable.length) return;
        const before = movable.map(i => ({ i, x: toMm(txt(i, 'Left')), y: toMm(txt(i, 'Top')), w: toMm(txt(i, 'Width')), h: toMm(txt(i, 'Height')) }));
        let moved = false, last = { dx: 0, dy: 0 };
        const move = ev => {
            const p = mmAt(ev);
            const dx = p.x - start.x, dy = p.y - start.y;
            if (!moved && Math.abs(dx) < 0.3 && Math.abs(dy) < 0.3) return;
            moved = true;
            last = { dx, dy };
            before.forEach(b => {
                const r = geometry(b, handle, dx, dy);
                const div = divOf.get(b.i);
                if (div) { div.style.left = px(r.x) + 'px'; div.style.top = px(r.y) + 'px'; div.style.width = px(r.w) + 'px'; div.style.height = px(r.h) + 'px'; }
            });
            status(`${handle ? 'Size' : 'Move'}: Δx ${r2(dx)} mm, Δy ${r2(dy)} mm`, p);
        };
        const up = () => {
            window.removeEventListener('mousemove', move);
            window.removeEventListener('mouseup', up);
            if (!moved) return;
            edit(() => before.forEach(b => {
                const r = geometry(b, handle, last.dx, last.dy);
                setSize(b.i, 'Left', r.x); setSize(b.i, 'Top', r.y);
                if (handle) { setSize(b.i, 'Width', r.w); setSize(b.i, 'Height', r.h); }
            }));
        };
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
    }

    function geometry(b, handle, dx, dy) {
        if (!handle) return { x: Math.max(0, snap(b.x + dx)), y: Math.max(0, snap(b.y + dy)), w: b.w, h: b.h };
        let { x, y, w, h } = b;
        const line = b.i.localName === 'Line';
        const min = line ? 0 : 0.5;
        if (handle.includes('e')) w = Math.max(min, snap(b.x + b.w + dx) - b.x);
        if (handle.includes('s')) h = Math.max(min, snap(b.y + b.h + dy) - b.y);
        if (handle.includes('w')) { const nx = Math.min(snap(b.x + dx), b.x + b.w - min); w = b.x + b.w - nx; x = Math.max(0, nx); }
        if (handle.includes('n')) { const ny = Math.min(snap(b.y + dy), b.y + b.h - min); h = b.y + b.h - ny; y = Math.max(0, ny); }
        return { x, y, w, h };
    }

    function startMarquee(e, start) {
        const box = el('div', 'marquee');
        sheet.appendChild(box);
        const move = ev => {
            const p = mmAt(ev);
            box.style.left = px(Math.min(p.x, start.x)) + 'px'; box.style.top = px(Math.min(p.y, start.y)) + 'px';
            box.style.width = px(Math.abs(p.x - start.x)) + 'px'; box.style.height = px(Math.abs(p.y - start.y)) + 'px';
        };
        const up = ev => {
            window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up);
            box.remove();
            const p = mmAt(ev);
            const r = { x1: Math.min(p.x, start.x), y1: Math.min(p.y, start.y), x2: Math.max(p.x, start.x), y2: Math.max(p.y, start.y) };
            if (r.x2 - r.x1 < 1 && r.y2 - r.y1 < 1) return;
            const hits = Array.from(S.doc.getElementsByTagNameNS(S.ns, '*')).filter(i => isItem(i) && !inTablix(i)).filter(i => {
                const b = absBox(i);
                return b && b.x >= r.x1 && b.y >= r.y1 && b.x + b.w <= r.x2 && b.y + b.h <= r.y2;
            });
            select(hits, e.ctrlKey || e.shiftKey);
        };
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
    }

    function startMeasure(e, start) {
        S.addMode = null;
        const line = el('div', 'measure-line');
        const label = el('div', 'measure-label');
        sheet.appendChild(line); sheet.appendChild(label);
        const move = ev => {
            const p = mmAt(ev);
            const dx = p.x - start.x, dy = p.y - start.y, d = Math.hypot(dx, dy);
            line.style.left = px(start.x) + 'px'; line.style.top = px(start.y) + 'px';
            line.style.width = px(d) + 'px';
            line.style.transform = `rotate(${Math.atan2(dy, dx)}rad)`;
            label.style.left = px(p.x) + 8 + 'px'; label.style.top = px(p.y) + 8 + 'px';
            label.textContent = `${r2(d)} mm (Δx ${r2(dx)}, Δy ${r2(dy)}) · ${r2(d / 25.4)} in`;
        };
        const up = () => {
            window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up);
            status(`Measured: ${label.textContent}`);
            setTimeout(() => { line.remove(); label.remove(); }, 2500);
        };
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
        e.preventDefault();
    }

    // ── keyboard ──
    let clipboard = null;
    function onKey(e) {
        const ctrl = e.ctrlKey || e.metaKey;
        if (e.key === 'Escape') { S.addMode = null; select([]); return; }
        if (ctrl && e.key.toLowerCase() === 'z') { undo(); e.preventDefault(); return; }
        if (ctrl && e.key.toLowerCase() === 'y') { redo(); e.preventDefault(); return; }
        if (ctrl && e.key.toLowerCase() === 'a') { const items = kid(bodyEl(), 'ReportItems'); select(items ? Array.from(items.children).filter(isItem) : []); e.preventDefault(); return; }
        if (ctrl && e.key.toLowerCase() === 'c') { copy(); e.preventDefault(); return; }
        if (ctrl && e.key.toLowerCase() === 'x') { copy(); remove(); e.preventDefault(); return; }
        if (ctrl && e.key.toLowerCase() === 'v') { paste(); e.preventDefault(); return; }
        if (ctrl && e.key.toLowerCase() === 'd') { copy(); paste(); e.preventDefault(); return; }
        if (e.key === 'Delete') { remove(); e.preventDefault(); return; }
        const arrows = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
        if (arrows[e.key] && S.selected.length) {
            const step = ctrl ? 0.1 : S.grid.snap ? S.grid.size : 0.5;
            const [dx, dy] = arrows[e.key].map(v => v * step);
            const movable = S.selected.filter(i => !inTablix(i));
            if (movable.length) edit(() => movable.forEach(i => {
                if (e.shiftKey) {
                    setSize(i, 'Width', Math.max(i.localName === 'Line' ? 0 : 0.5, toMm(txt(i, 'Width')) + dx));
                    setSize(i, 'Height', Math.max(i.localName === 'Line' ? 0 : 0.5, toMm(txt(i, 'Height')) + dy));
                } else {
                    setSize(i, 'Left', Math.max(0, toMm(txt(i, 'Left')) + dx));
                    setSize(i, 'Top', Math.max(0, toMm(txt(i, 'Top')) + dy));
                }
            }));
            e.preventDefault();
        }
    }
    document.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
    });

    function copy() {
        const items = S.selected.filter(i => !inTablix(i));
        if (items.length) clipboard = items.map(i => new XMLSerializer().serializeToString(i));
    }
    function paste() {
        if (!clipboard || S.readOnly) return;
        const holder = S.selected.length === 1 && S.selected[0].localName === 'Rectangle' && !inTablix(S.selected[0]) ? S.selected[0]
            : (S.selected[0] && containerOf(S.selected[0])) || bodyEl();
        const pasted = [];
        edit(() => clipboard.forEach(text => {
            const node = S.doc.importNode(new DOMParser().parseFromString(text, 'application/xml').documentElement, true);
            renameDeep(node);
            setSize(node, 'Left', toMm(txt(node, 'Left')) + 2);
            setSize(node, 'Top', toMm(txt(node, 'Top')) + 2);
            ensureItemsHolder(holder).appendChild(node);
            pasted.push(node);
        }));
        select(pasted.map(pathOf).map(byPath));
    }
    function renameDeep(node) {
        [node, ...node.querySelectorAll('*')].forEach(n => {
            if (n.getAttribute && n.getAttribute('Name') && isItem(n)) {
                const name = uniqueName(n.localName);
                n.setAttribute('Name', name);
                const dn = Array.from(n.children).find(c => c.localName === 'DefaultName' && c.namespaceURI === RD);
                if (dn) dn.textContent = name;
            }
        });
    }
    function remove() {
        if (!S.selected.length || S.readOnly) return;
        edit(() => S.selected.forEach(i => {
            if (inTablix(i)) {
                if (i.localName === 'Textbox') setValue(i, '');
                return;
            }
            const items = i.parentElement;
            i.remove();
            if (items && items.localName === 'ReportItems' && !Array.from(items.children).some(isItem)) items.remove();
        }));
        select([]);
    }

    // ── adding ──
    function newTextbox(name, value) {
        return build('Textbox', [
            build('CanGrow', ['false']), build('KeepTogether', ['true']),
            build('Paragraphs', [build('Paragraph', [build('TextRuns', [build('TextRun', [build('Value', [value || '']),
                build('Style', [build('FontFamily', ['Segoe UI']), build('FontSize', ['9pt'])])])]), build('Style')])]),
            rd('DefaultName', name),
            build('Style', [build('Border', [build('Style', ['None'])]), build('PaddingLeft', ['0pt']), build('PaddingRight', ['0pt']), build('PaddingTop', ['0pt']), build('PaddingBottom', ['0pt'])]),
        ], { Name: name });
    }
    function addItem(type, holder, x, y, value, size) {
        const name = uniqueName(type);
        const dims = size || { Textbox: [40, 5], Rectangle: [50, 30], Line: [50, 0], Image: [30, 20] }[type];
        let node;
        if (type === 'Textbox') node = newTextbox(name, value);
        else if (type === 'Rectangle') node = build('Rectangle', [build('KeepTogether', ['true']), build('Style', [build('Border', [build('Style', ['Solid'])])])], { Name: name });
        else if (type === 'Line') node = build('Line', [build('Style', [build('Border', [build('Style', ['Solid']), build('Width', ['1pt'])])])], { Name: name });
        else if (type === 'Image') {
            pendingImage = { holder, x, y, name };
            vscode.postMessage({ type: 'pickImage' });
            return;
        }
        const existing = kid(holder, 'ReportItems');
        const z = Math.max(0, ...(existing ? Array.from(existing.children).filter(isItem).map(i => +txt(i, 'ZIndex') || 0) : [0])) + 1;
        edit(() => {
            setTxt(node, 'Top', fromMm(y)); setTxt(node, 'Left', fromMm(x));
            setTxt(node, 'Width', fromMm(dims[0])); setTxt(node, 'Height', fromMm(dims[1]));
            setTxt(node, 'ZIndex', z);
            ensureItemsHolder(holder).appendChild(node);
        });
        select([byPath(pathOf(node))]);
    }
    let pendingImage = null;
    function addImage(image) {
        const target = pendingImage;
        pendingImage = null;
        const name = image.name.replace(/[^A-Za-z0-9_]/g, '_').replace(/^(\d)/, '_$1');
        edit(() => {
            const root = S.doc.documentElement;
            const store = ensure(root, 'EmbeddedImages');
            let entry = kids(store, 'EmbeddedImage').find(i => i.getAttribute('Name') === name);
            if (!entry) { entry = build('EmbeddedImage', [], { Name: name }); store.appendChild(entry); }
            setTxt(entry, 'MIMEType', image.mimeType);
            setTxt(entry, 'ImageData', image.data);
            if (target) {
                const node = build('Image', [build('Source', ['Embedded']), build('Value', [name]), build('Sizing', ['FitProportional']),
                    build('Style', [build('Border', [build('Style', ['None'])])])], { Name: target.name });
                setTxt(node, 'Top', fromMm(target.y)); setTxt(node, 'Left', fromMm(target.x));
                setTxt(node, 'Width', fromMm(30)); setTxt(node, 'Height', fromMm(20));
                ensureItemsHolder(target.holder).appendChild(node);
            } else if (S.selected.length === 1 && S.selected[0].localName === 'Image') {
                setTxt(S.selected[0], 'Source', 'Embedded');
                setTxt(S.selected[0], 'Value', name);
            }
        });
    }

    function onDrop(e) {
        e.preventDefault();
        if (S.readOnly) return;
        let data;
        try { data = JSON.parse(e.dataTransfer.getData('text/plain')); } catch { return; }
        const item = itemAt(e.target);
        if (item && item.localName === 'Textbox' && inTablix(item)) {
            const tablix = tablixOf(item);
            const sameSet = !data.dataSet || !tablix || txt(tablix, 'DataSetName') === data.dataSet;
            edit(() => setValue(item, data.kind === 'field' && sameSet ? `=Fields!${data.name}.Value` : expression(data)));
            return;
        }
        const holder = containerAt(e.target);
        const origin = originOf.get(holder) || { x: 0, y: 0 };
        const p = mmAt(e);
        addItem('Textbox', holder, snap(p.x - origin.x), snap(p.y - origin.y), expression(data));
    }
    function expression(data) {
        switch (data.kind) {
            case 'field': return `=First(Fields!${data.name}.Value, "${data.dataSet}")`;
            case 'parameter': return `=Parameters!${data.name}.Value`;
            case 'global': return `=Globals!${data.name}`;
            case 'label': return `=Labels!${data.name}`;
            default: return '';
        }
    }

    // ── values and properties ──
    function setValue(tb, value) {
        const paragraphs = ensure(tb, 'Paragraphs');
        let paragraph = kid(paragraphs, 'Paragraph');
        if (!paragraph) { paragraph = build('Paragraph', [build('TextRuns'), build('Style')]); paragraphs.appendChild(paragraph); }
        const runs = ensure(paragraph, 'TextRuns');
        let run = kid(runs, 'TextRun');
        if (!run) { run = build('TextRun', [build('Value'), build('Style')]); runs.appendChild(run); }
        ensure(run, 'Value').textContent = value;
    }
    function styleOf(item, forText) {
        if (item.localName === 'Textbox' && forText) {
            let run = firstRun(item);
            if (!run) { setValue(item, ''); run = firstRun(item); }
            return ensure(run, 'Style');
        }
        return ensure(item, 'Style');
    }

    // ── align and arrange ──
    function align(how) {
        const items = S.selected.filter(i => !inTablix(i));
        const ref = absBox(items[0]);
        edit(() => items.slice(1).forEach(i => {
            const b = absBox(i);
            if (!b) return;
            const dx = how === 'left' ? ref.x - b.x : how === 'right' ? ref.x + ref.w - b.x - b.w : how === 'center' ? ref.x + ref.w / 2 - b.x - b.w / 2 : 0;
            const dy = how === 'top' ? ref.y - b.y : how === 'bottom' ? ref.y + ref.h - b.y - b.h : how === 'middle' ? ref.y + ref.h / 2 - b.y - b.h / 2 : 0;
            setSize(i, 'Left', toMm(txt(i, 'Left')) + dx);
            setSize(i, 'Top', toMm(txt(i, 'Top')) + dy);
        }));
    }
    function sameSize(width, height) {
        const items = S.selected.filter(i => !inTablix(i));
        const ref = items[0];
        edit(() => items.slice(1).forEach(i => {
            if (width) setSize(i, 'Width', toMm(txt(ref, 'Width')));
            if (height) setSize(i, 'Height', toMm(txt(ref, 'Height')));
        }));
    }
    function distribute(horizontal) {
        const items = S.selected.filter(i => !inTablix(i)).map(i => ({ i, b: absBox(i) })).filter(x => x.b)
            .sort((a, b) => horizontal ? a.b.x - b.b.x : a.b.y - b.b.y);
        if (items.length < 3) return;
        const first = items[0].b, last = items[items.length - 1].b;
        const total = items.reduce((s, x) => s + (horizontal ? x.b.w : x.b.h), 0);
        const gap = ((horizontal ? last.x + last.w - first.x : last.y + last.h - first.y) - total) / (items.length - 1);
        let pos = horizontal ? first.x : first.y;
        edit(() => items.forEach(x => {
            const delta = pos - (horizontal ? x.b.x : x.b.y);
            setSize(x.i, horizontal ? 'Left' : 'Top', toMm(txt(x.i, horizontal ? 'Left' : 'Top')) + delta);
            pos += (horizontal ? x.b.w : x.b.h) + gap;
        }));
    }
    function zOrder(front) {
        edit(() => S.selected.filter(i => !inTablix(i)).forEach(i => {
            const siblings = Array.from(i.parentElement.children).filter(isItem).map(s => +txt(s, 'ZIndex') || 0);
            setTxt(i, 'ZIndex', front ? Math.max(...siblings) + 1 : Math.min(...siblings) - 1 < 0 ? 0 : Math.min(...siblings) - 1);
            if (!front) Array.from(i.parentElement.children).filter(s => isItem(s) && s !== i).forEach(s => setTxt(s, 'ZIndex', (+txt(s, 'ZIndex') || 0) + 1));
        }));
    }

    // ── tablix columns and rows ──
    function flatColumns(tablix) {
        const members = kids(kid(kid(tablix, 'TablixColumnHierarchy'), 'TablixMembers'), 'TablixMember');
        return members.every(m => !kid(m, 'Group') && !kid(m, 'TablixMembers')) ? members : null;
    }
    function insertColumn(tablix, at, before) {
        const members = flatColumns(tablix);
        if (!members) { vscode.postMessage({ type: 'info', message: 'This tablix has column groups (a matrix); change its columns in the RDL.' }); return; }
        edit(() => {
            const body = kid(tablix, 'TablixBody');
            const columns = kids(kid(body, 'TablixColumns'), 'TablixColumn');
            const index = before ? at : at + 1;
            const width = txt(columns[Math.min(at, columns.length - 1)], 'Width') || '25mm';
            const column = build('TablixColumn', [build('Width', [width])]);
            kid(body, 'TablixColumns').insertBefore(column, columns[index] || null);
            const memberList = kid(kid(tablix, 'TablixColumnHierarchy'), 'TablixMembers');
            memberList.insertBefore(build('TablixMember'), members[index] || null);
            kids(kid(body, 'TablixRows'), 'TablixRow').forEach(row => {
                const cells = kids(kid(row, 'TablixCells'), 'TablixCell');
                let col = 0, placed = false;
                for (const cell of cells) {
                    const span = Math.max(1, +txt(kid(cell, 'CellContents'), 'ColSpan') || 1);
                    if (col < index && index < col + span) {
                        // A merged cell across the new column takes it in.
                        setTxt(kid(cell, 'CellContents'), 'ColSpan', span + 1);
                        kid(row, 'TablixCells').insertBefore(build('TablixCell'), cell.nextSibling);
                        placed = true; break;
                    }
                    if (col === index) { kid(row, 'TablixCells').insertBefore(newCell(cell), cell); placed = true; break; }
                    col += span;
                }
                if (!placed) kid(row, 'TablixCells').appendChild(newCell(cells[cells.length - 1]));
            });
        });
    }
    function newCell(like) {
        const name = uniqueName('Textbox');
        const tb = newTextbox(name, '');
        const likeTb = like && Array.from(kid(like, 'CellContents')?.children || []).find(c => c.localName === 'Textbox');
        if (likeTb && kid(likeTb, 'Style')) { kid(tb, 'Style').replaceWith(kid(likeTb, 'Style').cloneNode(true)); }
        return build('TablixCell', [build('CellContents', [tb])]);
    }
    function deleteColumn(tablix, at) {
        const members = flatColumns(tablix);
        if (!members || members.length < 2) { vscode.postMessage({ type: 'info', message: members ? 'A tablix needs at least one column.' : 'This tablix has column groups (a matrix); change its columns in the RDL.' }); return; }
        edit(() => {
            const body = kid(tablix, 'TablixBody');
            kids(kid(body, 'TablixColumns'), 'TablixColumn')[at].remove();
            members[at].remove();
            kids(kid(body, 'TablixRows'), 'TablixRow').forEach(row => {
                let col = 0;
                for (const cell of kids(kid(row, 'TablixCells'), 'TablixCell')) {
                    const contents = kid(cell, 'CellContents');
                    const span = Math.max(1, +txt(contents, 'ColSpan') || 1);
                    if (at >= col && at < col + span) {
                        if (span > 1) { setTxt(contents, 'ColSpan', span - 1 > 1 ? span - 1 : null); const next = cell.nextElementSibling; if (next && !kid(next, 'CellContents')) next.remove(); }
                        else cell.remove();
                        break;
                    }
                    col += span;
                }
            });
        });
    }
    // Static rows (no group) at the top level of the row hierarchy, in row order.
    function rowMembers(tablix) {
        const leaves = [];
        const walk = list => kids(list, 'TablixMember').forEach(m => { const inner = kid(m, 'TablixMembers'); if (inner) walk(inner); else leaves.push(m); });
        walk(kid(kid(tablix, 'TablixRowHierarchy'), 'TablixMembers'));
        return leaves;
    }
    function insertRow(tablix, at, before) {
        const leaves = rowMembers(tablix);
        const member = leaves[at];
        if (!member || kid(member, 'Group') || member.parentElement !== kid(kid(tablix, 'TablixRowHierarchy'), 'TablixMembers')) {
            vscode.postMessage({ type: 'info', message: 'Rows can be added next to the tablix\'s fixed rows (headers and footers). Rows inside a group: change them in the RDL.' });
            return;
        }
        edit(() => {
            const rows = kids(kid(kid(tablix, 'TablixBody'), 'TablixRows'), 'TablixRow');
            const like = rows[at];
            const row = build('TablixRow', [build('Height', [txt(like, 'Height') || '5mm']), build('TablixCells', kids(kid(like, 'TablixCells'), 'TablixCell').map(c => {
                const contents = kid(c, 'CellContents');
                if (!contents) return build('TablixCell');
                const cell = newCell(c);
                if (txt(contents, 'ColSpan')) setTxt(kid(cell, 'CellContents'), 'ColSpan', txt(contents, 'ColSpan'));
                return cell;
            }))]);
            kid(kid(tablix, 'TablixBody'), 'TablixRows').insertBefore(row, before ? like : like.nextElementSibling);
            const newMember = build('TablixMember');
            if (kid(member, 'KeepWithGroup')) newMember.appendChild(kid(member, 'KeepWithGroup').cloneNode(true));
            member.parentElement.insertBefore(newMember, before ? member : member.nextElementSibling);
        });
    }
    function deleteRow(tablix, at) {
        const leaves = rowMembers(tablix);
        const member = leaves[at];
        if (!member || kid(member, 'Group') || leaves.length < 2) { vscode.postMessage({ type: 'info', message: 'Only fixed rows (no group) can be deleted here, and a tablix keeps at least one row.' }); return; }
        edit(() => {
            kids(kid(kid(tablix, 'TablixBody'), 'TablixRows'), 'TablixRow')[at].remove();
            member.remove();
        });
    }

    // ── properties panel ──
    function field(label, input, hint) {
        const row = el('label', 'prop');
        row.appendChild(el('span', 'prop-name', label));
        row.appendChild(input);
        if (hint) row.title = hint;
        return row;
    }
    function textInput(value, onChange, opts) {
        const i = el(opts && opts.multiline ? 'textarea' : 'input');
        if (!(opts && opts.multiline)) i.type = 'text';
        i.value = value ?? '';
        i.spellcheck = false;
        if (opts && opts.list) i.setAttribute('list', opts.list);
        if (opts && opts.prop) i.dataset.prop = opts.prop;
        if (S.readOnly) i.disabled = true;
        i.addEventListener('change', () => onChange(i.value));
        return i;
    }
    function numberInput(mm, onChange) {
        const i = el('input');
        i.type = 'number';
        i.step = '0.1';
        i.value = String(r2(mm));
        if (S.readOnly) i.disabled = true;
        i.addEventListener('change', () => { if (i.value !== '' && !isNaN(+i.value)) onChange(+i.value); });
        return i;
    }
    function selectInput(options, value, onChange) {
        const s = el('select');
        options.forEach(o => { const opt = el('option', '', Array.isArray(o) ? o[1] : o); opt.value = Array.isArray(o) ? o[0] : o; opt.selected = opt.value === (value ?? ''); s.appendChild(opt); });
        if (S.readOnly) s.disabled = true;
        s.addEventListener('change', () => onChange(s.value));
        return s;
    }
    function check(value, onChange) {
        const c = el('input');
        c.type = 'checkbox';
        c.checked = !!value;
        if (S.readOnly) c.disabled = true;
        c.addEventListener('change', () => onChange(c.checked));
        return c;
    }
    function group(title) {
        const g = el('fieldset', 'prop-group');
        g.appendChild(el('legend', '', title));
        props.appendChild(g);
        return g;
    }

    function renderProps() {
        if (!props) return;
        props.textContent = '';
        if (S.mode === 'preview') return previewProps();
        if (!S.selected.length) return pageProps();
        if (S.selected.length > 1) {
            props.appendChild(el('div', 'prop-title', `${S.selected.length} items`));
            props.appendChild(el('p', 'hint', 'Align, size and distribute them with the toolbar (the first item selected is the reference). Arrow keys move them all.'));
            return;
        }
        const item = S.selected[0];
        const type = item.localName;
        props.appendChild(el('div', 'prop-title', `${type} ${item.getAttribute('Name')}`));
        let g = group('General');
        g.appendChild(field('Name', textInput(item.getAttribute('Name'), v => {
            if (!/^[A-Za-z_]\w*$/.test(v) || allNames().has(v.toLowerCase())) { vscode.postMessage({ type: 'error', message: `'${v}' is not a valid, unused name.` }); renderProps(); return; }
            edit(() => item.setAttribute('Name', v));
        })));
        if (!inTablix(item)) {
            g = group('Position (mm from the container)');
            [['Left', 'x'], ['Top', 'y'], ['Width', 'Width'], ['Height', 'Height']].forEach(([name, label]) =>
                g.appendChild(field(label, numberInput(toMm(txt(item, name)), v => edit(() => setSize(item, name, v))), `${name}: ${txt(item, name) || '0'}`)));
            const abs = absBox(item);
            if (abs) g.appendChild(el('div', 'hint', `On the page: x ${r2(abs.x)} mm, y ${r2(abs.y)} mm from the sheet's top-left corner.`));
            g.appendChild(field('Z-order', textInput(txt(item, 'ZIndex') || '0', v => edit(() => setTxt(item, 'ZIndex', +v || 0)))));
        }
        g = group('Visibility');
        g.appendChild(field('Hidden', textInput(txt(kid(item, 'Visibility'), 'Hidden') || '', v => edit(() => {
            if (!v) { const vis = kid(item, 'Visibility'); if (vis) vis.remove(); } else setTxt(ensure(item, 'Visibility'), 'Hidden', v);
        })), 'true, false or an expression (=...)'));

        if (type === 'Textbox') textboxProps(item);
        if (type === 'Rectangle' || type === 'Textbox' || type === 'Image') boxProps(item);
        if (type === 'Line') lineProps(item);
        if (type === 'Image') imageProps(item);
        if (type === 'Tablix') tablixProps(item);
        if (inTablix(item)) cellProps(item);
    }

    function textboxProps(tb) {
        let g = group('Text');
        const runs = kids(kid(firstParagraph(tb), 'TextRuns'), 'TextRun');
        const paragraphs = kids(kid(tb, 'Paragraphs'), 'Paragraph');
        g.appendChild(field('Value', textInput(txt(firstRun(tb), 'Value') || '', v => edit(() => setValue(tb, v)), { multiline: true, prop: 'value' }),
            'Text, or an expression: =Fields!Name.Value, =Labels!@SYS123, =Parameters!Name.Value'));
        if (runs.length > 1 || paragraphs.length > 1) g.appendChild(el('div', 'hint', `Rich text: ${paragraphs.length} paragraph(s), ${runs.length} run(s) in the first; the value edits the first run. Edit the others in the RDL.`));
        g.appendChild(field('Can grow', check(txt(tb, 'CanGrow') === 'true', v => edit(() => setTxt(tb, 'CanGrow', v ? 'true' : 'false'))),
            'Grows to fit its text and pushes items below it down. Off for fixed positions on preprinted forms.'));
        g.appendChild(field('Can shrink', check(txt(tb, 'CanShrink') === 'true', v => edit(() => setTxt(tb, 'CanShrink', v ? 'true' : 'false')))));
        const fmt = txt(kid(tb, 'Style'), 'Format') || '';
        g.appendChild(field('Format', textInput(fmt, v => edit(() => setTxt(ensure(tb, 'Style'), 'Format', v))), '.NET format: N2, d, dd/MM/yyyy, #,0.00 …'));
        g = group('Font');
        const run = styleOf(tb, true);
        const fontInput = textInput(txt(run, 'FontFamily') || txt(kid(tb, 'Style'), 'FontFamily') || 'Arial', v => edit(() => setTxt(styleOf(tb, true), 'FontFamily', v)), { list: 'fonts' });
        g.appendChild(field('Family', fontInput, 'Fonts available to D365FO reports (✓ installed on this machine)'));
        g.appendChild(field('Size (pt)', textInput((txt(run, 'FontSize') || '10pt').replace(/pt$/, ''), v => edit(() => setTxt(styleOf(tb, true), 'FontSize', /^\d+(\.\d+)?$/.test(v) ? v + 'pt' : v)))));
        g.appendChild(field('Weight', selectInput(['', 'Thin', 'ExtraLight', 'Light', 'Normal', 'Medium', 'SemiBold', 'Bold', 'ExtraBold', 'Heavy'], txt(run, 'FontWeight') || '', v => edit(() => setTxt(styleOf(tb, true), 'FontWeight', v)))));
        g.appendChild(field('Style', selectInput(['', 'Normal', 'Italic'], txt(run, 'FontStyle') || '', v => edit(() => setTxt(styleOf(tb, true), 'FontStyle', v)))));
        g.appendChild(field('Decoration', selectInput(['', 'None', 'Underline', 'Overline', 'LineThrough'], txt(run, 'TextDecoration') || '', v => edit(() => setTxt(styleOf(tb, true), 'TextDecoration', v)))));
        g.appendChild(field('Color', textInput(txt(run, 'Color') || '', v => edit(() => setTxt(styleOf(tb, true), 'Color', v)))));
        g = group('Alignment and padding');
        const para = ensure(firstParagraph(tb) || ensure(ensure(tb, 'Paragraphs'), 'Paragraph'), 'Style');
        g.appendChild(field('Horizontal', selectInput(['', 'General', 'Left', 'Center', 'Right'], txt(para, 'TextAlign') || '', v => edit(() => setTxt(ensure(firstParagraph(tb), 'Style'), 'TextAlign', v)))));
        g.appendChild(field('Vertical', selectInput(['', 'Top', 'Middle', 'Bottom'], txt(kid(tb, 'Style'), 'VerticalAlign') || '', v => edit(() => setTxt(ensure(tb, 'Style'), 'VerticalAlign', v)))));
        ['Left', 'Right', 'Top', 'Bottom'].forEach(s => g.appendChild(field('Padding ' + s.toLowerCase() + ' (pt)',
            textInput((txt(kid(tb, 'Style'), 'Padding' + s) || '').replace(/pt$/, ''), v => edit(() => setTxt(ensure(tb, 'Style'), 'Padding' + s, v === '' ? '' : /^\d+(\.\d+)?$/.test(v) ? v + 'pt' : v))),
            'Text starts this far inside the box. 0 for exact positions; Visual Studio writes 2pt.')));
    }

    function boxProps(item) {
        const g = group('Border and fill');
        const style = () => ensure(item, 'Style');
        const b = kid(kid(item, 'Style'), 'Border');
        g.appendChild(field('Border', selectInput(['None', 'Solid', 'Dashed', 'Dotted', 'Double'], txt(b, 'Style') || 'None', v => edit(() => setTxt(ensure(style(), 'Border'), 'Style', v)))));
        g.appendChild(field('Border width', textInput(txt(b, 'Width') || '', v => edit(() => setTxt(ensure(style(), 'Border'), 'Width', /^\d+(\.\d+)?$/.test(v) ? v + 'pt' : v)))));
        g.appendChild(field('Border color', textInput(txt(b, 'Color') || '', v => edit(() => setTxt(ensure(style(), 'Border'), 'Color', v)))));
        g.appendChild(field('Background', textInput(txt(kid(item, 'Style'), 'BackgroundColor') || '', v => edit(() => setTxt(style(), 'BackgroundColor', v)))));
        if (kid(kid(item, 'Style'), 'TopBorder') || kid(kid(item, 'Style'), 'BottomBorder') || kid(kid(item, 'Style'), 'LeftBorder') || kid(kid(item, 'Style'), 'RightBorder'))
            g.appendChild(el('div', 'hint', 'Some sides have their own border; edit those in the RDL.'));
    }

    function lineProps(item) {
        const g = group('Line');
        const b = () => ensure(ensure(item, 'Style'), 'Border');
        g.appendChild(field('Style', selectInput(['Solid', 'Dashed', 'Dotted', 'Double', 'None'], txt(kid(kid(item, 'Style'), 'Border'), 'Style') || 'Solid', v => edit(() => setTxt(b(), 'Style', v)))));
        g.appendChild(field('Width', textInput(txt(kid(kid(item, 'Style'), 'Border'), 'Width') || '1pt', v => edit(() => setTxt(b(), 'Width', /^\d+(\.\d+)?$/.test(v) ? v + 'pt' : v)))));
        g.appendChild(field('Color', textInput(txt(kid(kid(item, 'Style'), 'Border'), 'Color') || '', v => edit(() => setTxt(b(), 'Color', v)))));
    }

    function imageProps(item) {
        const g = group('Image');
        g.appendChild(field('Source', selectInput(['Embedded', 'Database', 'External'], txt(item, 'Source') || 'Embedded', v => edit(() => setTxt(item, 'Source', v)))));
        const names = [...new Set([...Object.keys(S.images), ...kids(kid(S.doc.documentElement, 'EmbeddedImages'), 'EmbeddedImage').map(i => i.getAttribute('Name'))])];
        g.appendChild(field('Value', textInput(txt(item, 'Value') || '', v => edit(() => setTxt(item, 'Value', v)), { list: 'imageNames' }),
            'Embedded: the image name; Database: =Fields!Logo.Value; External: a URL'));
        const list = el('datalist');
        list.id = 'imageNames';
        names.forEach(n => { const o = el('option'); o.value = n; list.appendChild(o); });
        g.appendChild(list);
        g.appendChild(field('Sizing', selectInput(['AutoSize', 'Fit', 'FitProportional', 'Clip'], txt(item, 'Sizing') || 'AutoSize', v => edit(() => setTxt(item, 'Sizing', v)))));
        g.appendChild(button('file-media', 'Embed an image file', () => vscode.postMessage({ type: 'pickImage' }), { text: 'Embed image…', disabled: S.readOnly }));
    }

    function tablixProps(tablix) {
        const g = group('Tablix');
        g.appendChild(field('Dataset', selectInput(['', ...((S.design.dataSets || []).map(d => d.name))], txt(tablix, 'DataSetName') || '', v => edit(() => setTxt(tablix, 'DataSetName', v)))));
        g.appendChild(field('Keep together', check(txt(tablix, 'KeepTogether') === 'true', v => edit(() => setTxt(tablix, 'KeepTogether', v ? 'true' : 'false')))));
        g.appendChild(field('No rows message', textInput(txt(tablix, 'NoRowsMessage') || '', v => edit(() => setTxt(tablix, 'NoRowsMessage', v)))));
        const body = kid(tablix, 'TablixBody');
        const cg = group('Columns (mm)');
        kids(kid(body, 'TablixColumns'), 'TablixColumn').forEach((c, i) => cg.appendChild(field('Column ' + (i + 1), numberInput(toMm(txt(c, 'Width')), v => edit(() => setSize(c, 'Width', v))))));
        const rg = group('Rows (mm)');
        kids(kid(body, 'TablixRows'), 'TablixRow').forEach((r, i) => rg.appendChild(field('Row ' + (i + 1), numberInput(toMm(txt(r, 'Height')), v => edit(() => setSize(r, 'Height', v))))));
        const groups = Array.from(tablix.getElementsByTagNameNS(S.ns, 'Group')).map(gr => `${gr.getAttribute('Name')}${kid(kid(gr, 'GroupExpressions'), 'GroupExpression') ? ' (' + kids(kid(gr, 'GroupExpressions'), 'GroupExpression').map(x => x.textContent).join(', ') + ')' : ' (details)'}`);
        if (groups.length) rg.appendChild(el('div', 'hint', 'Groups: ' + groups.join('; ')));
    }

    function cellProps(item) {
        const tablix = tablixOf(item);
        const div = divOf.get(item);
        if (!tablix || !div) return;
        const row = +div.dataset.row, column = +div.dataset.column;
        const g = group(`Cell: row ${row + 1}, column ${column + 1} of ${tablix.getAttribute('Name')}`);
        const actions = el('div', 'actions');
        actions.appendChild(button('arrow-left', 'Insert column left', () => insertColumn(tablix, column, true), { text: 'Column left', disabled: S.readOnly }));
        actions.appendChild(button('arrow-right', 'Insert column right', () => insertColumn(tablix, column, false), { text: 'Column right', disabled: S.readOnly }));
        actions.appendChild(button('trash', 'Delete column', () => deleteColumn(tablix, column), { text: 'Delete column', disabled: S.readOnly }));
        actions.appendChild(button('arrow-up', 'Insert row above', () => insertRow(tablix, row, true), { text: 'Row above', disabled: S.readOnly }));
        actions.appendChild(button('arrow-down', 'Insert row below', () => insertRow(tablix, row, false), { text: 'Row below', disabled: S.readOnly }));
        actions.appendChild(button('trash', 'Delete row', () => deleteRow(tablix, row), { text: 'Delete row', disabled: S.readOnly }));
        g.appendChild(actions);
        const body = kid(tablix, 'TablixBody');
        const col = kids(kid(body, 'TablixColumns'), 'TablixColumn')[column];
        const r = kids(kid(body, 'TablixRows'), 'TablixRow')[row];
        if (col) g.appendChild(field('Column width (mm)', numberInput(toMm(txt(col, 'Width')), v => edit(() => setSize(col, 'Width', v)))));
        if (r) g.appendChild(field('Row height (mm)', numberInput(toMm(txt(r, 'Height')), v => edit(() => setSize(r, 'Height', v)))));
        g.appendChild(button('table', 'Select the tablix', () => select([tablix]), { text: 'Select tablix' }));
    }

    const PAPER_SIZES = { 'Letter portrait': [215.9, 279.4], 'Letter landscape': [279.4, 215.9], 'Legal portrait': [215.9, 355.6], 'A4 portrait': [210, 297],
        'A4 landscape': [297, 210], 'A5 portrait': [148, 210], 'Executive portrait': [184.15, 266.7], 'Cheque 8.5 x 3.5 in': [215.9, 88.9] };
    function pageProps() {
        props.appendChild(el('div', 'prop-title', 'Page'));
        const p = page();
        const pe = pageEl();
        let g = group('Paper (mm)');
        const preset = Object.entries(PAPER_SIZES).find(([, s]) => Math.abs(s[0] - p.width) < 0.3 && Math.abs(s[1] - p.height) < 0.3);
        g.appendChild(field('Size', selectInput([['', 'Custom'], ...Object.keys(PAPER_SIZES).map(k => [k, k])], preset ? preset[0] : '', v => {
            if (!v) return;
            edit(() => { setSize(pe, 'PageWidth', PAPER_SIZES[v][0]); setSize(pe, 'PageHeight', PAPER_SIZES[v][1]); });
        })));
        g.appendChild(field('Width', numberInput(p.width, v => edit(() => setSize(pe, 'PageWidth', v)))));
        g.appendChild(field('Height', numberInput(p.height, v => edit(() => setSize(pe, 'PageHeight', v)))));
        g = group('Margins (mm)');
        [['LeftMargin', 'Left', p.left], ['RightMargin', 'Right', p.right], ['TopMargin', 'Top', p.top], ['BottomMargin', 'Bottom', p.bottom]]
            .forEach(([n, l, v]) => g.appendChild(field(l, numberInput(v, x => edit(() => setSize(pe, n, x))))));
        g = group('Body (mm)');
        g.appendChild(field('Width', numberInput(p.bodyWidth, v => edit(() => setSize(section(), 'Width', v))), 'Body width + left and right margins must not exceed the page width, or every page gets a blank page.'));
        g.appendChild(field('Height', numberInput(p.bodyHeight, v => edit(() => setSize(bodyEl(), 'Height', v)))));
        g.appendChild(button('screen-full', 'Fit the body to the page between the margins', () => edit(() => setSize(section(), 'Width', p.width - p.left - p.right)), { text: 'Fit width to page', disabled: S.readOnly }));
        ['PageHeader', 'PageFooter'].forEach(kind => {
            g = group(kind === 'PageHeader' ? 'Page header' : 'Page footer');
            const sec = kid(pe, kind);
            g.appendChild(field('Show', check(!!sec, v => edit(() => {
                if (v) { const s = build(kind, [build('Height', [fromMm(15)]), build('PrintOnFirstPage', ['true']), build('PrintOnLastPage', ['true']), build('Style')]); pe.insertBefore(s, pe.firstChild); }
                else if (sec) sec.remove();
            }))));
            if (sec) {
                g.appendChild(field('Height (mm)', numberInput(toMm(txt(sec, 'Height')), v => edit(() => setSize(sec, 'Height', v))), 'Fixed: headers and footers never grow; content beyond is cut off.'));
                g.appendChild(field('First page', check(txt(sec, 'PrintOnFirstPage') === 'true', v => edit(() => setTxt(sec, 'PrintOnFirstPage', v ? 'true' : 'false')))));
                g.appendChild(field('Last page', check(txt(sec, 'PrintOnLastPage') === 'true', v => edit(() => setTxt(sec, 'PrintOnLastPage', v ? 'true' : 'false')))));
            }
        });
        g = group('Design');
        g.appendChild(field('Units for new sizes', selectInput([['in', 'Inches'], ['cm', 'Centimetres'], ['mm', 'Millimetres']], S.unit, v => { S.unit = v; render([]); })));
        g.appendChild(el('div', 'hint', `Schema: ${S.ns.replace('http://schemas.microsoft.com/sqlserver/reporting/', 'RDL ').replace('/reportdefinition', '')}. Positions keep the unit they were written in.`));
    }

    // ── report data pane ──
    function dataPane() {
        const pane = el('div', 'data');
        pane.setAttribute('role', 'region');
        pane.setAttribute('aria-label', 'Report data');
        pane.appendChild(el('div', 'pane-title', 'Report data'));
        const drag = (node, payload) => {
            node.draggable = !S.readOnly;
            node.addEventListener('dragstart', e => e.dataTransfer.setData('text/plain', JSON.stringify(payload)));
            node.title = 'Drag onto the page or into a tablix cell';
        };
        const tree = (title, icon, entries) => {
            const d = el('details', 'tree');
            d.open = true;
            const s = el('summary', '', title);
            d.appendChild(s);
            entries.forEach(([text, payload, sub]) => {
                const row = el('div', 'leaf');
                row.appendChild(el('span', 'codicon codicon-' + icon));
                row.appendChild(el('span', '', text));
                if (sub) row.appendChild(el('span', 'sub', sub));
                drag(row, payload);
                d.appendChild(row);
            });
            pane.appendChild(d);
        };
        (S.design.dataSets || []).forEach(ds => tree(`${ds.name} (${ds.dataSourceType})`, 'symbol-field',
            ds.fields.map(f => [f.name, { kind: 'field', dataSet: ds.name, name: f.name }, (f.type || '').replace('System.', '')])));
        tree('Parameters', 'symbol-parameter', (S.design.parameters || []).filter(p => !p.name.startsWith('AX_')).map(p => [p.name, { kind: 'parameter', name: p.name }, (p.type || '').replace('System.', '')]));
        tree('Built-in fields', 'symbol-constant', BUILT_INS.map(b => [b, { kind: 'global', name: b }]));
        const labelBox = el('div', 'label-add');
        const input = el('input');
        input.placeholder = '@SYS12345 or @File:Id';
        input.setAttribute('aria-label', 'Label to place');
        const add = button('add', 'Place a label textbox on the page', () => {
            const v = input.value.trim();
            if (!/^@[A-Za-z]\w*(:\w+)?$/.test(v)) { vscode.postMessage({ type: 'error', message: 'A label reference looks like @SYS12345 or @MyLabels:MyLabel.' }); return; }
            addItem('Textbox', bodyEl(), 5, 5, `=Labels!${v}`);
        }, { disabled: S.readOnly });
        labelBox.appendChild(input);
        labelBox.appendChild(add);
        const ld = el('details', 'tree');
        ld.open = true;
        ld.appendChild(el('summary', '', 'Labels'));
        ld.appendChild(labelBox);
        pane.appendChild(ld);
        const fonts = el('datalist');
        fonts.id = 'fonts';
        (S.fonts || []).forEach(f => { const o = el('option'); o.value = f.name; o.label = f.installed ? '✓ installed' : 'not installed here'; fonts.appendChild(o); });
        pane.appendChild(fonts);
        return pane;
    }

    // ── warnings ──
    function layoutWarnings() {
        const w = [];
        const p = page();
        if (p.bodyWidth > p.width - p.left - p.right + 0.01)
            w.push(`Body width ${r2(p.bodyWidth)} mm + margins ${r2(p.left + p.right)} mm is wider than the page (${r2(p.width)} mm): every page gets an extra blank page.`);
        if (p.bodyHeight > p.height - p.top - p.bottom - p.headerHeight - p.footerHeight + 0.01)
            w.push(`Body height ${r2(p.bodyHeight)} mm is taller than the space between header and footer: the body breaks onto another page.`);
        const supported = new Set((S.fonts || []).map(f => f.name.toLowerCase()));
        const installed = new Set((S.fonts || []).filter(f => f.installed).map(f => f.name.toLowerCase()));
        const used = new Set(Array.from(S.doc.getElementsByTagNameNS(S.ns, 'FontFamily')).map(f => f.textContent.trim()).filter(f => f && !f.startsWith('=')));
        used.forEach(f => {
            if (supported.size && !supported.has(f.toLowerCase())) w.push(`Font '${f}' is not available to D365FO reports in the cloud; another font is used there.`);
            else if (supported.size && !installed.has(f.toLowerCase())) w.push(`Font '${f}' is not installed on this machine: the preview substitutes it.`);
        });
        const containers = Array.from(S.doc.getElementsByTagNameNS(S.ns, 'ReportItems'));
        containers.forEach(c => {
            const items = Array.from(c.children).filter(isItem);
            items.filter(i => txt(i, 'CanGrow') === 'true').forEach(g => {
                const gx = toMm(txt(g, 'Left')), gw = toMm(txt(g, 'Width')), gb = toMm(txt(g, 'Top')) + toMm(txt(g, 'Height'));
                const below = items.filter(o => o !== g && toMm(txt(o, 'Top')) >= gb - 0.01 && toMm(txt(o, 'Left')) < gx + gw && toMm(txt(o, 'Left')) + toMm(txt(o, 'Width')) > gx);
                if (below.length) w.push(`${g.getAttribute('Name')} can grow and pushes ${below.map(b => b.getAttribute('Name')).slice(0, 4).join(', ')}${below.length > 4 ? '…' : ''} down when its text is longer.`);
            });
        });
        const body = kid(bodyEl(), 'ReportItems');
        Array.from(body ? body.children : []).filter(isItem).forEach(i => {
            if (toMm(txt(i, 'Left')) + toMm(txt(i, 'Width')) > p.bodyWidth + 0.01) w.push(`${i.localName} ${i.getAttribute('Name')} ends right of the body's edge.`);
        });
        return w;
    }
    function renderWarnings() {
        if (!warningsBox) return;
        warningsBox.textContent = '';
        const all = [...new Set([...layoutWarnings(), ...S.serverWarnings.filter(w => !/^Font |^Body /.test(w))])];
        warningsBox.hidden = !all.length;
        all.forEach(w => { const row = el('div', 'warning'); row.appendChild(el('span', 'codicon codicon-warning')); row.appendChild(el('span', '', w)); warningsBox.appendChild(row); });
    }

    // ── status bar ──
    function status(message, at) {
        if (!statusBar) return;
        statusBar.textContent = '';
        if (S.mode === 'design' && at) statusBar.appendChild(el('span', 'pos', `x ${r2(at.x)} mm · y ${r2(at.y)} mm (${r2(at.x / 25.4)} × ${r2(at.y / 25.4)} in)`));
        const sel = S.selected.length === 1 ? (() => { const b = absBox(S.selected[0]); return b ? `${S.selected[0].getAttribute('Name')}: x ${r2(b.x)}, y ${r2(b.y)}, ${r2(b.w)} × ${r2(b.h)} mm` : ''; })()
            : S.selected.length ? `${S.selected.length} items` : '';
        if (sel) statusBar.appendChild(el('span', 'selinfo', sel));
        if (message) statusBar.appendChild(el('span', 'msg', message));
    }

    // ── scan overlay ──
    function scanKey() { return `${S.info.name}.${S.designName}`.toLowerCase(); }
    function scanDialog() {
        if (S.scan && S.scan.show) { S.scan.show = false; saveScan(); render(S.selected.map(pathOf)); return; }
        if (S.scan) { S.scan.show = true; saveScan(); render(S.selected.map(pathOf)); return; }
        vscode.postMessage({ type: 'pickScan' });
    }
    function saveScan() {
        saved.scans = saved.scans || {};
        // The image itself stays in the panel's memory; only its placement is remembered.
        saved.scans[scanKey()] = S.scan ? { widthMm: S.scan.widthMm, heightMm: S.scan.heightMm, offsetX: S.scan.offsetX, offsetY: S.scan.offsetY, opacity: S.scan.opacity, name: S.scan.name } : undefined;
        persist();
    }
    function scanSettings() {
        if (!S.scan) return;
        const g = group('Scan overlay (not saved in the report)');
        g.appendChild(el('div', 'hint', `${S.scan.name}: laid under the page at the size of the paper it shows.`));
        g.appendChild(field('Paper', selectInput([['', 'Custom'], ...Object.keys(PAPERS).map(k => [k, k])], Object.keys(PAPERS).find(k => PAPERS[k][0] === S.scan.widthMm && PAPERS[k][1] === S.scan.heightMm) || '', v => {
            if (!v) return; S.scan.widthMm = PAPERS[v][0]; S.scan.heightMm = PAPERS[v][1]; saveScan(); render(S.selected.map(pathOf));
        })));
        g.appendChild(field('Width (mm)', numberInput(S.scan.widthMm, v => { S.scan.widthMm = v; saveScan(); render(S.selected.map(pathOf)); })));
        g.appendChild(field('Height (mm)', numberInput(S.scan.heightMm, v => { S.scan.heightMm = v; saveScan(); render(S.selected.map(pathOf)); })));
        g.appendChild(field('Offset x (mm)', numberInput(S.scan.offsetX || 0, v => { S.scan.offsetX = v; saveScan(); render(S.selected.map(pathOf)); })));
        g.appendChild(field('Offset y (mm)', numberInput(S.scan.offsetY || 0, v => { S.scan.offsetY = v; saveScan(); render(S.selected.map(pathOf)); })));
        const range = el('input');
        range.type = 'range'; range.min = '0.1'; range.max = '1'; range.step = '0.05'; range.value = String(S.scan.opacity);
        range.addEventListener('input', () => { S.scan.opacity = +range.value; saveScan(); const img = sheet && sheet.querySelector('.scan'); if (img) img.style.opacity = range.value; });
        g.appendChild(field('Opacity', range));
        g.appendChild(button('close', 'Remove the scan', () => { S.scan = null; saveScan(); render(S.selected.map(pathOf)); }, { text: 'Remove scan' }));
    }

    // ── preview ──
    function previewView() {
        const wrap = el('div', 'preview');
        if (!S.preview) { wrap.appendChild(el('div', 'empty', 'Rendering…')); return wrap; }
        if (!S.preview.success) {
            const box = el('div', 'empty');
            box.appendChild(el('div', 'error', S.preview.message || 'The preview failed.'));
            if (/runtime/i.test(S.preview.message || '')) box.appendChild(button('cloud-download', 'Install Report Preview Runtime', () => vscode.postMessage({ type: 'installRuntime' }), { text: 'Install runtime…' }));
            wrap.appendChild(box);
            return wrap;
        }
        const info = el('div', 'preview-info', `Pages ${S.preview.firstPage}–${S.preview.firstPage + S.preview.pages.length - 1} of ${S.preview.totalPages} · rendered by Microsoft's report engine (as PDF and print) at ${S.preview.dpi} dpi`);
        wrap.appendChild(info);
        (S.preview.warnings || []).forEach(w => { const row = el('div', 'warning'); row.appendChild(el('span', 'codicon codicon-warning')); row.appendChild(el('span', '', w)); wrap.appendChild(row); });
        S.preview.pages.forEach((png, i) => {
            const holder = el('div', 'preview-page');
            const p = S.preview.page;
            holder.style.width = px(p.width) + 'px';
            holder.style.height = px(p.height) + 'px';
            if (S.scan && S.previewSettings.overlay) {
                const scan = el('img', 'scan');
                scan.src = `data:${S.scan.mimeType};base64,${S.scan.data}`;
                scan.alt = '';
                scan.style.left = px(S.scan.offsetX || 0) + 'px';
                scan.style.top = px(S.scan.offsetY || 0) + 'px';
                scan.style.width = px(S.scan.widthMm) + 'px';
                scan.style.height = px(S.scan.heightMm) + 'px';
                scan.style.opacity = String(S.scan.opacity);
                holder.appendChild(scan);
            }
            const img = el('img', 'page-img' + (S.scan && S.previewSettings.overlay ? ' blend' : ''));
            img.src = 'data:image/png;base64,' + png;
            img.alt = `Page ${S.preview.firstPage + i}`;
            holder.appendChild(img);
            wrap.appendChild(holder);
        });
        return wrap;
    }
    function previewProps() {
        props.appendChild(el('div', 'prop-title', 'Preview'));
        const st = S.previewSettings;
        let g = group('Settings');
        g.appendChild(field('Language', textInput(st.language, v => { st.language = v; persist(); })));
        g.appendChild(field('Company', textInput(st.company, v => { st.company = v; persist(); })));
        g.appendChild(field('Sample rows', textInput(String(st.sampleRows), v => { st.sampleRows = Math.max(0, +v || 0); persist(); })));
        g.appendChild(field('Pages', textInput(String(st.pages), v => { st.pages = Math.min(20, Math.max(1, +v || 1)); persist(); })));
        g.appendChild(field('Resolution (dpi)', textInput(String(st.dpi), v => { st.dpi = Math.min(300, Math.max(48, +v || 110)); persist(); })));
        if (S.scan) g.appendChild(field('Show scan under the page', check(st.overlay, v => { st.overlay = v; persist(); render([]); })));
        g.appendChild(button('play', 'Render again', runPreview, { text: 'Render' }));
        g = group('Data (JSON rows per dataset)');
        g.appendChild(el('div', 'hint', 'Rows the preview uses; datasets left empty get generated sample rows. Use the values printed on your sample form to check widths.'));
        (S.design.dataSets || []).forEach(ds => {
            const sample = {};
            ds.fields.slice(0, 60).forEach(f => { sample[f.name] = /String/.test(f.type) ? f.name : /DateTime/.test(f.type) ? '2026-01-31' : /Int|Double|Decimal/.test(f.type) ? 12345.67 : null; });
            const area = textInput(st.data[ds.name] || '', v => {
                if (v.trim()) { try { const rows = JSON.parse(v); if (!Array.isArray(rows)) throw new Error('not an array'); } catch (err) { vscode.postMessage({ type: 'error', message: `${ds.name}: enter a JSON array of rows (${err.message}).` }); return; } }
                st.data[ds.name] = v; persist();
            }, { multiline: true });
            area.placeholder = JSON.stringify([sample], null, 1).slice(0, 600);
            area.rows = 6;
            g.appendChild(field(ds.name, area));
        });
    }
    function runPreview() {
        const st = S.previewSettings;
        const data = {};
        Object.entries(st.data || {}).forEach(([k, v]) => { if (v && v.trim()) { try { data[k] = JSON.parse(v); } catch { /* reported on entry */ } } });
        S.preview = null;
        if (S.mode === 'preview') render([]);
        vscode.postMessage({ type: 'preview', rdl: xml(), data, sampleRows: st.sampleRows, language: st.language, company: st.company, maxPages: st.pages, dpi: st.dpi });
    }

    function save() {
        if (S.readOnly) return;
        vscode.postMessage({ type: 'save', rdl: xml() });
    }

    // ── no precision design yet ──
    function noDesign() {
        app.textContent = '';
        const box = el('div', 'empty');
        box.appendChild(el('h2', '', S.info.name));
        const autos = S.info.designs.filter(d => d.kind !== 'precision');
        box.appendChild(el('p', '', autos.length
            ? `Its design${autos.length > 1 ? 's' : ''} (${autos.map(d => d.name).join(', ')}) ${autos.length > 1 ? 'are auto designs' : 'is an auto design'}: the layout is generated from the dataset. For a precise layout (preprinted forms, cheques, invoices), create a precision design from it, as Visual Studio does, or start a blank one.`
            : 'The report has no design yet.'));
        if (!S.readOnly) {
            autos.forEach(d => box.appendChild(button('wand', `Create a precision design from ${d.name}`,
                () => vscode.postMessage({ type: 'convertDesign', design: d.name }), { text: `Create precision design from ${d.name}…` })));
            const input = el('input');
            input.value = 'Report';
            input.setAttribute('aria-label', 'Design name');
            box.appendChild(input);
            box.appendChild(button('add', 'Create a blank precision design', () => {
                if (!/^[A-Za-z_]\w*$/.test(input.value)) { vscode.postMessage({ type: 'error', message: 'Use letters, digits and underscores.' }); return; }
                vscode.postMessage({ type: 'createDesign', name: input.value });
            }, { text: 'Create blank precision design' }));
        } else {
            box.appendChild(button('copy', 'Duplicate into my model', () => vscode.postMessage({ type: 'duplicate' }), { text: 'Duplicate into my model…' }));
        }
        app.appendChild(box);
    }

    // ── messages from the extension ──
    window.addEventListener('message', ev => {
        const m = ev.data;
        switch (m.type) {
            case 'busy': {
                let b = document.querySelector('.busy');
                if (m.busy) { if (!b) { b = el('div', 'busy'); document.body.appendChild(b); } b.textContent = m.text || 'Working…'; }
                else if (b) b.remove();
                break;
            }
            case 'load': {
                document.querySelector('.busy')?.remove();
                S.info = m.info;
                bodyState.report = m.state.report; bodyState.design = m.state.design; persist();
                S.readOnly = !m.info.isCustom;
                if (!m.design) { noDesign(); break; }
                S.design = m.design;
                S.designName = m.design.design || m.state.design;
                S.labels = m.design.labels || {};
                S.fonts = m.design.fonts || [];
                S.images = m.design.images || {};
                S.serverWarnings = m.design.warnings || [];
                const text = m.design.draft || m.design.rdl;
                const decl = /^\s*<\?xml[^>]*\?>\s*/.exec(m.design.rdl);
                S.header = decl ? decl[0].trim() + '\r\n' : '';
                S.doc = new DOMParser().parseFromString(text, 'application/xml');
                if (S.doc.getElementsByTagName('parsererror').length) { app.textContent = 'The design is not valid XML; open it as RDL text to fix it.'; break; }
                S.ns = S.doc.documentElement.namespaceURI;
                const unit = (Array.from(S.doc.documentElement.children).find(c => c.localName === 'ReportUnitType') || {}).textContent;
                S.unit = /cm/i.test(unit || '') ? 'cm' : /mm/i.test(unit || '') ? 'mm' : 'in';
                S.savedXml = new XMLSerializer().serializeToString(new DOMParser().parseFromString(m.design.rdl, 'application/xml').documentElement);
                S.savedXml = S.header + S.savedXml;
                S.undo = []; S.redo = []; S.preview = null;
                const placement = saved.scans && saved.scans[scanKey()];
                S.scan = null;
                if (placement) status(`A scan overlay was used last time (${placement.name}); load it again with the camera button.`);
                render([]);
                if (m.design.draft) changed();
                break;
            }
            case 'saved':
                document.querySelector('.busy')?.remove();
                if (m.success) {
                    S.savedXml = xml();
                    S.serverWarnings = m.warnings || [];
                    renderWarnings();
                    status(m.message);
                    changed();
                }
                break;
            case 'preview':
                document.querySelector('.busy')?.remove();
                S.preview = m;
                if (m.success === undefined) S.preview.success = true;
                if (S.mode === 'preview') render([]);
                break;
            case 'scan': {
                const p = page();
                const placement = saved.scans && saved.scans[scanKey()];
                S.scan = { name: m.name, mimeType: m.mimeType, data: m.data, widthMm: placement ? placement.widthMm : p.width, heightMm: placement ? placement.heightMm : p.height,
                    offsetX: placement ? placement.offsetX : 0, offsetY: placement ? placement.offsetY : 0, opacity: placement ? placement.opacity : 0.45, show: true };
                saveScan();
                render(S.selected.map(pathOf));
                select([]);
                break;
            }
            case 'image':
                addImage(m);
                break;
        }
    });

    // Page props show scan settings below the page settings.
    const pagePropsBase = pageProps;
    pageProps = function () { pagePropsBase(); scanSettings(); }; // eslint-disable-line no-func-assign

    vscode.postMessage({ type: 'ready' });
}());
