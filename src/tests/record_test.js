// Checks src/studio/static/record.js under Node: the Markdown renderer's escaping and blocks, and the shared event
// descriptions. Run by tests/test_record_js.py when `node` is on the PATH, or directly: node tests/record_test.js
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const sandbox = { window: {}, Number, String, Object, Array, Math, JSON };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'studio', 'static', 'record.js'), 'utf8'), sandbox);
const R = sandbox.window.PolsonRecord;

let failed = 0, passed = 0;
function eq(name, got, want) {
  if (got === want) { passed++; return; }
  failed++;
  console.log(`FAIL ${name}\n  got:  ${JSON.stringify(got)}\n  want: ${JSON.stringify(want)}`);
}
function has(name, got, part) { if (!got.includes(part)) console.log(`  ${name}: ${JSON.stringify(part)} not in ${JSON.stringify(got)}`); eq(name, got.includes(part), true); }
function lacks(name, got, part) { if (got.includes(part)) console.log(`  ${name}: ${JSON.stringify(part)} found in ${JSON.stringify(got)}`); eq(name, got.includes(part), false); }

// Escaping: nothing the agent writes becomes markup.
lacks('script tag escaped', R.markdown('<script>alert(1)</script>'), '<script>');
has('script tag shown as text', R.markdown('<script>alert(1)</script>'), '&lt;script&gt;');
lacks('img onerror escaped', R.markdown('<img src=x onerror=alert(1)>'), '<img');
lacks('javascript link not linked', R.markdown('[x](javascript:alert(1))'), '<a ');
lacks('quote cannot break out of href', R.markdown('[x](https://a.b/"onmouseover="alert(1))'), '"onmouseover');
has('http link linked', R.markdown('[docs](https://example.com/a?b=1&c=2)'), '<a href="https://example.com/a?b=1&amp;c=2" target="_blank" rel="noopener noreferrer">docs</a>');
lacks('code contents escaped', R.markdown('`<b>x</b>`'), '<b>x');
lacks('fenced code escaped', R.markdown('```\n<script>x</script>\n```'), '<script>');
has('NUL stripped', R.inline('a\u0000b'), 'ab');

// Inline.
eq('bold', R.inline('**My Thought Process**'), '<strong>My Thought Process</strong>');
eq('italic', R.inline('an *aside* here'), 'an <em>aside</em> here');
eq('snake_case untouched', R.inline('read_file and write_script'), 'read_file and write_script');
eq('underscore emphasis', R.inline('a _word_ here'), 'a <em>word</em> here');
eq('code keeps stars', R.inline('`**not bold**`'), '<code>**not bold**</code>');
eq('full stop inside bold', R.inline('**Dr. No** reads'), '<strong>Dr. No</strong> reads');
eq('multiplication left alone', R.inline('2 * 3 * 4'), '2 * 3 * 4');

// Blocks.
eq('heading demoted', R.markdown('# Commission Report'), '<h4>Commission Report</h4>');
eq('paragraphs', R.markdown('one\ntwo\n\nthree'), '<p>one two</p><p>three</p>');
eq('bullets', R.markdown('- a\n- **b**'), '<ul><li>a</li><li><strong>b</strong></li></ul>');
eq('numbered from 3', R.markdown('3. c\n4. d'), '<ol start="3"><li>c</li><li>d</li></ol>');
eq('list after paragraph', R.markdown('Plan:\n- a\n- b'), '<p>Plan:</p><ul><li>a</li><li>b</li></ul>');
eq('nested kept as text', R.markdown('- a\n  - b'), '<ul><li>a<br>• b</li></ul>');
eq('fence', R.markdown('```js\nconst a = 1;\n```'), '<pre><code>const a = 1;</code></pre>');
eq('unclosed fence ends at the end', R.markdown('```\nx'), '<pre><code>x</code></pre>');
eq('quote', R.markdown('> said\n> twice'), '<blockquote><p>said twice</p></blockquote>');
eq('rule', R.markdown('a\n\n---\n\nb'), '<p>a</p><hr><p>b</p>');
eq('table', R.markdown('| A | B |\n|---|---|\n| 1 | **2** |'),
   '<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td><strong>2</strong></td></tr></tbody></table>');
eq('escaped pipe in a cell', R.markdown('| A |\n|---|\n| a \\| b |'), '<table><thead><tr><th>A</th></tr></thead><tbody><tr><td>a | b</td></tr></tbody></table>');
eq('empty', R.markdown(''), '');
eq('null', R.markdown(null), '');

// Shared descriptions.
eq('tool with query', R.toolDetail({ tool: 'mcp__polson__Search', args: { query: 'Skia.Brush' } }), 'Search  Skia.Brush');
eq('tool sized script', R.toolParts({ tool: 'ExecuteScript', args: { script: '<438 chars>' } }).arg, 'a script of 438 characters');
eq('tool inline script title', R.toolParts({ tool: 'ExecuteScript', args: { script: '// Turn 4: Critique\nconst a = 1;' } }).arg, 'Turn 4: Critique');
eq('ask_director shows question', R.toolParts({ tool: 'ask_director', args: { question: 'Where next?', options: ['a'] } }).arg, 'Where next?');
eq('run.end completed', R.runEnd({ reason: 'completed' }).ok, true);
eq('run.end incomplete is not ok', R.runEnd({ status: 'incomplete', error: 'Error 429' }).ok, false);
eq('run.end halted text', R.runEnd({ reason: 'halted', limit: 'tokens', used: 2005223, cap: 2000000 }).text,
   'halted — 2,005,223 input tokens against a cap of 2,000,000');
eq('check kept', R.detail('check', { passed: false, accepted: true, claim: 'contrast', reason: 'display size' }),
   'failed, kept — contrast — kept because display size');

console.log(`${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
