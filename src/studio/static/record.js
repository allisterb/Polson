// What the run record says, as words and as HTML: shared by the developer run page and the client request page.
//
// Both pages read the same stream (/runs/<id>/events), and each used to keep its own copy of how an event is
// described. A fix to one never reached the other: an Antigravity run that ended incomplete read as completed on
// the developer page long after the client page had learned to say otherwise. One copy, here, loaded by both.
//
// A classic script, not a module: it sets `window.PolsonRecord` and has no dependencies.
(function () {
  'use strict';

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // region Markdown
  // Agents write Markdown and these pages show HTML. **Escape first, then add only tags made here**: nothing the
  // agent writes can produce markup of its own, which matters because its text can echo a visitor's brief. It
  // covers what agents write (headings, paragraphs, emphasis, code, lists, quotes, tables, rules, http links)
  // rather than all of CommonMark; anything else stays readable as text, never as broken markup.

  var SAFE_URL = /^https?:\/\/[^\s<>"']+$/i;

  // Inline formatting of one escaped run of text. Code spans are lifted out first so their contents are never
  // read as emphasis, and put back last.
  function inline(text) {
    var codes = [];
    var s = esc(String(text == null ? '' : text).replace(/\u0000/g, ''));
    s = s.replace(/`([^`\n]+)`/g, function (_, code) { codes.push(code); return '\u0000' + (codes.length - 1) + '\u0000'; });
    s = s.replace(/\[([^\]\n]+)\]\(([^)\s]+)\)/g, function (all, label, url) {
      var real = url.replace(/&amp;/g, '&');
      return SAFE_URL.test(real) ? '<a href="' + url + '" target="_blank" rel="noopener noreferrer">' + label + '</a>' : all;
    });
    s = s.replace(/\*\*(?=\S)([^*\n]+?)\*\*/g, '<strong>$1</strong>')
         .replace(/(^|[^\w])__(?=\S)([^_\n]+?)__(?=[^\w]|$)/g, '$1<strong>$2</strong>')
         .replace(/(^|[^*\w])\*(?=\S)([^*\n]+?)\*(?![*\w])/g, '$1<em>$2</em>')
         .replace(/(^|[^\w])_(?=\S)([^_\n]+?)_(?=[^\w]|$)/g, '$1<em>$2</em>')
         .replace(/~~(?=\S)([^~\n]+?)~~/g, '<del>$1</del>');
    return s.replace(/\u0000(\d+)\u0000/g, function (_, i) { return '<code>' + codes[+i] + '</code>'; });
  }

  var FENCE = /^\s*(```|~~~)/;
  var HEADING = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
  var RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
  var QUOTE = /^\s{0,3}>\s?/;
  var ITEM = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/;
  var DIVIDER = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;

  function cells(line) {
    var t = line.trim().replace(/^\|/, '').replace(/\|$/, '');
    return t.split(/(?<!\\)\|/).map(function (c) { return c.trim().replace(/\\\|/g, '|'); });
  }

  function startsBlock(line, next) {
    return FENCE.test(line) || HEADING.test(line) || RULE.test(line) || QUOTE.test(line) || ITEM.test(line)
      || (line.indexOf('|') >= 0 && next !== undefined && DIVIDER.test(next));
  }

  function markdown(src) {
    var lines = String(src == null ? '' : src).replace(/\r\n?/g, '\n').split('\n');
    var out = [], i = 0, m;
    while (i < lines.length) {
      var line = lines[i];
      if (!line.trim()) { i++; continue; }

      if ((m = FENCE.exec(line))) {
        var fence = m[1], code = [];
        for (i++; i < lines.length && lines[i].trim().indexOf(fence) !== 0; i++) code.push(lines[i]);
        i++;
        out.push('<pre><code>' + esc(code.join('\n')) + '</code></pre>');
        continue;
      }
      if ((m = HEADING.exec(line))) {
        // Headings in a transcript are an agent's section titles, not a page's: kept small.
        var level = Math.min(6, m[1].length + 3);
        out.push('<h' + level + '>' + inline(m[2]) + '</h' + level + '>');
        i++; continue;
      }
      if (RULE.test(line)) { out.push('<hr>'); i++; continue; }
      if (line.indexOf('|') >= 0 && i + 1 < lines.length && DIVIDER.test(lines[i + 1])) {
        var head = cells(line), body = [];
        for (i += 2; i < lines.length && lines[i].indexOf('|') >= 0 && lines[i].trim(); i++) body.push(cells(lines[i]));
        out.push('<table><thead><tr>' + head.map(function (c) { return '<th>' + inline(c) + '</th>'; }).join('') +
                 '</tr></thead><tbody>' + body.map(function (r) {
                   return '<tr>' + r.map(function (c) { return '<td>' + inline(c) + '</td>'; }).join('') + '</tr>';
                 }).join('') + '</tbody></table>');
        continue;
      }
      if (QUOTE.test(line)) {
        var quoted = [];
        for (; i < lines.length && QUOTE.test(lines[i]); i++) quoted.push(lines[i].replace(QUOTE, ''));
        out.push('<blockquote>' + markdown(quoted.join('\n')) + '</blockquote>');
        continue;
      }
      if ((m = ITEM.exec(line))) {
        var ordered = /\d/.test(m[2]), items = [], indent = m[1].length;
        while (i < lines.length) {
          var it = ITEM.exec(lines[i]);
          if (it && it[1].length <= indent + 1 && /\d/.test(it[2]) === ordered) { items.push([it[3]]); i++; continue; }
          // A continuation, or a nested item: kept with the item above, as text.
          if (lines[i].trim() && /^\s+/.test(lines[i]) && items.length) { items[items.length - 1].push(lines[i].trim().replace(/^([-*+]|\d{1,9}[.)])\s+/, '• ')); i++; continue; }
          break;
        }
        var tag = ordered ? 'ol' : 'ul';
        var first = ordered ? parseInt(m[2], 10) : 1;
        out.push('<' + tag + (ordered && first !== 1 ? ' start="' + first + '"' : '') + '>' +
                 items.map(function (parts) { return '<li>' + parts.map(inline).join('<br>') + '</li>'; }).join('') +
                 '</' + tag + '>');
        continue;
      }

      var para = [];
      for (; i < lines.length && lines[i].trim() && !(para.length && startsBlock(lines[i], lines[i + 1])); i++) para.push(lines[i].trim());
      out.push('<p>' + para.map(inline).join(' ') + '</p>');
    }
    return out.join('');
  }
  // endregion

  // region Describing events
  function motionNote(e) {
    var count = function (v, unit) { return Number.isFinite(Number(v)) && v !== null && v !== '' ? Number(v) + ' ' + unit : ''; };
    var secs = Number(e.durationMs) / 1000;
    var len = Number.isFinite(secs) && secs > 0 ? secs.toFixed(1) + 's' : '';
    if (e.motion === 'animation') return ['▶ ' + (len || 'animation'), count(e.frames, 'frames')].filter(Boolean).join(' · ');
    if (e.motion === 'video') return ['▶ ' + (len || 'video'), count(e.frames, 'frames'),
      Array.isArray(e.audio) && e.audio.length ? 'with sound' : 'silent'].filter(Boolean).join(' · ');
    if (e.motion === 'svg') return ['▶ ' + (len || 'animation'), 'animated svg'].join(' · ');
    if (e.motion === 'sheet') return ['contact sheet', count(e.cells, 'cells')].filter(Boolean).join(' · ');
    return '';
  }

  // A tool call, with the argument that says what it was actually doing.
  //
  // The name alone was almost no information: a row reading `Bash` is indistinguishable from any other `Bash`, so a
  // call that has been running for twenty minutes looks like every call that returned in forty milliseconds.
  // Which argument matters is per tool, and guessing wrongly is worse than showing none.
  var TOOL_ARG = {
    Bash: 'command', PowerShell: 'command',
    Read: 'file_path', Write: 'file_path', Edit: 'file_path', NotebookRead: 'file_path',
    Glob: 'pattern', Grep: 'pattern',
    WebFetch: 'url', WebSearch: 'query',
    Agent: 'description', Task: 'description',
    ask_director: 'question'
  };

  // { tool, arg }: the name without its MCP prefix, and the one argument worth showing, on one line.
  function toolParts(e) {
    var tool = String(e.tool || '').replace(/^mcp__[^_]+__/, '');
    var args = e.args || {};
    var shown = args[TOOL_ARG[tool]];
    if (shown == null) shown = args.scriptFile != null ? args.scriptFile : args.outFile != null ? args.outFile
      : args.query != null ? args.query : args.name;
    // An inline script's first line is usually its own title comment.
    if (shown == null && typeof args.script === 'string' && !/^<\d+ chars>$/.test(args.script)) {
      shown = (args.script.split('\n').map(function (l) { return l.trim(); }).filter(Boolean)[0] || '')
        .replace(/^\/\/\s*|^\/\*+\s*|\s*\*\/$/g, '');
    }
    if (shown == null) {
      var values = Object.keys(args).map(function (k) { return args[k]; }).filter(function (v) { return typeof v === 'string'; });
      if (values.length === 1) shown = values[0];
    }
    var flat = shown == null ? '' : String(shown).replace(/\s+/g, ' ').trim();
    // The record keeps a long argument as its length only: "<438 chars>" is how a whole script is noted.
    var sized = /^<(\d+) chars>$/.exec(flat);
    if (sized) flat = (tool === 'ExecuteScript' ? 'a script of ' : '') + Number(sized[1]).toLocaleString() + ' characters';
    return { tool: tool, arg: flat.length > 160 ? flat.slice(0, 160) + '…' : flat };
  }

  function toolDetail(e) {
    var p = toolParts(e);
    return p.arg ? p.tool + '  ' + p.arg : p.tool;
  }

  // How a run ended, from the agent's record. **Two producers.** `transcript.py` ends a run with `reason` -
  // completed / failed / halted, the last carrying `limit`, `used` and `cap`. `run.py` ends one with `status` -
  // ok / incomplete - and an `error`, and no `reason`. Keyed only on `reason`, an Antigravity run that ended
  // incomplete rendered as completed. `ok` is the verdict; `text` says it.
  function runEnd(e) {
    if (e.reason === 'halted') {
      var tokens = e.limit === 'tokens';
      var used = e.used == null ? null : (tokens ? (+e.used).toLocaleString() + ' input tokens' : (+e.used / 60).toFixed(0) + ' min');
      var cap = e.cap == null ? null : (tokens ? (+e.cap).toLocaleString() : (+e.cap / 60).toFixed(0) + ' min');
      return { ok: false, text: used && cap ? 'halted — ' + used + ' against a cap of ' + cap : 'halted' };
    }
    if (e.reason === 'failed') return { ok: false, text: e.error || 'failed' };
    if (e.reason === 'completed' && !e.error) return { ok: true, text: 'completed' };
    if (e.status && e.status !== 'ok' && e.status !== 'done') return { ok: false, text: e.error ? e.status + ' — ' + e.error : e.status };
    return e.error ? { ok: false, text: e.error } : { ok: true, text: 'completed' };
  }

  // One event as a line of text. The developer page shows it as each row's detail; the client page uses it where
  // its own wording does not differ.
  function detail(kind, e) {
    if (kind === 'note' || kind === 'message') return e.message || e.text || '';
    if (kind === 'render') return (e.artifact || '') + (motionNote(e) ? '  ' + motionNote(e) : '');
    if (kind === 'export' || kind === 'artifact.read') return e.artifact || '';
    // The host stored the block signed and empty: the agent did reason here, and its words were not kept.
    if (kind === 'thinking' && e.redacted && !e.text) return '(reasoning not stored by this host)';
    if (kind === 'inspect') return Object.entries(e.probes || {}).map(function (p) { return p[0] + ' ' + p[1]; }).join(', ');
    if (kind === 'tool.call') return toolDetail(e);
    if (kind === 'script.error') return e.error || e.script || '';
    if (kind === 'script.refused') return (e.source ? e.source + ' — ' : '') + (e.error || '');
    if (kind === 'run.error') return (e.error || '') + (e.traceback ? '\n\n' + e.traceback : '');
    if (kind === 'stage.begin' || kind === 'stage.end') return e.stage || '';
    if (kind === 'question.open') return e.text || '';
    // **Two unrelated producers write `budget`, so branch on shape.** `studio.py` warns when an allowance threshold
    // is crossed (`limit`, `share`, `spent`, `cap`); `DrawingMcpTools` sends a requisition snapshot (`total`,
    // `spent`, `remaining`, `cacheHits`, `tokensSpent`). Read as the first, the second rendered as
    // `0% of the token budget - 1 of NaN`.
    if (kind === 'budget') {
      if (e.limit) {
        var pct = Math.round((e.share || 0) * 100);
        return e.limit === 'time'
          ? pct + '% of the time budget — ' + (+e.spent).toFixed(1) + ' of ' + (+e.cap).toFixed(0) + ' min'
          : pct + '% of the token budget — ' + (+e.spent).toLocaleString() + ' of ' + (+e.cap).toLocaleString();
      }
      var parts = [(+e.spent || 0).toLocaleString() + ' of ' + (+e.total || 0).toLocaleString() + ' requisitions'];
      if (e.cacheHits) parts.push((+e.cacheHits).toLocaleString() + ' from cache');
      if (e.tokensSpent) parts.push((+e.tokensSpent).toLocaleString() + ' tokens');
      return parts.join(' — ');
    }
    if (kind === 'stream.lag' || kind === 'stream.truncated') return e.dropped + ' not shown';
    // A check is the one row that can be wrong, so its verdict leads, and an accepted failure carries its reason.
    if (kind === 'check') {
      var verdict = (e.passed ? 'pass' : e.accepted ? 'failed, kept' : 'FAILED') + (e.judged ? ' (judged by eye)' : '');
      return [verdict, e.claim, e.detail, e.accepted && e.reason ? 'kept because ' + e.reason : null].filter(Boolean).join(' — ');
    }
    // What follows is minutes of nothing, and a reader who does not know that thinks the run has died.
    if (kind === 'research.started') {
      var wait = e.expectSeconds ? ' — up to about ' + Math.round(e.expectSeconds / 60) + ' min, and the record stays quiet until it returns' : '';
      return (e.description || '') + wait;
    }
    if (kind === 'research.completed') return e.description || '';
    if (kind === 'usage') {
      var bits = [];
      if (e.inputTokens != null) bits.push('in ' + e.inputTokens.toLocaleString());
      if (e.cacheReadTokens) bits.push('cached ' + e.cacheReadTokens.toLocaleString());
      if (e.outputTokens != null) bits.push('out ' + e.outputTokens.toLocaleString());
      if (e.thinkingTokens) bits.push('thinking ' + e.thinkingTokens.toLocaleString());
      return bits.join(' · ');
    }
    if (kind === 'run.end') return runEnd(e).text;
    return String(e.text || e.script || e.message || '');
  }
  // endregion

  // region Evidence map
  // Query → reference → choice → what it is for, from studio.direction.evidence_map, drawn into an <svg>. Built with
  // DOM calls rather than markup, because every label is agent-written and textContent cannot become markup. Each
  // edge carries the reference check's verdict; hovering or focusing a box lights its whole path.
  var SVG_NS = 'http://www.w3.org/2000/svg';

  function svgEl(name, attrs, parent) {
    var el = document.createElementNS(SVG_NS, name);
    Object.keys(attrs || {}).forEach(function (k) { el.setAttribute(k, String(attrs[k])); });
    if (parent) parent.appendChild(el);
    return el;
  }

  // The agent's inline marks stripped for a label: `code`, **bold**, *italic*.
  function plain(s) { return String(s || '').replace(/[`*_]/g, '').replace(/\s+/g, ' ').trim(); }
  function clip(s, n) { return s.length > n ? s.slice(0, n - 1).replace(/\s+$/, '') + '…' : s; }

  // A label clipped to its box by measurement: the query column is monospaced and wider per character than any
  // estimate that suits the others. A map drawn while its tab is hidden measures 0, so the estimate stays.
  function fit(text, s, width) {
    var n = Math.floor(width / 6.4);
    text.textContent = clip(s, n);
    while (n > 4 && text.getComputedTextLength() > width) text.textContent = clip(s, --n);
  }

  function evidenceMap(svg, map) {
    // "For" names the piece each choice serves, which only says something when there are several (Film A, Film B); with
    // one, its width goes to the labels instead.
    var several = (map.pieces || []).length > 1;
    var cols = [
      { key: 'queries', head: 'Qloo query', charW: 6.6 },        // monospaced; both measured at 11px
      { key: 'refs', head: 'Reference', charW: 5.2 },
      { key: 'choices', head: 'Choice', charW: 5.2 }
    ].concat(several ? [{ key: 'pieces', head: 'For', charW: 5.2 }] : []);
    var gap = 70, boxH = 20, step = 26, top = 30;
    var label = {
      queries: function (q) { return [q.id + ' · ' + (q.op || 'taste') + (q.label ? ' — ' + q.label : ''), q.known ? '' : ' (not in this run’s record)']; },
      refs: function (r) { return r.kind === 'tag' ? ['“' + r.text + '”', ' (a Qloo tag)'] : [r.text, ' (a title Qloo returned)']; },
      choices: function (c) { return [plain(c.text), '']; },
      // `### Choices` under `## Film A: Tokyo` reads as Film A's: the parent heading first when there is one.
      pieces: function (p) { return [p.parent ? plain(p.parent) + ' · ' + plain(p.title) : plain(p.title), '']; }
    };
    var n = Math.max.apply(null, cols.map(function (c) { return (map[c.key] || []).length; }).concat([1]));
    var height = top + n * step + 8, x = 0;

    // Each column as wide as its longest label needs (within reason), then the width to spare shared equally, so every
    // column has the same room past its longest label and a column of short titles does not sit half empty.
    var avail = 1100 - gap * (cols.length - 1);
    cols.forEach(function (c) {
      var longest = Math.max.apply(null, (map[c.key] || []).map(function (item) { return label[c.key](item)[0].length; }).concat([8]));
      c.need = Math.min(Math.max(longest * c.charW + 14, 140), 460);
    });
    var spare = (avail - cols.reduce(function (s, c) { return s + c.need; }, 0)) / cols.length;
    cols.forEach(function (c) { c.w = Math.max(Math.round(c.need + spare), 120); c.x = x; x += c.w + gap; });
    var width = x - gap;
    svg.setAttribute('viewBox', '0 0 ' + width + ' ' + height);
    svg.style.minWidth = Math.round(width * 0.75) + 'px';
    Array.prototype.slice.call(svg.querySelectorAll(':scope > g')).forEach(function (g) { g.remove(); });

    // Each column spread over the full height, so a short column sits beside the middle of a long one.
    var pos = {};
    var gEdges = svgEl('g', { 'class': 'evedges' }, svg);
    var gNodes = svgEl('g', { 'class': 'evnodes' }, svg);
    cols.forEach(function (c) {
      svgEl('text', { x: c.x, y: 14, 'class': 'evhead' }, gNodes).textContent = c.head;
      var items = map[c.key] || [];
      var band = (height - top - 8) / Math.max(items.length, 1);
      items.forEach(function (item, i) {
        var y = top + band * (i + 0.5) - boxH / 2;
        pos[item.id] = { x: c.x, y: y, w: c.w };
        var parts = label[c.key](item);
        var g = svgEl('g', { 'class': 'evnode ev-' + c.key + (item.kind === 'tag' ? ' ev-tag' : ''), tabindex: 0, 'data-id': item.id }, gNodes);
        svgEl('title', {}, g).textContent = parts[0] + parts[1] + (item.state ? ' — ' + item.state : '');
        svgEl('rect', { x: c.x, y: y, width: c.w, height: boxH, rx: 3 }, g);
        var text = svgEl('text', { x: c.x + 7, y: y + 14 }, g);
        fit(text, parts[0], c.w - 14);
      });
    });

    var links = (map.edges || []).filter(function (e) { return pos[e.from] && pos[e.to]; });
    var paths = links.map(function (e) {
      var a = pos[e.from], b = pos[e.to];
      var x1 = a.x + a.w, y1 = a.y + boxH / 2, x2 = b.x, y2 = b.y + boxH / 2, mid = (x1 + x2) / 2;
      return svgEl('path', { d: 'M' + x1 + ',' + y1 + ' C' + mid + ',' + y1 + ' ' + mid + ',' + y2 + ' ' + x2 + ',' + y2,
                             'class': 'evedge ev-' + e.state, 'data-from': e.from, 'data-to': e.to }, gEdges);
    });

    // Hover or focus a box: its whole path lit — everything it rests on, and everything that rests on it.
    var down = {}, up = {};
    links.forEach(function (e) { (down[e.from] = down[e.from] || []).push(e.to); (up[e.to] = up[e.to] || []).push(e.from); });
    function reach(start, next, seen) {
      var todo = [start];
      seen[start] = true;
      while (todo.length) (next[todo.pop()] || []).forEach(function (m) { if (!seen[m]) { seen[m] = true; todo.push(m); } });
      return seen;
    }
    function light(id) {
      var on = id ? reach(id, up, reach(id, down, {})) : null;
      svg.classList.toggle('evfocus', !!on);
      Array.prototype.forEach.call(gNodes.querySelectorAll('.evnode'), function (g) { g.classList.toggle('on', !!on && !!on[g.dataset.id]); });
      paths.forEach(function (p) { p.classList.toggle('on', !!on && !!on[p.dataset.from] && !!on[p.dataset.to]); });
    }
    Array.prototype.forEach.call(gNodes.querySelectorAll('.evnode'), function (g) {
      g.addEventListener('mouseenter', function () { light(g.dataset.id); });
      g.addEventListener('focus', function () { light(g.dataset.id); });
      g.addEventListener('mouseleave', function () { light(null); });
      g.addEventListener('blur', function () { light(null); });
    });
  }
  // endregion

  window.PolsonRecord = {
    esc: esc, inline: inline, markdown: markdown, motionNote: motionNote,
    toolParts: toolParts, toolDetail: toolDetail, runEnd: runEnd, detail: detail,
    evidenceMap: evidenceMap, plain: plain
  };
})();
