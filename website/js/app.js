/*
 * Wiring: pickers, drag-and-drop, filters, tabs, keyboard navigation, theme.
 *
 * The viewer holds one list of loaded runs and one "current" trace. Every view is told about the
 * current trace and reports selections back here, so the details pane always follows whatever
 * was last clicked - in the tree, the methods table, or the raw event list.
 */
(function (PFT) {
  'use strict';

  var fmt = PFT.format;

  var el = {};
  var runs = [];
  var current = null;
  var treeView = null;
  var flowView = null;
  var methodsView = null;
  var briefView = null;
  var eventsView = null;
  var searchTimer = null;

  // The directory the current trace was read from, when we have a re-readable handle to it.
  // This is what makes "Reload" possible - see js/fsaccess.js.
  var directoryHandle = null;

  function byId(id) { return document.getElementById(id); }

  /**
   * Makes a view render only while its tab is on screen.
   *
   * The toolbar filter feeds every view, so one keystroke used to re-render all five - and four
   * of them are hidden. On a moderate run that was ~114 ms per keystroke, of which ~92 ms was
   * work nobody could see. Hidden views now just record that they are out of date and catch up
   * when their tab is opened.
   *
   * Wrapping the instance (rather than each view class) keeps this in one place and leaves the
   * five view modules unaware of tab state.
   */
  function deferWhileHidden(view) {
    var render = view.render.bind(view);
    view.isActive = false;
    view.needsRender = true;

    view.render = function () {
      if (!view.isActive) { view.needsRender = true; return; }
      view.needsRender = false;
      render();
    };

    // Some views do expensive work *before* rendering - the flowchart rebuilds and re-partitions
    // its graph - which the render guard alone would not catch.
    var rebuild = typeof view.repartition === 'function' ? view.repartition.bind(view) : null;
    if (rebuild) {
      view.needsRebuild = true;
      view.repartition = function () {
        if (!view.isActive) { view.needsRebuild = true; view.needsRender = true; return; }
        view.needsRebuild = false;
        rebuild();
      };
    }

    view.setActive = function (active) {
      view.isActive = active;
      if (!active) { return; }

      // Rebuild before render, since rebuilding produces what render draws.
      if (rebuild && view.needsRebuild) { view.repartition(); }
      else if (view.needsRender) { view.render(); }
    };

    return view;
  }

  function cacheElements() {
    ['layout', 'welcome', 'welcomeError', 'dropveil', 'runSummary', 'runFacts', 'runPickerWrap',
     'runSelect', 'search', 'searchHint', 'useRegex', 'onlyErrors', 'minDuration', 'threadFilter', 'tabCount',
     'treeView', 'flowView', 'methodsView', 'briefView', 'eventsView', 'detailsView', 'filePicker', 'dirPicker',
     'btnOpenFolder', 'btnOpenFiles', 'btnOpenFolder2', 'btnOpenFiles2', 'btnSample', 'btnTheme',
     'btnExpand', 'btnCollapse', 'btnReload', 'location', 'locationName', 'welcomeReopen',
     'btnReopen', 'btnForget', 'toast', 'splitter'].forEach(function (id) {
      el[id] = byId(id);
    });
  }

  /* ---------------------------------------------------------------- theme */

  function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    try { localStorage.setItem('pft-theme', theme); } catch (err) { /* private mode */ }
  }

  function initTheme() {
    var stored = null;
    try { stored = localStorage.getItem('pft-theme'); } catch (err) { /* private mode */ }
    if (stored) { document.documentElement.setAttribute('data-theme', stored); }

    el.btnTheme.addEventListener('click', function () {
      var currentTheme = document.documentElement.getAttribute('data-theme');
      if (!currentTheme) {
        currentTheme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
      }
      applyTheme(currentTheme === 'dark' ? 'light' : 'dark');
    });
  }

  /* ----------------------------------------------------------- loading UI */

  var toastTimer = null;

  /**
   * Errors go wherever the user is actually looking: onto the welcome card before a trace is
   * open, and into a transient toast once it is - the welcome card is hidden by then, so writing
   * only there would swallow the message.
   */
  function showError(message) {
    if (el.welcome.hidden) {
      el.toast.textContent = message;
      el.toast.hidden = false;
      clearTimeout(toastTimer);
      toastTimer = setTimeout(function () { el.toast.hidden = true; }, 8000);
      return;
    }

    el.welcomeError.textContent = message;
    el.welcomeError.hidden = false;
  }

  function clearError() {
    el.welcomeError.hidden = true;
    el.welcomeError.textContent = '';
    el.toast.hidden = true;
    clearTimeout(toastTimer);
  }

  /**
   * The single place a set of loaded runs becomes the visible trace. Every load path - folder
   * handle, file picker, drop, bundled sample, ?run= URL - ends here.
   *
   * On a reload the same run is kept selected when it is still present, so re-reading a folder
   * does not throw away where you were.
   */
  function showRuns(loadedRuns, keepRunId) {
    if (!loadedRuns || !loadedRuns.length) {
      showError('No readable events.jsonl was found there.');
      return false;
    }

    runs = loadedRuns;
    populateRunPicker();

    var index = runs.length - 1;
    if (keepRunId) {
      for (var i = 0; i < runs.length; i++) {
        var meta = runs[i].metadata;
        if (meta && meta.runId === keepRunId) { index = i; break; }
      }
    }

    selectRun(index);
    el.welcome.hidden = true;
    el.layout.hidden = false;
    el.runSummary.hidden = false;
    return true;
  }

  function handleFiles(files) {
    clearError();
    if (!files || !files.length) { return; }

    PFT.loader.loadFiles(files).then(function (loadedRuns) {
      setDirectoryHandle(null);
      showRuns(loadedRuns);
    }).catch(function (err) {
      showError(err && err.message ? err.message : String(err));
    });
  }

  /* ------------------------------------------------------- folder location */

  /** Reflects whether the current trace came from a folder we can re-read. */
  function setDirectoryHandle(handle) {
    directoryHandle = handle || null;
    el.btnReload.hidden = !directoryHandle;
    el.location.hidden = !directoryHandle;
    if (directoryHandle) {
      el.locationName.textContent = directoryHandle.name;
      el.location.title = 'Reading from the folder "' + directoryHandle.name +
        '". Browsers do not expose its full path.';
    }
  }

  /**
   * Reads a directory handle and shows what is in it. Used by the picker, by drag-and-drop, by
   * "Reopen", and by "Reload" - the only difference between them is where the handle came from.
   */
  function openDirectory(handle, keepRunId) {
    clearError();

    return PFT.fsaccess.ensureReadable(handle).then(function (granted) {
      if (!granted) {
        showError('Permission to read "' + handle.name + '" was declined.');
        return false;
      }
      return PFT.fsaccess.read(handle).then(function (result) {
        var loadedRuns = PFT.loader.assembleRuns(result.items);
        if (!showRuns(loadedRuns, keepRunId)) {
          showError('No events.jsonl was found in "' + handle.name +
            '". Pick a run directory, or the runs/ directory above it.');
          return false;
        }

        setDirectoryHandle(handle);
        PFT.fsaccess.remember(handle);
        hideReopen();

        if (result.truncated) {
          showError('That folder was too large to scan completely - only part of it was read. ' +
            'Pick the runs/ directory itself rather than a whole project.');
        }
        return true;
      });
    }).catch(function (err) {
      showError('Could not read "' + handle.name + '": ' + (err && err.message ? err.message : err));
      return false;
    });
  }

  function openFolder() {
    clearError();

    if (!PFT.fsaccess.isSupported()) {
      // Firefox and Safari: fall back to the one-shot directory picker, which loads the same
      // files but gives us nothing to re-read later.
      el.dirPicker.click();
      return;
    }

    PFT.fsaccess.pick().then(function (handle) {
      if (handle) { openDirectory(handle); }
    }, function () {
      // The picker was dismissed - not an error worth reporting.
    });
  }

  function reload() {
    if (!directoryHandle) { return; }

    var keepRunId = current && current.metadata ? current.metadata.runId : null;
    el.btnReload.disabled = true;
    openDirectory(directoryHandle, keepRunId).then(function () {
      el.btnReload.disabled = false;
      applyFilter();
    }, function () {
      el.btnReload.disabled = false;
    });
  }

  function hideReopen() {
    el.welcomeReopen.hidden = true;
  }

  /** Offers the last folder back on startup. Re-granting read access needs a click, so this is
   * only ever a button - never an automatic load. */
  function offerLastDirectory() {
    PFT.fsaccess.recall().then(function (handle) {
      if (!handle) { return; }

      el.btnReopen.textContent = 'Reopen "' + handle.name + '"';
      el.welcomeReopen.hidden = false;

      el.btnReopen.onclick = function () { openDirectory(handle); };
      el.btnForget.onclick = function () {
        PFT.fsaccess.forget();
        hideReopen();
      };
    });
  }

  function populateRunPicker() {
    el.runSelect.innerHTML = runs.map(function (trace, index) {
      return '<option value="' + index + '">' + fmt.escape(trace.sourceLabel) + '</option>';
    }).join('');
    el.runPickerWrap.hidden = runs.length < 2;
  }

  function selectRun(index) {
    current = runs[index];
    if (!current) { return; }

    el.runSelect.value = String(index);
    renderRunFacts(current);

    el.threadFilter.innerHTML = '<option value="">all</option>' + current.threads.map(function (id) {
      return '<option value="' + id + '">thread ' + id + '</option>';
    }).join('');

    treeView.setTrace(current);
    flowView.setTrace(current);
    methodsView.setTrace(current);
    briefView.setTrace(current);
    eventsView.setTrace(current);
    PFT.details.renderEmpty(el.detailsView, current);
    applyFilter();
    updateCounts();
  }

  function renderRunFacts(trace) {
    var meta = trace.metadata || {};
    var facts = [
      ['Application', meta.application || '—'],
      ['Run', (meta.runId || '').slice(0, 8) || '—'],
      ['Started', meta.startedAtUtc ? fmt.timestamp(meta.startedAtUtc) : '—'],
      ['Events', fmt.count(trace.events.length)],
      ['Calls', fmt.count(trace.invocations.length)]
    ];

    if (meta.droppedEventCount) {
      facts.push(['Dropped', fmt.count(meta.droppedEventCount)]);
    }

    el.runFacts.innerHTML = facts.map(function (pair) {
      var isWarning = pair[0] === 'Dropped';
      return '<div class="run-fact' + (isWarning ? ' is-warning' : '') + '">' +
        '<dt>' + fmt.escape(pair[0]) + '</dt><dd>' + fmt.escape(pair[1]) + '</dd></div>';
    }).join('');
  }

  function updateCounts() {
    if (!current) { el.tabCount.textContent = ''; return; }

    var active = document.querySelector('.tab.is-active');
    var view = active ? active.getAttribute('data-view') : 'tree';

    var filtered = filterResult.active;
    var matchedMethodCount = filtered ? Object.keys(filterResult.matchedMethods).length : current.stats.length;

    if (view === 'tree') {
      el.tabCount.textContent = filtered
        ? fmt.count(filterResult.matchCount) + ' of ' + fmt.count(current.invocations.length) + ' calls match'
        : fmt.count(current.invocations.length) + ' calls';
    } else if (view === 'flow') {
      el.tabCount.textContent = filtered
        ? fmt.count(matchedMethodCount) + ' of ' + fmt.count(current.stats.length) + ' methods shown'
        : fmt.count(current.stats.length) + ' methods, ' + fmt.count(current.invocations.length) + ' calls';
    } else if (view === 'methods') {
      el.tabCount.textContent = filtered
        ? fmt.count(matchedMethodCount) + ' of ' + fmt.count(current.stats.length) + ' methods'
        : fmt.count(current.stats.length) + ' methods';
    } else if (view === 'brief') {
      el.tabCount.textContent = filtered ? 'filtered summary' : 'paste-ready summary';
    } else {
      el.tabCount.textContent = filtered
        ? fmt.count(eventsView.visibleEvents().length) + ' of ' + fmt.count(current.events.length) + ' events'
        : fmt.count(current.events.length) + ' events';
    }
  }

  /* ------------------------------------------------------- drag and drop */

  function initDragAndDrop() {
    var depth = 0;

    window.addEventListener('dragenter', function (event) {
      event.preventDefault();
      depth++;
      el.dropveil.hidden = false;
    });

    window.addEventListener('dragover', function (event) {
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
    });

    window.addEventListener('dragleave', function (event) {
      event.preventDefault();

      // relatedTarget is null when the pointer left the window altogether, which is the one case
      // the enter/leave counter cannot see the end of - drop it straight back to zero so the
      // veil can never be left stuck over the page.
      depth = event.relatedTarget ? Math.max(0, depth - 1) : 0;
      if (!depth) { el.dropveil.hidden = true; }
    });

    window.addEventListener('dragend', function () {
      depth = 0;
      el.dropveil.hidden = true;
    });

    window.addEventListener('drop', function (event) {
      event.preventDefault();
      depth = 0;
      el.dropveil.hidden = true;

      var dataTransfer = event.dataTransfer;

      // A dropped folder can carry a re-readable handle. Prefer it, so dropping a run gets the
      // same Reload/Reopen behaviour as picking one.
      PFT.fsaccess.handleFromDrop(dataTransfer).then(function (handle) {
        if (handle) { return openDirectory(handle); }

        return PFT.loader.filesFromDataTransfer(dataTransfer).then(handleFiles, function (err) {
          showError('Could not read the dropped items: ' + (err && err.message ? err.message : err));
        });
      });
    });
  }

  /* -------------------------------------------------------------- filters */

  function currentFilter() {
    return {
      // Deliberately not lowercased here: in regex mode that would corrupt the pattern. The tree
      // lowercases it itself for plain-substring matching.
      text: el.search.value.trim(),
      regex: el.useRegex.checked,
      errorsOnly: el.onlyErrors.checked,
      minDurationUs: Number(el.minDuration.value) || 0,
      threadId: el.threadFilter.value
    };
  }

  var filterResult = { active: false, matchCount: 0 };

  /**
   * One filter pass, shared by every tab. The toolbar sits above the tabs, so it has to mean the
   * same thing in all of them - and scanning the trace once here is also the only way a large run
   * stays responsive while typing.
   */
  function applyFilter() {
    if (!current) { return; }

    var compiled = PFT.filter.compile(currentFilter());
    filterResult = PFT.filter.apply(current, compiled);
    filterResult.compiled = compiled;

    treeView.setFilter(filterResult);
    flowView.setFilter(filterResult);
    methodsView.setFilter(filterResult);
    briefView.setFilter(filterResult);
    eventsView.setFilter(filterResult);

    var invalid = Boolean(compiled.error);
    el.search.classList.toggle('is-invalid', invalid);
    el.search.setAttribute('aria-invalid', invalid ? 'true' : 'false');

    if (invalid) {
      el.searchHint.textContent = compiled.error;
      el.searchHint.classList.add('is-error');
    } else {
      // Only meaningful for the tree, and only accurate while the tree is the rendered tab.
      el.searchHint.textContent = (treeView.isActive && treeView.truncated) ? 'showing first rows only' : '';
      el.searchHint.classList.remove('is-error');
    }

    updateCounts();
  }

  function initFilters() {
    el.search.addEventListener('input', function () {
      clearTimeout(searchTimer);
      // Debounced: a filter pass rebuilds every visible row, and people type faster than that.
      searchTimer = setTimeout(applyFilter, 120);
    });

    el.useRegex.addEventListener('change', function () {
      el.search.placeholder = el.useRegex.checked
        ? 'Regular expression, e.g. ^Order.*(Async|Batch)$'
        : 'Filter by method, type, file, parameter…';
      applyFilter();
    });

    el.onlyErrors.addEventListener('change', applyFilter);
    el.minDuration.addEventListener('change', applyFilter);
    el.threadFilter.addEventListener('change', applyFilter);

    el.btnExpand.addEventListener('click', function () { treeView.expandAll(); updateCounts(); });
    el.btnCollapse.addEventListener('click', function () { treeView.collapseAll(); updateCounts(); });
  }

  /* ----------------------------------------------------------------- tabs */

  function initTabs() {
    var views = {
      tree: el.treeView, flow: el.flowView, methods: el.methodsView,
      brief: el.briefView, events: el.eventsView
    };

    var instances = [
      { name: 'tree', view: treeView },
      { name: 'flow', view: flowView },
      { name: 'methods', view: methodsView },
      { name: 'brief', view: briefView },
      { name: 'events', view: eventsView }
    ];

    document.querySelectorAll('.tab').forEach(function (tab) {
      tab.addEventListener('click', function () {
        document.querySelectorAll('.tab').forEach(function (other) { other.classList.remove('is-active'); });
        tab.classList.add('is-active');

        var target = tab.getAttribute('data-view');
        Object.keys(views).forEach(function (name) {
          views[name].classList.toggle('hidden', name !== target);
        });

        // Unhide first, then activate: a view that catches up on a deferred render needs its
        // container to have real dimensions while it does so.
        instances.forEach(function (entry) {
          entry.view.setActive(entry.name === target);
        });

        // The flowchart sizes itself to its container, which measures zero while the pane is
        // hidden - so it has to be fitted once it is actually on screen.
        if (target === 'flow') { flowView.fit(); }

        updateCounts();
      });
    });
  }

  /* ------------------------------------------------------------- splitter */

  // The layout puts the details pane beside the main view when there is room and underneath it
  // when there is not, so the splitter resizes whichever axis is currently in play.
  var AXES = {
    width: { property: '--details-width', storageKey: 'pft-details-width', fallback: 460, minimum: 300, keepFree: 360 },
    height: { property: '--details-height', storageKey: 'pft-details-height', fallback: 320, minimum: 160, keepFree: 200 }
  };

  function isStacked() {
    return window.matchMedia('(max-width: 1080px)').matches;
  }

  function currentAxis() {
    return isStacked() ? AXES.height : AXES.width;
  }

  function setDetailsSize(size, axis) {
    var target = axis || currentAxis();
    var available = target === AXES.height ? window.innerHeight : window.innerWidth;

    // Never let either pane be squeezed out of existence, however far the drag goes.
    var maximum = Math.max(target.minimum, available - target.keepFree);
    var clamped = Math.round(Math.min(Math.max(size, target.minimum), maximum));

    document.documentElement.style.setProperty(target.property, clamped + 'px');
    try { localStorage.setItem(target.storageKey, String(clamped)); } catch (err) { /* private mode */ }

    // The flowchart sizes itself to its container, so it has to be refitted after a resize.
    if (flowView) { flowView.fit(); }
  }

  function currentSize(axis) {
    var value = parseInt(getComputedStyle(document.documentElement)
      .getPropertyValue(axis.property), 10);
    return value || axis.fallback;
  }

  function initSplitter() {
    Object.keys(AXES).forEach(function (name) {
      var axis = AXES[name];
      var stored = null;
      try { stored = localStorage.getItem(axis.storageKey); } catch (err) { /* private mode */ }
      if (stored) { setDetailsSize(Number(stored), axis); }
    });

    var dragging = false;

    el.splitter.addEventListener('mousedown', function (event) {
      event.preventDefault();
      dragging = true;
      el.splitter.classList.add('is-dragging');
      document.body.classList.add('is-resizing');
    });

    window.addEventListener('mousemove', function (event) {
      if (!dragging) { return; }
      setDetailsSize(isStacked()
        ? window.innerHeight - event.clientY
        : window.innerWidth - event.clientX);
    });

    window.addEventListener('mouseup', function () {
      if (!dragging) { return; }
      dragging = false;
      el.splitter.classList.remove('is-dragging');
      document.body.classList.remove('is-resizing');
    });

    el.splitter.addEventListener('dblclick', function () {
      var axis = currentAxis();
      setDetailsSize(axis.fallback, axis);
    });

    // Keyboard-reachable, so the pane is resizable without a mouse.
    el.splitter.addEventListener('keydown', function (event) {
      var axis = currentAxis();
      var size = currentSize(axis);
      var grow = axis === AXES.height ? 'ArrowUp' : 'ArrowLeft';
      var shrink = axis === AXES.height ? 'ArrowDown' : 'ArrowRight';

      if (event.key === grow) { event.preventDefault(); setDetailsSize(size + 40, axis); }
      else if (event.key === shrink) { event.preventDefault(); setDetailsSize(size - 40, axis); }
      else if (event.key === 'Home') { event.preventDefault(); setDetailsSize(axis.fallback, axis); }
    });
  }

  /* --------------------------------------------------- keyboard in tree */

  function initKeyboard() {
    el.treeView.addEventListener('keydown', handleTreeKey);
    el.treeView.setAttribute('tabindex', '0');

    // Breadcrumbs in the details pane jump back up the call chain.
    el.detailsView.addEventListener('click', function (event) {
      var crumb = event.target.closest('[data-goto]');
      if (crumb) { treeView.reveal(crumb.getAttribute('data-goto')); }
    });
  }

  function handleTreeKey(event) {
    if (!current || !treeView.selectedTraceId) { return; }

    var node = current.byTraceId[treeView.selectedTraceId];

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        treeView.selectRelative(1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        treeView.selectRelative(-1);
        break;
      case 'ArrowRight':
        event.preventDefault();
        if (node.children.length && !treeView.expanded[node.traceId]) {
          treeView.toggle(node.traceId);
          treeView.select(node.traceId);
        }
        break;
      case 'ArrowLeft':
        event.preventDefault();
        if (treeView.expanded[node.traceId]) {
          treeView.toggle(node.traceId);
          treeView.select(node.traceId);
        } else if (node.parent) {
          treeView.reveal(node.parent.traceId);
        }
        break;
      default:
        break;
    }
  }

  /* ---------------------------------------------------------- entry point */

  function loadSample() {
    var sample = window.PFT_SAMPLE;
    if (!sample) {
      showError('The bundled sample is missing (website/sample/sample-trace.js did not load).');
      return;
    }

    var parsed = PFT.model.parseEvents(sample.eventsText);
    var trace = PFT.model.build(parsed.events, sample.run, {});
    trace.parseErrors = parsed.errors;
    trace.sourceLabel = PFT.loader.labelFor('sample', trace) + ' (bundled sample)';

    setDirectoryHandle(null);
    showRuns([trace]);
  }

  /**
   * When the viewer is *served* (not opened from disk) a run can be linked directly:
   * viewer.html?run=./traces/my-run. Same-origin only - it is a plain fetch.
   */
  function tryLoadFromQuery() {
    if (location.protocol !== 'http:' && location.protocol !== 'https:') { return false; }

    var match = /[?&]run=([^&]+)/.exec(location.search);
    if (!match) { return false; }

    var base = decodeURIComponent(match[1]).replace(/\/$/, '');

    Promise.all([
      fetch(base + '/events.jsonl').then(function (response) {
        if (!response.ok) { throw new Error('events.jsonl: HTTP ' + response.status); }
        return response.text();
      }),
      fetch(base + '/run.json').then(function (response) {
        return response.ok ? response.json() : null;
      }, function () { return null; })
    ]).then(function (results) {
      var parsed = PFT.model.parseEvents(results[0]);
      var trace = PFT.model.build(parsed.events, results[1], {});
      trace.parseErrors = parsed.errors;
      trace.sourceLabel = PFT.loader.labelFor(base, trace);

      setDirectoryHandle(null);
      showRuns([trace]);
    }).catch(function (err) {
      showError('Could not load "' + base + '": ' + (err && err.message ? err.message : err));
    });

    return true;
  }

  function init() {
    cacheElements();
    initTheme();

    treeView = deferWhileHidden(new PFT.TreeView(el.treeView, {
      onSelect: function (node) { PFT.details.render(el.detailsView, node, current); }
    }));

    flowView = deferWhileHidden(new PFT.FlowView(el.flowView, {
      onSelect: function (traceId) { treeView.reveal(traceId); }
    }));

    methodsView = deferWhileHidden(new PFT.MethodsView(el.methodsView, {
      onSelect: function (traceId) { treeView.reveal(traceId); }
    }));

    briefView = deferWhileHidden(new PFT.BriefView(el.briefView, {}));

    eventsView = deferWhileHidden(new PFT.EventsView(el.eventsView, {
      onSelect: function (traceId) { treeView.reveal(traceId); }
    }));

    // The call tree is the tab that opens first.
    treeView.setActive(true);

    el.btnOpenFolder.addEventListener('click', openFolder);
    el.btnOpenFolder2.addEventListener('click', openFolder);
    el.btnOpenFiles.addEventListener('click', function () { el.filePicker.click(); });
    el.btnOpenFiles2.addEventListener('click', function () { el.filePicker.click(); });
    el.btnSample.addEventListener('click', loadSample);
    el.btnReload.addEventListener('click', reload);

    el.dirPicker.addEventListener('change', function () { handleFiles(Array.prototype.slice.call(this.files)); });
    el.filePicker.addEventListener('change', function () { handleFiles(Array.prototype.slice.call(this.files)); });
    el.runSelect.addEventListener('change', function () { selectRun(Number(this.value)); });

    initFilters();
    initTabs();
    initKeyboard();
    initSplitter();
    initDragAndDrop();

    if (!tryLoadFromQuery()) { offerLastDirectory(); }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  // Exposed so the viewer can be driven programmatically - by tests, or by an embedding page
  // that already has a directory handle to hand over.
  PFT.app = {
    loadSample: loadSample,
    openDirectory: openDirectory,
    reload: reload
  };
})(window.PFT = window.PFT || {});
