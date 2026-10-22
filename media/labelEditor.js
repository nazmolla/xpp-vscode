// Label editor webview: renders label entries and sends load/save/delete/copy requests to the extension.
(function () {
    const vscode = acquireVsCodeApi();
    const $ = id => document.getElementById(id);
    let data = null;
    let languages = null;      // null = server default
    let editing = null;        // label ID being edited, '' for a new label
    let searchTimer;

    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const load = () => vscode.postMessage({ type: 'load', search: $('search').value, languages });
    const showError = msg => { $('error').textContent = msg || ''; $('error').hidden = !msg; };
    const reference = id => id.startsWith('@') ? id : '@' + data.labelFileId + ':' + id;

    function render() {
        if (!data) return;
        $('title').textContent = data.labelFileId + ' label file';
        $('badge').textContent = data.editable ? '' : 'read-only';
        $('badge').hidden = data.editable;
        $('add').hidden = !data.editable;
        const langs = data.languages;
        $('labels').querySelector('thead').innerHTML = '<tr><th>Label ID</th>' + langs.map(l => '<th>' + esc(l) + '</th>').join('')
            + '<th>Comment</th><th class="actions"></th></tr>';
        const rows = [];
        if (editing === '') rows.push(editRow({ id: '', texts: {}, comment: '' }, langs, true));
        for (const e of data.entries) {
            rows.push(editing === e.id ? editRow(e, langs, false) : viewRow(e, langs));
        }
        $('labels').querySelector('tbody').innerHTML = rows.join('');
        $('footer').textContent = data.entries.length < data.matches
            ? 'Showing ' + data.entries.length + ' of ' + data.matches + ' matching labels (' + data.total + ' in ' + data.language + '). Refine the search to see others.'
            : data.matches + ' label(s)' + (data.matches < data.total ? ' match (' + data.total + ' in ' + data.language + ')' : '') + '.';
    }

    function viewRow(e, langs) {
        const id = esc(e.id);
        return '<tr data-id="' + id + '"><td class="id">' + id + '</td>'
            + langs.map(l => '<td>' + (e.texts[l] === undefined ? '<span class="missing">(none)</span>' : esc(e.texts[l])) + '</td>').join('')
            + '<td class="comment">' + esc(e.comment) + '</td><td class="actions">'
            + '<button data-act="copy" title="Copy the reference to use in properties and code">Copy @ref</button>'
            + (data.editable ? '<button data-act="edit">Edit</button><button data-act="delete">Delete</button>' : '')
            + '</td></tr>';
    }

    function editRow(e, langs, isNew) {
        const id = esc(e.id);
        return '<tr class="editing" data-id="' + id + '"><td class="id">'
            + (isNew ? '<input data-field="id" placeholder="LabelId" aria-label="Label ID">' : id) + '</td>'
            + langs.map(l => '<td><input data-lang="' + esc(l) + '" value="' + esc(e.texts[l]) + '" aria-label="Text ' + esc(l) + '"></td>').join('')
            + '<td><input data-field="comment" value="' + esc(e.comment) + '" aria-label="Comment"></td>'
            + '<td class="actions"><button data-act="save">Save</button><button data-act="cancel">Cancel</button></td></tr>';
    }

    function save(row) {
        const isNew = editing === '';
        const labelId = isNew ? row.querySelector('[data-field=id]').value.trim() : editing;
        const texts = {};
        row.querySelectorAll('input[data-lang]').forEach(i => { if (i.value.trim()) texts[i.dataset.lang] = i.value; });
        if (!labelId) return showError('Enter a label ID.');
        if (!Object.keys(texts).length) return showError('Enter the text in at least one language.');
        showError('');
        vscode.postMessage({ type: 'save', labelId, texts, comment: row.querySelector('[data-field=comment]').value });
    }

    $('labels').addEventListener('click', ev => {
        const button = ev.target.closest('button');
        const row = ev.target.closest('tr');
        if (!button || !row) return;
        const id = row.dataset.id;
        switch (button.dataset.act) {
            case 'copy': vscode.postMessage({ type: 'copy', text: reference(id) }); break;
            case 'edit': editing = id; render(); break;
            case 'delete': vscode.postMessage({ type: 'delete', labelId: id }); break;
            case 'cancel': editing = null; showError(''); render(); break;
            case 'save': save(row); break;
        }
    });
    $('labels').addEventListener('keydown', ev => {
        const row = ev.target.closest('tr.editing');
        if (!row) return;
        if (ev.key === 'Enter') save(row);
        if (ev.key === 'Escape') { editing = null; render(); }
    });
    $('search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(load, 300); });
    $('add').addEventListener('click', () => { editing = ''; render(); const input = document.querySelector('[data-field=id]'); input && input.focus(); });
    $('refresh').addEventListener('click', load);
    $('properties').addEventListener('click', () => vscode.postMessage({ type: 'properties' }));
    $('languages').addEventListener('click', () => {
        const picker = $('languagePicker');
        if (!picker.hidden) { picker.hidden = true; return; }
        const shown = new Set(data.languages);
        picker.innerHTML = data.allLanguages.filter(l => l !== data.language).map(l =>
            '<label><input type="checkbox" value="' + esc(l) + '"' + (shown.has(l) ? ' checked' : '') + '> ' + esc(l) + '</label>').join('')
            + '<button id="applyLanguages">Show</button>';
        picker.hidden = false;
        $('applyLanguages').addEventListener('click', () => {
            languages = [...picker.querySelectorAll('input:checked')].map(i => i.value);
            picker.hidden = true;
            load();
        });
    });

    window.addEventListener('message', ev => {
        const msg = ev.data;
        if (msg.type === 'data') { data = msg.data; showError(''); render(); }
        else if (msg.type === 'saved') { editing = null; load(); }
        else if (msg.type === 'error') showError(msg.message);
    });
    load();
}());
