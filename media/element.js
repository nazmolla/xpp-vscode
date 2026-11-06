// Element designer webview: node tree on the left, Properties grid on the right.
(function () {
    const vscode = acquireVsCodeApi();
    if (document.body.dataset.xppState) {
        vscode.setState({ xpp: JSON.parse(document.body.dataset.xppState) });
    }
    const treeEl = document.getElementById('tree');
    treeEl.setAttribute('role', 'tree');
    treeEl.setAttribute('aria-label', 'Element nodes');
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

        treeEl.textContent = '';
        treeEl.title = editable ? 'Drag nodes onto other nodes, or Application Explorer elements onto this tree (hold Shift to drop into the designer).' : '';
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

    // Go To: one context key per reference the node has (xppRef_<key>), so its right-click menu lists only those.
    function refContext(refs) {
        const context = {};
        if (refs && Object.keys(refs).length > 0) {
            for (const key of Object.keys(refs)) context['xppRef_' + key] = true;
            context.xppRefCount = Object.keys(refs).length;
            context.xppRefs = JSON.stringify(refs);
        }
        return context;
    }

    function contextFor(node, parent) {
        const folders = node.children.filter(c => c.kind === 'folder' && c.type !== 'Events');
        const hasMethods = node.kind === 'folder' ? node.type === 'Methods' : folders.some(c => c.type === 'Methods');
        const canAdd = node.kind === 'folder' ? node.type !== 'Methods' && node.type !== 'Events' : folders.some(c => c.type !== 'Methods');
        const isEvents = node.kind === 'event' || (node.kind === 'folder' && node.type === 'Events');
        const inFolder = !!parent && parent.kind === 'folder';
        return JSON.stringify({
            webviewSection: 'xppNode',
            xppKind: node.kind,
            // Base element nodes in an extension's designer are read-only (change them through the extension's modifications).
            xppEditable: editable && !node.inherited,
            xppCanAdd: canAdd,
            xppHasMethods: hasMethods,
            xppIsMethod: node.kind === 'method',
            xppIsEvent: node.kind === 'event',
            xppHasEvents: isEvents || node.kind === 'method' || node.path.length === 0 || node.children.some(c => c.type === 'Events'),
            ...(node.eventSpec ? { xppEventSpec: node.eventSpec } : {}),
            xppCanDelete: (node.kind === 'item' || node.kind === 'method') && inFolder,
            // Rename and duplicate change the name, which for methods is part of their code.
            xppCanRename: node.kind === 'item' && inFolder,
            xppPath: JSON.stringify(node.path),
            xppType: node.type,
            ...(node.kind !== 'folder' && node.kind !== 'event' ? { xppName: node.name } : {}),
            ...(node.intrinsic ? { xppIntrinsic: node.intrinsic } : {}),
            ...(node.memberRefs ? { xppMemberRefs: true, item: { objectType: node.memberRefs.objectType, objectName: node.memberRefs.objectName }, memberName: node.memberRefs.memberName } : {}),
            ...refContext(node.refs),
            ...(node.accepts && editable ? { xppAccepts: JSON.stringify(node.accepts), xppOnBaseNode: !!node.inherited } : {}),
            // The element itself: the Application Explorer's element actions (references, extensions, compare...).
            ...(node.path.length === 0 && element ? { xppRoot: element.context, item: { objectType: element.objectType, objectName: element.objectName }, objectType: element.objectType, objectName: element.objectName } : {}),
            preventDefaultContextMenuItems: true,
        });
    }

    // F12 and Ctrl+click open the node's first reference (the server orders them: EDT, enum, base, table, ...).
    function goToFirst(node) {
        const refs = node && node.refs;
        const first = refs && Object.values(refs)[0];
        if (first) send('goTo', { reference: first });
        return !!first;
    }

    function renderNode(node, depth, parent) {
        const li = document.createElement('li');
        const row = document.createElement('div');
        row.className = 'row';
        row.setAttribute('role', 'treeitem');
        row.setAttribute('aria-level', String(depth + 1));
        row.setAttribute('aria-selected', 'false');
        row.dataset.path = pathKey(node.path);
        row.dataset.kind = node.kind;
        if (node.own) row.classList.add('own');
        if (node.inherited) row.classList.add('inherited');
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
            ul.setAttribute('role', 'group');
            ul.hidden = collapsed.has(pathKey(node.path));
            row.setAttribute('aria-expanded', String(!ul.hidden));
            shown.forEach(child => ul.appendChild(renderNode(child.node, depth + 1, child.parent)));
            li.appendChild(ul);
            toggle.addEventListener('click', e => {
                e.stopPropagation();
                toggleNode(row, ul, toggle, node.path);
            });
        }
        row.addEventListener('click', () => select(row));
        enableDragAndDrop(row, node);
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

    // Drag and drop, as in Visual Studio's designer: nodes of this element onto other nodes (field to field group, data
    // source field to the form design, reorder), and elements from the Application Explorer onto the nodes that take them.
    const NodeMime = 'application/vnd.xpp.node';
    const isNodeDrag = e => e.dataTransfer && [...e.dataTransfer.types].includes(NodeMime);
    const droppedUris = e => (e.dataTransfer.getData('text/uri-list') || e.dataTransfer.getData('text/plain') || '')
        .split(/\r?\n/).filter(u => u && !u.startsWith('#'));

    function enableDragAndDrop(row, node) {
        const draggable = editable && !node.inherited && (node.kind === 'item' || node.kind === 'method');
        row.draggable = draggable;
        if (draggable) {
            row.addEventListener('dragstart', e => {
                e.dataTransfer.setData(NodeMime, JSON.stringify(node.path));
                e.dataTransfer.effectAllowed = 'copyMove';
            });
        }
        const accepts = e => editable && (isNodeDrag(e) ? !node.inherited : !!node.accepts);
        row.addEventListener('dragover', e => {
            if (accepts(e)) {
                e.preventDefault();
                e.dataTransfer.dropEffect = isNodeDrag(e) ? 'move' : 'copy';
                row.classList.add('drop-target');
            }
        });
        row.addEventListener('dragleave', () => row.classList.remove('drop-target'));
        row.addEventListener('drop', e => {
            row.classList.remove('drop-target');
            if (!accepts(e)) return;
            e.preventDefault();
            e.stopPropagation();
            if (isNodeDrag(e)) {
                const sourcePath = JSON.parse(e.dataTransfer.getData(NodeMime));
                if (pathKey(sourcePath) !== pathKey(node.path)) send('dropNode', { target: node.path, sourcePath });
            } else {
                send('dropElements', { target: node.path, onBaseNode: !!node.inherited, uris: droppedUris(e) });
            }
        });
    }

    function enablePropertyDrop(tr, p, canEdit) {
        if (!canEdit || p.kind !== 'string') return;
        tr.addEventListener('dragover', e => {
            if (!isNodeDrag(e)) {
                e.preventDefault();
                e.dataTransfer.dropEffect = 'copy';
                tr.classList.add('drop-target');
            }
        });
        tr.addEventListener('dragleave', () => tr.classList.remove('drop-target'));
        tr.addEventListener('drop', e => {
            tr.classList.remove('drop-target');
            if (isNodeDrag(e) || !selectedPath) return;
            e.preventDefault();
            send('dropOnProperty', { path: selectedPath, property: p.name, uris: droppedUris(e) });
        });
    }

    function toggleNode(row, ul, toggle, path) {
        const key = pathKey(path);
        if (collapsed.has(key)) {
            collapsed.delete(key);
        } else {
            collapsed.add(key);
        }
        ul.hidden = collapsed.has(key);
        row.setAttribute('aria-expanded', String(!ul.hidden));
        toggle.classList.toggle('codicon-chevron-right', ul.hidden);
        toggle.classList.toggle('codicon-chevron-down', !ul.hidden);
    }

    function select(row, fromPreview) {
        if (!row) {
            return;
        }
        if (selectedRow) {
            selectedRow.classList.remove('selected');
            selectedRow.setAttribute('aria-selected', 'false');
            selectedRow.tabIndex = -1;
        }
        selectedRow = row;
        row.classList.add('selected');
        row.setAttribute('aria-selected', 'true');
        row.tabIndex = 0;
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
            send('select', { path: node.path, source: node.source || null });
        }
    }

    // Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) undo and redo property changes, except while typing in a field.
    document.addEventListener('keydown', e => {
        if (!(e.ctrlKey || e.metaKey) || ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName)) {
            return;
        }
        const key = e.key.toLowerCase();
        if (key === 'z' || key === 'y') {
            send(key === 'y' || e.shiftKey ? 'redo' : 'undo', {});
            e.preventDefault();
        }
    });

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
        } else if (e.key === 'F12' && selectedRow) {
            if (goToFirst(selectedRow._node)) e.preventDefault();
        }
    });

    // Properties window state, kept while other nodes are selected.
    let propFilter = '';
    let modifiedOnly = false;
    let byCategory = true;
    let lastProps = null;
    const PickTypes = {
        ExtendedDataType: ['AxEdt'], EnumType: ['AxEnum'], Table: ['AxTable'], RelatedTable: ['AxTable'], ConfigurationKey: ['AxConfigurationKey'],
        Extends: ['AxEdt', 'AxClass'], FormRef: ['AxForm'], MenuItemName: ['AxMenuItemDisplay', 'AxMenuItemAction', 'AxMenuItemOutput'],
        Query: ['AxQuery'], Form: ['AxForm'], Class: ['AxClass'],
    };
    const LabelProps = /^(Label|HelpText|Caption|Description|DeveloperDocumentation|SingularLabel)$/;

    function renderProperties(data) {
        lastProps = data;
        propsEl.innerHTML = '';
        const header = document.createElement('div');
        header.className = 'props-header';
        header.textContent = data.type.replace(/^Ax/, '');
        propsEl.appendChild(header);

        const bar = document.createElement('div');
        bar.className = 'props-toolbar';
        const filter = document.createElement('input');
        filter.type = 'search';
        filter.id = 'propFilter';
        filter.placeholder = 'Filter properties (Ctrl+F)';
        filter.setAttribute('aria-label', 'Filter properties by name or value');
        filter.value = propFilter;
        filter.addEventListener('input', () => {
            propFilter = filter.value;
            renderRows(table, footer, data);
        });
        const modified = toggleButton('codicon-circle-filled', 'Only properties changed from their default', modifiedOnly, on => {
            modifiedOnly = on;
            renderRows(table, footer, data);
        });
        const grouping = toggleButton('codicon-list-tree', 'Group by category (off: alphabetical)', byCategory, on => {
            byCategory = on;
            renderRows(table, footer, data);
        });
        bar.append(filter, modified, grouping);
        propsEl.appendChild(bar);

        const table = document.createElement('table');
        table.className = 'props';
        table.setAttribute('role', 'grid');
        table.setAttribute('aria-label', 'Properties');
        propsEl.appendChild(table);
        const footer = document.createElement('div');
        footer.className = 'props-footer';
        footer.setAttribute('aria-live', 'polite');
        propsEl.appendChild(footer);
        renderRows(table, footer, data);
    }

    function toggleButton(icon, title, on, changed) {
        const button = document.createElement('button');
        button.className = `toggle codicon ${icon}`;
        button.title = title;
        button.setAttribute('aria-label', title);
        button.setAttribute('aria-pressed', String(on));
        button.addEventListener('click', () => {
            const next = button.getAttribute('aria-pressed') !== 'true';
            button.setAttribute('aria-pressed', String(next));
            changed(next);
        });
        return button;
    }

    function describe(footer, p) {
        footer.textContent = '';
        const name = document.createElement('strong');
        name.textContent = p.name;
        footer.appendChild(name);
        const text = p.description || (p.modified ? 'Changed from the default.' : '');
        if (text) {
            footer.appendChild(document.createTextNode(`: ${text}`));
        }
    }

    function renderRows(table, footer, data) {
        table.textContent = '';
        const needle = propFilter.trim().toLowerCase();
        const shown = data.properties.filter(p => (!modifiedOnly || p.modified)
            && (!needle || p.name.toLowerCase().includes(needle) || String(p.value).toLowerCase().includes(needle)));
        if (!shown.length) {
            const tr = document.createElement('tr');
            const td = document.createElement('td');
            td.colSpan = 2;
            td.className = 'muted';
            td.textContent = needle ? `No properties match '${propFilter}'.` : 'No properties changed from their default.';
            tr.appendChild(td);
            table.appendChild(tr);
            return;
        }
        const groups = new Map();
        for (const p of shown) {
            const key = byCategory ? (p.category || 'Misc') : '';
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(p);
        }
        const order = ['Identification', 'Data', 'Appearance', 'Behavior', 'Configuration and security', 'Misc'];
        const keys = [...groups.keys()].sort((a, b) => {
            const ia = order.indexOf(a), ib = order.indexOf(b);
            return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
        });
        for (const key of keys) {
            if (key) {
                const tr = document.createElement('tr');
                tr.className = 'prop-category';
                const th = document.createElement('th');
                th.colSpan = 2;
                th.scope = 'colgroup';
                th.textContent = key;
                tr.appendChild(th);
                table.appendChild(tr);
            }
            for (const p of groups.get(key)) {
                table.appendChild(propertyRow(p, data, footer));
            }
        }
    }

    function propertyRow(p, data, footer) {
        const canEdit = data.editable && editable && !p.readOnly;
        const tr = document.createElement('tr');
        if (p.reference) {
            // Right-click on the row offers Go to <what the property names>, next to Cut/Copy/Paste for the value.
            tr.dataset.vscodeContext = JSON.stringify({
                webviewSection: 'xppProperty',
                ...refContext({ [p.reference.key]: p.reference }),
                preventDefaultContextMenuItems: false,
            });
        }
        tr.addEventListener('focusin', () => describe(footer, p));
        tr.addEventListener('mouseenter', () => describe(footer, p));
        const name = document.createElement('td');
        name.className = 'prop-name' + (p.modified ? ' modified' : '');
        name.textContent = p.name;
        if (p.modified) {
            name.title = `${p.name} (changed from the default)`;
            name.setAttribute('aria-label', `${p.name}, modified`);
        }
        tr.appendChild(name);
        const cell = document.createElement('td');
        cell.className = 'prop-value';
        const line = document.createElement('div');
        line.className = 'value-line';
        const editor = editorFor(p, canEdit);
        line.appendChild(editor);
        enablePropertyDrop(tr, p, canEdit);
        const picker = canEdit && p.kind === 'string' ? pickerFor(p) : null;
        if (picker) {
            line.appendChild(picker);
            editor.addEventListener('keydown', e => {
                if (e.altKey && e.key === 'ArrowDown') {
                    e.preventDefault();
                    picker.click();
                }
            });
        }
        if (p.reference) {
            line.appendChild(goToButton(p.reference));
        }
        cell.appendChild(line);
        if (p.resolved) {
            const hint = document.createElement('div');
            hint.className = 'resolved';
            hint.textContent = p.resolved;
            cell.appendChild(hint);
        }
        tr.appendChild(cell);
        return tr;
    }

    /** The … button: a label search (and create) or an element search, in VS Code's quick pick. */
    function pickerFor(p) {
        const isLabel = LabelProps.test(p.name);
        const types = PickTypes[p.name] || (p.reference && p.reference.objectType && p.reference.objectType !== 'AxLabelFile' ? [p.reference.objectType] : null);
        if (!isLabel && !types) {
            return null;
        }
        const button = document.createElement('button');
        button.className = 'goto codicon codicon-ellipsis';
        button.title = isLabel ? `Find or create a label for ${p.name} (Alt+Down)` : `Pick ${p.name} (Alt+Down)`;
        button.setAttribute('aria-label', button.title);
        button.addEventListener('click', () => {
            if (!selectedPath) return;
            if (isLabel) {
                send('pickLabel', { path: selectedPath, property: p.name, value: p.resolved || p.value });
            } else {
                send('pickReference', { path: selectedPath, property: p.name, objectTypes: types, value: p.value });
            }
        });
        return button;
    }

    document.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f' && lastProps) {
            const filter = document.getElementById('propFilter');
            if (filter) {
                e.preventDefault();
                filter.focus();
                filter.select();
            }
        }
    });

    function goToButton(reference) {
        const button = document.createElement('button');
        button.className = 'goto codicon codicon-go-to-file';
        button.title = reference.objectType === 'AxLabelFile'
            ? `Open label ${reference.labelId} in ${reference.objectName}`
            : `Go to ${reference.objectName} (${reference.objectType.replace(/^Ax/, '')})`;
        button.setAttribute('aria-label', button.title);
        button.addEventListener('click', () => send('goTo', { reference }));
        return button;
    }

    function editorFor(p, canEdit) {
        if (!canEdit) {
            const span = document.createElement('span');
            span.className = 'readonly-value';
            span.textContent = p.value;
            span.title = p.value;
            if (p.reference) {
                span.classList.add('ref-link');
                span.title = `${p.value} (click to open)`;
                span.addEventListener('click', () => send('goTo', { reference: p.reference }));
            }
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
            if (p.reference) {
                input.title = 'Ctrl+click to open';
                input.addEventListener('click', e => {
                    if (e.ctrlKey || e.metaKey) send('goTo', { reference: p.reference });
                });
            }
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
