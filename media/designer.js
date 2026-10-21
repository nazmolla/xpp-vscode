// Form designer webview: renders the merged form tree and forwards edit actions to the extension.
(function () {
    const vscode = acquireVsCodeApi();
    const toolbar = document.getElementById('toolbar');
    const outline = document.getElementById('outline');
    const canvas = document.getElementById('canvas');
    const properties = document.getElementById('properties');

    let tree = null;
    let selected = null;
    const parents = new Map();
    const collapsed = new Set();
    const preview = window.XppFormPreview({
        onSelect: name => select(findNode(tree.root, name)),
        selectedName: () => selected && selected.name,
    });

    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text !== undefined && text !== null) e.textContent = text;
        return e;
    }

    function caption(node) {
        return node.label || node.dataField || node.dataGroup || node.name;
    }

    function index(node, parent) {
        parents.set(node.name, parent);
        node.children.forEach(c => index(c, node));
    }

    function select(node) {
        if (!node) return;
        selected = node;
        outline.querySelectorAll('.selected').forEach(e => e.classList.remove('selected'));
        outline.querySelectorAll(`[data-name="${CSS.escape(node.name)}"]`).forEach(e => e.classList.add('selected'));
        preview.select(node.name);
        renderProperties();
    }

    // ---- toolbar ----
    function renderToolbar() {
        toolbar.replaceChildren();
        toolbar.append(el('strong', null, tree.form));
        toolbar.append(el('span', 'muted', ` ${tree.model}${tree.editable ? '' : ' (read-only, changes go to a form extension)'}`));
        const ext = el('span', 'muted', tree.extensions.length ? `  •  ${tree.extensions.length} extension(s): ${tree.extensions.join(', ')}` : '');
        toolbar.append(ext);
        const refresh = el('button', null, 'Refresh');
        refresh.addEventListener('click', () => vscode.postMessage({ command: 'refresh' }));
        toolbar.append(refresh);
    }

    // ---- outline ----
    function renderOutline() {
        outline.replaceChildren();
        const list = el('ul', 'tree');
        list.append(outlineItem(tree.root));
        outline.append(list);
    }

    function outlineItem(node) {
        const li = el('li');
        const row = el('div', 'row' + (node.addedBy ? ' added' : '') + (node.visible === 'No' ? ' hidden' : ''));
        row.dataset.name = node.name;
        const toggle = el('span', 'toggle', node.children.length ? (collapsed.has(node.name) ? '▸' : '▾') : ' ');
        toggle.addEventListener('click', e => {
            e.stopPropagation();
            collapsed.has(node.name) ? collapsed.delete(node.name) : collapsed.add(node.name);
            renderOutline();
            if (selected) select(selected);
        });
        row.append(toggle, el('span', 'name', node.name), el('span', 'type', node.type));
        row.title = node.addedBy ? `Added by ${node.addedBy}` : '';
        row.addEventListener('click', () => select(node));
        li.append(row);
        if (node.children.length && !collapsed.has(node.name)) {
            const ul = el('ul');
            node.children.forEach(c => ul.append(outlineItem(c)));
            li.append(ul);
        }
        return li;
    }

    // ---- preview ----
    function renderCanvas() {
        preview.render(canvas, tree);
    }

    // ---- properties ----
    function renderProperties() {
        properties.replaceChildren();
        if (!selected) {
            properties.append(el('p', 'muted', 'Select a control.'));
            return;
        }
        const n = selected;
        properties.append(el('h3', null, n.name));
        const table = el('table', 'props');
        [['Type', n.type], ['Label', n.label], ['Data source', n.dataSource], ['Data field', n.dataField], ['Field group', n.dataGroup],
         ['Visible', n.visible], ['Added by', n.addedBy || '(base form)'], ['Children', String(n.children.length)]]
            .filter(([, v]) => v !== undefined && v !== null && v !== '')
            .forEach(([k, v]) => {
                const tr = el('tr');
                tr.append(el('th', null, k), el('td', null, v));
                table.append(tr);
            });
        properties.append(table);

        const parent = parents.get(n.name);
        const actions = el('div', 'actions');
        const add = (label, command, extra) => {
            const b = el('button', null, label);
            b.addEventListener('click', () => vscode.postMessage(Object.assign({ command, name: n.name }, extra || {})));
            actions.append(b);
        };
        const placement = { target: n.name, parent: parent ? parent.name : 'Design', targetIsContainer: n.container };
        if (n.type !== 'Design' || n.container) {
            add(n.container ? 'Add fields inside…' : 'Add fields after…', 'addField', placement);
            add(n.container ? 'Add group inside…' : 'Add group after…', 'addGroup', placement);
        }
        if (n.type !== 'Design') {
            add(n.visible === 'No' ? 'Show' : 'Hide', 'setVisible', { visible: n.visible === 'No' });
            add('Change label…', 'setLabel', { current: n.label });
        }
        properties.append(actions);
        properties.append(el('p', 'muted', 'Changes are saved to the form extension <Form>.<YourModel> in your project.'));
    }

    window.addEventListener('message', event => {
        const msg = event.data;
        if (msg.type === 'tree') {
            tree = msg.data;
            parents.clear();
            index(tree.root, null);
            renderToolbar();
            renderOutline();
            renderCanvas();
            const keep = selected && parents.has(selected.name) ? findNode(tree.root, selected.name) : null;
            if (keep) select(keep); else renderProperties();
        } else if (msg.type === 'busy') {
            document.body.classList.toggle('busy', msg.on);
        } else if (msg.type === 'error') {
            const err = el('div', 'error', msg.message);
            err.addEventListener('click', () => err.remove());
            document.body.prepend(err);
        }
    });

    function findNode(node, name) {
        if (node.name === name) return node;
        for (const c of node.children) {
            const f = findNode(c, name);
            if (f) return f;
        }
        return null;
    }

    vscode.postMessage({ command: 'ready' });
})();
