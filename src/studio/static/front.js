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
          a.href = base + '/runs/' + encodeURIComponent(run) + '/deliverables/' + encodeURIComponent(d.name) + '?raw=1';
          a.target = '_blank'; li.appendChild(a); li.appendChild(el('span', 'fr-small', ' · ' + d.what));
          docs.appendChild(li);
        }
      });
      page.querySelector('[data-documents]').hidden = !docs.children.length;
    });
  }

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
  var failure = '';
  on('run.error', function (e) { failure = String(e.error || '').split('\n')[0]; });
  on('run.end', function (e) {
    var how = e.reason || e.status || '';
    var ok = !how || how === 'completed' || how === 'done';
    deliver(ok && !e.error ? 'delivered' : 'stopped');
    if (!ok || e.error) stopped(failure || String(e.error || how).split('\n')[0]);
  });
  on('run.closed', function (summary) {
    source.close();
    if (!finished) deliver(summary && summary.status === 'done' ? 'delivered' : 'stopped');
  });
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
