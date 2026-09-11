/*
 * The details pane: everything the trace recorded about one invocation.
 *
 * The ordering here is deliberate - signature, then timing, then the captured values, then the
 * exception, then the raw events. It answers "what was called", "how long did it take", "with
 * what", "what went wrong", in the order people actually ask.
 */
(function (PFT) {
  'use strict';

  var fmt = PFT.format;

  function badge(text, kind, title) {
    return '<span class="badge badge-' + kind + '"' + (title ? ' title="' + fmt.escape(title) + '"' : '') + '>' +
      fmt.escape(text) + '</span>';
  }

  function fact(label, value, extraClass) {
    return '<div class="fact' + (extraClass ? ' ' + extraClass : '') + '">' +
      '<dt>' + fmt.escape(label) + '</dt>' +
      '<dd>' + value + '</dd>' +
      '</div>';
  }

  /**
   * One captured value.
   *
   * Laid out as a header line (name, type, status) with the value on its own full-width line
   * below, rather than as four table columns. The details pane is a sidebar - splitting its
   * width four ways left the value, which is the part you actually came to read, with barely a
   * hundred pixels for what is often a JSON object.
   */
  function captureBlock(name, captured, trace) {
    var status = captured ? captured.serializationStatus : 'Unknown';
    var spilled = PFT.model.resolveSpilled(trace, captured);
    var value;

    if (spilled) {
      value = '<pre class="value">' + fmt.escape(fmt.prettyJson(spilled.value)) + '</pre>' +
        '<p class="value-note">Loaded from <code>objects/' + fmt.escape(captured.objectId) + '.json</code></p>';
    } else if (captured && captured.objectId) {
      value = '<p class="value-note">Spilled to <code>objects/' + fmt.escape(captured.objectId) +
        '.json</code>. Re-open the run with its <code>objects/</code> directory included to see it.</p>';
    } else if (!captured) {
      value = '<span class="muted">—</span>';
    } else if (status === 'Redacted') {
      value = '<span class="muted">Not captured: the name or an attribute marked it sensitive.</span>';
    } else if (status === 'Unavailable') {
      value = '<span class="muted">Not observable at this point in the call.</span>';
    } else if (status === 'Failed') {
      value = '<p class="value-error">' + fmt.escape(captured.error || 'Serialization failed') + '</p>' +
        (captured.toString ? '<pre class="value">' + fmt.escape(captured.toString) + '</pre>' : '');
    } else if (captured.value === null || captured.value === undefined) {
      value = '<span class="muted">null</span>';
    } else if (typeof captured.value === 'object') {
      value = '<pre class="value">' + fmt.escape(fmt.prettyJson(captured.value)) + '</pre>';
    } else {
      value = '<pre class="value">' + fmt.escape(fmt.inlineJson(captured.value)) + '</pre>';
    }

    var typeName = (captured && captured.type) || '';

    return '<div class="capture">' +
      '<div class="capture-head">' +
        '<span class="capture-name">' + fmt.escape(name) + '</span>' +
        (typeName ? '<code class="capture-type" title="' + fmt.escape(typeName) + '">' +
          fmt.escape(fmt.shortType(typeName)) + '</code>' : '') +
        '<span class="status status-' + fmt.statusClass(status) + '">' + fmt.escape(status) + '</span>' +
      '</div>' +
      '<div class="capture-value">' + value + '</div>' +
      '</div>';
  }

  function captureList(title, map, trace) {
    if (!map) { return ''; }
    var names = Object.keys(map);
    if (!names.length) { return ''; }

    return '<section class="detail-section">' +
      '<h3>' + fmt.escape(title) + '</h3>' +
      '<div class="captures">' +
        names.map(function (name) { return captureBlock(name, map[name], trace); }).join('') +
      '</div>' +
      '</section>';
  }

  function exceptionSection(node) {
    if (!node.exceptions.length) { return ''; }

    return '<section class="detail-section">' +
      '<h3>Exception' + (node.exceptions.length > 1 ? 's' : '') + '</h3>' +
      node.exceptions.map(function (event) {
        return '<div class="exception">' +
          '<p class="exception-type">' + fmt.escape(event.exceptionType || 'Exception') + '</p>' +
          '<p class="exception-message">' + fmt.escape(event.message || '') + '</p>' +
          (event.stackTrace ? '<details><summary>Stack trace</summary><pre class="value">' +
            fmt.escape(event.stackTrace) + '</pre></details>' : '') +
          '</div>';
      }).join('') +
      '</section>';
  }

  function callPath(node) {
    var path = [];
    var current = node;
    while (current) {
      path.unshift(current);
      current = current.parent;
    }

    return path.map(function (item, index) {
      var isLast = index === path.length - 1;
      var label = fmt.shortType(item.declaringType) + '.' + item.method;
      if (isLast) { return '<span class="crumb is-current">' + fmt.escape(label) + '</span>'; }
      return '<button type="button" class="crumb" data-goto="' + fmt.escape(item.traceId) + '">' +
        fmt.escape(label) + '</button>';
    }).join('<span class="crumb-sep">›</span>');
  }

  var details = {};

  details.renderEmpty = function (container, trace) {
    var meta = trace && trace.metadata;
    var warnings = [];

    if (trace && trace.parseErrors && trace.parseErrors.length) {
      warnings.push('<p class="notice notice-warn">' + fmt.count(trace.parseErrors.length) +
        ' line(s) in <code>events.jsonl</code> could not be parsed and were skipped. ' +
        'That usually means the traced process was killed while writing.</p>');
    }
    if (meta && meta.droppedEventCount > 0) {
      warnings.push('<p class="notice notice-warn">' + fmt.count(meta.droppedEventCount) +
        ' event(s) were dropped by the tracer&rsquo;s bounded queue, so this trace is incomplete. ' +
        'Raise <code>writerQueueCapacity</code> or lower <code>samplingRate</code> in ' +
        '<code>.flowtrace.json</code> to capture everything.</p>');
    }
    if (trace && trace.events.length > 200000) {
      warnings.push('<p class="notice notice-warn">This run holds ' + fmt.count(trace.events.length) +
        ' events, all of which are in memory. Long runs are better captured with ' +
        '<code>samplingRate</code> below 1.0, or with hot namespaces in ' +
        '<code>excludeNamespaces</code>, than by tracing everything and filtering afterwards.</p>');
    }
    if (meta && !meta.endedAtUtc) {
      warnings.push('<p class="notice notice-warn">This run has no end time &mdash; the traced ' +
        'process did not shut down cleanly, so the last events may be missing.</p>');
    }

    container.innerHTML =
      '<div class="detail-empty">' +
        warnings.join('') +
        '<h2>Select a call</h2>' +
        '<p>Pick any row in the call tree to see its parameters, return value, timing and thread.</p>' +
        (trace ? '<dl class="fact-grid">' +
          fact('Invocations', fmt.count(trace.invocations.length)) +
          fact('Events', fmt.count(trace.events.length)) +
          fact('Roots', fmt.count(trace.roots.length)) +
          fact('Threads', fmt.count(trace.threads.length)) +
          fact('Slowest call', fmt.duration(trace.maxDurationUs)) +
          fact('Spilled values', fmt.count(Object.keys(trace.objects).length)) +
        '</dl>' : '') +
      '</div>';
  };

  details.render = function (container, node, trace) {
    if (!node) { details.renderEmpty(container, trace); return; }

    var badges = '';
    if (node.status === 'threw') { badges += badge('threw', 'error'); }
    if (node.status === 'incomplete') { badges += badge('no exit recorded', 'warn'); }
    if (node.isOrphan) { badges += badge('orphan', 'warn', 'Its caller is not present in this trace'); }
    if (node.isThreadPoolThread) { badges += badge('thread pool', 'neutral'); }

    var childTotal = null;
    if (typeof node.durationUs === 'number' && typeof node.selfUs === 'number') {
      childTotal = node.durationUs - node.selfUs;
    }

    container.innerHTML =
      '<article class="detail">' +
        '<header class="detail-head">' +
          '<p class="detail-path">' + callPath(node) + '</p>' +
          '<h2>' +
            '<span class="detail-type">' + fmt.escape(fmt.shortType(node.declaringType)) + '.</span>' +
            fmt.escape(node.method) +
          '</h2>' +
          '<p class="detail-badges">' + badges + '</p>' +
        '</header>' +

        '<dl class="fact-grid">' +
          fact('Total', fmt.escape(fmt.duration(node.durationUs))) +
          fact('Self', fmt.escape(fmt.duration(node.selfUs))) +
          fact('In children', childTotal === null ? '—' : fmt.escape(fmt.duration(childTotal))) +
          fact('Direct calls', fmt.count(node.children.length)) +
          fact('Nested calls', fmt.count(node.descendantCount)) +
          fact('Thread', node.threadId === null ? '—' : fmt.escape('#' + node.threadId)) +
          fact('Task', node.taskId === null || node.taskId === undefined ? '—' : fmt.escape('#' + node.taskId)) +
          fact('Started', fmt.escape(fmt.timestamp(node.startedAt))) +
          fact('Source', node.file
            ? '<code title="' + fmt.escape(node.file) + '">' + fmt.escape(fmt.sourceLocation(node)) + '</code>'
            : '—', 'fact-wide') +
          fact('Trace id', '<code class="mono-small">' + fmt.escape(node.traceId) + '</code>', 'fact-wide') +
        '</dl>' +

        exceptionSection(node) +
        captureList('Parameters', node.parameters, trace) +
        (node.returnValue ? captureList('Return value', { 'return': node.returnValue }, trace) : '') +
        captureList('Out / ref parameters at exit', node.outParameters, trace) +

        '<section class="detail-section">' +
          '<details>' +
            '<summary>Raw events (' + node.events.length + ')</summary>' +
            '<pre class="value">' + fmt.escape(node.events.map(function (event) {
              var copy = {};
              Object.keys(event).forEach(function (key) {
                if (key !== '__order') { copy[key] = event[key]; }
              });
              return fmt.prettyJson(copy);
            }).join('\n')) + '</pre>' +
          '</details>' +
        '</section>' +
      '</article>';
  };

  PFT.details = details;
})(window.PFT = window.PFT || {});
