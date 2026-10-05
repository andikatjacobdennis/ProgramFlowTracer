/*
 * The call tree: one row per invocation, indented by call depth.
 *
 * Rows are built as an HTML string and written in one go. That is both the fastest way to put a
 * few thousand rows on screen without a framework, and the simplest - all interaction is handled
 * by a single delegated click listener on the container.
 *
 * Collapsed subtrees are not rendered at all, so a huge trace stays cheap until it is explored.
 */
(function (PFT) {
  'use strict';

  var fmt = PFT.format;

  // Above this many rows the browser starts to feel it, so rendering stops and says so rather
  // than locking up the tab.
  var MAX_ROWS = 4000;

  // Below this a plain full render is cheaper than the wrapper and scroll listener
  // virtualising costs; above it, DOM work is what makes filtering feel slow.
  var VIRTUAL_THRESHOLD = 200;

  function TreeView(container, options) {
    this.container = container;
    this.options = options || {};
    this.trace = null;
    this.expanded = Object.create(null);
    this.selectedTraceId = null;
    this.filterResult = { active: false, matchCount: 0 };
    this.filterError = null;
    this.lastRenderedCount = 0;
    this.truncated = false;

    var self = this;
    container.addEventListener('click', function (event) {
      var twisty = event.target.closest('.twisty');
      var row = event.target.closest('.row');
      if (!row) { return; }

      var traceId = row.getAttribute('data-trace-id');
      if (twisty) {
        self.toggle(traceId);
        return;
      }

      self.select(traceId);
    });
  }

  TreeView.prototype.setTrace = function (trace) {
    this.trace = trace;
    this.expanded = Object.create(null);
    this.selectedTraceId = null;
    this.truncated = false;
    this.autoExpand();
    this.render();
  };

  /** Takes the shared filter result computed once in app.js - see js/filter.js. */
  TreeView.prototype.setFilter = function (result) {
    this.filterResult = result;
    this.filterError = result.compiled ? result.compiled.error : null;
    this.render();
  };

  /**
   * Opens enough of the tree to be useful on load without flooding the pane: expand breadth-first
   * until we would exceed a comfortable number of visible rows.
   */
  TreeView.prototype.autoExpand = function () {
    if (!this.trace) { return; }

    var budget = 200;
    var queue = this.trace.roots.slice();
    var visible = queue.length;

    while (queue.length) {
      var node = queue.shift();
      if (!node.children.length) { continue; }
      if (visible + node.children.length > budget) { break; }
      this.expanded[node.traceId] = true;
      visible += node.children.length;
      for (var i = 0; i < node.children.length; i++) { queue.push(node.children[i]); }
    }
  };

  TreeView.prototype.expandAll = function () {
    if (!this.trace) { return; }
    var self = this;
    this.trace.invocations.forEach(function (node) {
      if (node.children.length) { self.expanded[node.traceId] = true; }
    });
    this.render();
  };

  TreeView.prototype.collapseAll = function () {
    this.expanded = Object.create(null);
    this.render();
  };

  TreeView.prototype.toggle = function (traceId) {
    if (this.expanded[traceId]) {
      delete this.expanded[traceId];
    } else {
      this.expanded[traceId] = true;
    }
    this.render();
  };

  /** Expands every ancestor of a node and scrolls it into view - used by "reveal in tree". */
  TreeView.prototype.reveal = function (traceId) {
    if (!this.trace) { return; }
    var node = this.trace.byTraceId[traceId];
    if (!node) { return; }

    var parent = node.parent;
    while (parent) {
      this.expanded[parent.traceId] = true;
      parent = parent.parent;
    }

    this.selectedTraceId = traceId;

    // Revealing usually arrives from another tab, where the tree's render is deferred until its
    // tab is opened. Record the intent so the scroll happens whenever that render actually runs -
    // doing it now would target a row list that has not been rebuilt yet.
    this.pendingReveal = traceId;

    this.render();
    this.select(traceId);
  };

  /** Brings a call into view whether the list is virtualised or fully rendered. */
  TreeView.prototype.scrollToTraceId = function (traceId) {
    if (this.virtual) {
      var index = this.indexOfTraceId(traceId);
      if (index !== -1) { this.virtual.scrollToIndex(index); }
      return;
    }

    var row = this.container.querySelector('[data-trace-id="' + traceId + '"]');
    if (row && row.scrollIntoView) { row.scrollIntoView({ block: 'center' }); }
  };

  /**
   * Moves the selection up or down the row list.
   *
   * Walks the view's own row array rather than the DOM: with virtualisation only a screenful of
   * rows exists as elements, so DOM-based navigation would stop dead at the window's edge.
   */
  TreeView.prototype.selectRelative = function (delta) {
    var index = this.indexOfTraceId(this.selectedTraceId);
    if (index === -1) { return; }

    var next = index + delta;
    if (next < 0 || !this.rows || next >= this.rows.length) { return; }

    var traceId = this.rows[next].traceId;
    this.selectedTraceId = traceId;
    this.scrollToTraceId(traceId);
    this.select(traceId);
  };

  TreeView.prototype.select = function (traceId) {
    this.selectedTraceId = traceId;

    var previous = this.container.querySelector('.row.is-selected');
    if (previous) { previous.classList.remove('is-selected'); }

    var row = this.container.querySelector('[data-trace-id="' + traceId + '"]');
    if (row) { row.classList.add('is-selected'); }

    if (this.options.onSelect) {
      this.options.onSelect(this.trace.byTraceId[traceId] || null);
    }
  };

  TreeView.prototype.hasFilter = function () {
    return Boolean(this.filterResult && this.filterResult.active);
  };

  /**
   * With a filter on, a node is shown when it matches or when it is on the path to something
   * that does - a matching leaf is meaningless without its callers. Both sets are computed once
   * for the whole app by filter.apply; this just labels them for row styling.
   */
  TreeView.prototype.computeKeepSet = function () {
    if (!this.hasFilter()) { return null; }

    var result = this.filterResult;
    var keep = Object.create(null);

    Object.keys(result.keptTraceIds).forEach(function (traceId) {
      keep[traceId] = result.matchedTraceIds[traceId] ? 'match' : 'ancestor';
    });

    keep.__matchCount = result.matchCount;
    return keep;
  };

  function rowHtml(node, view, keep, maxDurationUs) {
    var expandable = node.children.length > 0;
    var isExpanded = Boolean(view.expanded[node.traceId]);
    var classes = ['row', 'status-' + node.status];
    if (node.traceId === view.selectedTraceId) { classes.push('is-selected'); }
    if (keep && keep[node.traceId] === 'ancestor') { classes.push('is-context'); }

    var share = maxDurationUs > 0 && typeof node.durationUs === 'number'
      ? Math.max(1, Math.round((node.durationUs / maxDurationUs) * 100))
      : 0;

    var args = '';
    if (node.parameters) {
      var names = Object.keys(node.parameters);
      args = names.slice(0, 3).map(function (name) {
        return fmt.escape(name) + ': ' + fmt.escape(fmt.valuePreview(node.parameters[name], 24));
      }).join(', ');
      if (names.length > 3) { args += ', …'; }
    }

    var badges = '';
    if (node.exceptions.length) {
      badges += '<span class="badge badge-error" title="' + fmt.escape(node.exceptions[0].message || '') + '">' +
        fmt.escape(fmt.shortType(node.exceptions[0].exceptionType)) + '</span>';
    }
    if (node.status === 'incomplete') {
      badges += '<span class="badge badge-warn" title="No MethodExit was recorded for this invocation">no exit</span>';
    }
    if (node.isOrphan) {
      badges += '<span class="badge badge-warn" title="Its caller is not present in this trace">orphan</span>';
    }

    return '' +
      '<div class="' + classes.join(' ') + '" data-trace-id="' + fmt.escape(node.traceId) + '" style="--depth:' + node.depth + '">' +
        '<button type="button" class="twisty' + (expandable ? '' : ' is-leaf') + '" ' +
          (expandable ? 'aria-expanded="' + isExpanded + '" title="' + (isExpanded ? 'Collapse' : 'Expand') + ' (' + node.descendantCount + ' nested calls)"' : 'tabindex="-1" aria-hidden="true"') +
          '>' + (expandable ? (isExpanded ? '▾' : '▸') : '') + '</button>' +
        '<span class="dot" aria-hidden="true"></span>' +
        '<span class="row-name">' +
          '<span class="row-type">' + fmt.escape(fmt.shortType(node.declaringType)) + '.</span>' +
          '<span class="row-method">' + fmt.escape(node.method) + '</span>' +
        '</span>' +
        '<span class="row-args">(' + args + ')</span>' +
        badges +
        '<span class="row-spacer"></span>' +
        '<span class="row-source">' + fmt.escape(fmt.sourceLocation(node)) + '</span>' +
        '<span class="row-thread" title="Managed thread id">t' + fmt.escape(node.threadId === null ? '?' : node.threadId) + '</span>' +
        '<span class="row-bar" title="' + fmt.escape(fmt.duration(node.durationUs)) + ' of the slowest call">' +
          '<i style="width:' + share + '%"></i>' +
        '</span>' +
        '<span class="row-time">' + fmt.escape(fmt.duration(node.durationUs)) + '</span>' +
      '</div>';
  }

  /**
   * Flattens the tree into the list of rows that should currently be on screen: an iterative
   * pre-order walk over the expanded (and, when filtering, kept) nodes.
   *
   * Kept separate from drawing so the list can be many thousands of entries long while only the
   * visible window is ever turned into DOM.
   */
  TreeView.prototype.buildRowList = function (keep) {
    var rows = [];
    var stack = [];

    for (var i = this.trace.roots.length - 1; i >= 0; i--) {
      stack.push(this.trace.roots[i]);
    }

    while (stack.length) {
      var node = stack.pop();
      if (keep && !keep[node.traceId]) { continue; }

      rows.push(node);
      if (rows.length >= MAX_ROWS) { this.truncated = true; break; }

      // While filtering, keep the path to every match open regardless of manual collapse state.
      var open = keep ? true : Boolean(this.expanded[node.traceId]);
      if (open) {
        for (var c = node.children.length - 1; c >= 0; c--) {
          stack.push(node.children[c]);
        }
      }
    }

    return rows;
  };

  TreeView.prototype.render = function () {
    if (!this.trace) {
      this.disposeVirtual();
      this.container.innerHTML = '';
      return;
    }

    var keep = this.computeKeepSet();
    this.truncated = false;

    var rows = this.buildRowList(keep);
    this.rows = rows;
    this.keep = keep;
    this.rowIndex = null;
    this.lastRenderedCount = rows.length;
    this.matchCount = keep ? keep.__matchCount : this.trace.invocations.length;

    if (!rows.length) {
      this.disposeVirtual();
      this.container.innerHTML = '<p class="empty">Nothing matches these filters.</p>';
      return;
    }

    // Small lists render in one go: virtualising costs a wrapper and a scroll listener, which is
    // not worth it until there are more rows than a screen can hold several times over.
    if (rows.length <= VIRTUAL_THRESHOLD) {
      this.disposeVirtual();
      this.container.innerHTML = this.renderRows(0, rows.length) + this.footerHtml();
      this.honourPendingReveal();
      return;
    }

    this.ensureVirtual();
    this.virtual.mount();
    this.virtual.setTotal(rows.length);
    this.honourPendingReveal();
  };

  /** Completes a reveal that was requested while this view's tab was hidden. */
  TreeView.prototype.honourPendingReveal = function () {
    if (!this.pendingReveal) { return; }

    var traceId = this.pendingReveal;
    this.pendingReveal = null;
    this.scrollToTraceId(traceId);

    // scrollToIndex redraws the window, so re-apply the selected class to the row now on screen.
    var row = this.container.querySelector('[data-trace-id="' + traceId + '"]');
    if (row) { row.classList.add('is-selected'); }
  };

  TreeView.prototype.renderRows = function (start, end) {
    var html = [];
    for (var i = start; i < end; i++) {
      html.push(rowHtml(this.rows[i], this, this.keep, this.trace.maxDurationUs));
    }
    return html.join('');
  };

  TreeView.prototype.footerHtml = function () {
    return this.truncated
      ? '<p class="empty">Stopped after ' + fmt.count(MAX_ROWS) +
        ' rows. Narrow the filter, or collapse a branch, to see the rest.</p>'
      : '';
  };

  TreeView.prototype.ensureVirtual = function () {
    if (this.virtual) { return; }

    var self = this;
    this.virtual = new PFT.VirtualList(this.container, {
      rowHeight: PFT.rowHeightPx(),
      renderRange: function (start, end) { return self.renderRows(start, end); }
    });
  };

  TreeView.prototype.disposeVirtual = function () {
    if (!this.virtual) { return; }
    this.virtual.destroy();
    this.virtual = null;
  };

  /** Position of a call in the currently displayed row list, or -1. */
  TreeView.prototype.indexOfTraceId = function (traceId) {
    if (!this.rows) { return -1; }

    if (!this.rowIndex) {
      this.rowIndex = Object.create(null);
      for (var i = 0; i < this.rows.length; i++) {
        this.rowIndex[this.rows[i].traceId] = i;
      }
    }

    var found = this.rowIndex[traceId];
    return found === undefined ? -1 : found;
  };

  PFT.TreeView = TreeView;
})(window.PFT = window.PFT || {});
