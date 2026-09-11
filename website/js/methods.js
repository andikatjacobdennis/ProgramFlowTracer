/*
 * The two flat views that sit next to the call tree.
 *
 * "Methods" rolls every invocation up per method, which is how you find the hot or failing code
 * when the tree is too big to read. "Raw events" is the unfolded events.jsonl stream, for when
 * you need to see exactly what the tracer wrote.
 *
 * Both are read-only tables whose rows select an invocation in the shared details pane.
 */
(function (PFT) {
  'use strict';

  var fmt = PFT.format;
  // Above this the event list is virtualised; below it a plain render is simpler and cheaper.
  var VIRTUAL_THRESHOLD = 200;

  var COLUMNS = [
    { key: 'method', label: 'Method', numeric: false },
    { key: 'calls', label: 'Calls', numeric: true },
    { key: 'errors', label: 'Errors', numeric: true },
    { key: 'totalUs', label: 'Total', numeric: true },
    { key: 'selfUs', label: 'Self', numeric: true },
    { key: 'avgUs', label: 'Avg', numeric: true },
    { key: 'maxUs', label: 'Max', numeric: true }
  ];

  function MethodsView(container, options) {
    this.container = container;
    this.options = options || {};
    this.trace = null;
    this.sortKey = 'totalUs';
    this.sortDescending = true;

    var self = this;
    container.addEventListener('click', function (event) {
      var header = event.target.closest('[data-sort]');
      if (header) {
        var key = header.getAttribute('data-sort');
        if (self.sortKey === key) {
          self.sortDescending = !self.sortDescending;
        } else {
          self.sortKey = key;
          self.sortDescending = key !== 'method';
        }
        self.render();
        return;
      }

      var row = event.target.closest('[data-trace-id]');
      if (row && self.options.onSelect) {
        self.options.onSelect(row.getAttribute('data-trace-id'));
      }
    });
  }

  MethodsView.prototype.setTrace = function (trace) {
    this.trace = trace;
    this.render();
  };

  /** A method is shown when at least one of its invocations matches the toolbar filter. */
  MethodsView.prototype.setFilter = function (result) {
    this.filterResult = result;
    this.render();
  };

  MethodsView.prototype.visibleStats = function () {
    var result = this.filterResult;
    if (!result || !result.active) { return this.trace.stats; }

    return this.trace.stats.filter(function (row) {
      return Boolean(result.matchedMethods[row.key]);
    });
  };

  MethodsView.prototype.render = function () {
    if (!this.trace) { this.container.innerHTML = ''; return; }

    var key = this.sortKey;
    var descending = this.sortDescending;
    var rows = this.visibleStats().slice().sort(function (a, b) {
      var left = a[key];
      var right = b[key];
      if (key === 'method') {
        left = a.key.toLowerCase();
        right = b.key.toLowerCase();
      }
      if (left === null || left === undefined) { left = -1; }
      if (right === null || right === undefined) { right = -1; }
      if (left < right) { return descending ? 1 : -1; }
      if (left > right) { return descending ? -1 : 1; }
      return 0;
    });

    var maxTotal = rows.reduce(function (max, row) { return row.totalUs > max ? row.totalUs : max; }, 0);

    var head = COLUMNS.map(function (column) {
      var isSorted = column.key === key;
      return '<th data-sort="' + column.key + '" class="' +
        (column.numeric ? 'num ' : '') + (isSorted ? 'is-sorted' : '') + '" ' +
        'title="Sort by ' + column.label.toLowerCase() + '">' +
        fmt.escape(column.label) + (isSorted ? (descending ? ' ▾' : ' ▴') : '') +
        '</th>';
    }).join('');

    var body = rows.map(function (row) {
      var share = maxTotal > 0 ? Math.max(1, Math.round((row.totalUs / maxTotal) * 100)) : 0;
      return '<tr data-trace-id="' + fmt.escape(row.sample.traceId) + '"' +
          (row.errors ? ' class="has-errors"' : '') + '>' +
        '<td class="col-method">' +
          '<span class="row-bar row-bar-inline"><i style="width:' + share + '%"></i></span>' +
          '<span class="row-type">' + fmt.escape(fmt.shortType(row.declaringType)) + '.</span>' +
          '<span class="row-method">' + fmt.escape(row.method) + '</span>' +
          (row.file ? '<span class="row-source">' + fmt.escape(fmt.fileName(row.file)) +
            (row.line ? ':' + row.line : '') + '</span>' : '') +
        '</td>' +
        '<td class="num">' + fmt.count(row.calls) + '</td>' +
        '<td class="num' + (row.errors ? ' num-error' : '') + '">' + (row.errors ? fmt.count(row.errors) : '—') + '</td>' +
        '<td class="num">' + fmt.escape(fmt.duration(row.totalUs)) + '</td>' +
        '<td class="num">' + fmt.escape(fmt.duration(row.selfUs)) + '</td>' +
        '<td class="num">' + fmt.escape(fmt.duration(row.avgUs)) + '</td>' +
        '<td class="num">' + fmt.escape(fmt.duration(row.maxUs)) + '</td>' +
      '</tr>';
    }).join('');

    var filtered = this.filterResult && this.filterResult.active;
    var note = filtered
      ? 'Showing ' + fmt.count(rows.length) + ' of ' + fmt.count(this.trace.stats.length) +
        ' methods &mdash; those with at least one call matching the filter. Times still cover ' +
        '<em>every</em> call of the method, not only the matching ones.'
      : 'Times are summed across every call of that method. <strong>Self</strong> excludes time ' +
        'spent inside traced callees. Selecting a row opens its first recorded call.';

    this.container.innerHTML =
      '<table class="grid methods">' +
        '<thead><tr>' + head + '</tr></thead>' +
        '<tbody>' + (body || '<tr><td colspan="7" class="empty">Nothing matches these filters.</td></tr>') + '</tbody>' +
      '</table>' +
      '<p class="table-note">' + note + '</p>';
  };

  function EventsView(container, options) {
    this.container = container;
    this.options = options || {};
    this.trace = null;

    var self = this;
    container.addEventListener('click', function (event) {
      var copyButton = event.target.closest('[data-copy-events]');
      if (copyButton) {
        self.copyEvents(copyButton, copyButton.getAttribute('data-copy-events'));
        return;
      }

      var row = event.target.closest('[data-trace-id]');
      if (row && self.options.onSelect) {
        self.options.onSelect(row.getAttribute('data-trace-id'));
      }
    });
  }

  /**
   * The events as JSON Lines - the same shape as events.jsonl itself, so what you paste into a
   * file, into `jq`, or into a chat window is the raw record rather than a rendering of it.
   *
   * Re-serialised from the parsed objects rather than kept as the original text: holding a copy
   * of every line would double the memory a large trace costs. Key order survives JSON.parse and
   * JSON.stringify, so records match the file field for field; the viewer's own bookkeeping (a
   * "__"-prefixed ordinal) is dropped.
   *
   * Not byte-identical, though: System.Text.Json's default encoder escapes characters such as
   * an apostrophe and a plus sign as \\u0027 / \\u002B, where JSON.stringify writes them
   * literally. Both parse to the same value, so nothing downstream can tell the difference.
   */
  EventsView.prototype.eventsAsJsonl = function (scope) {
    var events = scope === 'all' ? this.trace.events : this.visibleEvents();
    var lines = [];

    for (var i = 0; i < events.length; i++) {
      lines.push(JSON.stringify(events[i], function (key, value) {
        return key.slice(0, 2) === '__' ? undefined : value;
      }));
    }

    return lines.join('\n') + '\n';
  };

  EventsView.prototype.copyEvents = function (button, scope) {
    PFT.clipboard.writeFromButton(button, this.eventsAsJsonl(scope));
  };

  EventsView.prototype.setTrace = function (trace) {
    this.trace = trace;
    this.render();
  };

  /** An event is shown when the invocation it belongs to matches the toolbar filter. */
  EventsView.prototype.setFilter = function (result) {
    this.filterResult = result;
    this.render();
  };

  EventsView.prototype.visibleEvents = function () {
    var result = this.filterResult;
    if (!result || !result.active) { return this.trace.events; }

    return this.trace.events.filter(function (event) {
      return Boolean(result.matchedTraceIds[event.traceId]);
    });
  };

  /** Builds the <tr> markup for one slice of the event list. */
  EventsView.prototype.renderRows = function (start, end) {
    var events = this.events;
    var body = [];

    for (var i = start; i < end; i++) {
      var event = events[i];
      var detail = '';

      if (event.eventType === 'Exception') {
        detail = '<span class="evt-detail evt-error">' +
          fmt.escape(fmt.shortType(event.exceptionType)) + ': ' +
          fmt.escape(fmt.ellipsis(event.message || '', 90)) + '</span>';
      } else if (event.eventType === 'MethodEnter' && event.parameters) {
        detail = '<span class="evt-detail">' + Object.keys(event.parameters).map(function (name) {
          return fmt.escape(name) + '=' + fmt.escape(fmt.valuePreview(event.parameters[name], 20));
        }).join(', ') + '</span>';
      } else if (event.eventType === 'MethodExit' && event.returnValue) {
        detail = '<span class="evt-detail">→ ' + fmt.escape(fmt.valuePreview(event.returnValue, 60)) + '</span>';
      }

      body.push('<tr data-trace-id="' + fmt.escape(event.traceId) + '">' +
        // The event's position in the file, not in the filtered list - so a number here still
        // means something when you go looking for it in events.jsonl.
        '<td class="num muted">' + ((typeof event.__order === 'number' ? event.__order : i) + 1) + '</td>' +
        '<td><span class="evt evt-' + fmt.escape(event.eventType) + '">' + fmt.escape(event.eventType) + '</span></td>' +
        '<td class="col-method">' +
          '<span class="row-type">' + fmt.escape(fmt.shortType(event.declaringType)) + '.</span>' +
          '<span class="row-method">' + fmt.escape(event.method) + '</span>' +
          detail +
        '</td>' +
        '<td class="num">' + fmt.escape(event.threadId === null || event.threadId === undefined ? '—' : 't' + event.threadId) + '</td>' +
        '<td class="num">' + fmt.escape(typeof event.durationMicroseconds === 'number' ? fmt.duration(event.durationMicroseconds) : '—') + '</td>' +
        '<td class="mono-small muted">' + fmt.escape((event.timestampUtc || '').slice(11, 23)) + '</td>' +
      '</tr>');
    }

    return body.join('');
  };

  /**
   * Renders the event list, virtualising the rows once there are enough of them to matter.
   *
   * The whole list is no longer capped: with only a screenful of <tr> elements alive at a time,
   * 50,000 events cost the same DOM as 50. Two spacer rows carry the height of everything above
   * and below the window so the scrollbar and the row positions stay truthful.
   */
  EventsView.prototype.render = function () {
    if (!this.trace) { this.container.innerHTML = ''; return; }

    var events = this.visibleEvents();
    var total = this.trace.events.length;
    var filtered = this.filterResult && this.filterResult.active;

    this.events = events;

    var note;
    if (filtered) {
      note = fmt.count(events.length) + ' of ' + fmt.count(total) +
        ' events, from the calls matching the filter, in the order the tracer wrote them.';
    } else {
      note = fmt.count(total) + ' events, in the order the tracer wrote them.';
    }

    var toolbar =
      '<div class="events-bar">' +
        '<button type="button" class="btn btn-primary" data-copy-events="visible"' +
          (events.length ? '' : ' disabled') + '>' +
          'Copy ' + fmt.count(events.length) + (filtered ? ' matching' : '') + ' events' +
        '</button>' +
        (filtered
          ? '<button type="button" class="btn" data-copy-events="all">Copy all ' +
            fmt.count(total) + '</button>'
          : '') +
        '<span class="events-hint">JSON Lines &mdash; one event per line, same records as <code>events.jsonl</code></span>' +
      '</div>';

    if (!events.length) {
      this.container.innerHTML = toolbar + tableHtml(
        '<tr><td colspan="6" class="empty">No events from calls matching these filters.</td></tr>') +
        '<p class="table-note">' + note + '</p>';
      return;
    }

    this.container.innerHTML = toolbar + tableHtml('') + '<p class="table-note">' + note + '</p>';
    var body = this.container.querySelector('tbody');

    if (events.length <= VIRTUAL_THRESHOLD) {
      body.innerHTML = this.renderRows(0, events.length);
      return;
    }

    var self = this;
    var rowHeight = PFT.rowHeightPx();
    var topSpacer = document.createElement('tr');
    var bottomSpacer = document.createElement('tr');
    topSpacer.className = bottomSpacer.className = 'vspacer';
    body.appendChild(topSpacer);
    body.appendChild(bottomSpacer);

    function paint() {
      var scroller = self.container;
      var first = Math.max(0, Math.floor(scroller.scrollTop / rowHeight) - 10);
      var count = Math.ceil((scroller.clientHeight || 400) / rowHeight) + 20;
      var last = Math.min(events.length, first + count);
      if (first === self.paintedFrom && last === self.paintedTo) { return; }

      self.paintedFrom = first;
      self.paintedTo = last;

      topSpacer.style.height = (first * rowHeight) + 'px';
      bottomSpacer.style.height = ((events.length - last) * rowHeight) + 'px';

      while (topSpacer.nextSibling && topSpacer.nextSibling !== bottomSpacer) {
        body.removeChild(topSpacer.nextSibling);
      }

      var slice = document.createElement('tbody');
      slice.innerHTML = self.renderRows(first, last);
      while (slice.firstChild) { body.insertBefore(slice.firstChild, bottomSpacer); }
    }

    this.paintedFrom = -1;
    this.paintedTo = -1;
    if (this.onScroll) { this.container.removeEventListener('scroll', this.onScroll); }
    this.onScroll = paint;
    this.container.addEventListener('scroll', paint, { passive: true });
    paint();
  };

  function tableHtml(bodyHtml) {
    return '<table class="grid events">' +
      '<thead><tr><th class="num">#</th><th>Event</th><th>Method</th><th class="num">Thread</th>' +
      '<th class="num">Duration</th><th>Time (UTC)</th></tr></thead>' +
      '<tbody>' + bodyHtml + '</tbody>' +
      '</table>';
  }

  PFT.MethodsView = MethodsView;
  PFT.EventsView = EventsView;
})(window.PFT = window.PFT || {});
