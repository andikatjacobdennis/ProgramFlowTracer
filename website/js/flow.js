/*
 * The Flow tab: the run drawn as a flowchart.
 *
 * The call tree shows every invocation, which is the truth but is unreadable past a few hundred
 * rows. This view aggregates instead - one node per *method*, one edge per caller/callee pair,
 * labelled with how many times that call happened. That is the shape of the program's flow,
 * which is usually what you want to see before drilling into a specific invocation.
 *
 * The graph itself comes from model.callGraph, which the Brief tab shares. Everything here is
 * hand-rolled inline SVG: no layout library, no build step. Nodes are assigned to layers by
 * longest path from an entry point, then packed left-to-right within each layer. Recursion (a
 * real possibility in any call graph) is handled explicitly - see markBackEdges.
 */
(function (PFT) {
  'use strict';

  var fmt = PFT.format;

  var NODE_W = 190;
  var NODE_H = 46;
  var GAP_X = 34;
  var GAP_Y = 78;
  var GAP_SUB = 22;
  var MAX_PER_ROW = 8;
  var PAD = 56;
  // How many methods fit on one chart and still be readable. The partitioner splits to this,
  // so it is a layout decision rather than a cutoff - nothing is ever dropped for being over it.
  var NODES_PER_CHART = 80;

  /* --------------------------------------------------------------- graph */

  /** The shared method-level graph, plus the per-node fields this view lays out with. */
  function buildGraph(trace) {
    var graph = PFT.model.callGraph(trace);

    graph.order.forEach(function (key) {
      var node = graph.nodes[key];
      node.layer = 0;
      node.x = 0;
      node.y = 0;
    });

    return graph;
  }

  /**
   * A call graph is not a DAG - recursion and mutual recursion are both ordinary - so the cycles
   * have to be broken before anything can be layered. A depth-first walk from the entry points
   * marks exactly the edges that close a cycle (an edge back to a node still open on the DFS
   * stack); those are the recursive calls, and every other edge is genuine forward flow.
   *
   * Doing it this way rather than inferring "back edge" from the final layer numbers matters:
   * layer comparison also flags plain forward calls whose target happens to sit higher up, which
   * would draw them as recursion and say so in the legend.
   */
  function markBackEdges(graph) {
    var adjacency = Object.create(null);
    graph.edges.forEach(function (edge) {
      edge.isBack = false;
      (adjacency[edge.from] || (adjacency[edge.from] = [])).push(edge);
    });

    var WHITE = 0, GREY = 1, BLACK = 2;
    var colour = Object.create(null);
    graph.order.forEach(function (key) { colour[key] = WHITE; });

    // Iterative, because a deeply recursive trace is exactly the input that would blow a
    // recursive DFS's own stack.
    function walk(start) {
      var stack = [{ key: start, index: 0 }];
      colour[start] = GREY;

      while (stack.length) {
        var frame = stack[stack.length - 1];
        var out = adjacency[frame.key] || [];

        if (frame.index >= out.length) {
          colour[frame.key] = BLACK;
          stack.pop();
          continue;
        }

        var edge = out[frame.index++];
        if (edge.isSelfCall) { edge.isBack = true; continue; }

        if (colour[edge.to] === GREY) { edge.isBack = true; continue; }
        if (colour[edge.to] === WHITE) {
          colour[edge.to] = GREY;
          stack.push({ key: edge.to, index: 0 });
        }
      }
    }

    // Entry points first, so the DFS tree follows the program's real flow; anything left over is
    // only reachable through a cycle and gets its own walk.
    graph.order.forEach(function (key) {
      if (graph.nodes[key].isRoot && colour[key] === WHITE) { walk(key); }
    });
    graph.order.forEach(function (key) {
      if (colour[key] === WHITE) { walk(key); }
    });
  }

  /** Longest-path layering over the forward edges only - a DAG once back edges are excluded. */
  function assignLayers(graph) {
    var limit = graph.order.length;
    graph.order.forEach(function (key) { graph.nodes[key].layer = 0; });

    for (var pass = 0; pass < limit; pass++) {
      var changed = false;

      for (var i = 0; i < graph.edges.length; i++) {
        var edge = graph.edges[i];
        if (edge.isBack) { continue; }

        var from = graph.nodes[edge.from];
        var to = graph.nodes[edge.to];
        if (!from || !to) { continue; }

        if (from.layer + 1 > to.layer) {
          to.layer = from.layer + 1;
          changed = true;
        }
      }

      if (!changed) { break; }
    }
  }

  /**
   * Orders each layer to reduce edge crossings, by the barycentre heuristic: put every node near
   * the average position of what it connects to, sweeping down then up a few times.
   *
   * Without this a layer sits in whatever order the graph happened to produce - siblings end up
   * scattered, and every edge crosses several others. It is the single biggest difference
   * between a readable layered graph and a tangle.
   */
  function orderLayers(graph, layers) {
    var predecessors = Object.create(null);
    var successors = Object.create(null);

    graph.edges.forEach(function (edge) {
      if (edge.isBack) { return; }
      (successors[edge.from] || (successors[edge.from] = [])).push(edge.to);
      (predecessors[edge.to] || (predecessors[edge.to] = [])).push(edge.from);
    });

    function reindex() {
      layers.forEach(function (layer) {
        if (!layer) { return; }
        layer.forEach(function (node, index) { node.orderIndex = index; });
      });
    }

    function sortLayer(layer, neighbours) {
      if (!layer) { return; }

      layer.forEach(function (node) {
        var keys = neighbours[node.key] || [];
        var total = 0;
        var counted = 0;

        keys.forEach(function (key) {
          var other = graph.nodes[key];
          if (other && typeof other.orderIndex === 'number') {
            total += other.orderIndex;
            counted++;
          }
        });

        // A node with nothing in the adjacent layer keeps its current place rather than being
        // dragged to position zero.
        node.barycentre = counted ? total / counted : node.orderIndex;
      });

      layer.sort(function (a, b) {
        return (a.barycentre - b.barycentre) || (a.orderIndex - b.orderIndex);
      });
    }

    /** Crossings between one pair of adjacent layers, from their current orderings. */
    function crossingsBetween(upper, lower) {
      if (!upper || !lower) { return 0; }

      var lowerIndex = Object.create(null);
      lower.forEach(function (node, index) { lowerIndex[node.key] = index; });

      var pairs = [];
      upper.forEach(function (node, upperPosition) {
        (successors[node.key] || []).forEach(function (key) {
          var position = lowerIndex[key];
          if (position !== undefined) { pairs.push([upperPosition, position]); }
        });
      });

      var count = 0;
      for (var i = 0; i < pairs.length; i++) {
        for (var j = i + 1; j < pairs.length; j++) {
          if ((pairs[i][0] - pairs[j][0]) * (pairs[i][1] - pairs[j][1]) < 0) { count++; }
        }
      }
      return count;
    }

    function localCrossings(index) {
      return crossingsBetween(layers[index - 1], layers[index]) +
        crossingsBetween(layers[index], layers[index + 1]);
    }

    /**
     * Barycentre gets the ordering roughly right but is blind to pairs it could simply swap.
     * Trying each adjacent swap and keeping the ones that reduce crossings is the usual
     * companion pass, and it is cheap at the sizes a single chart holds.
     */
    function transpose() {
      for (var pass = 0; pass < 4; pass++) {
        var improved = false;

        for (var index = 0; index < layers.length; index++) {
          var layer = layers[index];
          if (!layer || layer.length < 2) { continue; }

          for (var i = 0; i + 1 < layer.length; i++) {
            var before = localCrossings(index);
            var swap = layer[i];
            layer[i] = layer[i + 1];
            layer[i + 1] = swap;

            if (localCrossings(index) < before) {
              improved = true;
            } else {
              layer[i + 1] = layer[i];
              layer[i] = swap;
            }
          }
        }

        if (!improved) { break; }
      }
    }

    reindex();

    for (var sweep = 0; sweep < 4; sweep++) {
      if (sweep % 2 === 0) {
        for (var down = 1; down < layers.length; down++) { sortLayer(layers[down], predecessors); }
      } else {
        for (var up = layers.length - 2; up >= 0; up--) { sortLayer(layers[up], successors); }
      }
      reindex();
    }

    transpose();
    reindex();
  }

  function layout(graph) {
    markBackEdges(graph);
    assignLayers(graph);

    var layers = [];
    graph.order.forEach(function (key) {
      var node = graph.nodes[key];
      (layers[node.layer] || (layers[node.layer] = [])).push(node);
    });

    orderLayers(graph, layers);

    // Fan-out is the normal shape of real code - one dispatcher calling eighty handlers - and a
    // layer laid out as a single row would be thousands of pixels wide and unreadable at any
    // zoom that fits. Wide layers wrap into stacked sub-rows instead.
    var rows = [];
    layers.forEach(function (layer) {
      if (!layer) { return; }

      // Split evenly rather than filling rows to the brim and leaving a stub: a layer of nine
      // reads far better as 5 + 4 than as 8 + 1. Wrapping at all costs something - it puts some
      // of a layer below the layer beneath it - so it only starts once a row would be genuinely
      // too wide to follow.
      var rowCount = Math.ceil(layer.length / MAX_PER_ROW);
      var perRow = Math.ceil(layer.length / rowCount);

      for (var start = 0; start < layer.length; start += perRow) {
        rows.push({ nodes: layer.slice(start, start + perRow), isContinuation: start > 0 });
      }
    });

    var widest = 0;
    rows.forEach(function (row) {
      if (row.nodes.length > widest) { widest = row.nodes.length; }
    });

    var canvasWidth = Math.max(NODE_W, widest * NODE_W + (widest - 1) * GAP_X);
    var y = 0;
    var isFirstRow = true;

    rows.forEach(function (row) {
      // Sub-rows of one layer sit closer together than separate layers do, so the layering is
      // still legible at a glance. Only the very first row starts at zero - testing `y` for that
      // silently stacked the second row on top of the first, since y is still 0 at that point.
      if (isFirstRow) {
        isFirstRow = false;
      } else if (row.isContinuation) {
        y += NODE_H + GAP_SUB;
      } else {
        y += NODE_H + GAP_Y;
      }

      var rowWidth = row.nodes.length * NODE_W + (row.nodes.length - 1) * GAP_X;
      // Continuation rows are offset by half a cell so that an edge dropping into them passes
      // through the gap between the nodes above, instead of straight through one of them.
      var stagger = row.isContinuation ? (NODE_W + GAP_X) / 2 : 0;
      var startX = (canvasWidth - rowWidth) / 2 + stagger;

      row.nodes.forEach(function (node, position) {
        node.x = startX + position * (NODE_W + GAP_X);
        node.y = y;
      });
    });

    return {
      width: canvasWidth + PAD * 2,
      height: y + NODE_H + PAD * 2
    };
  }

  /* ------------------------------------------------------------- drawing */

  function truncate(text, maxWidth, perChar) {
    var limit = Math.floor(maxWidth / perChar);
    return text.length > limit ? text.slice(0, Math.max(1, limit - 1)) + '…' : text;
  }

  /**
   * A local function is recorded with a declaring type of "Outer+LocalName", which would render
   * as "Outer+SumTotals.SumTotals". Show the type that actually contains it instead.
   */
  function typeLabel(node) {
    var short = fmt.shortType(node.declaringType);
    var suffix = '+' + node.method;
    return short.slice(-suffix.length) === suffix ? short.slice(0, -suffix.length) : short;
  }

  var CORNER = 9;

  /**
   * Forward edges are drawn as orthogonal elbows with rounded corners: straight down out of the
   * caller, one horizontal run in the gap between layers, straight down into the callee.
   *
   * Long diagonal curves are what make a layered graph read as spaghetti - they sweep across the
   * whole width and no two of them are parallel. Orthogonal runs from different edges overlap
   * into what looks like a bus instead, which is how a hand-drawn flowchart handles the same
   * situation.
   */
  function edgePath(from, to, isBack) {
    var x1 = from.x + NODE_W / 2;
    var y1 = from.y + NODE_H;
    var x2 = to.x + NODE_W / 2;
    var y2 = to.y;

    if (isBack) { return backPath(from, to); }

    // Directly below: no elbow needed at all.
    if (Math.abs(x1 - x2) < 2) { return 'M' + x1 + ',' + y1 + ' L' + x2 + ',' + y2; }

    // Wrapped sub-rows can put a callee level with, or above, its caller. Keep the horizontal
    // run inside the vertical space that actually exists between the two.
    var span = y2 - y1;
    var midY = span > 2 * CORNER ? y1 + span / 2 : y1 + Math.max(2, span / 2);
    var radius = Math.min(CORNER, Math.abs(midY - y1), Math.abs(y2 - midY), Math.abs(x2 - x1) / 2);
    var direction = x2 > x1 ? 1 : -1;

    if (radius < 1) { return 'M' + x1 + ',' + y1 + ' L' + x1 + ',' + midY + ' L' + x2 + ',' + midY + ' L' + x2 + ',' + y2; }

    return 'M' + x1 + ',' + y1 +
      ' L' + x1 + ',' + (midY - radius) +
      ' Q' + x1 + ',' + midY + ' ' + (x1 + direction * radius) + ',' + midY +
      ' L' + (x2 - direction * radius) + ',' + midY +
      ' Q' + x2 + ',' + midY + ' ' + x2 + ',' + (midY + radius) +
      ' L' + x2 + ',' + y2;
  }

  /** Recursion, routed out into a side lane so it never runs underneath the forward edges. */
  function backPath(from, to) {
    var side = from.x <= to.x ? -1 : 1;
    var x1 = side < 0 ? from.x : from.x + NODE_W;
    var x2 = side < 0 ? to.x : to.x + NODE_W;
    var y1 = from.y + NODE_H / 2;
    var y2 = to.y + NODE_H / 2;
    var lane = side < 0 ? Math.min(x1, x2) - 26 : Math.max(x1, x2) + 26;

    return 'M' + x1 + ',' + y1 +
      ' L' + lane + ',' + y1 +
      ' L' + lane + ',' + y2 +
      ' L' + x2 + ',' + y2;
  }

  function selfLoopPath(node) {
    var x = node.x + NODE_W;
    var y = node.y + NODE_H / 2;
    var lane = x + 22;
    return 'M' + x + ',' + (y - 10) +
      ' L' + lane + ',' + (y - 10) +
      ' L' + lane + ',' + (y + 10) +
      ' L' + x + ',' + (y + 10);
  }

  /** A boundary stub: calls that cross into or out of the part being drawn. */
  function portSvg(node) {
    return '<g class="flow-node is-port" data-key="' + fmt.escape(node.key) + '" ' +
        'transform="translate(' + node.x + ',' + node.y + ')">' +
      '<title>' + fmt.escape(node.portLabel) + '\n' + fmt.count(node.calls) +
        ' call(s) cross this boundary</title>' +
      '<rect class="flow-box" width="' + NODE_W + '" height="' + NODE_H + '" rx="' + (NODE_H / 2) + '"></rect>' +
      '<text class="flow-type" x="12" y="19">' +
        (node.portDirection === 'in' ? 'called from' : 'calls out to') + '</text>' +
      '<text class="flow-method" x="12" y="34">' +
        fmt.escape(truncate(node.portLabel, NODE_W - 78, 6.9)) + '</text>' +
      '<text class="flow-stat" x="' + (NODE_W - 12) + '" y="34" text-anchor="end">' +
        fmt.count(node.calls) + 'x</text>' +
    '</g>';
  }

  function nodeSvg(node, maxTotalUs) {
    if (node.isPort) { return portSvg(node); }

    var classes = ['flow-node'];
    if (node.errors) { classes.push('has-errors'); }
    if (node.isContext) { classes.push('is-context'); }
    if (node.isRoot) { classes.push('is-entry'); }

    // Entry points get a stadium outline, the way a flowchart marks a start terminal.
    var rx = node.isRoot ? NODE_H / 2 : 4;
    var share = maxTotalUs > 0 ? Math.max(0, Math.min(1, node.totalUs / maxTotalUs)) : 0;

    return '<g class="' + classes.join(' ') + '" data-key="' + fmt.escape(node.key) + '" ' +
        'data-trace-id="' + fmt.escape(node.sample.traceId) + '" ' +
        'transform="translate(' + node.x + ',' + node.y + ')" tabindex="0" role="button">' +
      '<title>' + fmt.escape(node.key) + '\n' + fmt.count(node.calls) + ' call(s), ' +
        fmt.duration(node.totalUs) + ' total, ' + fmt.duration(node.selfUs) + ' self' +
        (node.errors ? '\n' + fmt.count(node.errors) + ' threw' : '') + '</title>' +
      '<rect class="flow-box" width="' + NODE_W + '" height="' + NODE_H + '" rx="' + rx + '"></rect>' +
      '<rect class="flow-heat" x="0" y="' + (NODE_H - 3) + '" width="' + (share * NODE_W) + '" height="3"></rect>' +
      '<text class="flow-type" x="12" y="17">' +
        fmt.escape(truncate(typeLabel(node), NODE_W - 24, 5.4)) + '</text>' +
      '<text class="flow-method" x="12" y="32">' +
        fmt.escape(truncate(node.method, NODE_W - 84, 6.9)) + '</text>' +
      '<text class="flow-stat" x="' + (NODE_W - 12) + '" y="32" text-anchor="end">' +
        fmt.escape(fmt.count(node.calls) + '× · ' + fmt.duration(node.totalUs)) + '</text>' +
    '</g>';
  }

  function edgeSvg(edge, graph, showLabels) {
    var from = graph.nodes[edge.from];
    var to = graph.nodes[edge.to];
    if (!from || !to) { return ''; }

    var path = edge.isSelfCall ? selfLoopPath(from) : edgePath(from, to, edge.isBack);
    var classes = ['flow-edge'];
    if (edge.isBack) { classes.push('is-back'); }
    if (edge.errors) { classes.push('has-errors'); }

    var label = '';
    if (showLabels && edge.calls > 1 && !edge.isBack) {
      // On the vertical drop just above the callee, not floating at the midpoint of a diagonal -
      // that put labels on top of unrelated nodes. The pill behind it keeps it readable where a
      // horizontal run passes underneath.
      var lx = to.x + NODE_W / 2;
      var ly = to.y - 8;
      var text = fmt.count(edge.calls) + '×';
      var width = 12 + text.length * 6;
      label =
        '<rect class="flow-edge-label-bg" x="' + (lx - width / 2) + '" y="' + (ly - 10) +
          '" width="' + width + '" height="13" rx="6"></rect>' +
        '<text class="flow-edge-label" x="' + lx + '" y="' + ly + '" text-anchor="middle">' +
          text + '</text>';
    }

    return '<path class="' + classes.join(' ') + '" d="' + path + '" ' +
      'data-from="' + fmt.escape(edge.from) + '" data-to="' + fmt.escape(edge.to) + '"></path>' + label;
  }

  /* ---------------------------------------------------------------- view */

  function FlowView(container, options) {
    this.container = container;
    this.options = options || {};
    this.trace = null;
    this.graph = null;
    this.split = null;
    this.strategy = 'auto';
    this.partIndex = 0;
    this.size = { width: 0, height: 0 };
    this.zoom = 1;
    this.pan = { x: 0, y: 0 };
    this.svg = null;

    var self = this;

    container.addEventListener('click', function (event) {
      var zoomButton = event.target.closest('[data-zoom]');
      if (zoomButton) {
        self.applyZoom(zoomButton.getAttribute('data-zoom'));
        return;
      }

      var step = event.target.closest('[data-part]');
      if (step) {
        self.step(step.getAttribute('data-part') === 'next' ? 1 : -1);
        return;
      }

      var node = event.target.closest('.flow-node');
      // Boundary stubs stand for methods that live in another part; there is no single
      // invocation behind them to select.
      if (node && !node.classList.contains('is-port') && self.options.onSelect) {
        self.options.onSelect(node.getAttribute('data-trace-id'));
      }
    });

    container.addEventListener('change', function (event) {
      if (event.target.closest('[data-part-select]')) {
        self.partIndex = Number(event.target.value) || 0;
        self.render();
        return;
      }

      if (event.target.closest('[data-strategy]')) {
        self.strategy = event.target.value;
        self.partIndex = 0;
        self.repartition();
      }
    });

    container.addEventListener('keydown', function (event) {
      if (event.key !== 'Enter' && event.key !== ' ') { return; }
      var node = event.target.closest('.flow-node');
      if (node && self.options.onSelect) {
        event.preventDefault();
        self.options.onSelect(node.getAttribute('data-trace-id'));
      }
    });

    // Hovering a node dims everything it is not connected to, which is the only practical way to
    // read a dense graph.
    container.addEventListener('mouseover', function (event) {
      var node = event.target.closest('.flow-node');
      self.highlight(node ? node.getAttribute('data-key') : null);
    });

    container.addEventListener('mouseleave', function () { self.highlight(null); });

    this.initPanZoom();
  }

  FlowView.prototype.initPanZoom = function () {
    var self = this;
    var dragging = false;
    var start = null;

    this.container.addEventListener('wheel', function (event) {
      if (!self.svg) { return; }
      event.preventDefault();
      self.zoom = Math.max(0.25, Math.min(2.5, self.zoom * (event.deltaY < 0 ? 1.12 : 0.89)));
      self.applyTransform();
    }, { passive: false });

    this.container.addEventListener('mousedown', function (event) {
      if (!self.svg || event.target.closest('.flow-node') || event.target.closest('[data-zoom]')) { return; }
      dragging = true;
      start = { x: event.clientX - self.pan.x, y: event.clientY - self.pan.y };
      self.container.classList.add('is-panning');
    });

    window.addEventListener('mousemove', function (event) {
      if (!dragging) { return; }
      self.pan = { x: event.clientX - start.x, y: event.clientY - start.y };
      self.applyTransform();
    });

    window.addEventListener('mouseup', function () {
      dragging = false;
      self.container.classList.remove('is-panning');
    });
  };

  FlowView.prototype.applyZoom = function (action) {
    if (action === 'in') { this.zoom = Math.min(2.5, this.zoom * 1.25); }
    else if (action === 'out') { this.zoom = Math.max(0.25, this.zoom / 1.25); }
    else { this.fit(); return; }
    this.applyTransform();
  };

  FlowView.prototype.fit = function () {
    if (!this.svg || !this.size.width) { return; }
    var box = this.container.getBoundingClientRect();
    var available = box.height - 44;
    this.zoom = Math.max(0.25, Math.min(1, Math.min(box.width / this.size.width, available / this.size.height)));
    this.pan = { x: 0, y: 0 };
    this.applyTransform();
  };

  FlowView.prototype.applyTransform = function () {
    if (!this.svg) { return; }
    var stage = this.svg.querySelector('.flow-stage');
    stage.setAttribute('transform',
      'translate(' + (this.pan.x + PAD) + ',' + (this.pan.y + PAD) + ') scale(' + this.zoom + ')');
    var label = this.container.querySelector('[data-zoom-level]');
    if (label) { label.textContent = Math.round(this.zoom * 100) + '%'; }
  };

  FlowView.prototype.highlight = function (key) {
    if (!this.svg) { return; }
    this.svg.classList.toggle('is-highlighting', Boolean(key));
    if (!key) {
      this.svg.querySelectorAll('.is-related, .is-focus').forEach(function (el) {
        el.classList.remove('is-related', 'is-focus');
      });
      return;
    }

    var related = Object.create(null);
    related[key] = true;

    this.svg.querySelectorAll('.flow-edge').forEach(function (edge) {
      var from = edge.getAttribute('data-from');
      var to = edge.getAttribute('data-to');
      var touches = from === key || to === key;
      edge.classList.toggle('is-related', touches);
      if (touches) { related[from] = true; related[to] = true; }
    });

    this.svg.querySelectorAll('.flow-node').forEach(function (node) {
      var nodeKey = node.getAttribute('data-key');
      node.classList.toggle('is-related', Boolean(related[nodeKey]));
      node.classList.toggle('is-focus', nodeKey === key);
    });
  };

  FlowView.prototype.setTrace = function (trace) {
    this.trace = trace;
    this.partIndex = 0;
    this.zoom = 1;
    this.pan = { x: 0, y: 0 };
    this.repartition();
  };

  /**
   * Keeps the methods that matched the filter plus the callers that lead to one, so a match is
   * never left floating with no route to it. Callers are flagged as context and drawn faded.
   */
  function restrictGraph(graph, result) {
    var nodes = Object.create(null);
    var order = [];

    graph.order.forEach(function (key) {
      if (!result.keptMethods[key]) { return; }
      var node = graph.nodes[key];
      node.isContext = !result.matchedMethods[key];
      nodes[key] = node;
      order.push(key);
    });

    return {
      nodes: nodes,
      order: order,
      edges: graph.edges.filter(function (edge) {
        return Boolean(nodes[edge.from] && nodes[edge.to]);
      })
    };
  }

  FlowView.prototype.setFilter = function (result) {
    this.filterResult = result;
    this.partIndex = 0;
    this.repartition();
  };

  FlowView.prototype.repartition = function () {
    if (!this.trace) { return; }

    var graph = buildGraph(this.trace);
    var result = this.filterResult;

    if (result && result.active) { graph = restrictGraph(graph, result); }
    this.isFiltered = Boolean(result && result.active);

    if (!graph.order.length) {
      this.split = null;
      this.renderChrome();
      this.svg.querySelector('.flow-stage').innerHTML = '';
      var bar = this.container.querySelector('[data-part-bar]');
      if (bar) { bar.hidden = true; }
      return;
    }

    this.split = PFT.partition.split(this.trace, {
      strategy: this.strategy,
      budget: NODES_PER_CHART,
      graph: graph
    });

    if (this.partIndex >= this.split.parts.length) { this.partIndex = 0; }
    this.render();
  };

  /**
   * Assembles the one part being drawn: its own methods, the edges between them, and a stub node
   * for each neighbouring part so the calls leaving the picture are still visible.
   */
  FlowView.prototype.buildPartGraph = function (part) {
    var source = this.split.graph;
    var nodes = Object.create(null);
    var order = [];

    part.keys.forEach(function (key) {
      var node = source.nodes[key];
      if (!node) { return; }
      node.isPort = false;
      node.layer = 0;
      node.x = 0;
      node.y = 0;
      nodes[key] = node;
      order.push(key);
    });

    var edges = part.internalEdges.slice();

    function addPort(direction, group) {
      var key = '__' + direction + ':' + group.other;
      nodes[key] = {
        key: key,
        isPort: true,
        portDirection: direction,
        portLabel: group.other,
        calls: group.calls,
        errors: 0,
        totalUs: 0,
        selfUs: 0,
        isRoot: direction === 'in',
        layer: 0,
        x: 0,
        y: 0
      };
      order.push(key);

      // Several crossing edges can land on the same method inside this part; show one stub edge
      // per endpoint rather than a bundle of parallel lines.
      var perEndpoint = Object.create(null);
      group.links.forEach(function (link) {
        perEndpoint[link.key] = (perEndpoint[link.key] || 0) + link.calls;
      });

      Object.keys(perEndpoint).forEach(function (endpoint) {
        if (!nodes[endpoint]) { return; }
        edges.push(direction === 'in'
          ? { from: key, to: endpoint, calls: perEndpoint[endpoint], errors: 0, isSelfCall: false, isPortEdge: true }
          : { from: endpoint, to: key, calls: perEndpoint[endpoint], errors: 0, isSelfCall: false, isPortEdge: true });
      });
    }

    part.inbound.forEach(function (group) { addPort('in', group); });
    part.outbound.forEach(function (group) { addPort('out', group); });

    return { nodes: nodes, order: order, edges: edges };
  };

  function describeStrategy(strategy) {
    switch (strategy) {
      case 'namespace': return 'namespace';
      case 'entry': return 'entry point';
      case 'component': return 'independent flow';
      case 'none': return 'nothing - one chart';
      default: return strategy;
    }
  }

  FlowView.prototype.currentPart = function () {
    return this.split ? this.split.parts[this.partIndex] : null;
  };

  FlowView.prototype.step = function (delta) {
    if (!this.split || this.split.parts.length < 2) { return; }
    var count = this.split.parts.length;
    this.partIndex = (this.partIndex + delta + count) % count;
    this.render();
  };

  FlowView.prototype.renderChrome = function () {
    // Re-adopt the existing canvas rather than assuming we built it: the chrome can already be
    // in place from an earlier render, and the reference has to be valid either way.
    if (this.container.querySelector('.flow-toolbar')) {
      this.svg = this.container.querySelector('.flow-canvas');
      return;
    }

    var strategies = [
      ['auto', 'Auto'],
      ['namespace', 'By namespace'],
      ['entry', 'By entry point'],
      ['component', 'By independent flow'],
      ['none', 'One chart']
    ].map(function (pair) {
      return '<option value="' + pair[0] + '">' + pair[1] + '</option>';
    }).join('');

    this.container.innerHTML =
      '<div class="flow-toolbar">' +
        '<button type="button" class="btn btn-quiet" data-zoom="out" title="Zoom out">&minus;</button>' +
        '<span class="flow-zoom" data-zoom-level>100%</span>' +
        '<button type="button" class="btn btn-quiet" data-zoom="in" title="Zoom in">+</button>' +
        '<button type="button" class="btn btn-quiet" data-zoom="fit">Fit</button>' +
        '<span class="flow-legend">' +
          '<span class="flow-key flow-key-entry"></span> entry point' +
          '<span class="flow-key flow-key-error"></span> threw' +
          '<span class="flow-key flow-key-back"></span> recursive' +
          '<span class="flow-key flow-key-port"></span> crosses boundary' +
        '</span>' +
      '</div>' +
      '<div class="part-bar" data-part-bar hidden>' +
        '<button type="button" class="btn btn-quiet" data-part="prev" title="Previous part">&lsaquo;</button>' +
        '<select data-part-select></select>' +
        '<button type="button" class="btn btn-quiet" data-part="next" title="Next part">&rsaquo;</button>' +
        // Split comes before the summary: the controls must survive a narrow pane, and the
        // summary is the part that can afford to be squeezed or wrapped.
        '<label class="field field-inline"><span>Split</span>' +
          '<select data-strategy>' + strategies + '</select>' +
        '</label>' +
        '<span class="part-summary" data-part-summary></span>' +
      '</div>' +
      '<svg class="flow-canvas" xmlns="http://www.w3.org/2000/svg">' +
        '<defs>' +
          '<marker id="pft-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto-start-reverse">' +
            '<path class="flow-arrow" d="M0,0 L8,4 L0,8 z"></path>' +
          '</marker>' +
        '</defs>' +
        '<g class="flow-stage"></g>' +
      '</svg>';

    this.svg = this.container.querySelector('.flow-canvas');
  };

  FlowView.prototype.renderPartBar = function () {
    var bar = this.container.querySelector('[data-part-bar]');
    var parts = this.split.parts;

    // The bar is only meaningful once there is more than one chart to move between - but the
    // strategy control lives on it, so it stays available whenever a split is possible.
    bar.hidden = parts.length < 2 && this.split.strategy === 'none' &&
      this.trace.stats.length <= this.split.budget && !this.isFiltered;

    this.container.querySelector('[data-strategy]').value = this.strategy;

    var select = this.container.querySelector('[data-part-select]');
    select.innerHTML = parts.map(function (part, index) {
      return '<option value="' + index + '">' + fmt.escape(part.title) +
        ' (' + part.methodCount + ' methods)' + '</option>';
    }).join('');
    select.value = String(this.partIndex);
    select.disabled = parts.length < 2;

    var part = this.currentPart();
    var summary = ['Part ' + (this.partIndex + 1) + ' of ' + parts.length];

    // Name the seam actually in use. Without this "Auto" gives no sign of what it chose, and
    // changing the Split control looks as though it did nothing.
    summary.push('split by ' + describeStrategy(this.split.strategy));
    if (this.isFiltered) { summary.push('filtered'); }
    if (part) {
      summary.push(fmt.count(part.callCount) + ' calls');
      if (part.errorCount) { summary.push(fmt.count(part.errorCount) + ' threw'); }
      if (part.inbound.length || part.outbound.length) {
        summary.push(part.inbound.length + ' in / ' + part.outbound.length + ' out');
      }
      if (part.subtitle) { summary.push(part.subtitle); }
    }
    this.container.querySelector('[data-part-summary]').textContent = summary.join(' · ');
  };

  FlowView.prototype.render = function () {
    if (!this.trace) { this.container.innerHTML = ''; this.svg = null; return; }

    this.renderChrome();

    if (!this.split) {
      this.svg.querySelector('.flow-stage').innerHTML =
        '<text class="flow-empty" x="24" y="40">Nothing matches these filters.</text>';
      return;
    }

    this.renderPartBar();

    var part = this.currentPart();
    if (!part) { return; }

    this.graph = this.buildPartGraph(part);
    this.size = layout(this.graph);

    var maxTotalUs = 0;
    var self = this;
    this.graph.order.forEach(function (key) {
      var node = self.graph.nodes[key];
      if (node.totalUs > maxTotalUs) { maxTotalUs = node.totalUs; }
    });

    // Call-count labels help on a sparse chart and turn a dense one into confetti.
    var showLabels = this.graph.edges.length <= 30;
    var edges = this.graph.edges.map(function (edge) {
      return edgeSvg(edge, self.graph, showLabels);
    }).join('');
    var nodes = this.graph.order.map(function (key) {
      return nodeSvg(self.graph.nodes[key], maxTotalUs);
    }).join('');

    this.svg.querySelector('.flow-stage').innerHTML = edges + nodes;
    this.zoom = 1;
    this.pan = { x: 0, y: 0 };
    this.fit();
  };

  PFT.FlowView = FlowView;
})(window.PFT = window.PFT || {});
