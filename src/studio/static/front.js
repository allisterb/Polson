// The user-facing studio's behaviour. Pages work without it: popovers are <details>, choices are
// real inputs, and every action is a form post. This adds what a server page cannot do alone:
// summaries that follow the choices, file previews, and the live request page.
(function () {
  'use strict';

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }

  var ICON = {
    check: '<path d="M5 12.5l4.5 4.5L19 7"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    document: '<path d="M7 3h7l4 4v14H7zM14 3v4h4M10 12h5M10 16h5"/>',
    download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>'
  };
  function icon(name, size) {
    var s = size || 20;
    var wrap = document.createElement('span');
    wrap.innerHTML = '<svg class="ps-icon" width="' + s + '" height="' + s + '" viewBox="0 0 24 24" aria-hidden="true">' + ICON[name] + '</svg>';
    return wrap.firstChild;
  }

  // region Composer
  var form = document.querySelector('[data-compose]');
  if (form) {
    var pops = Array.prototype.slice.call(form.querySelectorAll('details.fr-pop'));

    function summarise(pop) {
      var out = pop.querySelector('[data-summary]');
      if (!out) return;
      if (pop.hasAttribute('data-audiences')) {
        var who = Array.prototype.map.call(pop.querySelectorAll('[data-audience]'), function (row) {
          var place = row.querySelector('input').value.trim(), age = row.querySelector('select');
          return place ? place + (age.value ? ' (' + age.selectedOptions[0].textContent + ')' : '') : '';
        }).filter(Boolean);
        out.textContent = who.length ? who.join(', ') : 'Who is it for?';
        out.classList.toggle('is-empty', !who.length);
        return;
      }
      var picked = Array.prototype.map.call(pop.querySelectorAll('input:checked'), function (i) {
        return i.closest('[data-label]').getAttribute('data-label');
      });
      var custom = pop.querySelector('[data-custom]');
      if (custom && custom.value.trim()) picked.push(custom.value.trim().replace(/\s*[xX×]\s*/, ' × ') + ' px');
      out.textContent = picked.length ? picked.join(', ') : 'Choose';
      out.classList.toggle('is-empty', !picked.length);
    }

    function syncRoute() {
      var checked = form.querySelector('input[name=route]:checked') || form.querySelector('input[name=route]');
      var route = checked ? checked.value : '0';
      form.querySelectorAll('details[data-route]').forEach(function (d) {
        var on = d.getAttribute('data-route') === route;
        d.hidden = !on;
        var fs = d.querySelector('fieldset');
        if (fs) fs.disabled = !on;
        if (!on) d.open = false;
        // Every route's styles share the name `kind`, so one radio group spans them: the shown block
        // starts from "Let the studio choose" whenever the route changes under it.
        if (on && !d.querySelector('input:checked')) { var first = d.querySelector('input[type=radio]'); if (first) first.checked = true; }
        summarise(d);
      });
    }

    pops.forEach(function (pop) {
      summarise(pop);
      pop.addEventListener('change', function (e) {
        summarise(pop);
        if (e.target.name === 'route') syncRoute();
        if (e.target.type === 'radio') pop.open = false;
      });
      pop.addEventListener('input', function () { summarise(pop); });
      pop.addEventListener('toggle', function () {
        if (!pop.open) return;
        pops.forEach(function (o) { if (o !== pop) o.open = false; });
        // A field near the right edge opens its panel leftwards rather than off the page.
        var panel = pop.querySelector('.fr-panel');
        if (!panel) return;
        panel.style.left = '';
        var over = panel.getBoundingClientRect().right - (document.documentElement.clientWidth - 16);
        if (over > 0) panel.style.left = (-over) + 'px';
      });
    });
    syncRoute();

    // Audiences: every row is in the page so the form works without this; empty rows past the first wait
    // behind "Add another audience".
    var audiences = form.querySelector('[data-audiences]');
    if (audiences) {
      var rows = Array.prototype.slice.call(audiences.querySelectorAll('[data-audience]'));
      var add = audiences.querySelector('[data-add-audience]');
      var syncRows = function () {
        var shown = rows.filter(function (r, i) { return i === 0 || r.querySelector('input').value.trim(); }).length;
        rows.forEach(function (r, i) { r.hidden = i >= shown && !r.querySelector('input').value.trim(); });
        add.hidden = shown >= rows.length;
      };
      add.addEventListener('click', function () {
        var next = rows.filter(function (r) { return r.hidden; })[0];
        if (next) { next.hidden = false; next.querySelector('input').focus(); }
        add.hidden = !rows.some(function (r) { return r.hidden; });
      });
      syncRows();
    }

    document.addEventListener('click', function (e) {
      pops.forEach(function (pop) { if (pop.open && !pop.contains(e.target)) pop.open = false; });
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') pops.forEach(function (pop) { pop.open = false; });
    });

    // Sizes and formats are required groups; the browser cannot require "one of these checkboxes".
    form.addEventListener('submit', function (e) {
      var sizes = form.querySelectorAll('input[name=sizes]:checked').length || form.querySelector('[data-custom]').value.trim();
      var formats = form.querySelectorAll('input[name=formats]:checked').length;
      var who = !audiences || Array.prototype.some.call(audiences.querySelectorAll('input[name=aud_place]'), function (i) { return i.value.trim(); });
      if (!who) { e.preventDefault(); audiences.open = true; audiences.querySelector('input').focus(); return; }
      if (!sizes || !formats) {
        e.preventDefault();
        var pop = (!sizes ? form.querySelector('input[name=sizes]') : form.querySelector('input[name=formats]')).closest('details');
        pop.open = true;
      }
    });

    // The brief counter.
    var brief = form.querySelector('[data-count]'), counter = form.querySelector('[data-counter]');
    if (brief && counter) {
      var count = function () { counter.textContent = brief.value.length + ' / ' + brief.maxLength; };
      brief.addEventListener('input', count); count();
    }

    // Files: one input holds the whole set, rebuilt through DataTransfer so tiles can add and remove.
    var input = form.querySelector('[data-files]'), shelf = form.querySelector('[data-assets]');
    var chosen = [];
    function render() {
      shelf.querySelectorAll('figure').forEach(function (f) { f.remove(); });
      chosen.forEach(function (file, i) {
        var fig = el('figure', 'ps-asset');
        var thumb = el('div', 'ps-asset-thumb');
        if (/^image\//.test(file.type)) {
          var img = el('img'); img.alt = ''; img.src = URL.createObjectURL(file); thumb.appendChild(img);
        } else thumb.appendChild(icon('document', 28));
        var cap = el('figcaption', 'ps-asset-body');
        var name = el('span', 'ps-asset-name', file.name); name.title = file.name;
        cap.appendChild(name);
        cap.appendChild(el('span', 'ps-asset-role', (file.size / 1024 < 1024 ? Math.ceil(file.size / 1024) + ' KB' : (file.size / 1048576).toFixed(1) + ' MB')));
        var rm = el('button', 'ps-asset-remove'); rm.type = 'button'; rm.setAttribute('aria-label', 'Remove ' + file.name);
        rm.appendChild(icon('close', 16));
        rm.addEventListener('click', function () { chosen.splice(i, 1); sync(); });
        fig.appendChild(thumb); fig.appendChild(cap); fig.appendChild(rm);
        shelf.insertBefore(fig, shelf.lastElementChild);
      });
    }
    function sync() {
      try {
        var dt = new DataTransfer();
        chosen.forEach(function (f) { dt.items.add(f); });
        input.files = dt.files;
      } catch (err) { /* older browsers keep the last selection only */ }
      render();
    }
    if (input && shelf) {
      input.addEventListener('change', function () {
        Array.prototype.forEach.call(input.files, function (f) {
          if (!chosen.some(function (c) { return c.name === f.name && c.size === f.size; })) chosen.push(f);
        });
        sync();
      });
    }
  }
  // endregion

  // region Request page
  var page = document.querySelector('[data-request]');
  if (!page || !page.dataset.run) return;

  var base = page.dataset.base, run = page.dataset.run;
  var timeline = page.querySelector('[data-timeline]');
  var viewer = page.querySelector('[data-viewer]'), caption = page.querySelector('[data-caption]');
  var pill = document.getElementById('status-pill');
  var questions = page.querySelector('[data-questions]');
  var stages = [], started = null, finished = false, dirty = false;
  var IMAGE = /\.(webp|png|jpe?g|svg)$/i, VIDEO = /\.(mp4|webm)$/i;

  function artifactUrl(path) {
    return base + '/runs/' + encodeURIComponent(run) + '/artifact/' + path.split('/').map(encodeURIComponent).join('/');
  }
  function clock(ts) {
    if (!started || !ts) return '';
    var s = Math.max(0, Math.round((Date.parse(ts) - started) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }
  function setStatus(status, label) {
    if (!pill) return;
    pill.className = 'ps-pill ps-pill-' + status;
    pill.textContent = label;
  }
  function stage(name) {
    for (var i = 0; i < stages.length; i++) if (stages[i].name === name) return stages[i];
    return null;
  }
  function begin(name, ts) {
    var s = stage(name);
    stages.forEach(function (o) { if (o.state === 'current' && o !== s) o.state = 'done'; });
    if (!s) { s = { name: name, state: 'current', note: '', thumbs: [], time: clock(ts) }; stages.push(s); }
    s.state = 'current';
    return s;
  }
  function show(path) {
    viewer.textContent = '';
    var media;
    if (VIDEO.test(path)) { media = el('video'); media.controls = true; media.muted = true; media.loop = true; media.autoplay = true; }
    else { media = el('img'); media.alt = 'The latest render'; }
    media.src = artifactUrl(path);
    viewer.appendChild(media);
  }

  function draw() {
    dirty = false;
    timeline.textContent = '';
    var list = stages.slice();
    if (finished) list.push({ name: 'Delivered', state: 'done', note: '', thumbs: [] });
    list.forEach(function (s) {
      var li = el('li', 'ps-run-step is-' + s.state);
      if (s.state === 'current') li.setAttribute('aria-current', 'step');
      var mark = el('span', 'ps-run-mark');
      if (s.state === 'done') mark.appendChild(icon('check', 14));
      else if (s.state === 'current') mark.appendChild(el('span', 'ps-run-dot'));
      li.appendChild(mark);
      li.appendChild(el('span', 'ps-run-name', s.name));
      li.appendChild(el('span', 'ps-run-time', s.time || ''));
      if (s.note) li.appendChild(el('p', 'ps-run-note', s.note.length > 220 ? s.note.slice(0, 217) + '…' : s.note));
      if (s.thumbs.length) {
        var row = el('div', 'ps-run-thumbs');
        s.thumbs.slice(-3).forEach(function (p) {
          var img = el('img'); img.alt = ''; img.loading = 'lazy'; img.src = artifactUrl(p);
          img.style.cursor = 'pointer'; img.addEventListener('click', function () { show(p); caption.textContent = s.name; });
          row.appendChild(img);
        });
        li.appendChild(row);
      }
      timeline.appendChild(li);
    });
    if (!list.length) {
      var wait = el('li', 'ps-run-step is-current');
      var m = el('span', 'ps-run-mark'); m.appendChild(el('span', 'ps-run-dot')); wait.appendChild(m);
      wait.appendChild(el('span', 'ps-run-name', 'Reading the brief')); wait.appendChild(el('span', 'ps-run-time', ''));
      timeline.appendChild(wait);
    }
  }
  // A timer rather than requestAnimationFrame: a frame callback never fires in a background tab, and a
  // replayed run then draws nothing until someone looks.
  function later() { if (!dirty) { dirty = true; setTimeout(draw, 30); } }

  function budget(e) {
    if (typeof e.total !== 'number' || !e.total) return;
    var box = page.querySelector('[data-budget]');
    box.hidden = false;
    var f = Math.min(1, (e.spent || 0) / e.total);
    page.querySelector('[data-budget-fill]').style.width = (f * 100).toFixed(1) + '%';
    page.querySelector('[data-budget-value]').textContent = (e.spent || 0) + ' of ' + e.total;
    box.classList.toggle('is-low', f >= 0.9);
  }

  function ask(e) {
    var card = el('section', 'ps-q'); card.id = 'q-' + e.id;
    var from = el('div', 'ps-q-from');
    var p = el('span', 'ps-pill ps-pill-needs-you', 'Needs you'); from.appendChild(p);
    from.appendChild(el('span', null, 'The studio asks'));
    card.appendChild(from);
    card.appendChild(el('h3', 'ps-q-text', e.text || 'The studio has a question'));
    var opts = el('div', 'ps-q-opts'); opts.setAttribute('role', 'radiogroup');
    var choice = null;
    var answerable = page.dataset.answerable === '1';
    // The agent's pick is marked "(Recommended)" in its words, or by convention is listed first.
    var marked = (e.options || []).some(function (t) { return /^\s*\(recommended\)/i.test(t); });
    (e.options || []).forEach(function (text, index) {
      var b = el('button', 'ps-q-opt'); b.type = 'button'; b.setAttribute('role', 'radio'); b.setAttribute('aria-checked', 'false');
      b.appendChild(el('span', 'ps-q-dot'));
      // The agent marks its pick in the option's own words, "(Recommended) …". Shown as the system's tag;
      // the reply still sends the text exactly as asked, because that is what the director matches on.
      var rec = marked ? /^\s*\(recommended\)\s*/i.test(text) : index === 0;
      var body = el('span', 'ps-q-body'); body.appendChild(el('span', 'ps-q-opt-label', text.replace(/^\s*\(recommended\)\s*/i, '')));
      if (rec) body.appendChild(el('span', 'ps-q-rec', 'Recommended'));
      b.appendChild(body);
      b.addEventListener('click', function () {
        opts.querySelectorAll('.ps-q-opt').forEach(function (o) { o.setAttribute('aria-checked', 'false'); });
        b.setAttribute('aria-checked', 'true'); choice = text;
      });
      opts.appendChild(b);
    });
    card.appendChild(opts);

    // A studio watching another host's run cannot settle its questions: show the question, not a button that fails.
    if (!answerable) {
      opts.querySelectorAll('.ps-q-opt').forEach(function (o) { o.disabled = true; o.style.cursor = 'default'; });
      card.appendChild(el('p', 'ps-q-ctx', 'This question is being answered where the run is being driven, not on this page.'));
      questions.appendChild(card);
      setStatus('needs-you', 'Needs you');
      return;
    }

    var own = el('input', 'ps-q-own'); own.placeholder = 'Or answer in your own words'; own.setAttribute('aria-label', 'Your own answer');
    card.appendChild(own);
    var foot = el('div', 'ps-q-foot');
    var skip = el('button', 'ps-btn ps-btn-quiet', 'Let the studio choose'); skip.type = 'button';
    var send = el('button', 'ps-btn ps-btn-primary', 'Send answer'); send.type = 'button';
    foot.appendChild(skip); foot.appendChild(send); card.appendChild(foot);

    function reply(skipped) {
      var body = new URLSearchParams({ question: e.id });
      if (skipped) body.set('skip', '1');
      if (!skipped && choice) body.set('option', choice);
      if (!skipped && own.value.trim()) body.set('text', own.value.trim());
      if (!skipped && !choice && !own.value.trim()) { own.focus(); return; }
      send.disabled = skip.disabled = true;
      fetch(base + '/runs/' + encodeURIComponent(run) + '/answer', { method: 'POST', body: body })
        .then(function (r) {
          if (r.ok) { card.remove(); return; }
          if (r.status === 409) {
            // Settled elsewhere: it timed out, or another window answered first. Not an error to the person.
            foot.textContent = '';
            foot.appendChild(el('span', 'fr-small', 'Already settled: it timed out or was answered in another window.'));
            setTimeout(function () { card.remove(); clearBadge(); }, 4000);
            return;
          }
          send.disabled = skip.disabled = false;
        })
        .catch(function () { send.disabled = skip.disabled = false; });
    }
    skip.addEventListener('click', function () { reply(true); });
    send.addEventListener('click', function () { reply(false); });
    questions.appendChild(card);
    setStatus('needs-you', 'Needs you');
  }

  // Says why a run stopped, once, above the progress list. Whatever was already rendered stays offered.
  function stopped(why) {
    if (page.querySelector('[data-stopped]')) return;
    var note = el('p', 'fr-notice is-failed', 'The run stopped before the studio finished' + (why ? ': ' + (why.length > 160 ? why.slice(0, 157) + '…' : why) : '.') +
                  ' Anything it already made is below; ask for a change to carry on.');
    note.setAttribute('data-stopped', '');
    questions.parentNode.insertBefore(note, questions);
  }

  // The sidebar count is drawn when the page loads; one run at a time means this page's questions are all of it.
  function clearBadge() {
    if (questions.children.length) return;
    var badge = document.querySelector('.ps-nav-badge'); if (badge) badge.remove();
  }

  var stoppedOnce = false;
  function deliver(status) {
    clearBadge();
    // The record has two endings (the server's and the agent's) in either order; a stop is never undone by the other.
    if (status === 'stopped') stoppedOnce = true;
    if (stoppedOnce) status = 'stopped';
    finished = status !== 'stopped';
    var strip = page.querySelector('[data-agent-now]'); if (strip) strip.hidden = true;
    stages.forEach(function (s) { if (s.state === 'current') s.state = finished ? 'done' : s.state; });
    later();
    setStatus(finished ? 'delivered' : 'stopped', finished ? 'Delivered' : 'Stopped');
    pieces();
  }

  // The finished pieces and documents. Also asked for on load when the page already knows the request is
  // finished: a run driven from Claude Code ends without a run.end, so the stream alone never says so.
  function pieces() {
    fetch(base + '/runs/' + encodeURIComponent(run) + '/deliverables').then(function (r) { return r.ok ? r.json() : null; }).then(function (data) {
      if (!data) return;
      var downloads = page.querySelector('[data-downloads]'), docs = page.querySelector('[data-doc-list]');
      downloads.textContent = ''; docs.textContent = '';
      var pieces = data.deliverables.filter(function (d) { return d.kind === 'render'; });

      // One piece per audience (final_a, final_b…) gets a switch; the viewer prefers a looping .webp to a
      // video player, and each piece carries its own downloads.
      function present(d) {
        var media = d.formats.filter(function (f) { return /\.webp$/i.test(f.path); })[0]
                 || d.formats.filter(function (f) { return IMAGE.test(f.path) || VIDEO.test(f.path); })[0];
        if (media) show(media.path);
        var what = d.what || 'the finished piece';
        caption.textContent = what.charAt(0).toUpperCase() + what.slice(1);
        downloads.textContent = '';
        d.formats.forEach(function (f) {
          var a = el('a', 'ps-btn ps-btn-secondary ps-btn-sm'); a.href = artifactUrl(f.path); a.setAttribute('download', '');
          a.appendChild(icon('download', 16)); a.appendChild(document.createTextNode('.' + f.format));
          downloads.appendChild(a);
        });
      }
      var old = page.querySelector('[data-pieces]'); if (old) old.remove();
      if (pieces.length > 1) {
        var tabs = el('div', 'ps-tabs fr-pieces'); tabs.setAttribute('role', 'tablist'); tabs.setAttribute('data-pieces', '');
        pieces.forEach(function (d, i) {
          var letter = (/audience ([A-Z])/i.exec(d.what || '') || [])[1];
          var t = el('button', 'ps-tab', letter ? 'Audience ' + letter.toUpperCase() : 'Piece ' + (i + 1));
          t.type = 'button'; t.setAttribute('role', 'tab'); t.setAttribute('aria-selected', i === 0 ? 'true' : 'false');
          t.addEventListener('click', function () {
            tabs.querySelectorAll('.ps-tab').forEach(function (o) { o.setAttribute('aria-selected', 'false'); });
            t.setAttribute('aria-selected', 'true'); present(d);
          });
          tabs.appendChild(t);
        });
        viewer.parentNode.insertBefore(tabs, viewer);
      }
      if (pieces.length) present(pieces[0]);

      // direction.md is written at the end, so the learned panel is fetched again once the run has delivered.
      fetch(base + '/requests/' + encodeURIComponent(page.dataset.name) + '/learned').then(function (r) { return r.ok ? r.text() : ''; }).then(function (html) {
        var slot = page.querySelector('[data-learned]');
        if (!html || !slot) return;
        var box = document.createElement('div'); box.innerHTML = html;
        var fresh = box.querySelector('[data-learned]'); if (fresh) slot.replaceWith(fresh);
      });

      data.deliverables.forEach(function (d) {
        if (d.kind === 'document') {
          var li = el('li'); var a = el('a', null, d.name);
          a.href = deliverableUrl(d.name) + '?raw=1';
          a.target = '_blank'; li.appendChild(a); li.appendChild(el('span', 'fr-small', ' · ' + d.what));
          // A plain click reads it here; the link stays the raw file, for a new tab, a copy or a save.
          a.addEventListener('click', function (ev) {
            if (ev.button || ev.ctrlKey || ev.metaKey || ev.shiftKey || ev.altKey || !doc) return;
            ev.preventDefault(); read(d, a);
          });
          docs.appendChild(li);
        }
      });
      page.querySelector('[data-documents]').hidden = !docs.children.length;
    });
  }

  // The reading panel: a delivered document opened under the piece. Markdown arrives rendered by the server
  // with raw HTML off, a script highlighted; both are escaped there, so nothing the agent wrote becomes markup.
  var doc = page.querySelector('[data-doc]'), reading = null, asked = 0;
  function deliverableUrl(name) { return base + '/runs/' + encodeURIComponent(run) + '/deliverables/' + encodeURIComponent(name); }
  function closeDoc() {
    doc.hidden = true; reading = null;
    page.querySelectorAll('[data-doc-list] a[aria-current]').forEach(function (o) { o.removeAttribute('aria-current'); });
  }
  function read(d, link) {
    if (reading === d.name) { closeDoc(); return; }
    closeDoc(); reading = d.name; link.setAttribute('aria-current', 'true');
    var md = /\.md$/i.test(d.name), mine = ++asked, body = doc.querySelector('[data-doc-body]');
    doc.querySelector('[data-doc-title]').textContent = d.name;
    doc.querySelector('[data-doc-what]').textContent = d.what ? d.what.charAt(0).toUpperCase() + d.what.slice(1) : '';
    doc.querySelector('[data-doc-raw]').href = deliverableUrl(d.name) + '?raw=1';
    body.className = md ? 'fr-prose' : 'fr-code fr-doc-code'; body.textContent = 'Loading…';
    doc.hidden = false;
    doc.scrollIntoView({ behavior: 'smooth', block: 'start' });
    fetch(deliverableUrl(d.name) + (md ? '?rendered=1' : ''))
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
      .then(function (html) { if (mine === asked) body.innerHTML = html; },
            function () { if (mine === asked) body.textContent = d.name + ' could not be shown here. The Raw link opens the file itself.'; });
  }
  if (doc) doc.querySelector('[data-doc-close]').addEventListener('click', closeDoc);

  var source = new EventSource(base + '/runs/' + encodeURIComponent(run) + '/events');
  function on(type, fn) { source.addEventListener(type, function (m) { try { fn(JSON.parse(m.data)); } catch (err) { /* a malformed line is skipped, as the record reader does */ } }); }

  on('run.start', function (e) { if (!started && e.ts) started = Date.parse(e.ts); });
  on('stage.begin', function (e) { if (!started && e.ts) started = Date.parse(e.ts); begin(e.stage || 'Working', e.ts); later(); });
  on('stage.continue', function (e) { begin(e.stage || 'Working', e.ts); later(); });
  on('stage.end', function (e) { var s = stage(e.stage); if (s) s.state = 'done'; later(); });
  on('note', function (e) { var s = stage(e.stage) || begin(e.stage || 'Working', e.ts); if (e.message) s.note = e.message; later(); });
  on('render', function (e) {
    var path = e.artifact || '';
    if ((e.motion || '').toLowerCase() === 'sheet' || !(IMAGE.test(path) || VIDEO.test(path))) return;
    var s = stage(e.stage) || begin(e.stage || 'Working', e.ts);
    if (IMAGE.test(path)) s.thumbs.push(path);
    if (!finished) { show(path); caption.textContent = 'The latest render · ' + (e.stage || ''); }
    later();
  });
  on('budget', budget);
  on('question.open', ask);
  on('question.closed', function (e) {
    var card = document.getElementById('q-' + e.id); if (card) card.remove();
    if (!questions.children.length && !finished) setStatus('running', 'In the studio');
    clearBadge();
  });
  // The server's run.end says only that the MCP server stopped. The agent's says how the run ended, as
  // `reason` or `status`; anything but a completion is a stop, even when the files were already written.
  // How events are described and the agent's Markdown rendered: shared with the developer run page.
  var R = window.PolsonRecord;
  var failure = '';
  on('run.error', function (e) { failure = String(e.error || '').split('\n')[0]; });
  on('run.end', function (e) {
    var end = R.runEnd(e);
    deliver(end.ok ? 'delivered' : 'stopped');
    if (!end.ok) stopped(failure || String(end.text).split('\n')[0]);
  });
  on('run.closed', function (summary) {
    source.close();
    if (!finished) deliver(summary && summary.status === 'done' ? 'delivered' : 'stopped');
  });

  // region Agent view
  // The developer page's transcript, in this design and from the same stream: what the agent thought, said and
  // did. Token counts, budgets and probe measurements are left to that page; this is the work, not the meter.
  var views = page.querySelector('.fr-views');
  var log = page.querySelector('[data-agent-log]'), empty = page.querySelector('[data-agent-empty]');
  var now = page.querySelector('[data-agent-now]'), nowDid = page.querySelector('[data-agent-did]');
  var nowAgo = page.querySelector('[data-agent-ago]'), count = page.querySelector('[data-agent-count]');
  var firstAt = null, lastAt = null, lastDid = '', rows = 0, lastFold = null, lastFoldKey = null, began = false;

  function select(view) {
    if (!views) return;
    views.querySelectorAll('.ps-tab').forEach(function (t) {
      var on = t.dataset.view === view;
      t.setAttribute('aria-selected', on ? 'true' : 'false');
      t.tabIndex = on ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
    });
    if (view === 'agent' && log) log.scrollTop = log.scrollHeight;
    if (view === 'audiences' || view === 'contrast' || view === 'choices') loadTaste();
    try { history.replaceState(null, '', view !== 'progress' ? '#' + view : location.pathname + location.search); } catch (err) { /* a view, not state */ }
  }
  // A view named in the address, if the page has that tab.
  function viewFromHash() {
    var v = location.hash.slice(1);
    return views && views.querySelector('.ps-tab[data-view="' + v.replace(/[^a-z]/g, '') + '"]') ? v : 'progress';
  }
  if (views) {
    views.addEventListener('click', function (ev) { var t = ev.target.closest('.ps-tab'); if (t) select(t.dataset.view); });
    views.addEventListener('keydown', function (ev) {
      if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') return;
      var tabs = Array.prototype.slice.call(views.querySelectorAll('.ps-tab'));
      var i = tabs.indexOf(document.activeElement); if (i < 0) return;
      var next = tabs[(i + (ev.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length];
      select(next.dataset.view); next.focus();
    });
    if (location.hash === '#agent') select('agent');
    // A link to #agent from this same page is a hash change, not a load.
    window.addEventListener('hashchange', function () { select(viewFromHash()); });
  }

  // Minutes and seconds since the first thing the record says, as the progress list counts them.
  function since(ts) {
    var t = ts ? Date.parse(ts) : NaN;
    if (isNaN(t)) return '';
    if (firstAt === null) firstAt = t;
    var s = Math.max(0, Math.round((t - firstAt) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  // The agent's own writing is Markdown, rendered by the shared script: escaped first, so it adds only its own tags.
  function md(text) { var d = el('div', 'fr-md'); d.innerHTML = R.markdown(text); return d; }

  // Everything up to and including this project's own directory, in either separator: `C:\…\trailer5\brief.md`
  // reads as `brief.md`.
  var projectPath = new RegExp('^.*?[\\\\/]' + String(page.dataset.name || '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '[\\\\/]', 'g');

  // One line for the "now" strip, without the Markdown marks.
  function gist(text) { return String(text || '').replace(/[*_`#>|]+/g, '').replace(/\s+/g, ' ').trim(); }

  function tickNow() {
    if (!lastAt || !nowAgo) return;
    var s = Math.round((Date.now() - lastAt) / 1000);
    nowAgo.textContent = s < 10 ? '' : s < 90 ? s + 's ago' : Math.floor(s / 60) + 'm ' + (s % 60) + 's ago';
  }
  setInterval(tickNow, 1000);

  // A request the page already knows is over has nothing being done now. Claude Code ends a run without a
  // run.end, so for those the strip would otherwise keep the last thing said, in the running colour, for good.
  var over = page.dataset.status === 'delivered' || page.dataset.status === 'stopped';
  function doing(text) {
    if (!now || !text || over || finished) return;
    now.hidden = false; lastAt = Date.now(); lastDid = text;
    nowDid.textContent = text.length > 110 ? text.slice(0, 109) + '…' : text; tickNow();
  }

  // One row: when, what kind of thing, and the thing. `fold` is for prose too long to show whole: thinking
  // opens on a click, what the agent said is clipped to a few lines.
  function row(kind, label, ts, body, fold) {
    if (!log) return null;
    var follow = log.scrollHeight - log.scrollTop - log.clientHeight < 48;
    var li = el('li', 'fr-log-row is-' + kind);
    li.appendChild(el('span', 'fr-log-time', since(ts)));
    li.appendChild(el('span', 'fr-log-kind', label));
    var b = el('div', 'fr-log-body');
    if (typeof body === 'string') b.textContent = body; else if (body) b.appendChild(body);
    li.appendChild(b);
    if (fold) {
      b.classList.add(fold); li.tabIndex = 0; li.setAttribute('aria-expanded', 'false');
      var toggle = function () { var open = b.classList.toggle(fold); li.setAttribute('aria-expanded', open ? 'false' : 'true'); };
      li.addEventListener('click', toggle);
      li.addEventListener('keydown', function (ev) { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); toggle(); } });
    }
    log.appendChild(li); rows += 1;
    if (count) count.textContent = rows;
    if (empty) empty.hidden = true;
    if (follow) log.scrollTop = log.scrollHeight;
    lastFold = null; lastFoldKey = null;
    return li;
  }

  function line(text, cls) { return el('span', cls || null, text); }

  // A render or a script opens under its own row rather than in another view: the transcript is read in order,
  // and switching views lost the reader's place in it. A second click closes it. `fill` puts the thing in.
  function viewable(path, thumb, fill) {
    var b = el('button', 'fr-log-open'); b.type = 'button'; b.setAttribute('aria-expanded', 'false');
    b.title = 'Show it here';
    if (thumb && IMAGE.test(path)) { var img = el('img'); img.alt = ''; img.loading = 'lazy'; img.src = artifactUrl(path); b.appendChild(img); }
    b.appendChild(line(path, 'fr-spec'));
    b.addEventListener('click', function (ev) {
      ev.stopPropagation();
      var body = b.closest('.fr-log-body'), open = body.querySelector('.fr-log-media');
      if (open) { open.remove(); b.setAttribute('aria-expanded', 'false'); b.title = 'Show it here'; return; }
      var fig = el('figure', 'fr-log-media');
      // Selecting a line of code, or using a video's controls, is not a click on the row.
      fig.addEventListener('click', function (e) { e.stopPropagation(); });
      (fill || media)(fig, path); body.appendChild(fig);
      b.setAttribute('aria-expanded', 'true'); b.title = 'Hide it';
    });
    return b;
  }
  function media(fig, path) {
    var m;
    if (VIDEO.test(path)) { m = el('video'); m.controls = true; m.muted = true; m.loop = true; m.autoplay = true; m.playsInline = true; }
    else { m = el('img'); m.alt = path; }
    m.src = artifactUrl(path);
    var full = el('a', 'fr-small', 'Open full size'); full.href = artifactUrl(path); full.target = '_blank'; full.rel = 'noopener';
    fig.appendChild(m); fig.appendChild(full);
  }
  // The script exactly as the engine ran it, from the copy it saved under scripts/, highlighted by the server
  // as the developer page shows it. That fragment is Pygments' escaped output, so it carries no markup of the agent's.
  // `sent` is what a tool call's record kept of an inline script, when the script is opened from that call:
  // the copy is shown only if it agrees.
  function script(fig, path, sent) {
    var box = el('div', 'fr-code', 'Loading…'); fig.appendChild(box);
    fetch(base + '/runs/' + encodeURIComponent(run) + '/' + path.split('/').map(encodeURIComponent).join('/'))
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
      .then(function (html) {
        box.innerHTML = html;
        if (typeof sent === 'string' && !agrees(box, sent)) box.textContent = 'The saved ' + path + ' is not the script this call sent, so it is not shown.';
      }, function () { box.textContent = path + ' could not be read.'; });
  }
  // Whether highlighted source is what a call sent. The record keeps a long argument as its length only
  // (`<438 chars>`, in code points, as Python counts them); Pygments drops the newlines at either end and any
  // carriage returns, so a shown script can be up to a line's worth shorter than it was sent.
  function agrees(box, sent) {
    var pre = box.querySelector('td.code pre') || box.querySelector('pre'); if (!pre) return false;
    var text = pre.textContent.replace(/\r/g, '').replace(/^\n+|\n+$/g, '');
    var sized = /<(\d+) chars>$/.exec(sent);
    if (sized) { var d = Number(sized[1]) - Array.from(text).length; return d >= 0 && d <= text.split('\n').length + 1; }
    return text === sent.replace(/\r/g, '').replace(/^\n+|\n+$/g, '');
  }
  var SCRIPT = /^scripts\/[\w.-]+\.js$/;
  // A script row: what ran, openable when the record names the saved copy, then whatever follows it.
  function ranScript(e, after) {
    var b = el('span'), name = e.script || 'a script';
    b.appendChild(SCRIPT.test(name) ? viewable(name, false, script) : line(name, 'fr-spec'));
    if (after) b.appendChild(after);
    return b;
  }

  // An ExecuteScript row opens the script that call ran. Nothing in the record joins the agent's call to the
  // server's execution, so they are paired in order: the server runs a session's calls one at a time, in the
  // order they were made, and saves each before it runs (a call made while another runs waits its turn). A
  // call refused before that (bad `args`, a missing scriptFile) saves nothing; since 2026-10-19 the server says
  // so with `script.refused`, which keeps the count, but an older record has a gap that shifts every later pair.
  // So each pair is still checked against what the call named, and linking stops at the first that disagrees:
  // no link is better than the wrong script.
  var calls = [], starts = [], paired = 0, unpaired = false, sourced = false;
  function sameFile(a, b) {
    a = String(a).replace(/\\/g, '/').replace(/^\.\//, ''); b = String(b).replace(/\\/g, '/').replace(/^\.\//, '');
    return a === b || a.slice(-b.length - 1) === '/' + b || b.slice(-a.length - 1) === '/' + a;
  }
  function pair() {
    while (!unpaired && paired < calls.length && paired < starts.length) {
      var c = calls[paired], s = starts[paired], file = c.args.scriptFile;
      paired += 1;
      // Since 2026-10 the server names a file source on its start; an older record names none for either kind.
      sourced = sourced || s.source != null;
      if (s.source != null ? file == null || !sameFile(s.source, file) : file != null && sourced) { unpaired = true; return; }
      if (!c.li || !SCRIPT.test(s.script || '')) continue;
      var body = c.li.querySelector('.fr-log-body > span'), sent = file == null ? c.args.script : undefined, path = s.script;
      body.appendChild(line('  ')); body.appendChild(viewable(path, false, function (fig) { script(fig, path, sent); }));
    }
  }

  var AGENT = {
    'run.begin': function (e) { if (began) return; began = true; row('start', 'Started', e.ts, 'The agent began work on the brief.'); },
    'run.start': function (e) { if (began) return; began = true; row('start', 'Started', e.ts, 'The agent began work on the brief.'); },
    'stage.begin': function (e) {
      if (!log) return;
      var li = el('li', 'fr-log-stage'); li.appendChild(el('span', 'fr-log-time', since(e.ts)));
      li.appendChild(el('span', null, e.stage || 'Stage')); log.appendChild(li);
      lastFold = null; lastFoldKey = null; doing('Working on ' + (e.stage || 'a new stage'));
    },
    'thinking': function (e) {
      if (e.redacted && !e.text) { row('thinking', 'Thinking', e.ts, '(reasoning not stored by this host)'); return; }
      var text = String(e.text || '').trim(); if (!text) return;
      row('thinking', 'Thinking', e.ts, md(text), 'is-folded'); doing('Thinking');
    },
    'text': function (e) {
      var text = String(e.text || '').trim(); if (!text) return;
      if (e.source === 'SYSTEM' && e.status === 'ERROR') { row('failed', 'Provider', e.ts, text, 'is-clamped'); return; }
      row('said', 'Said', e.ts, md(text), 'is-clamped'); doing(gist(text));
    },
    'tool.call': function (e) {
      var t = R.toolParts(e), key = t.tool + ' ' + t.arg;
      // A host's file tools name absolute paths; inside the project, the path from its root is the readable part.
      t.arg = t.arg.replace(projectPath, '');
      // A repeated call is one wait polled several times, not a loop: it folds into the row above.
      var executes = t.tool === 'ExecuteScript';
      if (key === lastFoldKey && lastFold) {
        var n = (Number(lastFold.dataset.repeats) || 1) + 1; lastFold.dataset.repeats = n;
        var badge = lastFold.querySelector('.fr-log-repeat') || lastFold.querySelector('.fr-log-body').appendChild(el('span', 'fr-log-repeat'));
        badge.textContent = ' ×' + n;
        // A folded call still ran a script of its own, so it keeps its place in the pairing with no row to open.
        if (executes) { calls.push({ li: null, args: e.args || {} }); pair(); }
        return;
      }
      var body = el('span'); body.appendChild(line(t.tool, 'fr-log-tool'));
      if (t.arg) body.appendChild(line(' ' + t.arg));
      var li = row('tool', 'Tool', e.ts, body);
      lastFold = li; lastFoldKey = key; doing(t.arg ? t.tool + ': ' + t.arg : t.tool);
      if (executes) { calls.push({ li: li, args: e.args || {} }); pair(); }
    },
    'script.start': function (e) { starts.push(e); pair(); },
    // A call the server turned down before saving anything takes its place in the pairing with no script to open.
    'script.refused': function (e) {
      starts.push(e); pair();
      row('failed', 'Refused', e.ts, String(R.detail('script.refused', e)).split('\n')[0], 'is-clamped');
    },
    'script.ok': function (e) {
      var ms = typeof e.ms === 'number' ? line('  ' + (e.ms < 1000 ? e.ms + ' ms' : (e.ms / 1000).toFixed(1) + ' s'), 'fr-log-meta') : null;
      row('ran', 'Ran', e.ts, ranScript(e, ms));
    },
    'script.error': function (e) {
      var why = String(R.detail('script.error', e)).split('\n')[0];
      row('failed', 'Failed', e.ts, e.script ? ranScript(e, el('span', 'fr-log-why', why)) : why, 'is-clamped');
    },
    'render': function (e) {
      var path = e.artifact || ''; if ((e.motion || '').toLowerCase() === 'sheet') return;
      row('render', 'Rendered', e.ts, IMAGE.test(path) || VIDEO.test(path) ? viewable(path, true, media) : line(path, 'fr-spec'));
      doing('Rendered ' + path);
    },
    'artifact.read': function (e) {
      var path = e.artifact || '';
      row('look', 'Looked at', e.ts, IMAGE.test(path) || VIDEO.test(path) ? viewable(path, false, media) : line(path, 'fr-spec'));
    },
    'note': function (e) { if (e.message) row('note', 'Note', e.ts, md(e.message), 'is-clamped'); },
    'check': function (e) { row(e.passed || e.accepted ? 'check' : 'failed', 'Checked', e.ts, R.detail('check', e), 'is-clamped'); },
    'question.open': function (e) {
      var b = el('span'); b.appendChild(line(e.text || 'A question for you'));
      if ((e.options || []).length) {
        var ul = el('ul', 'fr-log-options');
        e.options.forEach(function (o) { ul.appendChild(el('li', null, String(o).replace(/^\s*\(recommended\)\s*/i, ''))); });
        b.appendChild(ul);
      }
      row('ask', 'Asked you', e.ts, b); doing('Waiting for your answer');
    },
    'answer': function (e) {
      var said = e.skipped ? 'You let the agent choose.' : (e.text || (e.selected || []).join(', ') || 'You answered.');
      row('you', 'You', e.ts, said);
    },
    'question.closed': function (e) { if (e.reason === 'timeout') row('you', 'No answer', e.ts, 'The question timed out, so the agent chose for itself.'); },
    // The first message, before the agent has done anything, is the opening the studio sends to start the run
    // (on ADK, the intake's own words), not something the person typed. Later ones are the person speaking.
    'message': function (e) {
      if (!(e.text || e.message)) return;
      var opening = rows <= 1;
      row(opening ? 'meta' : 'you', opening ? 'Opening' : 'You', e.ts, e.text || e.message);
    },
    'research.started': function (e) { row('tool', 'Researching', e.ts, R.detail('research.started', e)); doing('Researching: ' + (e.description || '')); },
    'research.completed': function (e) { row('tool', 'Research done', e.ts, e.description || ''); },
    'asset.requisition': function (e) { row('tool', 'Requested', e.ts, e.description || e.prompt || e.artifact || 'an image'); },
    'asset.refused': function (e) { row('failed', 'Refused', e.ts, e.reason || e.description || ''); },
    'document.read': function (e) { row('look', 'Read', e.ts, line(e.descriptor || e.path || e.document || '', 'fr-spec')); },
    'video.read': function (e) { row('look', 'Watched', e.ts, line(e.descriptor || '', 'fr-spec')); },
    // Dropped calls and dropped starts need not number the same, so nothing after a gap can be paired.
    'stream.truncated': function (e) { unpaired = true; row('meta', 'Earlier', e.ts, (e.dropped || 'Some') + ' earlier events are not shown.'); },
    'run.error': function (e) { row('failed', 'Error', e.ts, String(e.error || '').split('\n')[0], 'is-clamped'); },
    'run.end': function (e) {
      if (e.src === 'server') return;
      var end = R.runEnd(e);
      row(end.ok ? 'end' : 'failed', end.ok ? 'Finished' : 'Stopped', e.ts,
          end.ok ? 'The agent finished the work.' : String(end.text).split('\n')[0]);
      if (now) { now.hidden = true; lastAt = null; }
    }
  };
  if (log) Object.keys(AGENT).forEach(function (kind) { on(kind, AGENT[kind]); });
  // endregion

  // region Qloo views: Audiences, Contrast, Choices
  // From /requests/<name>/taste (studio.direction.taste_views): the profiles the run fetched, its contrasts, and the
  // evidence map. Every label is a Qloo title or the agent's words, so everything is built with textContent.
  var DOMAIN = { 'urn:entity:movie': 'Films', 'urn:entity:tv_show': 'TV', 'urn:entity:artist': 'Music',
                 'urn:entity:book': 'Books', 'urn:entity:brand': 'Brands', 'urn:entity:place': 'Places',
                 'urn:entity:podcast': 'Podcasts', 'urn:entity:video_game': 'Games', 'urn:entity:destination': 'Destinations',
                 'urn:entity:person': 'People' };
  function domainName(type) { return DOMAIN[type] || String(type || '').replace('urn:entity:', ''); }
  function ageName(age) {
    if (!age) return '';
    var m = /^(\d+)_to_(\d+)$/.exec(age);
    return m ? m[1] + '–' + m[2] : String(age).replace(/_/g, ' ');
  }
  function audienceName(a) {
    var who = a && a.audience || {};
    var bits = [who.place, ageName(who.age), who.gender].filter(Boolean);
    return (a.label ? a.label.charAt(0).toUpperCase() + a.label.slice(1) : a.id) + (bits.length ? ' — ' + bits.join(', ') : '');
  }
  function pct(x) { return Math.round(x * 100) + '%'; }

  // A ranked list of titles: Qloo's rank, the title, and its pace where Qloo's tags give one.
  function ranked(items, limit) {
    var ol = el('ol', 'fr-ranked');
    (items || []).slice(0, limit || 10).forEach(function (t) {
      var li = el('li');
      li.appendChild(el('span', 'fr-rank', t.rank != null ? String(t.rank) : (t.rankA != null ? t.rankA + '·' + t.rankB : '')));
      li.appendChild(el('em', '', t.name));
      if (t.pace) li.appendChild(el('span', 'fr-pace-tag fr-pace-' + t.pace, t.pace === 'fast' ? 'fast-paced' : 'slow-paced'));
      ol.appendChild(li);
    });
    if (!ol.children.length) ol.appendChild(el('li', 'fr-small', 'None.'));
    return ol;
  }

  // Fast against slow among an audience's films and shows, by Qloo's own tags, as one bar.
  function paceBar(p, caption) {
    var box = el('div', 'fr-pacebar');
    var decided = (p.fast || 0) + (p.slow || 0);
    box.appendChild(el('p', 'fr-small', caption || (decided
      ? p.fast + ' fast-paced and ' + p.slow + ' slow-paced of ' + p.n + ' films and shows'
      : 'No pace tags among ' + (p.n || 0) + ' films and shows')));
    if (decided) {
      var bar = el('div', 'fr-bar');
      var f = el('span', 'fr-bar-fast'); f.style.width = pct(p.fast / Math.max(p.n, 1)); f.title = p.fast + ' fast-paced';
      var s = el('span', 'fr-bar-slow'); s.style.width = pct(p.slow / Math.max(p.n, 1)); s.title = p.slow + ' slow-paced';
      bar.appendChild(f); bar.appendChild(s); box.appendChild(bar);
    }
    return box;
  }

  function renderAudiences(box, audiences) {
    box.textContent = '';
    audiences.forEach(function (a) {
      var card = el('section', 'fr-card fr-aud');
      card.appendChild(el('h3', 'fr-heading', audienceName(a)));
      card.appendChild(el('p', 'fr-small', 'Profile ' + a.id + (a.distinctive != null ? ' · ' + a.distinctive + ' distinctive titles' : '') + ' · source: Qloo'));
      if (a.pace && a.pace.n) card.appendChild(paceBar(a.pace));
      if (!a.domains || !a.domains.length) {
        card.appendChild(el('p', 'fr-small', 'This run recorded the counts only; runs from now on record the titles too.'));
      } else {
        var grid = el('div', 'fr-aud-domains');
        a.domains.forEach(function (d) {
          var col = el('div');
          col.appendChild(el('p', 'ps-label', domainName(d.type) + ' (' + d.n + ' distinctive)'));
          col.appendChild(ranked(d.top, 12));
          grid.appendChild(col);
        });
        card.appendChild(grid);
      }
      box.appendChild(card);
    });
  }

  // Tags that lean one way, as a back-to-back bar per tag: the share of each side's own titles that carry it.
  function leanChart(d, nameA, nameB) {
    var rows = [];
    (d.tagsA || []).forEach(function (t) { rows.push({ tag: t.tag, a: t.a, b: t.b, titles: t.titles }); });
    (d.tagsB || []).forEach(function (t) { rows.push({ tag: t.tag, a: t.b, b: t.a, titles: t.titles }); });
    if (!rows.length) return el('p', 'fr-small', 'No tag leans either way on two or more titles.');
    var onlyA = (d.onlyA || []).length || 1, onlyB = (d.onlyB || []).length || 1;
    var chart = el('div', 'fr-lean');
    var head = el('div', 'fr-lean-row fr-lean-head');
    head.appendChild(el('span', '', nameA)); head.appendChild(el('span', '', 'Tag')); head.appendChild(el('span', '', nameB));
    chart.appendChild(head);
    rows.forEach(function (r) {
      var row = el('div', 'fr-lean-row');
      row.title = 'On ' + r.a + ' of ' + nameA + "'s own titles and " + r.b + ' of ' + nameB + "'s, e.g. " + (r.titles || []).join(', ');
      var left = el('span', 'fr-lean-a'), right = el('span', 'fr-lean-b');
      var la = el('i'); la.style.width = pct(Math.min(r.a / onlyA, 1)); left.appendChild(el('b', '', String(r.a))); left.appendChild(la);
      var rb = el('i'); rb.style.width = pct(Math.min(r.b / onlyB, 1)); right.appendChild(rb); right.appendChild(el('b', '', String(r.b)));
      row.appendChild(left); row.appendChild(el('span', 'fr-lean-tag', r.tag)); row.appendChild(right);
      chart.appendChild(row);
    });
    return chart;
  }

  function renderContrast(box, contrasts, audiences) {
    box.textContent = '';
    var byId = {};
    audiences.forEach(function (a) { byId[a.id] = a; });
    contrasts.forEach(function (c) {
      var nameA = byId[c.a.id] ? audienceName(byId[c.a.id]).split(' — ')[0] : (c.a.label || c.a.id);
      var nameB = byId[c.b.id] ? audienceName(byId[c.b.id]).split(' — ')[0] : (c.b.label || c.b.id);
      var card = el('section', 'fr-card fr-contrast');
      card.appendChild(el('h3', 'fr-heading', nameA + ' and ' + nameB));
      card.appendChild(el('p', 'fr-small', 'Profiles ' + c.a.id + ' and ' + c.b.id + ' · source: Qloo'));
      if (c.pace && c.pace.a && c.pace.b) {
        var pace = el('div', 'fr-contrast-pace');
        pace.appendChild(paceBar(c.pace.a, nameA + ': ' + c.pace.a.fast + ' fast, ' + c.pace.a.slow + ' slow'));
        pace.appendChild(paceBar(c.pace.b, nameB + ': ' + c.pace.b.fast + ' fast, ' + c.pace.b.slow + ' slow'));
        card.appendChild(pace);
        if (c.pace.summary) card.appendChild(el('p', 'fr-quote', c.pace.summary));
      }
      (c.domains || []).forEach(function (d) {
        var sec = el('div', 'fr-contrast-domain');
        var shared = (d.shared || []).length, union = shared + (d.onlyA || []).length + (d.onlyB || []).length;
        sec.appendChild(el('p', 'ps-label', domainName(d.type) + ': ' + shared + ' in common of ' + union + ' distinctive titles'
          + (d.overlap != null ? ' (' + pct(d.overlap) + ')' : '')));
        var meter = el('div', 'fr-bar fr-overlap');
        var o = el('span', 'fr-bar-shared'); o.style.width = pct(d.overlap || 0); meter.appendChild(o);
        sec.appendChild(meter);
        var cols = el('div', 'fr-venn');
        [['Only ' + nameA, d.onlyA], ['Both', d.shared], ['Only ' + nameB, d.onlyB]].forEach(function (pair) {
          var col = el('div');
          col.appendChild(el('p', 'ps-label', pair[0] + ' (' + (pair[1] || []).length + ')'));
          col.appendChild(ranked(pair[1], 10));
          cols.appendChild(col);
        });
        sec.appendChild(cols);
        sec.appendChild(el('p', 'ps-label', 'Tags that lean one way'));
        sec.appendChild(leanChart(d, nameA, nameB));
        card.appendChild(sec);
      });
      box.appendChild(card);
    });
  }

  var tasteLoading = null;
  function loadTaste() {
    if (tasteLoading) return tasteLoading;
    tasteLoading = fetch(base + '/requests/' + encodeURIComponent(page.dataset.name) + '/taste')
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data || !views) return;
        var shown = {
          audiences: (data.audiences || []).length > 0,
          contrast: (data.contrasts || []).length > 0,
          choices: !!(data.map && (data.map.choices || []).length)
        };
        Object.keys(shown).forEach(function (v) {
          var tab = views.querySelector('.ps-tab[data-view="' + v + '"]'); if (tab) tab.hidden = !shown[v];
        });
        if (shown.audiences) renderAudiences(page.querySelector('[data-audiences]'), data.audiences);
        if (shown.contrast) renderContrast(page.querySelector('[data-contrast]'), data.contrasts, data.audiences || []);
        if (shown.choices) {
          R.evidenceMap(page.querySelector('[data-evmap]'), data.map);
          var rc = data.referenceCheck, note = page.querySelector('[data-choices-check]');
          note.textContent = !rc ? 'The run has not checked its references yet.'
            : rc.passed ? 'Every one of the ' + rc.checked + ' Qloo references was returned by the query it cites.'
            : rc.unverified + ' Qloo reference' + (rc.unverified === 1 ? ' was' : 's were') + ' not found in the query cited.';
        }
        // A link to #contrast and the like opens once its tab exists.
        var wanted = viewFromHash();
        if (wanted !== 'progress' && shown[wanted] && views.querySelector('[aria-selected="true"]').dataset.view !== wanted) select(wanted);
      })
      .catch(function () { /* the views stay as they were */ })
      .then(function () { tasteLoading = null; });
    return tasteLoading;
  }
  // As the run fetches, contrasts and checks, the views follow — a moment after, so a burst refetches once.
  var tasteTimer = null;
  ['taste.query', 'taste.contrast', 'taste.check'].forEach(function (kind) {
    on(kind, function () { clearTimeout(tasteTimer); tasteTimer = setTimeout(loadTaste, 800); });
  });
  if (views) loadTaste();
  // endregion
  later();
  if (page.dataset.status === 'delivered' || page.dataset.status === 'stopped') {
    // After the replay has had a moment, so a piece from the stream's own run.end is not fetched twice.
    setTimeout(function () {
      if (!finished && !stoppedOnce) {
        stages.forEach(function (s) { if (s.state === 'current') s.state = 'done'; });
        finished = page.dataset.status === 'delivered'; later(); pieces();
      }
    }, 1500);
  }
  // endregion
})();
