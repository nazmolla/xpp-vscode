// Element designer webview: node tree on the left, Properties grid on the right.
(function () {
    const vscode = acquireVsCodeApi();
    const treeEl = document.getElementById('tree');
    const propsEl = document.getElementById('properties');
    const errorEl = document.getElementById('error');
    const previewEl = document.getElementById('preview');
    let editable = false;
    let element = null;
    let elementModel = '';

    // Codicons per node, like Visual Studio's designer icons.
    const ElementIcons = [
        [/^AxTable/, 'table'], [/^AxClass/, 'symbol-class'], [/^AxForm/, 'window'], [/^AxEnum/, 'symbol-enum'], [/^AxEdt/, 'symbol-type-parameter'],
        [/^AxView/, 'eye'], [/^AxQuery/, 'filter'], [/^Ax(Composite|Aggregate)?DataEntity/, 'symbol-structure'], [/^AxMap/, 'symbol-interface'],
        [/^AxMenuItem/, 'link'], [/^AxMenu/, 'menu'], [/^AxSecurity/, 'shield'], [/^AxLabelFile/, 'symbol-string'], [/^AxReport/, 'graph'],
        [/^AxService/, 'server'], [/^AxWorkflow/, 'workflow'], [/^Ax(ConfigurationKey|LicenseCode)/, 'key'], [/^AxTile/, 'preview'], [/^AxSkill/, 'sparkle'],
    ];
    const FolderIcons = {
        Fields: 'symbol-field', FieldGroups: 'group-by-ref-type', Indexes: 'list-ordered', FullTextIndexes: 'whole-word', Relations: 'references',
        DeleteActions: 'trash', StateMachines: 'type-hierarchy-sub', Mappings: 'arrow-swap', Methods: 'symbol-method', Events: 'zap',
        DataSources: 'database', Parts: 'layout', Ranges: 'filter', Keys: 'key', Design: 'layout', ArrayElements: 'symbol-array',
        TableReferences: 'table', ViewMetadata: 'eye', EnumValues: 'symbol-enum-member', DataSourceLinks: 'link', DerivedDataSources: 'type-hierarchy',
        Controls: 'symbol-misc',
    };
    const ControlIcons = [
        [/ActionPane/, 'tools'], [/ButtonGroup/, 'symbol-namespace'], [/MenuButton/, 'menu'], [/Button/, 'symbol-event'], [/Grid|ListView|Table/, 'table'],
        [/TabPage/, 'folder'], [/Tab/, 'folder-library'], [/Group/, 'symbol-namespace'], [/String|Guid/, 'symbol-string'],
        [/Int|Real/, 'symbol-number'], [/Date|Time/, 'calendar'], [/CheckBox/, 'check'], [/ComboBox|Radio|Segmented/, 'list-selection'],
        [/Image/, 'file-media'], [/Html|Static/, 'note'], [/Tree/, 'list-tree'], [/Filter/, 'search'],
    ];
    const pick = (list, text, fallback) => (list.find(([re]) => re.test(text || '')) || [null, fallback])[1];

    function iconOf(node) {
        if (node.kind === 'element') return pick(ElementIcons, element && element.objectType, 'symbol-file');
        if (node.kind === 'folder') return FolderIcons[node.type] || 'folder';
        if (node.kind === 'method') return 'symbol-method';
        if (node.kind === 'event') return 'zap';
        const type = node.type || '';
        if (/^Form.*Control$/.test(type)) return pick(ControlIcons, type, 'symbol-misc');
        if (/EnumValue/.test(type)) return 'symbol-enum-member';
        if (/FieldGroup/.test(type)) return 'group-by-ref-type';
        if (/Field/.test(type)) return 'symbol-field';
        if (/Index/.test(type)) return 'list-ordered';
        if (/Relation/.test(type)) return 'references';
        if (/DataSource/.test(type)) return 'database';
        if (/Design/.test(type)) return 'layout';
        if (/Range/.test(type)) return 'filter';
        if (/StateMachine/.test(type)) return 'type-hierarchy-sub';
        return 'symbol-property';
    }

    // Form controls read "Name (Button Group)", like Visual Studio.
    function controlTypeLabel(type) {
        const m = /^Form(\w+?)Control$/.exec(type || '');
        return m ? m[1].replace(/([a-z])([A-Z])/g, '$1 $2') : null;
    }

    // Children as shown: inline folders (class methods, enum values, nested controls) are replaced by their children.
    function visibleChildren(node) {
        const result = [];
        for (const child of node.children) {
            if (child.inline) {
                for (const grand of visibleChildren(child)) result.push({ node: grand.node, parent: grand.parent === child ? child : grand.parent });
            } else {
                result.push({ node: child, parent: node });
            }
        }
        return result;
    }
    let selectedPath = null;
    let selectedRow = null;
    let pendingSelect = null;
    const collapsed = new Set();
    const controlRows = new Map();
    const preview = window.XppFormPreview({
        onSelect: name => {
            const row = controlRows.get(name.toLowerCase());
            if (row) {
                expandTo(row);
                select(row, true);
            }
        },
        selectedName: () => selectedRow && controlName(selectedRow._node),
    });

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
            case 'formTree':
                document.body.classList.add('has-preview');
                previewEl.hidden = false;
                preview.render(previewEl, msg.data);
                break;
            case 'selectPath':
                pendingSelect = msg.path;
                selectByPath(msg.path);
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
        element = msg.element || null;
        elementModel = data.model || '';
        document.getElementById('title').textContent = `${data.objectName} (${msg.typeLabel})`;
        const badge = document.getElementById('badge');
        badge.textContent = data.editable ? data.model : `${data.model || 'standard'} · read-only`;
        badge.className = data.editable ? 'badge' : 'badge readonly';
        document.getElementById('viewCode').hidden = !msg.hasCode;
        document.getElementById('formLayout').hidden = !msg.isForm;

        treeEl.textContent = '';
        controlRows.clear();
        const ul = document.createElement('ul');
        ul.className = 'tree';
        ul.appendChild(renderNode(data.root, 0, null));
        treeEl.appendChild(ul);
        if (data.truncated) {
            const note = document.createElement('div');
            note.className = 'muted';
            note.textContent = 'This element is very large; some nodes are not shown.';
            treeEl.appendChild(note);
        }
        const wanted = pendingSelect || selectedPath;
        pendingSelect = null;
        if (!wanted || !selectByPath(wanted)) {
            select(treeEl.querySelector('.row'));
        }
    }

    function selectByPath(path) {
        const row = treeEl.querySelector(`[data-path="${CSS.escape(pathKey(path))}"]`);
        if (!row) {
            return false;
        }
        expandTo(row);
        select(row);
        return true;
    }

    function expandTo(row) {
        for (let li = row.parentElement.parentElement; li && li !== treeEl; li = li.parentElement) {
            if (li.tagName === 'UL' && li.hidden) {
                const ownerRow = li.parentElement.querySelector(':scope > .row');
                toggleNode(ownerRow, li, ownerRow.querySelector('.twisty'), ownerRow._node.path);
            }
        }
        row.scrollIntoView({ block: 'nearest' });
    }

    // Form controls live under Design/Controls; their names are unique in the form.
    function controlName(node) {
        if (!node) return null;
        if (node.path.length === 1 && node.path[0] === 'Design') return 'Design';
        return node.kind === 'item' && node.path.length >= 3 && node.path[node.path.length - 2] === 'Controls' ? node.path[node.path.length - 1] : null;
    }

    function contextFor(node, parent) {
        const folders = node.children.filter(c => c.kind === 'folder' && c.type !== 'Events');
        const hasMethods = node.kind === 'folder' ? node.type === 'Methods' : folders.some(c => c.type === 'Methods');
        const canAdd = node.kind === 'folder' ? node.type !== 'Methods' && node.type !== 'Events' : folders.some(c => c.type !== 'Methods');
        const isEvents = node.kind === 'event' || (node.kind === 'folder' && node.type === 'Events');
        return JSON.stringify({
            webviewSection: 'xppNode',
            xppKind: node.kind,
            xppEditable: editable,
            xppCanAdd: canAdd,
            xppHasMethods: hasMethods,
            xppIsMethod: node.kind === 'method',
            xppIsEvent: node.kind === 'event',
            xppHasEvents: isEvents || node.kind === 'method' || node.path.length === 0 || node.children.some(c => c.type === 'Events'),
            ...(node.eventSpec ? { xppEventSpec: node.eventSpec } : {}),
            xppCanDelete: (node.kind === 'item' || node.kind === 'method') && !!parent && parent.kind === 'folder',
            xppPath: JSON.stringify(node.path),
            xppType: node.type,
            // The element itself: the Application Explorer's element actions (references, extensions, compare...).
            ...(node.path.length === 0 && element ? { xppRoot: element.context, item: { objectType: element.objectType, objectName: element.objectName }, objectType: element.objectType, objectName: element.objectName } : {}),
            preventDefaultContextMenuItems: true,
        });
    }

    function renderNode(node, depth, parent) {
        const li = document.createElement('li');
        const row = document.createElement('div');
        row.className = 'row';
        row.dataset.path = pathKey(node.path);
        row.dataset.kind = node.kind;
        row.dataset.vscodeContext = contextFor(node, parent);
        row.style.paddingLeft = `${depth * 14 + 4}px`;
        row.tabIndex = -1;
        const control = controlName(node);
        if (control) {
            controlRows.set(control.toLowerCase(), row);
        }

        const shown = visibleChildren(node);
        const hasChildren = shown.length > 0;
        // Folders start collapsed below the first level, like Visual Studio.
        if (hasChildren && depth >= 1 && !collapsed.has('init:' + pathKey(node.path))) {
            collapsed.add('init:' + pathKey(node.path));
            collapsed.add(pathKey(node.path));
        }
        const toggle = document.createElement('span');
        toggle.className = 'twisty codicon ' + (hasChildren ? (collapsed.has(pathKey(node.path)) ? 'codicon-chevron-right' : 'codicon-chevron-down') : 'codicon-blank');
        row.appendChild(toggle);

        const icon = document.createElement('span');
        icon.className = `icon codicon codicon-${iconOf(node)} ${node.kind}`;
        row.appendChild(icon);

        const name = document.createElement('span');
        name.className = 'name';
        name.textContent = node.kind === 'element' && elementModel ? `${node.name} [${elementModel}]` : node.name;
        row.appendChild(name);

        const controlType = node.kind === 'item' ? controlTypeLabel(node.type) : null;
        if (controlType) {
            const type = document.createElement('span');
            type.className = 'type';
            type.textContent = `(${controlType})`;
            row.appendChild(type);
        }
        if (node.kind === 'folder') {
            row.title = `${node.name}: ${node.children.length} item(s)`;
        }
        li.appendChild(row);

        if (hasChildren) {
            const ul = document.createElement('ul');
            ul.hidden = collapsed.has(pathKey(node.path));
            shown.forEach(child => ul.appendChild(renderNode(child.node, depth + 1, child.parent)));
            li.appendChild(ul);
            toggle.addEventListener('click', e => {
                e.stopPropagation();
                toggleNode(row, ul, toggle, node.path);
            });
        }
        row.addEventListener('click', () => select(row));
        // Right-click selects the node first, like a tree view.
        row.addEventListener('contextmenu', () => select(row));
        row.addEventListener('dblclick', () => {
            if (node.kind === 'method') {
                send('openMethod', { path: node.path });
            } else if (node.kind === 'event') {
                send('copyEventHandler', { spec: node.eventSpec });
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
        toggle.classList.toggle('codicon-chevron-right', ul.hidden);
        toggle.classList.toggle('codicon-chevron-down', !ul.hidden);
    }

    function select(row, fromPreview) {
        if (!row) {
            return;
        }
        if (selectedRow) {
            selectedRow.classList.remove('selected');
        }
        selectedRow = row;
        row.classList.add('selected');
        row.focus({ preventScroll: !!fromPreview });
        const node = row._node;
        selectedPath = node.path;
        const control = controlName(node);
        if (!fromPreview) {
            preview.select(control);
        }
        if (node.kind === 'folder') {
            propsEl.innerHTML = '';
            const info = document.createElement('div');
            info.className = 'muted';
            info.textContent = `${node.name}: ${node.children.length} item(s).`;
            propsEl.appendChild(info);
        } else if (node.kind === 'event') {
            propsEl.innerHTML = '';
            const info = document.createElement('div');
            info.className = 'muted';
            info.textContent = `${node.name}: right-click to copy an event handler method or to find the existing handlers. Double-click copies the handler.`;
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
                toggleNode(selectedRow, ul, selectedRow.querySelector('.twisty'), selectedRow._node.path);
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
