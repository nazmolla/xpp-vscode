// Renders a form (the merged control tree from get_form_tree) the way the web client shows it, for the designers.
// Sizes, fonts and colors follow the web client (Finance and Operations, light theme): the action pane card with its tab
// row and button groups, the page header, fast tab cards, table of contents navigation, fields and grids. Hidden controls
// and the extension and configuration key markers only show on request ("Show hidden controls and markers").
window.XppFormPreview = function (options) {
    const activeTab = new Map();
    const activeActionTab = new Map();
    // Fast tabs the user opened or closed; otherwise the first one of a group is open and the rest closed, as in the web client.
    const fastTabOpen = new Map();
    // The menu button or drop dialog button whose list is open.
    let openMenu = null;
    const ButtonTypes = new Set(['Button', 'CommandButton', 'MenuFunctionButton', 'MenuButton', 'DropDialogButton']);
    const InputTypes = new Set(['String', 'Integer', 'Int64', 'Real', 'Date', 'DateTime', 'Time', 'Guid', 'ComboBox', 'CheckBox', 'RadioButton', 'ReferenceGroup', 'SegmentedEntry', 'Window', 'Image']);
    // Captions of command buttons without a label, as the web client shows them.
    const CommandLabels = { New: 'New', DeleteRecord: 'Delete', SaveRecord: 'Save', Restore: 'Refresh', Edit: 'Edit', Copy: 'Copy', Paste: 'Paste', Print: 'Print', Find: 'Find' };
    const SvgNs = 'http://www.w3.org/2000/svg';
    // 16 x 16 outline icons in the web client's style.
    const Icons = {
        back: 'M13.5 8H3M7.5 3.5L3 8l4.5 4.5',
        edit: 'M3 13l.8-3L11 2.8 13.2 5 6 12.2 3 13zM9.8 4l2.2 2.2',
        add: 'M8 2.5v11M2.5 8h11',
        delete: 'M2.5 4.5h11M6 4.5V3h4v1.5M4 4.5l.7 9h6.6l.7-9M6.7 7v4.5M9.3 7v4.5',
        save: 'M3 2.5h8l2.5 2.5v8.5h-11zM5 2.5v3.5h5v-3.5M5 13.5V9.5h6v4',
        search: 'M10 10l4 4M11 6.5a4.5 4.5 0 1 1-9 0 4.5 4.5 0 0 1 9 0z',
        more: 'M3 8h.01M8 8h.01M13 8h.01',
        list: 'M2.5 4h11M2.5 8h11M2.5 12h11',
        chevronDown: 'M4 6l4 4 4-4',
        chevronUp: 'M4 10l4-4 4 4',
        calendar: 'M2.5 3.5h11v10h-11zM2.5 6.5h11M5 2v3M11 2v3',
        refresh: 'M13 3.5V7H9.5M12.6 7A5 5 0 1 0 13 9.5',
        popout: 'M9.5 2.5h4v4M13.5 2.5L7 9M11.5 9.5v4h-9v-9h4',
        office: 'M3 4l6-2 4 1.5v9L9 14l-6-2zM9 2v12M3 12l6-1V5L3 4',
        attach: 'M11 5.5L6.3 10.2a1.5 1.5 0 0 0 2.1 2.1l5-5a3 3 0 0 0-4.2-4.2l-5 5a4.5 4.5 0 0 0 6.4 6.4L13 12',
    };
    let container = null;
    let tree = null;
    let markers = false;
    // DetailsMaster forms: the grid (list) view or the details view of the record. The web client opens them on the list.
    let detailsView = false;
    // A hidden control selected in the designer is shown (dimmed) with its parents, so it can be found.
    let revealed = new Set();
    const parentOf = new Map();

    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text !== undefined && text !== null) e.textContent = text;
        return e;
    }

    function icon(name, className) {
        const svg = document.createElementNS(SvgNs, 'svg');
        svg.setAttribute('viewBox', '0 0 16 16');
        svg.setAttribute('class', 'fo-icon' + (className ? ' ' + className : ''));
        svg.setAttribute('aria-hidden', 'true');
        const path = document.createElementNS(SvgNs, 'path');
        path.setAttribute('d', Icons[name]);
        svg.append(path);
        return svg;
    }

    function caption(node) {
        return node.label || (node.command && CommandLabels[node.command]) || node.dataField || node.dataGroup || node.name;
    }

    function pattern() {
        return (tree.pattern || (tree.root && tree.root.pattern) || '').toLowerCase();
    }

    /** Details patterns open a record in view mode (fields read, underlined); other forms open in edit mode (boxed). */
    function readMode() {
        return /^details/.test(pattern());
    }

    function shown(node) {
        return node.visible !== 'No' || markers || revealed.has(node.name);
    }

    function visibleChildren(node) {
        return node.children.filter(shown);
    }

    function decorate(e, node) {
        e.dataset.name = node.name;
        markNode(e, node);
        e.addEventListener('click', ev => {
            ev.stopPropagation();
            options.onSelect(node.name);
        });
        return e;
    }

    /** Tooltip and designer markers: hidden (Visible No), added by an extension, shown only with a configuration key. */
    function markNode(e, node) {
        const notes = [];
        if (node.addedBy) notes.push(`added by ${node.addedBy}`);
        if (node.configurationKey) notes.push(`configuration key ${node.configurationKey}`);
        e.title = notes.length ? `${node.name} (${notes.join(', ')})` : node.name;
        if (node.addedBy && markers) e.classList.add('added');
        if (node.configurationKey && markers) e.classList.add('keyed');
        if (node.visible === 'No') e.classList.add('hidden');
    }

    function index(node, parent) {
        parentOf.set(node.name, parent);
        node.children.forEach(c => index(c, node));
    }

    // Shows the tab page or view holding a control, so selecting it in the tree brings it into view.
    function reveal(name) {
        let changed = false;
        const path = [];
        for (let n = findNode(tree.root, name); n; n = parentOf.get(n.name)) path.unshift(n);
        if (!path.length) return false;
        const nextRevealed = new Set(markers ? [] : path.some(n => n.visible === 'No') ? path.map(n => n.name) : []);
        if ([...nextRevealed].join('|') !== [...revealed].join('|')) {
            revealed = nextRevealed;
            changed = true;
        }
        for (let i = 0; i < path.length - 1; i++) {
            const node = path[i];
            const child = path[i + 1];
            if (node.type === 'Tab' && isDetailsMain(node)) {
                const wantDetails = (child.style || '') !== 'DetailsFormGrid';
                if (detailsView !== wantDetails) { detailsView = wantDetails; changed = true; }
            } else if (node.type === 'Tab' && !isFastTabs(node)) {
                const i2 = visibleChildren(node).indexOf(child);
                if (i2 >= 0 && (activeTab.get(node.name) || 0) !== i2) { activeTab.set(node.name, i2); changed = true; }
            } else if (node.type === 'TabPage' && fastTabOpen.get(node.name) === false) {
                fastTabOpen.set(node.name, true);
                changed = true;
            } else if (node.type === 'Tab' && isFastTabs(node) && child.type === 'TabPage' && !fastTabOpen.has(child.name)
                && visibleChildren(node).indexOf(child) > 0) {
                fastTabOpen.set(child.name, true);
                changed = true;
            } else if (node.type === 'ActionPane' && child.type === 'ActionPaneTab') {
                const tabs = actionTabs(node);
                const i3 = tabs.indexOf(child);
                if (i3 >= 0 && (activeActionTab.get(node.name) || 0) !== i3) { activeActionTab.set(node.name, i3); changed = true; }
            }
        }
        return changed;
    }

    function findNode(node, name) {
        if (!node) return null;
        if (node.name === name) return node;
        for (const c of node.children) {
            const hit = findNode(c, name);
            if (hit) return hit;
        }
        return null;
    }

    // The web client lays a tab out by its Style, or by the form pattern when the style is Auto.
    function tabStyle(node, depth) {
        const style = (node.style || 'Auto').toLowerCase();
        if (style === 'fasttabs' || style === 'verticaltabs' || style === 'tabs' || style === 'panorama') return style;
        const p = pattern();
        if (depth <= 2 && p.includes('tableofcontents')) return 'verticaltabs';
        if (depth <= 3 && (p.includes('detailsmaster') || p.includes('simpledetails') || p.includes('detailstransaction'))) return 'fasttabs';
        return node.children.some(c => c.children.length > 0 && c.children.every(x => x.type === 'Group' || x.type === 'Tab')) && depth > 2 ? 'fasttabs' : 'tabs';
    }

    function isFastTabs(node) {
        return (node.style || '').toLowerCase() === 'fasttabs' || node._style === 'fasttabs';
    }

    /** The main tab of a details form: one page for the record (DetailsFormDetails), one for the list (DetailsFormGrid). */
    function isDetailsMain(node) {
        return node.type === 'Tab' && node.children.some(p => p.style === 'DetailsFormDetails' || p.style === 'DetailsTransactionDetails')
            && node.children.some(p => p.style === 'DetailsFormGrid' || p.style === 'DetailsTransactionGrid');
    }

    function renderChildren(parent, node, depth) {
        visibleChildren(node).filter(c => !isTitleContainer(c)).forEach(c => parent.append(renderNode(c, depth + 1)));
    }

    /** Detail forms keep the record title fields in a group the web client shows as the page title. */
    function isTitleContainer(node) {
        return node.type === 'Group' && /^DetailTitleContainer$/i.test(node.style || '');
    }

    function findTitleContainer(node) {
        if (isTitleContainer(node)) return node;
        for (const c of node.children) {
            if (c.type === 'Tab' || c.type === 'TabPage' || c.type === 'Group') {
                const hit = findTitleContainer(c);
                if (hit) return hit;
            }
        }
        return null;
    }

    const ContainerTypes = new Set(['Group', 'Tab', 'Grid', 'ListView', 'Tree', 'ButtonGroup', 'ActionPane']);

    /** Child groups sit side by side (wrapping); fields, and groups mixed with fields, stack in one column. */
    function arrangesInRow(kids) {
        return kids.length > 0 && kids.every(c => ContainerTypes.has(c.type));
    }

    function hasDataSource() {
        return Array.isArray(tree.dataSources) && tree.dataSources.length > 0;
    }

    function renderNode(node, depth) {
        if (depth > 40) return el('div', 'muted', '…');
        const t = node.type;
        if (t === 'Design') return renderDesign(node, depth);
        if (t === 'Tab') {
            if (isDetailsMain(node)) return renderDetailsMain(node, depth);
            const style = tabStyle(node, depth);
            node._style = style;
            return style === 'fasttabs' ? renderFastTabs(node, depth)
                : style === 'verticaltabs' ? renderVerticalTabs(node, depth)
                    : renderTabs(node, depth);
        }
        if (t === 'TabPage') {
            const page = decorate(el('div', 'tabpage' + (arrangesInRow(visibleChildren(node)) ? ' row' : '')), node);
            renderChildren(page, node, depth);
            return page;
        }
        if (t === 'Group') return renderGroup(node, depth);
        if (t === 'Grid' || t === 'ListView' || t === 'Tree') return renderGrid(node);
        if (t === 'ActionPane') return renderActionPane(node);
        if (t === 'ButtonGroup' || t === 'ActionPaneTab') {
            const group = decorate(el('div', 'button-row'), node);
            renderChildren(group, node, depth);
            return group;
        }
        if (ButtonTypes.has(t)) {
            const button = decorate(el('button', 'fo-button', caption(node)), node);
            button.type = 'button';
            if (isMenu(node)) wireMenu(button, node);
            return button;
        }
        if (t === 'StaticText') return decorate(el('div', 'static-text', node.label || ''), node);
        if (t === 'Custom' && /quickfilter/i.test(node.name)) return renderQuickFilter(node);
        if (InputTypes.has(t)) return fieldControl(node, true);
        const other = decorate(el('div', 'other'), node);
        other.append(el('span', 'type', t), el('span', null, ' ' + caption(node)));
        renderChildren(other, node, depth);
        return other;
    }

    // ---------- page ----------
    function renderDesign(node, depth) {
        const form = decorate(el('div', 'form' + (readMode() ? ' read-mode' : '')), node);
        const bar = el('div', 'fp-bar');
        const toggle = el('label', 'fp-toggle');
        const box = el('input');
        box.type = 'checkbox';
        box.checked = markers;
        box.addEventListener('change', () => { markers = box.checked; revealed = new Set(); draw(); });
        box.addEventListener('click', ev => ev.stopPropagation());
        toggle.addEventListener('click', ev => ev.stopPropagation());
        toggle.title = 'Shows controls whose Visible is No, outlines what extensions added (green) and what a configuration key controls (amber); the web client hides controls whose key is off in the environment.';
        toggle.append(box, el('span', null, 'Show hidden controls and markers'));
        bar.append(toggle);
        form.append(bar);

        const panes = visibleChildren(node).filter(c => c.type === 'ActionPane');
        if (panes.length) panes.forEach(c => form.append(renderNode(c, depth + 1)));
        else form.append(renderActionPane(null));
        form.append(renderHeader(node));
        const body = el('div', 'form-body');
        visibleChildren(node).filter(c => c.type !== 'ActionPane').forEach(c => body.append(renderNode(c, depth + 1)));
        form.append(body);
        return form;
    }

    function renderHeader(design) {
        const header = el('div', 'form-header');
        const formCaption = tree.caption || tree.form;
        const p = pattern();
        const main = design.children.find(isDetailsMain);
        const viewSelector = () => {
            const view = el('span', 'fo-view', 'Standard view');
            view.append(icon('chevronDown', 'fo-chevron'));
            return view;
        };
        if (main && detailsView) {
            const crumb = el('div', 'fo-breadcrumb');
            const link = el('a', 'fo-link', formCaption);
            link.href = '#';
            link.addEventListener('click', ev => { ev.preventDefault(); ev.stopPropagation(); detailsView = false; draw(); });
            crumb.append(link, el('span', 'fo-sep', '|'), viewSelector());
            header.append(crumb, el('div', 'fo-title', formCaption));
        } else if (main || p.includes('listpage') || p.includes('simplelist')) {
            const top = el('div', 'fo-caption', formCaption);
            const title = el('div', 'fo-title');
            title.append(viewSelector());
            header.append(top, title);
        } else {
            const top = el('div', 'fo-caption');
            top.append(viewSelector());
            header.append(top, el('div', 'fo-title', formCaption));
        }
        return header;
    }

    // ---------- action pane ----------
    function actionTabs(pane) {
        return pane ? visibleChildren(pane).filter(c => c.type === 'ActionPaneTab') : [];
    }

    /** Buttons that sit on the action pane itself (not in a tab): the web client shows them in its top row. */
    function rootButtons(pane) {
        if (!pane) return [];
        const result = [];
        for (const c of visibleChildren(pane).filter(x => x.type !== 'ActionPaneTab')) {
            if (ButtonTypes.has(c.type)) result.push(c);
            else if (c.type === 'ButtonGroup') result.push(...visibleChildren(c).filter(b => ButtonTypes.has(b.type)));
        }
        return result;
    }

    function commandIcon(button) {
        const text = `${button.command || ''} ${button.name}`.toLowerCase();
        if (button.command === 'New' || /^new|\bnew/.test(button.name.toLowerCase())) return 'add';
        if (button.command === 'DeleteRecord' || /delete/.test(text)) return 'delete';
        if (button.command === 'SaveRecord') return 'save';
        return null;
    }

    function toolbarButton(iconName, text, title) {
        const b = el('button', 'ap-cmd');
        b.type = 'button';
        b.title = title || text || '';
        if (iconName) b.append(icon(iconName));
        if (text) b.append(el('span', null, text));
        return b;
    }

    function renderActionPane(pane) {
        const card = pane ? decorate(el('div', 'actionpane'), pane) : el('div', 'actionpane');
        const row = el('div', 'ap-toolbar');
        const main = tree.root.children.find(isDetailsMain);
        const back = toolbarButton('back', null, main && detailsView ? 'Back to the list' : 'Back');
        back.addEventListener('click', ev => {
            if (main && detailsView) { ev.stopPropagation(); detailsView = false; draw(); }
        });
        row.append(back, el('span', 'ap-divider'));
        if (main && detailsView) row.append(toolbarButton('list', null, 'Show list'), el('span', 'ap-divider'));
        if (hasDataSource() && readMode()) row.append(toolbarButton('edit', 'Edit'));
        else if (hasDataSource() && !pattern().includes('dialog')) row.append(toolbarButton('save', 'Save'));
        for (const b of rootButtons(pane)) {
            const cmd = decorate(toolbarButton(commandIcon(b), caption(b)), b);
            if (isMenu(b)) wireMenu(cmd, b);
            row.append(cmd);
        }
        const tabs = actionTabs(pane);
        const active = Math.min(activeActionTab.get(pane ? pane.name : '') || 0, Math.max(0, tabs.length - 1));
        if (tabs.length && !row.lastChild.classList.contains('ap-divider')) row.append(el('span', 'ap-divider'));
        tabs.forEach((tab, i) => {
            const header = el('button', 'ap-tab' + (i === active ? ' active' : ''));
            header.type = 'button';
            header.append(el('span', 'ap-tab-label', caption(tab)));
            header.dataset.name = tab.name;
            markNode(header, tab);
            header.addEventListener('click', ev => {
                ev.stopPropagation();
                activeActionTab.set(pane.name, i);
                draw();
                options.onSelect(tab.name);
            });
            row.append(header);
        });
        if (!tabs.length) row.append(el('button', 'ap-tab', 'Options'));
        row.append(toolbarButton('search', null, 'Search for an action'), toolbarButton('more', null, 'More'));
        const right = el('span', 'ap-right');
        right.append(toolbarButton('office', null, 'Open in Microsoft Office'), toolbarButton('attach', null, 'Attachments'), toolbarButton('refresh', null, 'Refresh'), toolbarButton('popout', null, 'Open in new window'));
        row.append(right);
        card.append(row);

        const tab = tabs[active];
        if (tab) {
            const flyout = el('div', 'ap-flyout');
            const content = decorate(el('div', 'ap-content'), tab);
            const loose = [];
            const flush = () => {
                if (loose.length) content.append(renderButtonGroup(null, loose.splice(0)));
            };
            for (const c of visibleChildren(tab)) {
                if (c.type === 'ButtonGroup') {
                    flush();
                    content.append(renderButtonGroup(c, visibleChildren(c)));
                } else {
                    loose.push(c);
                }
            }
            flush();
            flyout.append(content, toolbarButton('chevronUp', null, 'Collapse'));
            flyout.lastChild.classList.add('ap-collapse');
            card.append(flyout);
        }
        return card;
    }

    function renderButtonGroup(group, buttons) {
        const box = group ? decorate(el('div', 'ap-group'), group) : el('div', 'ap-group');
        box.append(el('div', 'ap-group-title', group ? group.label || '' : ''));
        const columns = el('div', 'ap-group-content');
        buttons.forEach(b => {
            const button = decorate(el('button', 'ap-button'), b);
            button.type = 'button';
            button.append(el('span', null, caption(b)));
            if (isMenu(b)) wireMenu(button, b);
            columns.append(button);
        });
        box.append(columns);
        return box;
    }

    // ---------- drop-down buttons ----------
    function isMenu(node) {
        return node.type === 'MenuButton' || node.type === 'DropDialogButton';
    }

    /** Menu buttons and drop dialog buttons open their list below them, as in the web client. */
    function wireMenu(button, node) {
        button.dataset.menu = node.name;
        button.setAttribute('aria-haspopup', 'true');
        button.setAttribute('aria-expanded', String(openMenu === node.name));
        if (!button.querySelector('.fo-chevron')) button.append(icon('chevronDown', 'fo-chevron'));
        button.addEventListener('click', () => {
            openMenu = openMenu === node.name ? null : node.name;
            draw();
        });
    }

    function renderOpenMenu(form) {
        if (!openMenu) return;
        const node = findNode(tree.root, openMenu);
        const anchor = form.querySelector(`[data-menu="${CSS.escape(openMenu)}"]`);
        if (!node || !anchor) { openMenu = null; return; }
        const menu = el('div', 'fo-menu');
        menu.setAttribute('role', 'menu');
        const addItems = (parent, level) => visibleChildren(parent).forEach(c => {
            const item = decorate(el('button', 'fo-menu-item'), c);
            item.type = 'button';
            item.setAttribute('role', 'menuitem');
            item.style.paddingLeft = `${10 + level * 12}px`;
            item.append(el('span', null, caption(c)));
            menu.append(item);
            if (isMenu(c)) addItems(c, level + 1);
        });
        addItems(node, 0);
        if (!menu.children.length) {
            // A drop dialog shows the form its menu item opens; the preview names it.
            menu.append(el('div', 'fo-menu-note', node.type === 'DropDialogButton' ? `Opens ${caption(node)} as a drop dialog` : 'No items'));
        }
        const formBox = form.getBoundingClientRect();
        const box = anchor.getBoundingClientRect();
        menu.style.left = `${box.left - formBox.left}px`;
        menu.style.top = `${box.bottom - formBox.top}px`;
        menu.style.minWidth = `${box.width}px`;
        menu.addEventListener('click', ev => ev.stopPropagation());
        form.append(menu);
    }

    // ---------- details forms ----------
    function renderDetailsMain(node, depth) {
        const wanted = detailsView ? ['DetailsFormDetails', 'DetailsTransactionDetails'] : ['DetailsFormGrid', 'DetailsTransactionGrid'];
        const page = node.children.find(p => wanted.includes(p.style)) || node.children[0];
        const view = decorate(el('div', 'details-view'), node);
        if (page) {
            const content = decorate(el('div', 'tabpage' + (detailsView ? '' : ' grid-view')), page);
            renderChildren(content, page, depth + 1);
            view.append(content);
            if (!detailsView) {
                content.addEventListener('dblclick', ev => { ev.stopPropagation(); detailsView = true; draw(); });
            }
        }
        return view;
    }

    // ---------- tabs ----------
    function renderTabs(node, depth) {
        const tab = decorate(el('div', 'tab'), node);
        const pages = visibleChildren(node);
        const headers = el('div', 'tab-headers');
        const active = Math.min(activeTab.get(node.name) || 0, Math.max(0, pages.length - 1));
        pages.forEach((p, i) => headers.append(tabHeader(node, p, i, i === active, 'tab-header')));
        tab.append(headers);
        if (pages[active]) tab.append(renderNode(pages[active], depth + 1));
        return tab;
    }

    function renderVerticalTabs(node, depth) {
        const tab = decorate(el('div', 'vtabs'), node);
        const pages = visibleChildren(node);
        const nav = el('div', 'vtabs-nav');
        const active = Math.min(activeTab.get(node.name) || 0, Math.max(0, pages.length - 1));
        pages.forEach((p, i) => nav.append(tabHeader(node, p, i, i === active, 'vtab')));
        const body = el('div', 'vtabs-body');
        if (pages[active]) {
            const page = pages[active];
            const content = decorate(el('div', 'vtabs-page'), page);
            // A table of contents page usually holds fast tabs; they are sections of the white panel there.
            visibleChildren(page).forEach(c => {
                const child = renderNode(c, depth + 2);
                if (child.classList.contains('fasttabs')) child.classList.add('in-panel');
                content.append(child);
            });
            body.append(content);
        }
        tab.append(nav, body);
        return tab;
    }

    function tabHeader(tabNode, page, index, active, className) {
        const h = el('div', className + (active ? ' active' : ''), caption(page));
        h.dataset.name = page.name;
        markNode(h, page);
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
        visibleChildren(node).forEach((page, index) => {
            const open = fastTabOpen.has(page.name) ? fastTabOpen.get(page.name) : index === 0;
            const section = decorate(el('section', 'fasttab' + (open ? ' open' : '')), page);
            const header = el('div', 'fasttab-header');
            const toggle = el('span', 'fasttab-toggle');
            toggle.append(icon(open ? 'chevronUp' : 'chevronDown'));
            header.append(el('span', 'fasttab-caption', caption(page)), fastTabSummary(page), toggle);
            header.addEventListener('click', ev => {
                ev.stopPropagation();
                fastTabOpen.set(page.name, !open);
                draw();
                options.onSelect(page.name);
            });
            section.append(header);
            if (open) {
                const body = el('div', 'fasttab-body');
                renderChildren(body, page, depth + 1);
                section.append(body);
            }
            tab.append(section);
        });
        return tab;
    }

    /** The header values of a fast tab: its fields marked FastTabSummary, blank ("--") without data. */
    function fastTabSummary(page) {
        const summary = el('span', 'fasttab-summary');
        const fields = [];
        const collect = n => visibleChildren(n).forEach(c => { if (c.fastTabSummary) fields.push(c); collect(c); });
        collect(page);
        fields.forEach(f => {
            const value = el('span', 'fasttab-summary-value', '--');
            value.title = `${caption(f)}: (blank)`;
            value.dataset.summaryFor = f.name;
            summary.append(value);
        });
        return summary;
    }

    // ---------- groups and fields ----------
    function renderGroup(node, depth) {
        const declared = parseInt(node.columns, 10);
        const columns = Math.max(1, declared || 0);
        const kids = visibleChildren(node);
        if (/^CustomFilter$/i.test(node.style || '')) {
            // The list page filter row: quick filter and filter fields side by side.
            const filters = decorate(el('div', 'custom-filter'), node);
            kids.forEach(c => filters.append(renderNode(c, depth + 1)));
            return filters;
        }
        // A group of groups sits side by side when its Columns is Auto (-1) or more than one; with one column it stacks.
        // ArrangeMethod HorizontalLeft/Right/Wrap puts every child in one row (Vertical is the default and says nothing).
        const horizontal = /^Horizontal/.test(node.arrangeMethod || '');
        const row = horizontal || (arrangesInRow(kids) && (declared < 0 || declared > 1));
        const box = decorate(el('div', 'group' + (row && !horizontal ? ' group-container' : '')), node);
        if (node.label && (node.frameType || '').toLowerCase() !== 'none') box.append(el('div', 'group-caption', node.label));
        const body = el('div', 'group-body' + (row ? ' row' : ''));
        if (node.arrangeMethod === 'HorizontalRight') body.style.justifyContent = 'flex-end';
        if (horizontal && node.arrangeMethod !== 'HorizontalWrap') body.style.flexWrap = 'nowrap';
        if (columns > 1 && !row) body.style.columnCount = String(columns);
        applySize(box, node);
        kids.filter(c => !isTitleContainer(c)).forEach(c => body.append(renderNode(c, depth + 1)));
        if (!node.children.length && node.groupFields) {
            node.groupFields.forEach(f => body.append(fieldControl({ name: node.name, label: f.label, fieldKind: f.fieldKind, type: '' }, false)));
        }
        if (!kids.length && !node.groupFields && markers) body.append(el('span', 'muted', 'empty'));
        box.append(body);
        return box;
    }

    function renderQuickFilter(node) {
        const box = decorate(el('div', 'quick-filter'), node);
        box.append(icon('search'), el('span', 'quick-filter-text', 'Filter'));
        return box;
    }

    // ---------- size properties ----------
    /** Characters of DisplayLength to pixels, as the web client sizes a field (about 7.4 px a character plus padding). */
    function charactersToPixels(characters) {
        return Math.round(characters * 7.4 + 15);
    }

    /** Width and Height in Manual mode are pixels; SizeToAvailable fills the space left in the container. */
    function applySize(e, node) {
        if (node.widthMode === 'Manual' && node.width > 0) {
            e.style.width = `${node.width}px`;
            e.style.minWidth = '0';
        } else if (node.widthMode === 'SizeToAvailable') {
            e.classList.add('fill-width');
        }
        if (node.heightMode === 'Manual' && node.height > 0) {
            e.style.height = `${node.height}px`;
            e.style.overflow = 'hidden';
        }
    }

    function renderGrid(node) {
        const grid = decorate(el('div', 'grid'), node);
        const table = el('table');
        const head = el('tr');
        const check = el('th', 'grid-check');
        check.append(el('span', 'grid-circle'));
        head.append(check);
        const columns = visibleChildren(node);
        columns.forEach(c => head.append(decorate(el('th', c.fieldKind === 'number' ? 'num' : null, caption(c)), c)));
        if (!columns.length) head.append(el('th', 'muted', node.type));
        table.append(head);
        // Placeholder rows: the first column is the record's link, as in the web client. VisibleRows fixes the count; a grid
        // that sizes to the available height shows more.
        const widths = [64, 48, 72, 40, 56];
        const rows = node.visibleRows > 0 ? Math.min(node.visibleRows, 30) : node.heightMode === 'SizeToAvailable' ? 8 : 4;
        for (let r = 0; r < rows; r++) {
            const row = el('tr');
            const circle = el('td', 'grid-check');
            circle.append(el('span', 'grid-circle'));
            row.append(circle);
            (columns.length ? columns : [null]).forEach((c, i) => {
                const cell = el('td', c && c.fieldKind === 'number' ? 'num' : null);
                const bar = el('span', 'grid-bar' + (i === 0 ? ' link' : ''));
                bar.style.width = `${widths[(r + i) % widths.length]}%`;
                if (i === 0) {
                    bar.title = 'Open the record';
                    bar.addEventListener('click', ev => {
                        // On a details form's list, the record link opens the details view, as in the web client.
                        if (tree.root.children.some(isDetailsMain) && !detailsView) {
                            ev.stopPropagation();
                            detailsView = true;
                            draw();
                        }
                    });
                }
                cell.append(bar);
                row.append(cell);
            });
            if (r === 0) row.classList.add('current');
            table.append(row);
        }
        grid.append(table);
        if (node.widthMode === 'Manual' || node.heightMode === 'Manual') applySize(grid, node);
        return grid;
    }

    function fieldControl(node, selectable) {
        const kind = node.fieldKind || (node.type === 'CheckBox' ? 'toggle'
            : node.type === 'Real' || node.type === 'Integer' || node.type === 'Int64' ? 'number'
                : node.type === 'Date' ? 'date'
                    : node.type === 'DateTime' ? 'datetime'
                        : node.type === 'ComboBox' ? 'enum' : 'text');
        const field = el('div', 'field');
        if (selectable) decorate(field, node);
        if (node.showLabel !== 'No') field.append(el('label', null, node.label || (selectable ? '' : node.name)));
        if (kind === 'toggle') {
            const toggle = el('div', 'toggle-row');
            const box = el('span', 'toggle');
            box.append(el('span', 'toggle-thumb'));
            toggle.append(box, el('span', 'toggle-text', 'No'));
            field.append(toggle);
        } else if (kind === 'number') {
            field.append(el('div', 'input num'));
        } else if (kind === 'date' || kind === 'datetime') {
            const input = el('div', 'input with-icon');
            input.append(icon('calendar'));
            field.append(input);
        } else if (kind === 'enum' || node.type === 'ReferenceGroup' || node.type === 'SegmentedEntry' || kind === 'reference') {
            const input = el('div', 'input with-icon');
            input.append(icon('chevronDown'));
            field.append(input);
        } else if (node.multiLine || node.displayHeight > 1) {
            // A multi-line string is a text box DisplayHeight lines high (three when not set).
            const input = el('div', 'input multiline');
            input.style.height = `${(node.displayHeight > 1 ? node.displayHeight : 3) * 20 + 10}px`;
            field.append(input);
        } else {
            field.append(el('div', 'input'));
        }
        if (node.alignment) {
            const input = field.querySelector('.input');
            if (input) input.style.justifyContent = node.alignment === 'Right' ? 'flex-end' : 'center';
        }
        if (node.displayLength > 0) {
            field.style.width = `${charactersToPixels(node.displayLength)}px`;
        }
        applySize(field, node);
        return field;
    }

    function draw() {
        if (container && tree) {
            parentOf.clear();
            index(tree.root, null);
            const form = renderNode(tree.root, 0);
            container.replaceChildren(form);
            renderOpenMenu(form);
            highlight(options.selectedName ? options.selectedName() : null);
        }
    }

    // A click outside the open list closes it, as in the web client (capture: controls stop the click from bubbling).
    function closeMenuOnOutsideClick(ev) {
        if (openMenu && !(ev.target.closest && (ev.target.closest('.fo-menu') || ev.target.closest('[data-menu]')))) {
            openMenu = null;
            setTimeout(draw, 0);
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
            if (container !== target) {
                if (container) container.removeEventListener('click', closeMenuOnOutsideClick, true);
                target.addEventListener('click', closeMenuOnOutsideClick, true);
            }
            container = target;
            tree = formTree;
            draw();
        },
        select(name) {
            if (tree && reveal(name)) draw();
            else highlight(name);
        },
        /** Shows controls whose Visible is No and marks what extensions added, as the designer did before. */
        setMarkers(on) {
            markers = !!on;
            revealed = new Set();
            draw();
        },
    };
};
