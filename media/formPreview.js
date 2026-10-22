// Renders a form (the merged control tree from get_form_tree) the way the web client shows it, for the designers.
window.XppFormPreview = function (options) {
    const activeTab = new Map();
    const collapsedFastTabs = new Set();
    const ButtonTypes = new Set(['Button', 'CommandButton', 'MenuFunctionButton', 'MenuButton', 'DropDialogButton']);
    const InputTypes = new Set(['String', 'Integer', 'Int64', 'Real', 'Date', 'DateTime', 'Time', 'Guid', 'ComboBox', 'CheckBox', 'RadioButton', 'ReferenceGroup', 'SegmentedEntry', 'Window', 'Image']);
    let container = null;
    let tree = null;

    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text !== undefined && text !== null) e.textContent = text;
        return e;
    }

    function caption(node) {
        return node.label || node.dataField || node.dataGroup || node.name;
    }

    function decorate(e, node) {
        e.dataset.name = node.name;
        e.title = node.addedBy ? `${node.name} (added by ${node.addedBy})` : node.name;
        if (node.addedBy) e.classList.add('added');
        if (node.visible === 'No') e.classList.add('hidden');
        e.addEventListener('click', ev => {
            ev.stopPropagation();
            options.onSelect(node.name);
        });
        return e;
    }

    // Shows the tab page holding a control, so selecting it in the tree brings it into view.
    function reveal(name) {
        let changed = false;
        const walk = node => {
            if (node.name === name) return true;
            for (let i = 0; i < node.children.length; i++) {
                if (walk(node.children[i])) {
                    if (node.type === 'Tab' && !isFastTabs(node) && (activeTab.get(node.name) || 0) !== i) {
                        activeTab.set(node.name, i);
                        changed = true;
                    }
                    if (node.type === 'TabPage' && collapsedFastTabs.delete(node.name)) changed = true;
                    return true;
                }
            }
            return false;
        };
        if (tree) walk(tree.root);
        return changed;
    }

    // The web client lays a tab out by its Style, or by the form pattern when the style is Auto.
    function tabStyle(node, depth) {
        const style = (node.style || 'Auto').toLowerCase();
        if (style === 'fasttabs' || style === 'verticaltabs' || style === 'tabs' || style === 'panorama') return style;
        const pattern = (tree.pattern || '').toLowerCase();
        if (depth <= 2 && pattern.includes('tableofcontents')) return 'verticaltabs';
        if (depth <= 2 && (pattern.includes('detailsmaster') || pattern.includes('simpledetails'))) return 'fasttabs';
        return node.children.some(p => p.children.length > 0 && p.children.every(c => c.type === 'Group' || c.type === 'Tab')) && depth > 2 ? 'fasttabs' : 'tabs';
    }

    function isFastTabs(node) {
        return (node.style || '').toLowerCase() === 'fasttabs' || node._style === 'fasttabs';
    }

    function renderChildren(parent, node, depth) {
        node.children.forEach(c => parent.append(renderNode(c, depth + 1)));
    }

    function renderNode(node, depth) {
        if (depth > 40) return el('div', 'muted', '…');
        const t = node.type;

        if (t === 'Design') {
            const form = decorate(el('div', 'form'), node);
            const actionPanes = node.children.filter(c => c.type === 'ActionPane');
            actionPanes.forEach(c => form.append(renderNode(c, depth + 1)));
            form.append(el('div', 'form-title', tree.caption || tree.form));
            node.children.filter(c => c.type !== 'ActionPane').forEach(c => form.append(renderNode(c, depth + 1)));
            return form;
        }
        if (t === 'Tab') {
            const style = tabStyle(node, depth);
            node._style = style;
            return style === 'fasttabs' ? renderFastTabs(node, depth)
                : style === 'verticaltabs' ? renderVerticalTabs(node, depth)
                    : renderTabs(node, depth);
        }
        if (t === 'TabPage') {
            const page = decorate(el('div', 'tabpage'), node);
            renderChildren(page, node, depth);
            return page;
        }
        if (t === 'Group') {
            return renderGroup(node, depth);
        }
        if (t === 'Grid' || t === 'ListView' || t === 'Tree') {
            return renderGrid(node);
        }
        if (t === 'ActionPane') {
            const pane = decorate(el('div', 'actionpane'), node);
            const tabs = node.children.filter(c => c.type === 'ActionPaneTab');
            const strip = el('div', 'actionpane-strip');
            (tabs.length ? tabs : [node]).forEach((tab, i) => {
                const header = el('span', 'actionpane-tab' + (i === 0 ? ' active' : ''), tab === node ? '' : caption(tab));
                if (tab !== node) decorate(header, tab);
                strip.append(header);
            });
            pane.append(strip);
            const body = el('div', 'actionpane-body');
            const first = tabs[0] || node;
            first.children.forEach(c => body.append(renderNode(c, depth + 1)));
            // Without tabs the pane itself was rendered as the first tab above; its children must not appear twice.
            if (tabs.length) {
                node.children.filter(c => c.type !== 'ActionPaneTab').forEach(c => body.append(renderNode(c, depth + 1)));
            }
            pane.append(body);
            return pane;
        }
        if (t === 'ActionPaneTab' || t === 'ButtonGroup') {
            const group = decorate(el('div', 'buttongroup'), node);
            if (t === 'ButtonGroup' && node.label) group.append(el('div', 'buttongroup-caption', node.label));
            const buttons = el('div', 'buttongroup-buttons');
            node.children.forEach(c => buttons.append(renderNode(c, depth + 1)));
            group.append(buttons);
            return group;
        }
        if (ButtonTypes.has(t)) {
            return decorate(el('button', 'fake-button', caption(node)), node);
        }
        if (t === 'StaticText') {
            return decorate(el('div', 'static-text', node.label || ''), node);
        }
        if (InputTypes.has(t)) {
            return renderField(node);
        }
        const other = decorate(el('div', 'other'), node);
        other.append(el('span', 'type', t), el('span', null, ' ' + caption(node)));
        renderChildren(other, node, depth);
        return other;
    }

    function renderTabs(node, depth) {
        const tab = decorate(el('div', 'tab'), node);
        const pages = node.children;
        const headers = el('div', 'tab-headers');
        const active = Math.min(activeTab.get(node.name) || 0, Math.max(0, pages.length - 1));
        pages.forEach((p, i) => headers.append(tabHeader(node, p, i, i === active, 'tab-header')));
        tab.append(headers);
        if (pages[active]) tab.append(renderNode(pages[active], depth + 1));
        return tab;
    }

    function renderVerticalTabs(node, depth) {
        const tab = decorate(el('div', 'vtabs'), node);
        const pages = node.children;
        const nav = el('div', 'vtabs-nav');
        const active = Math.min(activeTab.get(node.name) || 0, Math.max(0, pages.length - 1));
        pages.forEach((p, i) => nav.append(tabHeader(node, p, i, i === active, 'vtab')));
        const body = el('div', 'vtabs-body');
        if (pages[active]) body.append(renderNode(pages[active], depth + 1));
        tab.append(nav, body);
        return tab;
    }

    function tabHeader(tabNode, page, index, active, className) {
        const h = el('div', className + (active ? ' active' : '') + (page.addedBy ? ' added' : '') + (page.visible === 'No' ? ' hidden' : ''), caption(page));
        h.dataset.name = page.name;
        h.addEventListener('click', ev => {
            ev.stopPropagation();
            activeTab.set(tabNode.name, index);
            draw();
            options.onSelect(page.name);
        });
        return h;
    }

    function renderFastTabs(node, depth) {
        const tab = decorate(el('div', 'fasttabs'), node);
        node.children.forEach(page => {
            const section = decorate(el('section', 'fasttab'), page);
            const header = el('div', 'fasttab-header');
            const collapsed = collapsedFastTabs.has(page.name);
            header.append(el('span', 'fasttab-caption', caption(page)), el('span', 'fasttab-toggle', collapsed ? '\u2304' : '\u2303'));
            header.addEventListener('click', ev => {
                ev.stopPropagation();
                if (collapsedFastTabs.has(page.name)) collapsedFastTabs.delete(page.name); else collapsedFastTabs.add(page.name);
                draw();
                options.onSelect(page.name);
            });
            section.append(header);
            if (!collapsed) {
                const body = el('div', 'fasttab-body');
                renderChildren(body, page, depth + 1);
                section.append(body);
            }
            tab.append(section);
        });
        return tab;
    }

    function renderGroup(node, depth) {
        const columns = Math.max(1, parseInt(node.columns, 10) || 0);
        const hasGroups = node.children.some(c => c.type === 'Group');
        const box = decorate(el('div', 'group' + (hasGroups ? ' group-container' : '')), node);
        if (node.label && (node.frameType || '').toLowerCase() !== 'none') box.append(el('div', 'group-caption', node.label));
        const body = el('div', 'group-body');
        if (columns > 1) body.style.columnCount = String(columns);
        renderChildren(body, node, depth);
        if (!node.children.length && node.groupFields) {
            node.groupFields.forEach(f => body.append(fieldControl({ name: node.name, label: f.label, fieldKind: f.fieldKind, type: '' }, false)));
        }
        if (!node.children.length && !node.groupFields) body.append(el('span', 'muted', 'empty'));
        box.append(body);
        return box;
    }

    function renderGrid(node) {
        const grid = decorate(el('div', 'grid'), node);
        const table = el('table');
        const head = el('tr');
        head.append(el('th', 'grid-check', ''));
        const columns = node.children.length ? node.children : [];
        columns.forEach(c => head.append(decorate(el('th', null, caption(c)), c)));
        if (!columns.length) head.append(el('th', 'muted', node.type));
        table.append(head);
        for (let r = 0; r < 3; r++) {
            const row = el('tr');
            row.append(el('td', 'grid-check', ''));
            (columns.length ? columns : [null]).forEach(c => row.append(el('td', c && c.fieldKind === 'number' ? 'num' : null, '')));
            table.append(row);
        }
        grid.append(table);
        return grid;
    }

    function renderField(node) {
        return fieldControl(node, true);
    }

    function fieldControl(node, selectable) {
        const kind = node.fieldKind || (node.type === 'CheckBox' ? 'toggle'
            : node.type === 'Real' || node.type === 'Integer' ? 'number'
                : node.type === 'Date' ? 'date'
                    : node.type === 'DateTime' ? 'datetime'
                        : node.type === 'ComboBox' ? 'enum' : 'text');
        const field = el('div', 'field');
        if (selectable) decorate(field, node);
        if (node.showLabel !== 'No') field.append(el('label', null, node.label || (selectable ? '' : node.name)));
        if (kind === 'toggle') {
            const toggle = el('div', 'toggle-row');
            toggle.append(el('span', 'toggle'), el('span', 'toggle-text', 'No'));
            field.append(toggle);
        } else if (kind === 'number') {
            field.append(el('div', 'input num', '0'));
        } else if (kind === 'date' || kind === 'datetime') {
            const input = el('div', 'input with-icon');
            input.append(el('span', 'icon', '\uD83D\uDCC5'));
            field.append(input);
        } else if (kind === 'enum' || node.type === 'ReferenceGroup' || node.type === 'SegmentedEntry' || kind === 'reference') {
            const input = el('div', 'input with-icon');
            input.append(el('span', 'icon', '\u02C5'));
            field.append(input);
        } else {
            field.append(el('div', 'input'));
        }
        return field;
    }

    function draw() {
        if (container && tree) {
            container.replaceChildren(renderNode(tree.root, 0));
            highlight(options.selectedName ? options.selectedName() : null);
        }
    }

    function highlight(name) {
        if (!container) return;
        container.querySelectorAll('.selected').forEach(e => e.classList.remove('selected'));
        if (!name) return;
        const hits = container.querySelectorAll(`[data-name="${CSS.escape(name)}"]`);
        hits.forEach(e => e.classList.add('selected'));
        if (hits[0]) hits[0].scrollIntoView({ block: 'nearest' });
    }

    return {
        render(target, formTree) {
            container = target;
            tree = formTree;
            draw();
        },
        select(name) {
            if (reveal(name)) draw();
            else highlight(name);
        },
    };
};
