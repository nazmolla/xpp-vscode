// Renders a form (the merged control tree from get_form_tree) the way it looks at run time, for the designers.
window.XppFormPreview = function (options) {
    const activeTab = new Map();
    const FieldTypes = new Set(['String', 'Integer', 'Int64', 'Real', 'Date', 'DateTime', 'Time', 'Guid', 'ComboBox', 'CheckBox', 'RadioButton', 'ReferenceGroup', 'SegmentedEntry', 'StaticText', 'Image', 'Window']);
    const ButtonTypes = new Set(['Button', 'CommandButton', 'MenuFunctionButton', 'MenuButton', 'DropDialogButton']);
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
                    if (node.type === 'Tab' && (activeTab.get(node.name) || 0) !== i) {
                        activeTab.set(node.name, i);
                        changed = true;
                    }
                    return true;
                }
            }
            return false;
        };
        if (tree) walk(tree.root);
        return changed;
    }

    function renderNode(node, depth) {
        if (depth > 40) return el('div', 'muted', '…');
        const t = node.type;

        if (t === 'Design') {
            const form = decorate(el('div', 'form'), node);
            form.append(el('div', 'form-title', tree.caption || tree.form));
            node.children.forEach(c => form.append(renderNode(c, depth + 1)));
            return form;
        }
        if (t === 'Tab') {
            const tab = decorate(el('div', 'tab'), node);
            const pages = node.children;
            const headers = el('div', 'tab-headers');
            const active = Math.min(activeTab.get(node.name) || 0, Math.max(0, pages.length - 1));
            pages.forEach((p, i) => {
                const h = el('button', 'tab-header' + (i === active ? ' active' : '') + (p.addedBy ? ' added' : ''), caption(p));
                h.dataset.name = p.name;
                h.addEventListener('click', ev => {
                    ev.stopPropagation();
                    activeTab.set(node.name, i);
                    draw();
                    options.onSelect(p.name);
                });
                headers.append(h);
            });
            tab.append(headers);
            if (pages[active]) tab.append(renderNode(pages[active], depth + 1));
            return tab;
        }
        if (t === 'TabPage' || t === 'Group') {
            const box = decorate(el('fieldset', t === 'TabPage' ? 'page' : 'group'), node);
            if (t === 'Group') box.append(el('legend', null, caption(node)));
            const body = el('div', 'flow');
            node.children.forEach(c => body.append(renderNode(c, depth + 1)));
            if (!node.children.length) body.append(el('span', 'muted', node.dataGroup ? `field group ${node.dataGroup}` : 'empty'));
            box.append(body);
            return box;
        }
        if (t === 'Grid' || t === 'ListView' || t === 'Tree') {
            const grid = decorate(el('div', 'grid'), node);
            const table = el('table');
            const head = el('tr');
            node.children.forEach(c => head.append(decorate(el('th', null, caption(c)), c)));
            if (!node.children.length) head.append(el('th', 'muted', t));
            table.append(head);
            for (let r = 0; r < 2; r++) {
                const row = el('tr');
                (node.children.length ? node.children : [null]).forEach(() => row.append(el('td', null, ' ')));
                table.append(row);
            }
            grid.append(table);
            return grid;
        }
        if (t === 'ActionPane' || t === 'ActionPaneTab' || t === 'ButtonGroup') {
            const bar = decorate(el('div', t === 'ActionPane' ? 'actionpane' : 'buttongroup'), node);
            if (t === 'ActionPaneTab') bar.append(el('span', 'apt-caption', caption(node)));
            node.children.forEach(c => bar.append(renderNode(c, depth + 1)));
            return bar;
        }
        if (ButtonTypes.has(t)) {
            return decorate(el('button', 'fake-button', caption(node)), node);
        }
        if (FieldTypes.has(t)) {
            const field = decorate(el('div', 'field'), node);
            field.append(el('label', null, caption(node)));
            field.append(el('div', 'input' + (t === 'CheckBox' ? ' checkbox' : ''), t === 'ComboBox' || t === 'ReferenceGroup' ? '▾' : ''));
            return field;
        }
        const other = decorate(el('div', 'other'), node);
        other.append(el('span', 'type', t), el('span', null, ' ' + caption(node)));
        node.children.forEach(c => other.append(renderNode(c, depth + 1)));
        return other;
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
