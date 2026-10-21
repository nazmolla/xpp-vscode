// Element designer webview: node tree on the left, Properties grid on the right.
(function () {
    const vscode = acquireVsCodeApi();
    const treeEl = document.getElementById('tree');
    const propsEl = document.getElementById('properties');
    const errorEl = document.getElementById('error');
    let editable = false;
    let selectedPath = null;
    let selectedRow = null;
    const collapsed = new Set();

    const send = (command, data) => vscode.postMessage(Object.assign({ command }, data || {}));
    const pathKey = path => path.join('\u0001');

    document.getElementById('refresh').addEventListener('click', () => send('refresh'));
    document.getElementById('viewCode').addEventListener('click', () => send('viewCode'));
    document.getElementById('formLayout').addEventListener('click', () => send('formLayout'));
    errorEl.addEventListener('click', () => { errorEl.hidden = true; });

    window.addEventListener('message', event => {
        const msg = event.data;
        switch (msg.type) {
            case 'busy':
                document.body.classList.toggle('busy', msg.on);
                break;
            case 'error':
                errorEl.textContent = msg.message;
                errorEl.hidden = false;
                break;
            case 'tree':
                renderTree(msg);
                break;
            case 'properties':
                if (selectedPath && pathKey(msg.path) === pathKey(selectedPath)) {
                    renderProperties(msg.data);
                }
                break;
        }
    });

    function renderTree(msg) {
        const data = msg.data;
        editable = data.editable;
        document.getElementById('title').textContent = `${data.objectName} (${msg.typeLabel})`;
        const badge = document.getElementById('badge');
        badge.textContent = data.editable ? data.model : `${data.model || 'standard'} · read-only`;
        badge.className = data.editable ? 'badge' : 'badge readonly';
        document.getElementById('viewCode').hidden = !msg.hasCode;
        document.getElementById('formLayout').hidden = !msg.isForm;

        treeEl.textContent = '';
        const ul = document.createElement('ul');
        ul.className = 'tree';
        ul.appendChild(renderNode(data.root, 0));
        treeEl.appendChild(ul);
        if (data.truncated) {
            const note = document.createElement('div');
            note.className = 'muted';
            note.textContent = 'This element is very large; some nodes are not shown.';
            treeEl.appendChild(note);
        }
        const keep = selectedPath && treeEl.querySelector(`[data-path="${CSS.escape(pathKey(selectedPath))}"]`);
        select(keep || treeEl.querySelector('.row'));
    }

    function renderNode(node, depth) {
        const li = document.createElement('li');
        const row = document.createElement('div');
        row.className = 'row';
        row.dataset.path = pathKey(node.path);
        row.dataset.kind = node.kind;
        row.style.paddingLeft = `${depth * 14 + 4}px`;
        row.tabIndex = -1;

        const hasChildren = node.children.length > 0;
        // Folders start collapsed below the first level, like Visual Studio.
        if (hasChildren && depth >= 1 && !collapsed.has('init:' + pathKey(node.path))) {
            collapsed.add('init:' + pathKey(node.path));
            collapsed.add(pathKey(node.path));
        }
        const toggle = document.createElement('span');
        toggle.className = 'toggle';
        toggle.textContent = hasChildren ? (collapsed.has(pathKey(node.path)) ? '\u25B8' : '\u25BE') : '';
        row.appendChild(toggle);

        const icon = document.createElement('span');
        icon.className = 'icon ' + node.kind;
        row.appendChild(icon);

        const name = document.createElement('span');
        name.className = 'name';
        name.textContent = node.name;
        row.appendChild(name);

        if (node.kind === 'item' && node.type) {
            const type = document.createElement('span');
            type.className = 'type';
            type.textContent = node.type;
            row.appendChild(type);
        }
        if (node.kind === 'folder') {
            const count = document.createElement('span');
            count.className = 'type';
            count.textContent = `(${node.children.length})`;
            row.appendChild(count);
        }
        li.appendChild(row);

        if (hasChildren) {
            const ul = document.createElement('ul');
            ul.hidden = collapsed.has(pathKey(node.path));
            node.children.forEach(child => ul.appendChild(renderNode(child, depth + 1)));
            li.appendChild(ul);
            toggle.addEventListener('click', e => {
                e.stopPropagation();
                toggleNode(row, ul, toggle, node.path);
            });
        }
        row.addEventListener('click', () => select(row, node));
        row.addEventListener('dblclick', () => {
            if (node.kind === 'method') {
                send('openMethod', { path: node.path });
            } else if (hasChildren) {
                toggleNode(row, li.querySelector('ul'), toggle, node.path);
            }
        });
        row._node = node;
        return li;
    }

    function toggleNode(row, ul, toggle, path) {
        const key = pathKey(path);
        if (collapsed.has(key)) {
            collapsed.delete(key);
        } else {
            collapsed.add(key);
        }
        ul.hidden = collapsed.has(key);
        toggle.textContent = ul.hidden ? '\u25B8' : '\u25BE';
    }

    function select(row) {
        if (!row) {
            return;
        }
        if (selectedRow) {
            selectedRow.classList.remove('selected');
        }
        selectedRow = row;
        row.classList.add('selected');
        row.focus();
        const node = row._node;
        selectedPath = node.path;
        if (node.kind === 'folder') {
            propsEl.innerHTML = '';
            const info = document.createElement('div');
            info.className = 'muted';
            info.textContent = `${node.name}: ${node.children.length} item(s).`;
            propsEl.appendChild(info);
        } else if (node.kind === 'method') {
            propsEl.innerHTML = '';
            const info = document.createElement('div');
            info.className = 'muted';
            info.textContent = 'Double-click to open the method in the code editor.';
            propsEl.appendChild(info);
        } else {
            send('select', { path: node.path });
        }
    }

    // Keyboard navigation in the tree.
    treeEl.addEventListener('keydown', e => {
        const rows = [...treeEl.querySelectorAll('.row')].filter(r => r.offsetParent !== null);
        const index = rows.indexOf(selectedRow);
        if (e.key === 'ArrowDown' && index < rows.length - 1) {
            select(rows[index + 1]);
            e.preventDefault();
        } else if (e.key === 'ArrowUp' && index > 0) {
            select(rows[index - 1]);
            e.preventDefault();
        } else if ((e.key === 'ArrowRight' || e.key === 'ArrowLeft') && selectedRow) {
            const ul = selectedRow.parentElement.querySelector(':scope > ul');
            if (ul && (e.key === 'ArrowRight') === ul.hidden) {
                toggleNode(selectedRow, ul, selectedRow.querySelector('.toggle'), selectedRow._node.path);
            }
            e.preventDefault();
        } else if (e.key === 'Enter' && selectedRow && selectedRow._node.kind === 'method') {
            send('openMethod', { path: selectedRow._node.path });
        }
    });

    function renderProperties(data) {
        propsEl.innerHTML = '';
        const header = document.createElement('div');
        header.className = 'props-header';
        header.textContent = data.type.replace(/^Ax/, '');
        propsEl.appendChild(header);

        const table = document.createElement('table');
        table.className = 'props';
        for (const p of data.properties) {
            const tr = document.createElement('tr');
            const name = document.createElement('td');
            name.className = 'prop-name';
            name.textContent = p.name;
            tr.appendChild(name);
            const cell = document.createElement('td');
            cell.className = 'prop-value';
            cell.appendChild(editorFor(p, data.editable && editable && !p.readOnly));
            if (p.resolved) {
                const hint = document.createElement('div');
                hint.className = 'resolved';
                hint.textContent = p.resolved;
                cell.appendChild(hint);
            }
            tr.appendChild(cell);
            table.appendChild(tr);
        }
        propsEl.appendChild(table);
    }

    function editorFor(p, canEdit) {
        if (!canEdit) {
            const span = document.createElement('span');
            span.className = 'readonly-value';
            span.textContent = p.value;
            span.title = p.value;
            return span;
        }
        let input;
        if (p.options) {
            input = document.createElement('select');
            const options = p.value === '' ? [''].concat(p.options) : p.options;
            for (const o of options) {
                const opt = document.createElement('option');
                opt.value = o;
                opt.textContent = o;
                opt.selected = o === p.value;
                input.appendChild(opt);
            }
            input.addEventListener('change', () => commit(p, input.value));
        } else {
            input = document.createElement('input');
            input.type = 'text';
            input.value = p.value;
            input.addEventListener('keydown', e => {
                if (e.key === 'Enter') {
                    input.blur();
                } else if (e.key === 'Escape') {
                    input.value = p.value;
                    input.blur();
                }
            });
            input.addEventListener('change', () => commit(p, input.value));
        }
        return input;
    }

    function commit(p, value) {
        if (value === p.value || !selectedPath) {
            return;
        }
        send('setProperty', { path: selectedPath, property: p.name, value });
    }

    send('ready');
})();
