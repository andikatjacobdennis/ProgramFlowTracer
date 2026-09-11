/*
 * Turns a raw events.jsonl stream into the structure every view actually wants: a tree of
 * *invocations*.
 *
 * events.jsonl is deliberately flat and append-only - each method call shows up as a MethodEnter
 * line, later a MethodExit line, and possibly one or more Exception lines in between, all tied
 * together by traceId. This module folds those lines back into one object per invocation and
 * rebuilds the parent/child call tree from parentTraceId.
 */
(function (PFT) {
  'use strict';

  var model = {};

  /**
   * Parses JSON Lines. Malformed lines are collected rather than thrown, because a trace from a
   * process that was killed mid-write ends with a torn last line and is still worth reading.
   */
  model.parseEvents = function (text) {
    var events = [];
    var errors = [];
    var lines = String(text).split(/\r?\n/);

    for (var i = 0; i < lines.length; i++) {
      var line = lines[i].trim();
      if (!line) { continue; }
      try {
        var parsed = JSON.parse(line);
        parsed.__order = events.length;
        events.push(parsed);
      } catch (err) {
        errors.push({ line: i + 1, message: err.message, text: lines[i].slice(0, 200) });
      }
    }

    return { events: events, errors: errors };
  };

  function newInvocation(event) {
    return {
      traceId: event.traceId,
      parentTraceId: event.parentTraceId || null,
      method: event.method || '(unknown)',
      declaringType: event.declaringType || '',
      file: event.file || null,
      line: event.line || null,
      column: event.column || null,
      threadId: event.threadId === undefined ? null : event.threadId,
      taskId: event.taskId === undefined ? null : event.taskId,
      isThreadPoolThread: event.isThreadPoolThread === undefined ? null : event.isThreadPoolThread,
      enter: null,
      exit: null,
      exceptions: [],
      events: [],
      parameters: null,
      returnValue: null,
      outParameters: null,
      durationUs: null,
      selfUs: null,
      startedAt: event.timestampUtc || null,
      order: event.__order,
      children: [],
      parent: null,
      depth: 0,
      descendantCount: 0,
      isOrphan: false,
      status: 'incomplete',
      searchText: ''
    };
  }

  /**
   * The haystack the search box matches against: signature, source file, exception type, and
   * captured parameter names/values - so "1001" or "ArgumentException" finds the right frame,
   * not just a method name.
   */
  function buildSearchText(node) {
    // The qualified name goes in as one token, because "OrderService.ProcessOrder" is how people
    // write both a search and a regular expression. Joining type and method with a space instead
    // would make every `Type\.Method` pattern silently match nothing.
    var parts = [
      (node.declaringType ? node.declaringType + '.' : '') + node.method,
      node.method,
      node.file || ''
    ];

    node.exceptions.forEach(function (event) {
      parts.push(event.exceptionType || '', event.message || '');
    });

    function addCaptures(map) {
      if (!map) { return; }
      Object.keys(map).forEach(function (name) {
        parts.push(name);
        var captured = map[name];
        if (!captured || captured.value === null || captured.value === undefined) { return; }
        if (typeof captured.value === 'object') {
          try { parts.push(JSON.stringify(captured.value).slice(0, 200)); } catch (err) { /* ignore */ }
        } else {
          parts.push(String(captured.value));
        }
      });
    }

    addCaptures(node.parameters);
    addCaptures(node.outParameters);
    if (node.returnValue && node.returnValue.value !== null && node.returnValue.value !== undefined) {
      if (typeof node.returnValue.value === 'object') {
        try { parts.push(JSON.stringify(node.returnValue.value).slice(0, 200)); } catch (err) { /* ignore */ }
      } else {
        parts.push(String(node.returnValue.value));
      }
    }

    return parts.join(' ').toLowerCase();
  }

  /**
   * Folds events into invocations, links parents to children, and computes the derived numbers
   * (self time, depth, subtree size). Returns the whole navigable trace.
   */
  model.build = function (events, metadata, objects) {
    var byTraceId = Object.create(null);
    var ordered = [];

    events.forEach(function (event) {
      if (!event || !event.traceId) { return; }

      var invocation = byTraceId[event.traceId];
      if (!invocation) {
        invocation = newInvocation(event);
        byTraceId[event.traceId] = invocation;
        ordered.push(invocation);
      }

      invocation.events.push(event);

      switch (event.eventType) {
        case 'MethodEnter':
          invocation.enter = event;
          invocation.parameters = event.parameters || null;
          invocation.startedAt = event.timestampUtc || invocation.startedAt;
          // An Exception line can be the first one we see for a trace if the stream was cut,
          // so let the richer Enter metadata win whenever it does turn up.
          invocation.method = event.method || invocation.method;
          invocation.declaringType = event.declaringType || invocation.declaringType;
          break;
        case 'MethodExit':
          invocation.exit = event;
          invocation.returnValue = event.returnValue || null;
          invocation.outParameters = event.outParameters || null;
          if (typeof event.durationMicroseconds === 'number') {
            invocation.durationUs = event.durationMicroseconds;
          }
          break;
        case 'Exception':
          invocation.exceptions.push(event);
          break;
        default:
          break;
      }
    });

    // Link the tree. A parentTraceId we never saw any event for (a trace truncated at the front,
    // or a parent in a namespace that was excluded from instrumentation) leaves the child
    // stranded - promote it to a root and mark it, rather than dropping the whole subtree.
    var roots = [];
    ordered.forEach(function (invocation) {
      var parent = invocation.parentTraceId ? byTraceId[invocation.parentTraceId] : null;
      if (parent && parent !== invocation) {
        invocation.parent = parent;
        parent.children.push(invocation);
      } else {
        if (invocation.parentTraceId) { invocation.isOrphan = true; }
        roots.push(invocation);
      }
    });

    function byOrder(a, b) { return a.order - b.order; }
    roots.sort(byOrder);
    ordered.forEach(function (invocation) { invocation.children.sort(byOrder); });

    // Depth, subtree size and self time, walked iteratively - anything recursive enough to be
    // interesting to trace is also deep enough to blow the JS stack recursively.
    var stack = [];
    for (var r = roots.length - 1; r >= 0; r--) {
      stack.push({ node: roots[r], depth: 0 });
    }

    var postOrder = [];
    while (stack.length) {
      var frame = stack.pop();
      frame.node.depth = frame.depth;
      postOrder.push(frame.node);
      for (var i = frame.node.children.length - 1; i >= 0; i--) {
        stack.push({ node: frame.node.children[i], depth: frame.depth + 1 });
      }
    }

    for (var j = postOrder.length - 1; j >= 0; j--) {
      var node = postOrder[j];
      var childTotal = 0;
      var descendants = 0;

      for (var c = 0; c < node.children.length; c++) {
        var child = node.children[c];
        descendants += 1 + child.descendantCount;
        if (typeof child.durationUs === 'number') { childTotal += child.durationUs; }
      }

      node.descendantCount = descendants;
      if (typeof node.durationUs === 'number') {
        // An async method's children can outlive the awaiting frame, so clamp at zero rather
        // than reporting a negative self time.
        node.selfUs = Math.max(0, node.durationUs - childTotal);
      }

      node.status = node.exceptions.length ? 'threw' : (node.exit ? 'ok' : 'incomplete');
      node.searchText = buildSearchText(node);
    }

    var threadSet = Object.create(null);
    ordered.forEach(function (invocation) {
      if (invocation.threadId !== null) { threadSet[invocation.threadId] = true; }
    });

    var maxDurationUs = 0;
    ordered.forEach(function (invocation) {
      if (typeof invocation.durationUs === 'number' && invocation.durationUs > maxDurationUs) {
        maxDurationUs = invocation.durationUs;
      }
    });

    return {
      metadata: metadata || null,
      objects: objects || {},
      events: events,
      invocations: ordered,
      byTraceId: byTraceId,
      roots: roots,
      threads: Object.keys(threadSet).map(Number).sort(function (a, b) { return a - b; }),
      maxDurationUs: maxDurationUs,
      stats: model.methodStats(ordered)
    };
  };

  /** Per-method roll-up powering the "Methods" tab. */
  model.methodStats = function (invocations) {
    var rows = Object.create(null);

    invocations.forEach(function (invocation) {
      var key = (invocation.declaringType ? invocation.declaringType + '.' : '') + invocation.method;
      var row = rows[key];
      if (!row) {
        row = rows[key] = {
          key: key,
          method: invocation.method,
          declaringType: invocation.declaringType,
          file: invocation.file,
          line: invocation.line,
          calls: 0,
          errors: 0,
          totalUs: 0,
          selfUs: 0,
          maxUs: 0,
          timed: 0,
          avgUs: null,
          sample: invocation
        };
      }

      row.calls++;
      if (invocation.exceptions.length) { row.errors++; }
      if (typeof invocation.durationUs === 'number') {
        row.timed++;
        row.totalUs += invocation.durationUs;
        if (invocation.durationUs > row.maxUs) { row.maxUs = invocation.durationUs; }
      }
      if (typeof invocation.selfUs === 'number') { row.selfUs += invocation.selfUs; }
    });

    return Object.keys(rows).map(function (key) {
      var row = rows[key];
      row.avgUs = row.timed ? row.totalUs / row.timed : null;
      return row;
    });
  };

  /** The identity a method is aggregated under, across all of its invocations. */
  model.methodKey = function (invocation) {
    return (invocation.declaringType ? invocation.declaringType + '.' : '') + invocation.method;
  };

  /**
   * Collapses the invocation tree into a method-level call graph: one node per method, one edge
   * per caller/callee pair with the number of times that call happened. Shared by the flowchart
   * and the brief, which both describe the run's shape rather than its individual calls.
   */
  model.callGraph = function (trace) {
    var nodes = Object.create(null);
    var edges = Object.create(null);
    var order = [];

    trace.stats.forEach(function (row) {
      nodes[row.key] = {
        key: row.key,
        method: row.method,
        declaringType: row.declaringType,
        calls: row.calls,
        errors: row.errors,
        totalUs: row.totalUs,
        selfUs: row.selfUs,
        sample: row.sample,
        isRoot: false,
        incoming: 0
      };
      order.push(row.key);
    });

    trace.invocations.forEach(function (invocation) {
      var key = model.methodKey(invocation);
      var node = nodes[key];
      if (!node) { return; }

      if (!invocation.parent) {
        node.isRoot = true;
        return;
      }

      var from = model.methodKey(invocation.parent);
      if (!nodes[from]) { return; }

      var id = from + '   ' + key;
      var edge = edges[id];
      if (!edge) {
        edge = edges[id] = { from: from, to: key, calls: 0, errors: 0, isSelfCall: from === key };
        node.incoming++;
      }
      edge.calls++;
      if (invocation.exceptions.length) { edge.errors++; }
    });

    // A method whose every caller was excluded from instrumentation has neither a root
    // invocation nor an incoming edge; treat it as an entry point rather than stranding it.
    order.forEach(function (key) {
      if (!nodes[key].incoming) { nodes[key].isRoot = true; }
    });

    return {
      nodes: nodes,
      order: order,
      edges: Object.keys(edges).map(function (id) { return edges[id]; })
    };
  };

  /** Resolves a CapturedValue that was spilled to objects/{objectId}.json, when we loaded it. */
  model.resolveSpilled = function (trace, captured) {
    if (!captured || !captured.objectId) { return null; }
    if (trace.objects && trace.objects[captured.objectId]) { return trace.objects[captured.objectId]; }
    return null;
  };

  PFT.model = model;
})(window.PFT = window.PFT || {});
