/*
 * Splitting a large run into parts small enough to look at (or to hand to a model) one at a time.
 *
 * A trace from a real application - hundreds or thousands of classes, running for hours - has a
 * call graph nobody can read as a single diagram and no assistant can take as a single prompt.
 * Rather than truncating it and pretending, this cuts the graph along a seam that means something
 * to a programmer, and records what crosses each seam so no part looks self-contained when it is
 * not.
 *
 * Seams, in the order `auto` prefers them:
 *
 *   namespace  - the subsystem boundary the author already chose. Depth is picked adaptively:
 *                the shallowest grouping whose largest group still fits the budget.
 *   entry      - everything reachable from one entry point: "what happens when X is called".
 *                Parts overlap where they share helpers, which is honest - shared code is shared.
 *   component  - weakly connected components: flows that never touch each other at all.
 *   chunk      - last resort for a single group that is still too big. Arbitrary, and says so.
 *
 * Every part carries `ports`: one pseudo-node per neighbouring part per direction, so a chart or
 * a brief can show "12 calls arrive here from Billing" instead of silently dropping the edge.
 */
(function (PFT) {
  'use strict';

  var DEFAULT_BUDGET = 90;
  var MAX_NAMESPACE_DEPTH = 6;

  var partition = {};

  partition.strategies = ['auto', 'namespace', 'entry', 'component', 'none'];

  /* ------------------------------------------------------------ namespaces */

  /** "ExampleApp.Orders.OrderService" -> "ExampleApp.Orders"; local functions lose their "+Local". */
  function namespaceOf(declaringType) {
    if (!declaringType) { return '(global)'; }
    var type = String(declaringType).split('+')[0];
    var cut = type.lastIndexOf('.');
    return cut === -1 ? '(global)' : type.slice(0, cut);
  }

  function prefix(namespace, depth) {
    var segments = namespace.split('.');
    return segments.length <= depth ? namespace : segments.slice(0, depth).join('.');
  }

  /**
   * Picks the shallowest namespace depth whose largest group fits the budget. Shallow groupings
   * match how people talk about a system ("the Ordering side"); going deeper than necessary just
   * fragments it.
   */
  function chooseDepth(graph, budget) {
    for (var depth = 1; depth <= MAX_NAMESPACE_DEPTH; depth++) {
      var sizes = Object.create(null);
      var largest = 0;

      for (var i = 0; i < graph.order.length; i++) {
        var node = graph.nodes[graph.order[i]];
        var key = prefix(namespaceOf(node.declaringType), depth);
        sizes[key] = (sizes[key] || 0) + 1;
        if (sizes[key] > largest) { largest = sizes[key]; }
      }

      if (largest <= budget) { return depth; }
    }

    return MAX_NAMESPACE_DEPTH;
  }

  /* -------------------------------------------------------------- grouping */

  function groupsToParts(graph, groups, order, kind) {
    return order.map(function (title) {
      return { id: kind + ':' + title, kind: kind, title: title, keys: groups[title] };
    });
  }

  function byNamespace(graph, budget) {
    var depth = chooseDepth(graph, budget);
    var groups = Object.create(null);
    var order = [];

    graph.order.forEach(function (key) {
      var title = prefix(namespaceOf(graph.nodes[key].declaringType), depth);
      if (!groups[title]) { groups[title] = []; order.push(title); }
      groups[title].push(key);
    });

    order.sort();
    return groupsToParts(graph, groups, order, 'namespace');
  }

  function adjacency(graph, undirected) {
    var out = Object.create(null);
    graph.edges.forEach(function (edge) {
      (out[edge.from] || (out[edge.from] = [])).push(edge.to);
      if (undirected) { (out[edge.to] || (out[edge.to] = [])).push(edge.from); }
    });
    return out;
  }

  /** Everything reachable from one entry point. Iterative - these graphs can be deep. */
  function reachableFrom(start, links) {
    var seen = Object.create(null);
    var stack = [start];
    seen[start] = true;
    var keys = [start];

    while (stack.length) {
      var key = stack.pop();
      var next = links[key] || [];
      for (var i = 0; i < next.length; i++) {
        if (seen[next[i]]) { continue; }
        seen[next[i]] = true;
        keys.push(next[i]);
        stack.push(next[i]);
      }
    }

    return keys;
  }

  function byEntryPoint(graph) {
    var links = adjacency(graph, false);
    var parts = [];

    graph.order.forEach(function (key) {
      if (!graph.nodes[key].isRoot) { return; }
      // The qualified name, not the bare method: six entry points all called "Handle" would
      // otherwise give six parts with identical titles and no way to tell them apart.
      parts.push({
        id: 'entry:' + key,
        kind: 'entry',
        title: key,
        subtitle: 'everything reachable from ' + key,
        keys: reachableFrom(key, links)
      });
    });

    // Anything no entry point reaches (only reachable through a cycle) still has to appear.
    var covered = Object.create(null);
    parts.forEach(function (part) {
      part.keys.forEach(function (key) { covered[key] = true; });
    });

    var orphans = graph.order.filter(function (key) { return !covered[key]; });
    if (orphans.length) {
      parts.push({ id: 'entry:__unreached', kind: 'entry', title: '(not reached from any entry point)', keys: orphans });
    }

    return parts;
  }

  function byComponent(graph) {
    var links = adjacency(graph, true);
    var seen = Object.create(null);
    var parts = [];

    graph.order.forEach(function (key) {
      if (seen[key]) { return; }
      var keys = reachableFrom(key, links);
      keys.forEach(function (member) { seen[member] = true; });
      parts.push({
        id: 'component:' + parts.length,
        kind: 'component',
        title: 'Flow ' + (parts.length + 1),
        subtitle: keys.length + ' connected method(s)',
        keys: keys
      });
    });

    return parts;
  }

  /** Last resort: an oversized group cut into numbered slices, labelled as arbitrary. */
  function chunkPart(part, budget) {
    if (part.keys.length <= budget) { return [part]; }

    var slices = [];
    var total = Math.ceil(part.keys.length / budget);

    for (var i = 0; i < total; i++) {
      slices.push({
        id: part.id + '#' + (i + 1),
        kind: part.kind,
        title: part.title + ' (' + (i + 1) + '/' + total + ')',
        subtitle: 'split by size, not by meaning - this group has no smaller natural boundary',
        keys: part.keys.slice(i * budget, (i + 1) * budget)
      });
    }

    return slices;
  }

  /* ---------------------------------------------------------------- ports */

  /**
   * Works out what crosses each part's boundary. Without this a part reads as the whole story;
   * with it, a chart can show "3 calls arrive from Billing" as an explicit edge to nowhere.
   */
  function addPorts(parts, graph) {
    var owner = Object.create(null);
    parts.forEach(function (part) {
      part.keySet = Object.create(null);
      part.keys.forEach(function (key) {
        part.keySet[key] = true;
        // With overlapping strategies a method can sit in several parts; first one wins for the
        // purpose of naming the other side of a boundary.
        if (owner[key] === undefined) { owner[key] = part.title; }
      });
    });

    parts.forEach(function (part) {
      var inbound = Object.create(null);
      var outbound = Object.create(null);
      part.internalEdges = [];

      graph.edges.forEach(function (edge) {
        var fromInside = Boolean(part.keySet[edge.from]);
        var toInside = Boolean(part.keySet[edge.to]);

        if (fromInside && toInside) { part.internalEdges.push(edge); return; }

        if (toInside) {
          var source = owner[edge.from] || '(elsewhere)';
          var into = inbound[source] || (inbound[source] = { other: source, calls: 0, links: [] });
          into.calls += edge.calls;
          into.links.push({ key: edge.to, from: edge.from, calls: edge.calls });
        } else if (fromInside) {
          var target = owner[edge.to] || '(elsewhere)';
          var outOf = outbound[target] || (outbound[target] = { other: target, calls: 0, links: [] });
          outOf.calls += edge.calls;
          outOf.links.push({ key: edge.from, to: edge.to, calls: edge.calls });
        }
      });

      part.inbound = Object.keys(inbound).map(function (k) { return inbound[k]; });
      part.outbound = Object.keys(outbound).map(function (k) { return outbound[k]; });
    });
  }

  function summarise(parts, graph, trace) {
    parts.forEach(function (part) {
      var calls = 0;
      var errors = 0;
      var totalUs = 0;
      var entries = [];

      part.keys.forEach(function (key) {
        var node = graph.nodes[key];
        if (!node) { return; }
        calls += node.calls;
        errors += node.errors;
        totalUs += node.selfUs;
        if (node.isRoot) { entries.push(key); }
      });

      part.methodCount = part.keys.length;
      part.callCount = calls;
      part.errorCount = errors;
      part.selfUs = totalUs;
      part.entryKeys = entries;
    });
  }

  /* ---------------------------------------------------------------- split */

  /**
   * Splits a trace's call graph into parts.
   *
   * options.strategy - one of partition.strategies; "auto" picks by size.
   * options.budget   - target maximum methods per part.
   */
  partition.split = function (trace, options) {
    var settings = options || {};
    var budget = settings.budget || DEFAULT_BUDGET;
    var graph = settings.graph || PFT.model.callGraph(trace);
    var strategy = settings.strategy || 'auto';

    var parts;
    var chosen = strategy;

    if (strategy === 'auto') {
      if (graph.order.length <= budget) {
        chosen = 'none';
      } else {
        chosen = 'namespace';
        var candidate = byNamespace(graph, budget);
        // A namespace split that leaves everything in one bucket has told us nothing; a run whose
        // types share no common structure is better cut by independent flow.
        if (candidate.length < 2) { chosen = 'component'; }
      }
    }

    switch (chosen) {
      case 'namespace': parts = byNamespace(graph, budget); break;
      case 'entry': parts = byEntryPoint(graph); break;
      case 'component': parts = byComponent(graph); break;
      default:
        parts = [{ id: 'all', kind: 'all', title: 'Whole run', keys: graph.order.slice() }];
        chosen = 'none';
        break;
    }

    // Anything still over budget gets sliced, so a part always fits what it promised to fit.
    if (chosen !== 'none') {
      var expanded = [];
      parts.forEach(function (part) {
        chunkPart(part, budget).forEach(function (slice) { expanded.push(slice); });
      });
      parts = expanded;
    }

    // Biggest first: the part someone most likely wants is the one with the most going on.
    parts.sort(function (a, b) { return b.keys.length - a.keys.length; });

    addPorts(parts, graph);
    summarise(parts, graph, trace);

    return { strategy: chosen, budget: budget, graph: graph, parts: parts };
  };

  partition.namespaceOf = namespaceOf;

  PFT.partition = partition;
})(window.PFT = window.PFT || {});
