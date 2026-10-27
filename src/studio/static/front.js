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
  function megabytes(bytes) { return bytes < 1048576 ? Math.ceil(bytes / 1024) + ' KB' : (bytes / 1048576).toFixed(1) + ' MB'; }

  // region Composer
  var form = document.querySelector('[data-compose]');
  if (form) {
    var pops = Array.prototype.slice.call(form.querySelectorAll('details.fr-pop'));

    function summarise(pop) {
      var out = pop.querySelector('[data-summary]');
      if (!out) return;
      if (pop.hasAttribute('data-audiences')) {
        var who = Array.prototype.map.call(pop.querySelectorAll('[data-audience]'), function (row) {
          var place = row.querySelector('input').value.trim(), age = row.querySelector('select[name=aud_age]');
          var lang = row.querySelector('[data-language]'), said = [];
          if (age && age.value) said.push(age.selectedOptions[0].textContent);
          if (lang && lang.value) said.push('in ' + lang.selectedOptions[0].textContent);
          return place ? place + (said.length ? ' (' + said.join(', ') + ')' : '') : '';
        }).filter(Boolean);
        out.textContent = who.length ? who.join(', ') : pop.hasAttribute('data-optional') ? 'Anyone (optional)' : 'Who is it for?';
        out.classList.toggle('is-empty', !who.length);
        return;
      }
      if (pop.hasAttribute('data-techniques')) {
        // A choice per technique: the title counts what was asked for and ruled out, the rest is the studio's.
        var use = pop.querySelectorAll('input[value=request]:checked').length;
        var deny = pop.querySelectorAll('input[value=deny]:checked').length;
        var said = [];
        if (use) said.push(use + ' used');
        if (deny) said.push(deny + ' not used');
        pop.querySelectorAll('[data-count]').forEach(function (c) { c.textContent = said.length ? ' (' + said.join(' / ') + ')' : ''; });
        out.classList.toggle('is-empty', !said.length);
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
        // A single choice closes its panel; the design techniques are many choices, so theirs stays open.
        if (e.target.type === 'radio' && !pop.hasAttribute('data-techniques')) pop.open = false;
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
      var who = !audiences || audiences.hasAttribute('data-optional')
        || Array.prototype.some.call(audiences.querySelectorAll('input[name=aud_place]'), function (i) { return i.value.trim(); });
      if (!who) { e.preventDefault(); audiences.open = true; audiences.querySelector('input').focus(); return; }
      if (!sizes || !formats) {
        e.preventDefault();
        var pop = (!sizes ? form.querySelector('input[name=sizes]') : form.querySelector('input[name=formats]')).closest('details');
        pop.open = true;
      }
    });

    // The brief counter. The text box, not the first [data-count]: the Graphic design field's tally spans carry it too,
    // come first, and have no value, so the counter threw and every handler after it was never attached.
    var brief = form.querySelector('textarea[data-count]'), counter = form.querySelector('[data-counter]');
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
      function still(d) {
        return d.formats.filter(function (f) { return /\.webp$/i.test(f.path); })[0]
            || d.formats.filter(function (f) { return IMAGE.test(f.path) || VIDEO.test(f.path); })[0];
      }
      function whatOf(d) { var what = d.what || 'the finished piece'; return what.charAt(0).toUpperCase() + what.slice(1); }
      function fillDownloads(box, d) {
        d.formats.forEach(function (f) {
          var a = el('a', 'ps-btn ps-btn-secondary ps-btn-sm'); a.href = artifactUrl(f.path); a.setAttribute('download', '');
          a.appendChild(icon('download', 16)); a.appendChild(document.createTextNode('.' + f.format));
          box.appendChild(a);
        });
      }
      function present(d) {
        var media = still(d);
        if (media) show(media.path);
        caption.textContent = whatOf(d);
        downloads.textContent = '';
        fillDownloads(downloads, d);
      }

      // The Deliverables tab: every piece at once, each with its downloads.
      var finishedList = page.querySelector('[data-finished-list]');
      if (finishedList) {
        finishedList.textContent = '';
        pieces.forEach(function (d) {
          var card = el('figure', 'fr-finished-piece'), media = still(d);
          if (media && VIDEO.test(media.path)) {
            var v = el('video'); v.src = artifactUrl(media.path); v.controls = true; v.preload = 'metadata'; v.muted = true;
            card.appendChild(v);
          } else if (media) {
            var img = el('img'); img.src = artifactUrl(media.path); img.alt = whatOf(d); img.loading = 'lazy';
            card.appendChild(img);
          }
          var cap = el('figcaption'); cap.appendChild(el('span', 'fr-small', whatOf(d)));
          var box = el('div', 'fr-downloads'); fillDownloads(box, d); cap.appendChild(box);
          card.appendChild(cap); finishedList.appendChild(card);
        });
        page.querySelector('[data-finished]').hidden = !pieces.length;
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

      loadLearned();

      // One row each: the file, and what it holds. A document is read here; a book is a PDF the studio set, so it
      // downloads.
      data.deliverables.forEach(function (d) {
        if (d.kind !== 'document' && d.kind !== 'book') return;
        // Each link says what a click does by its icon: a document is read on this page, a book saved, with its size.
        var tr = el('tr'), a = el('a'), what = d.what || '', book = d.kind === 'book';
        a.appendChild(icon(book ? 'download' : 'document', 14));
        a.appendChild(el('span', 'fr-spec', d.name));
        if (book && d.bytes) a.appendChild(el('span', 'fr-doc-action', megabytes(d.bytes)));
        a.title = book ? 'Download ' + d.name : 'Read ' + d.name + ' on this page';
        if (book) {
          a.href = artifactUrl(d.path); a.setAttribute('download', d.name);
        } else {
          a.href = deliverableUrl(d.name) + '?raw=1'; a.target = '_blank';
          // A plain click reads it here; the link stays the raw file, for a new tab, a copy or a save.
          a.addEventListener('click', function (ev) {
            if (ev.button || ev.ctrlKey || ev.metaKey || ev.shiftKey || ev.altKey || !doc) return;
            ev.preventDefault(); read(d, a);
          });
        }
        var link = el('td'); link.appendChild(a);
        tr.appendChild(link);
        tr.appendChild(el('td', 'fr-small', what.charAt(0).toUpperCase() + what.slice(1)));
        docs.appendChild(tr);
      });
      page.querySelector('[data-documents]').hidden = !docs.children.length;
      deliverablesTab();
    });
  }

  // What the studio learned: the pace counts arrive with each profile query and the titles with direction.md, so the
  // panel is fetched again whenever the Qloo views are (loadTaste) and once the run has delivered.
  function loadLearned() {
    return fetch(base + '/requests/' + encodeURIComponent(page.dataset.name) + '/learned').then(function (r) { return r.ok ? r.text() : ''; }).then(function (html) {
      var slot = page.querySelector('[data-learned]');
      if (!html || !slot) return;
      var box = document.createElement('div'); box.innerHTML = html;
      var fresh = box.querySelector('[data-learned]'); if (fresh) slot.replaceWith(fresh);
      deliverablesTab();
    }).catch(function () { /* the panel stays as it was */ });
  }
  // The Deliverables tab shows once it has anything: a piece, a document, or something learned.
  function deliverablesTab() {
    var tab = views && views.querySelector('.ps-tab[data-view="deliverables"]');
    if (!tab) return;
    var learnedBox = page.querySelector('[data-learned]');
    tab.hidden = !(page.querySelector('[data-finished-list]').children.length || page.querySelector('[data-doc-list]').children.length
                   || (learnedBox && !learnedBox.hidden));
    if (!tab.hidden && viewFromHash() === 'deliverables' && tab.getAttribute('aria-selected') !== 'true') select('deliverables');
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
      .then(function (html) { if (mine === asked) { body.innerHTML = html; artifactLinks(body); } },
            function () { if (mine === asked) body.textContent = d.name + ' could not be shown here. The Raw link opens the file itself.'; });
  }
  if (doc) doc.querySelector('[data-doc-close]').addEventListener('click', closeDoc);

  // A render the document names (`artifacts/03_matrix_a.png`) arrives marked by the server, which links only what
  // this run serves. A plain click shows it under the paragraph, list item or table that names it, and a second
  // click puts it away; the href is the file itself, for a new tab or a save.
  function artifactLinks(body) {
    body.querySelectorAll('a[data-artifact]').forEach(function (a) {
      var path = a.dataset.artifact, shown = null;
      a.href = artifactUrl(path); a.setAttribute('aria-expanded', 'false');
      a.addEventListener('click', function (ev) {
        if (ev.button || ev.ctrlKey || ev.metaKey || ev.shiftKey || ev.altKey) return;
        ev.preventDefault();
        if (shown) { shown.remove(); shown = null; a.setAttribute('aria-expanded', 'false'); return; }
        shown = el('figure', 'fr-artifact-figure');
        var media = VIDEO.test(path) ? el('video') : /\.pdf$/i.test(path) ? el('iframe') : el('img');
        media.src = a.href;
        if (media.tagName === 'VIDEO') { media.controls = true; media.muted = true; media.loop = true; media.autoplay = true; }
        else if (media.tagName === 'IMG') media.alt = path;
        else media.title = path;
        var cap = el('figcaption', 'fr-small', path);
        media.addEventListener('error', function () { cap.textContent = path + ' could not be shown here; the link opens the file itself.'; });
        shown.appendChild(media); shown.appendChild(cap);
        var item = a.closest('li'), block = a.closest('table') || a.closest('p, blockquote, h1, h2, h3, h4, h5, h6');
        if (item && !(block && item.contains(block))) item.appendChild(shown);
        else (block || a).insertAdjacentElement('afterend', shown);
        a.setAttribute('aria-expanded', 'true');
      });
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
    if (view === 'audiences' || view === 'contrast' || view === 'choices' || view === 'verification') loadTaste();
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

  // A ranked list of titles: Qloo's rank, the title, and its pace where Qloo's tags give one. With `paced`, the titles
  // past the limit that carry a pace tag follow, so every title the pace bar counts can be found.
  function ranked(items, limit, paced) {
    var ol = el('ol', 'fr-ranked');
    function add(t) {
      var li = el('li');
      li.appendChild(el('span', 'fr-rank', t.rank != null ? String(t.rank) : (t.rankA != null ? t.rankA + '·' + t.rankB : '')));
      li.appendChild(el('em', '', t.name));
      if (t.pace) li.appendChild(el('span', 'fr-pace-tag fr-pace-' + t.pace, t.pace === 'fast' ? 'fast-paced' : 'slow-paced'));
      ol.appendChild(li);
    }
    items = items || []; limit = limit || 10;
    items.slice(0, limit).forEach(add);
    var further = paced ? items.slice(limit).filter(function (t) { return t.pace; }) : [];
    if (further.length) {
      ol.appendChild(el('li', 'fr-small fr-ranked-gap', 'Further down, with a pace tag'));
      further.forEach(add);
    }
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

  // An insights query asked about an audience: its entities by the item number a direction cites, each with the tags the
  // direction cites from this query. The top 12, then every further entity carrying a cited tag.
  function listCard(l) {
    var card = el('section', 'fr-card fr-aud');
    card.appendChild(el('h3', 'fr-heading', audienceName({ id: l.id, label: l.label, audience: l.audience })));
    card.appendChild(el('p', 'fr-small', 'Query ' + l.id + ' · ' + domainName(l.type) + (l.n != null ? ' · ' + l.n + ' results' : '') + ' · source: Qloo'));
    if (!l.entities) {
      card.appendChild(el('p', 'fr-small', 'This run recorded the count only; runs from now on record the results too.'));
      return card;
    }
    var marked = l.entities.filter(function (x) { return (x.cited || []).length; });
    var tags = {};
    marked.forEach(function (x) { x.cited.forEach(function (t) { tags[t] = true; }); });
    card.appendChild(el('p', 'fr-small', Object.keys(tags).length
      ? Object.keys(tags).length + ' tags the direction cites from this query, on ' + marked.length + ' of these ' + l.entities.length + ':'
      : 'The direction cites no tag from this query.'));
    var ol = el('ol', 'fr-ranked');
    function add(x) {
      var li = el('li');
      li.appendChild(el('span', 'fr-rank', String(x.rank)));
      li.appendChild(el('em', '', x.name));
      if ((x.cited || []).length) {
        var chips = el('span', 'fr-chips');
        x.cited.forEach(function (t) { chips.appendChild(el('span', 'fr-tag-chip', t)); });
        li.appendChild(chips);
      }
      ol.appendChild(li);
    }
    l.entities.slice(0, 12).forEach(add);
    var further = l.entities.slice(12).filter(function (x) { return (x.cited || []).length; });
    if (further.length) {
      ol.appendChild(el('li', 'fr-small fr-ranked-gap', 'Further down, with a cited tag'));
      further.forEach(add);
    }
    card.appendChild(ol);
    return card;
  }

  function renderAudiences(box, audiences, lists) {
    box.textContent = '';
    audiences.forEach(function (a) {
      var card = el('section', 'fr-card fr-aud');
      card.appendChild(el('h3', 'fr-heading', audienceName(a)));
      card.appendChild(el('p', 'fr-small', 'Profile ' + a.id + (a.distinctive != null ? ' · ' + a.distinctive + ' distinctive titles' : '') + ' · source: Qloo'));
      if (a.pace && a.pace.n) {
        card.appendChild(paceBar(a.pace));
        // Runs before 2026-10-26 recorded only the top 12 titles of each list, so some counted titles cannot be shown.
        var counted = (a.pace.fast || 0) + (a.pace.slow || 0), shown = 0;
        (a.domains || []).forEach(function (d) { shown += (d.top || []).filter(function (t) { return t.pace; }).length; });
        if (a.domains && a.domains.length && shown < counted)
          card.appendChild(el('p', 'fr-small', shown + ' of the ' + counted + ' paced titles are listed below: this run recorded only the top of each list.'));
      }
      if (!a.domains || !a.domains.length) {
        card.appendChild(el('p', 'fr-small', 'This run recorded the counts only; runs from now on record the titles too.'));
      } else {
        var grid = el('div', 'fr-aud-domains');
        a.domains.forEach(function (d) {
          var col = el('div');
          col.appendChild(el('p', 'ps-label', domainName(d.type) + ' (' + d.n + ' distinctive)'));
          col.appendChild(ranked(d.top, 12, true));
          grid.appendChild(col);
        });
        card.appendChild(grid);
      }
      box.appendChild(card);
    });
    lists.forEach(function (l) { box.appendChild(listCard(l)); });
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

  // ── Verification: what the film shows, where each came from, and whether a watcher read it ──
  var SOURCE = { brief: 'Brief', document: 'Document', computed: 'Computed', qloo: 'Qloo', research: 'Research',
                 scale: 'Scale', 'own words': 'Own words', 'unsourced figure': 'No source' };
  function srcKey(s) { return String(s || 'own words').replace(/\s+/g, '-'); }
  var SVGNS = 'http://www.w3.org/2000/svg';
  function svgEl(name, attrs, parent) {
    var n = document.createElementNS(SVGNS, name);
    Object.keys(attrs || {}).forEach(function (k) { n.setAttribute(k, String(attrs[k])); });
    if (parent) parent.appendChild(n);
    return n;
  }

  // The film as a strip of time: each text a bar across the seconds it shows, coloured by its source and outlined by
  // whether the watcher read it; a settled stray a mark at its second. Built with DOM calls: every label is film text.
  function filmTimeline(film) {
    var items = (film.items || []).filter(function (i) { return i.from != null; });
    var duration = film.duration || items.reduce(function (m, i) { return Math.max(m, i.to || 0); }, 1);
    var W = 1000, barH = 18, gap = 4, axis = 22;
    var lanes = [];
    var placed = items.slice().sort(function (a, b) { return a.from - b.from; }).map(function (it) {
      var lane = 0;
      while (lanes[lane] != null && lanes[lane] > it.from - 0.05) lane++;
      lanes[lane] = it.to;
      return { it: it, lane: lane };
    });
    var height = axis + Math.max(lanes.length, 1) * (barH + gap) + 18;
    var svg = svgEl('svg', { viewBox: '0 0 ' + W + ' ' + height, 'class': 'fr-vtl', role: 'img',
                              'aria-label': 'Every text on screen across the film, coloured by its source' });
    var x = function (t) { return Math.max(0, Math.min(W, t / duration * W)); };
    var tick = duration > 40 ? 10 : duration > 12 ? 5 : 1;
    for (var t = 0; t <= duration + 1e-6; t += tick) {
      svgEl('line', { x1: x(t), x2: x(t), y1: axis - 6, y2: height - 4, 'class': 'vt-grid' }, svg);
      svgEl('text', { x: x(t) + 2, y: 12, 'class': 'vt-tick' }, svg).textContent = t + ' s';
    }
    placed.forEach(function (p) {
      var it = p.it, y = axis + p.lane * (barH + gap);
      var w = Math.max(x(it.to + 1 / 12) - x(it.from), 3);
      var g = svgEl('g', { 'class': 'vt-bar vt-src-' + srcKey(it.source) + (it.status === 'missed' || it.status === 'unchecked' ? ' vt-missed' : '') }, svg);
      svgEl('title', {}, g).textContent = it.text.replace(/\n/g, ' ') + ' — on screen ' + it.from.toFixed(1) + '–' + it.to.toFixed(1) + ' s · '
        + (SOURCE[it.source] || it.source) + (it.detail ? ' (' + it.detail + ')' : '') + ' · '
        + (it.status === 'seen' ? 'read by the watcher' + (it.readAs && it.readAs !== it.text ? ' as “' + it.readAs + '”' : '')
          : it.status === 'unchecked' ? UNCHECKED : 'not read by the watcher');
      svgEl('rect', { x: x(it.from), y: y, width: w, height: barH, rx: 3 }, g);
      if (w > 34) svgEl('text', { x: x(it.from) + 4, y: y + 13 }, g).textContent = clipText(it.text.replace(/\n/g, ' '), Math.floor(w / 6.2));
    });
    (film.strays || []).forEach(function (s) {
      if (s.at == null) return;
      var g = svgEl('g', { 'class': 'vt-stray vt-' + s.verdict }, svg);
      svgEl('title', {}, g).textContent = 'Read “' + s.read + '” at ' + s.at + ' s' + (s.near ? ' (the film set “' + s.near + '”)' : '')
        + ' — ' + (s.verdict === 'real' ? 'a fault in the film' : 'the watcher misread it') + (s.note ? ': ' + s.note : '');
      svgEl('line', { x1: x(s.at), x2: x(s.at), y1: axis - 4, y2: height - 4 }, g);
      svgEl('circle', { cx: x(s.at), cy: height - 10, r: 5 }, g);
    });
    return svg;
  }
  function clipText(s, n) { return s.length > n ? s.slice(0, Math.max(n - 1, 1)) + '…' : s; }

  // A still with every text it drew outlined where it was drawn, coloured by its source and dashed where the reader did
  // not read it: the still's counterpart of the film's strip of time. Built with DOM calls: every label is the piece's text.
  function imageMap(check) {
    var W = check.width || 1, H = check.height || 1;
    var svg = svgEl('svg', { viewBox: '0 0 ' + W + ' ' + H, 'class': 'fr-vimg', role: 'img',
                              'aria-label': 'The image as read, every text outlined by its source' });
    var pic = svgEl('image', { x: 0, y: 0, width: W, height: H }, svg);
    pic.setAttribute('href', base + '/runs/' + run + '/artifact/' + check.image);
    var stroke = Math.max(W, H) / 400;
    (check.items || []).forEach(function (it) {
      if (it.status === 'off the canvas') return;
      var g = svgEl('g', { 'class': 'vt-bar vt-src-' + srcKey(it.source) + (it.status === 'missed' || it.status === 'unchecked' ? ' vt-missed' : '') }, svg);
      svgEl('title', {}, g).textContent = it.text + ' · ' + (SOURCE[it.source] || it.source) + (it.detail ? ' (' + it.detail + ')' : '') + ' · '
        + (it.status === 'seen' ? 'read' + (it.readAs && it.readAs !== it.text ? ' as “' + it.readAs + '”' : ' as set')
          : it.status === 'unchecked' ? UNCHECKED : 'not read');
      (it.boxes && it.boxes.length ? it.boxes : [it.box]).forEach(function (b) {
        if (b) svgEl('rect', { x: b[0], y: b[1], width: b[2], height: b[3], rx: stroke, 'stroke-width': stroke }, g);
      });
    });
    return svg;
  }

  // What an Arabic line's status says: the reader misreads Arabic, so its line is checked by eye, not read back.
  var UNCHECKED = 'not checked: the reader cannot be trusted to read Arabic, so the studio checked it by eye';

  // The summary line and the legend, shared by a film's card and a still's.
  function verifySummary(check, what) {
    var items = check.items || [];
    var seen = items.filter(function (i) { return i.status === 'seen'; }).length;
    var unchecked = items.filter(function (i) { return i.status === 'unchecked'; }).length;
    var unsourced = items.filter(function (i) { return i.source === 'unsourced figure'; }).length;
    var real = (check.strays || []).filter(function (s) { return s.verdict === 'real'; }).length;
    return items.length + ' texts ' + what + ' · ' + seen + ' read back as set by an independent ' + (check.video ? 'watcher' : 'reader')
      + (check.model ? ' (' + check.model + ')' : '')
      + (unchecked ? ' · ' + unchecked + ' in Arabic, which the reader cannot check' : '')
      + ' · ' + (unsourced ? unsourced + ' figure' + (unsourced === 1 ? '' : 's') + ' with no source' : 'every figure sourced')
      + (real ? ' · ' + real + ' fault' + (real === 1 ? '' : 's') + ' found' : '')
      + (check.pending ? ' · ' + check.pending + ' reading' + (check.pending === 1 ? '' : 's') + ' still to be looked at' : '');
  }
  function verifyLegend(items) {
    var legend = el('div', 'fr-vlegend');
    Object.keys(SOURCE).forEach(function (k) {
      if (!items.some(function (i) { return (i.source || 'own words') === k; })) return;
      legend.appendChild(el('span', 'fr-vkey vt-src-' + srcKey(k), SOURCE[k]));
    });
    legend.appendChild(el('span', 'fr-vkey fr-vkey-missed', 'not read back'));
    return legend;
  }

  // A derivation as its steps in order, each joined to the next by what the check found: proved, agreeing at every point
  // tried, or differing at a point it names. Built with DOM calls: every step is the agent's mathematics.
  var STEP = { proved: 'proved', agrees: 'agrees at every point tried', differs: 'does not follow', unsettled: 'not settled',
               unread: 'could not be read' };
  function stepNumber(x) {
    if (typeof x !== 'number') return String(x);
    var s = Math.abs(x) >= 1e5 || (x !== 0 && Math.abs(x) < 1e-3) ? x.toExponential(3) : String(+x.toPrecision(6));
    return s.replace('-', '−');
  }
  function stepWhy(p, equations) {
    var why = STEP[p.verdict] || p.verdict;
    if (p.verdict === 'proved' && p.provided) why += ', provided ' + p.provided;
    if (p.verdict === 'differs') {
      var at = p.at ? Object.keys(p.at).map(function (k) { return k + ' = ' + stepNumber(p.at[k]); }).join(', ') : '';
      if (at) why += ': at ' + at;
      // Two equations' values are each one's lhs − rhs there; elsewhere they kept another ratio, so a solution changed.
      if (p.values && p.values.length === 2) why += (at ? ', ' : ': ') + (equations
        ? 'lhs − rhs is ' + stepNumber(p.values[0]) + ' in one and ' + stepNumber(p.values[1]) + ' in the other, not the ratio they keep elsewhere, so a solution was gained or lost'
        : 'the two sides give ' + stepNumber(p.values[0]) + ' and ' + stepNumber(p.values[1]));
    }
    if (p.approximate) why += ' (an integral evaluated numerically)';
    return why;
  }
  function derivationCard(d) {
    var card = el('section', 'fr-card fr-verify fr-deriv');
    card.appendChild(el('h3', 'fr-heading', d.name));
    var counts = d.counts || {};
    var tally = ['proved', 'agrees', 'differs', 'unsettled', 'unread'].filter(function (k) { return counts[k]; })
      .map(function (k) { return counts[k] + ' ' + (k === 'agrees' ? 'agree without proof' : k === 'differs' ? 'do not follow' : k); });
    var head = el('p', 'fr-small');
    head.appendChild(el('span', 'fr-vcheck ' + (d.passed ? 'is-pass' : 'is-fail'), d.passed ? 'Pass' : 'Fail'));
    head.appendChild(document.createTextNode(' ' + (d.passed ? 'Every step follows from the last' : 'A step does not follow from the last')
      + ' · ' + (d.steps || []).length + ' steps · ' + tally.join(' · ') + ' · checked by computer algebra in the engine, not by a model'));
    card.appendChild(head);

    var list = el('div', 'fr-dsteps');
    (d.steps || []).forEach(function (s, i) {
      list.appendChild(el('div', 'fr-dstep', s));
      var p = (d.pairs || [])[i];
      if (!p) return;
      // Two equations in a row are a step of a solution: ⇔ where it holds, not =.
      var equations = / = /.test(s) && / = /.test(d.steps[i + 1] || '');
      var mark = { proved: equations ? '⇔' : '=', agrees: equations ? '⇔' : '≈', differs: equations ? '⇎' : '≠' }[p.verdict] || '?';
      var link = el('div', 'fr-dlink is-' + p.verdict);
      link.title = p.detail || '';
      link.appendChild(el('span', 'fr-dmark', mark));
      link.appendChild(el('span', 'fr-small', stepWhy(p, equations)));
      if (p.read) {
        link.appendChild(el('span', 'fr-small fr-dread', 'As the watcher read it: “' + (p.read.a || '?') + '” to “' + (p.read.b || '?') + '”, '
          + (p.read.verdict === 'differs' ? 'which does not follow: the reading is wrong here, not the mathematics' : STEP[p.read.verdict] || p.read.verdict)));
      }
      list.appendChild(link);
    });
    card.appendChild(list);
    return card;
  }

  // Claims checked by code a second model wrote: each claim, its verdict, what each check found, and — folded, but one
  // click away — the code itself, since the code is the only place a check of the wrong thing would show.
  var CLAIM = { holds: ['Holds', 'is-pass'], fails: ['Fails', 'is-fail'], unchecked: ['Not checked', 'is-kept'],
                error: ['Did not run', 'is-kept'], refused: ['Refused', 'is-fail'] };
  function claimsCard(claims) {
    var card = el('section', 'fr-card fr-verify fr-claims');
    card.appendChild(el('h3', 'fr-heading', 'Claims, checked by code'));
    var model = claims.map(function (c) { return c.model; }).filter(Boolean)[0];
    card.appendChild(el('p', 'fr-small', 'Each claim the piece makes in words, checked by JavaScript that ' + (model || 'Nemotron')
      + ' wrote and the engine ran with nothing but its maths routines. The verdict is the code’s; the code is shown so what was checked can be read.'));
    var list = el('ul', 'fr-vchecks');
    claims.forEach(function (c) {
      var li = el('li');
      var chip = CLAIM[c.verdict] || [c.verdict, 'is-kept'];
      li.appendChild(el('span', 'fr-vcheck ' + chip[1], chip[0]));
      var body = el('div');
      body.appendChild(el('span', 'fr-claim', c.claim));
      if (c.context) body.appendChild(el('p', 'fr-small', 'Given: ' + c.context));
      if ((c.checks || []).length) {
        var ul = el('ul', 'fr-claimchecks');
        c.checks.forEach(function (k) {
          ul.appendChild(el('li', 'fr-small ' + (k.passed ? 'is-pass' : 'is-fail'), (k.passed ? '✓ ' : '✗ ') + k.what + (k.detail ? ' — ' + k.detail : '')));
        });
        body.appendChild(ul);
      }
      if (c.error) body.appendChild(el('p', 'fr-small', c.error));
      if (c.code) {
        var d = el('details'); d.appendChild(el('summary', 'fr-small', 'the code that checked it'));
        d.appendChild(el('pre', 'fr-code', c.code)); body.appendChild(d);
      }
      li.appendChild(body); list.appendChild(li);
    });
    card.appendChild(list);
    return card;
  }

  function renderVerification(box, v) {
    box.textContent = '';
    (v.films || []).forEach(function (film) {
      var items = film.items || [];
      var card = el('section', 'fr-card fr-verify');
      card.appendChild(el('h3', 'fr-heading', film.video));
      card.appendChild(el('p', 'fr-small', verifySummary(film, 'on screen')));
      card.appendChild(verifyLegend(items));
      var scroll = el('div', 'fr-evscroll'); scroll.appendChild(filmTimeline(film)); card.appendChild(scroll);

      var table = el('table', 'fr-vtable');
      var head = el('tr');
      ['Text on screen', 'Seconds', 'Source', 'Read back'].forEach(function (h) { head.appendChild(el('th', '', h)); });
      table.appendChild(head);
      items.forEach(function (it) {
        var tr = el('tr', it.source === 'unsourced figure' ? 'is-flag' : '');
        tr.appendChild(el('td', 'fr-vtext', it.text.replace(/\n/g, ' / ')));
        tr.appendChild(el('td', 'fr-spec', it.from == null ? 'never shown' : it.from.toFixed(1) + '–' + it.to.toFixed(1)));
        var src = el('td'); src.appendChild(el('span', 'fr-vkey vt-src-' + srcKey(it.source), SOURCE[it.source] || it.source));
        if (it.detail) src.appendChild(el('span', 'fr-small', ' ' + it.detail));
        tr.appendChild(src);
        tr.appendChild(el('td', '', it.status === 'seen' ? (it.readAs && it.readAs.replace(/\s+/g, ' ') !== it.text.replace(/\s+/g, ' ') ? '✓ as “' + it.readAs + '”' : '✓ as set')
          : it.status === 'missed' ? 'not read (' + (it.seconds || 0).toFixed(1) + ' s on screen)'
          : it.status === 'unchecked' ? 'not checked: Arabic, checked by eye' : '—'));
        table.appendChild(tr);
      });
      card.appendChild(table);

      if ((film.strays || []).length) {
        card.appendChild(el('p', 'ps-label', 'Readings of text the film never set, after looking at the frame'));
        var list = el('div', 'fr-vstrays');
        film.strays.forEach(function (s) {
          var row = el('figure', 'fr-vstray vt-' + s.verdict);
          if (s.frame) { var img = el('img'); img.loading = 'lazy'; img.alt = 'The frame at ' + s.at + ' s'; img.src = base + '/runs/' + run + '/artifact/' + s.frame; row.appendChild(img); }
          var cap = el('figcaption', 'fr-small');
          cap.textContent = 'Read “' + s.read + '” at ' + s.at + ' s' + (s.near ? '; the film set “' + s.near + '”' : '') + '. '
            + (s.verdict === 'real' ? 'A fault in the film.' : s.kind === 'unreadable script'
              ? 'Arabic, which the watcher cannot read reliably: the studio looked at the frame and it shows the line as set.'
              : 'The frame shows what the film set: the watcher misread it.') + (s.note ? ' ' + s.note : '');
          row.appendChild(cap);
          list.appendChild(row);
        });
        card.appendChild(list);
      }
      box.appendChild(card);
    });

    (v.images || []).forEach(function (check) {
      var items = check.items || [];
      var card = el('section', 'fr-card fr-verify');
      card.appendChild(el('h3', 'fr-heading', check.image));
      card.appendChild(el('p', 'fr-small', verifySummary(check, 'on the image')));
      card.appendChild(verifyLegend(items));
      card.appendChild(imageMap(check));

      var table = el('table', 'fr-vtable');
      var head = el('tr');
      ['Text on the image', 'Size', 'Source', 'Read back'].forEach(function (h) { head.appendChild(el('th', '', h)); });
      table.appendChild(head);
      items.forEach(function (it) {
        var tr = el('tr', it.source === 'unsourced figure' ? 'is-flag' : '');
        tr.appendChild(el('td', 'fr-vtext', it.text + (it.count > 1 ? ' (×' + it.count + ')' : '')));
        tr.appendChild(el('td', 'fr-spec', it.size != null ? it.size + ' px' : '—'));
        var src = el('td'); src.appendChild(el('span', 'fr-vkey vt-src-' + srcKey(it.source), SOURCE[it.source] || it.source));
        if (it.detail) src.appendChild(el('span', 'fr-small', ' ' + it.detail));
        tr.appendChild(src);
        tr.appendChild(el('td', '', it.status === 'seen' ? (it.readAs && it.readAs.replace(/\s+/g, ' ') !== it.text.replace(/\s+/g, ' ') ? '✓ as “' + it.readAs + '”' : '✓ as set')
          : it.status === 'missed' ? 'not read' + (it.closest ? ' (closest: “' + it.closest + '”)' : '')
          : it.status === 'unchecked' ? 'not checked: Arabic, checked by eye' : 'off the canvas'));
        table.appendChild(tr);
      });
      card.appendChild(table);

      if ((check.strays || []).length) {
        card.appendChild(el('p', 'ps-label', 'Readings of text the image never set, after looking at the crop'));
        var list = el('div', 'fr-vstrays');
        check.strays.forEach(function (s) {
          var row = el('figure', 'fr-vstray vt-' + s.verdict);
          if (s.frame) { var img = el('img'); img.loading = 'lazy'; img.alt = 'Where it was read'; img.src = base + '/runs/' + run + '/artifact/' + s.frame; row.appendChild(img); }
          var cap = el('figcaption', 'fr-small');
          cap.textContent = 'Read “' + s.read + '”' + (s.near ? '; the image set “' + s.near + '”' : '') + '. '
            + (s.verdict === 'real' ? 'A fault in the image.' : 'The image shows what was set: the reader misread it.') + (s.note ? ' ' + s.note : '');
          row.appendChild(cap);
          list.appendChild(row);
        });
        card.appendChild(list);
      }
      box.appendChild(card);
    });

    (v.derivations || []).forEach(function (d) { box.appendChild(derivationCard(d)); });
    if ((v.claims || []).length) box.appendChild(claimsCard(v.claims));

    if ((v.checks || []).length) {
      var cc = el('section', 'fr-card fr-verify');
      cc.appendChild(el('h3', 'fr-heading', 'Checks the studio ran'));
      var ul = el('ul', 'fr-vchecks');
      v.checks.forEach(function (c) {
        var li = el('li');
        li.appendChild(el('span', 'fr-vcheck ' + (c.passed ? 'is-pass' : c.accepted ? 'is-kept' : 'is-fail'), c.passed ? 'Pass' : c.accepted ? 'Kept' : 'Fail'));
        var body = el('div');
        body.appendChild(el('span', '', c.claim));
        if (c.judged) body.appendChild(el('span', 'fr-vjudged', 'judged by a model'));
        if (c.detail || c.reason) {
          var d = el('details'); d.appendChild(el('summary', 'fr-small', 'what was measured'));
          d.appendChild(el('p', 'fr-small', (c.detail || '') + (c.reason ? ' — kept because: ' + c.reason : ''))); body.appendChild(d);
        }
        li.appendChild(body); ul.appendChild(li);
      });
      cc.appendChild(ul);
      box.appendChild(cc);
    }

    var rc = v.references, docs = v.documents || [];
    if (rc || docs.length) {
      var sc = el('section', 'fr-card fr-verify');
      sc.appendChild(el('h3', 'fr-heading', 'Sources'));
      if (rc) {
        var p = el('p', 'fr-small', rc.passed ? 'All ' + rc.checked + ' Qloo references in the direction were returned by the queries they cite. '
          : rc.unverified + ' Qloo reference' + (rc.unverified === 1 ? ' was' : 's were') + ' not found in the query cited. ');
        var link = el('button', 'ps-btn ps-btn-quiet ps-btn-sm', 'See the evidence map'); link.type = 'button';
        link.addEventListener('click', function () { select('choices'); });
        p.appendChild(link); sc.appendChild(p);
      }
      docs.forEach(function (d) { sc.appendChild(el('p', 'fr-small', d.document + ' — read by ' + (d.model || 'a model') + ', every figure checked against its page')); });
      box.appendChild(sc);
    }
  }

  var tasteLoading = null;
  function loadTaste() {
    if (tasteLoading) return tasteLoading;
    loadLearned();
    tasteLoading = fetch(base + '/requests/' + encodeURIComponent(page.dataset.name) + '/taste')
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data || !views) return;
        var shown = {
          audiences: (data.audiences || []).length + (data.lists || []).length > 0,
          contrast: (data.contrasts || []).length > 0,
          choices: !!(data.map && (data.map.choices || []).length),
          verification: !!(data.verification && ((data.verification.films || []).length || (data.verification.images || []).length
            || (data.verification.derivations || []).length || (data.verification.claims || []).length
            || (data.verification.checks || []).length))
        };
        Object.keys(shown).forEach(function (v) {
          var tab = views.querySelector('.ps-tab[data-view="' + v + '"]'); if (tab) tab.hidden = !shown[v];
        });
        if (shown.audiences) renderAudiences(page.querySelector('[data-audiences]'), data.audiences || [], data.lists || []);
        if (shown.contrast) renderContrast(page.querySelector('[data-contrast]'), data.contrasts, data.audiences || []);
        if (shown.verification) renderVerification(page.querySelector('[data-verification]'), data.verification);
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
  ['taste.query', 'taste.contrast', 'taste.check', 'film.verify', 'film.stray', 'image.verify', 'image.stray', 'derivation.verify', 'claim.verify', 'check'].forEach(function (kind) {
    on(kind, function () { clearTimeout(tasteTimer); tasteTimer = setTimeout(loadTaste, 800); });
  });
  // Choices is drawn from direction.md, which the agent writes with its own file tools, not through the engine, so no
  // event above marks it: nightshift5's last edit came after its last check and an open page never showed it. The
  // record has the call, not its result, so the reload waits for the write to land.
  on('tool.call', function (e) {
    var args = e.args || {};
    if (Object.keys(args).some(function (k) { return typeof args[k] === 'string' && /(^|[\\/])direction\.md$/.test(args[k]); })) {
      clearTimeout(tasteTimer); tasteTimer = setTimeout(loadTaste, 2500);
    }
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
