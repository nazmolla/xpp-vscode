// Checks the code view's source splitting and the formatter on tricky X++ (no server needed): node scripts/source-test.js
const assert = require('assert');
const path = require('path');
const { composeSource, splitSource, methodSpans } = require(path.join(__dirname, '..', 'out', 'xppSource.js'));
const { formatXpp } = require(path.join(__dirname, '..', 'out', 'codeIntel', 'formatter.js'));

let failures = 0;
function check(name, fn) {
    try {
        fn();
        console.log(`PASS ${name}`);
    } catch (e) {
        failures++;
        console.log(`FAIL ${name}: ${e.message}`);
    }
}

const verbatimMethod = [
    '    public str query()',
    '    {',
    "        str folder = @'C:\\temp\\';",
    "        return @'select * from t",
    "            where t.x = ''}'' and t.y = \"{\"",
    "            // not a comment'; ",
    '    }',
].join('\n');

check('single-quoted verbatim strings', () => {
    const declaration = 'class XvsVerbatim\n{\n}';
    const methods = [{ name: 'query', source: verbatimMethod }, { name: 'other', source: '    public void other()\n    {\n    }' }];
    const text = composeSource(declaration, methods);
    const split = splitSource(text, declaration);
    assert.strictEqual(split.methods.length, 2);
    assert.strictEqual(split.methods[0], verbatimMethod);
    assert.deepStrictEqual(methodSpans(text).map(s => s.name), ['query', 'other']);
});

check('verbatim string in an attribute', () => {
    const declaration = "[SysObsolete(@'C:\\temp\\', false)]\nclass XvsAttr\n{\n}";
    const text = composeSource(declaration, [{ name: 'run', source: '    public void run()\n    {\n    }' }]);
    const split = splitSource(text, declaration);
    assert.strictEqual(split.declaration, declaration);
    assert.deepStrictEqual(split.methods, ['    public void run()\n    {\n    }']);
});

check('interface declarations', () => {
    const declaration = 'public interface XvsIFace\n{\n}';
    const methods = [{ name: 'run', source: '    public void run()\n    {\n    }' }, { name: 'name', source: '    public str name()\n    {\n    }' }];
    const text = composeSource(declaration, methods);
    const split = splitSource(text, declaration);
    assert.strictEqual(split.declaration, declaration);
    assert.deepStrictEqual(split.methods, methods.map(m => m.source));
});

check('formatter keeps single-quoted verbatim strings', () => {
    const source = "void f()\n{\n    str s = @'a {\n  b }\nc';\n}\n";
    assert.strictEqual(formatXpp(source), source);
});

check('methods with tab-indented lines are shown and saved unchanged', () => {
    const declaration = 'class XvsTabs\n{\n}';
    const method = '    public void run()\n    {\n        switch (x)\n        {\n\t\tcase 1:\n\t\t\tbreak;\n        }\n    }';
    const split = splitSource(composeSource(declaration, [{ name: 'run', source: method }]), declaration);
    assert.deepStrictEqual(split.methods, [method]);
});

console.log(`failures: ${failures}`);
process.exit(failures ? 1 : 0);
