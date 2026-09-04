/*
 * The Brief tab: the whole run boiled down to a block of text you can paste into an LLM and ask
 * for a sequence diagram, an activity diagram, a use-case diagram - or just read yourself.
 *
 * The hard part is not "dump the trace", it is dumping the *right* amount of it. A raw
 * events.jsonl is far too long and mostly redundant; a bare method list is too little to draw
 * anything meaningful from. So the brief keeps what the diagram types actually need:
 *
 *   participants + entry points   -> use-case and sequence lifelines
 *   ordered call flow with args   -> sequence messages, activity steps
 *   folded repeats ("x3")         -> loops, without the transcript of every iteration
 *   thread changes                -> async boundaries and parallel lanes
 *   aggregated caller -> callee   -> the overall control flow
 *   exceptions with propagation   -> alternate/exception paths
 *
 * and drops everything that would only add volume: per-call ids, timestamps, stack traces, and
 * the second through nth identical sibling call.
 *
 * The notation is explained inline at the top of the output, so the text stands on its own for
 * both a human reader and a model that has never seen this tool.
 */
(function (PFT) {
  'use strict';

  var fmt = PFT.format;

  // Every section needs a ceiling, not just the call flow: on a wide run the aggregated call
  // graph and the participant list are what actually blow the size up.
  var LEVELS = {
    compact: { maxLines: 60, maxDepth: 4, valueChars: 0, maxEdges: 40, maxMethodsPerType: 20, label: 'Compact' },
    standard: { maxLines: 180, maxDepth: 8, valueChars: 32, maxEdges: 120, maxMethodsPerType: 40, label: 'Standard' },
    full: { maxLines: 600, maxDepth: 24, valueChars: 80, maxEdges: 500, maxMethodsPerType: 250, label: 'Full' }
  };

  var brief = {};

  brief.levels = LEVELS;

  /**
   * The toolbar filter in force while a brief is being built.
   *
   * Module-scoped rather than threaded through a dozen signatures - building is synchronous, so
   * there is only ever one in flight. A filtered brief is labelled as such in its own header:
   * handing someone a summary that silently describes part of a run would be worse than useless.
   */
  var activeFilter = null;

  function isKept(node) {
    return !activeFilter || !activeFilter.active || Boolean(activeFilter.keptTraceIds[node.traceId]);
  }

  function isMatchedNode(node) {
    return !activeFilter || !activeFilter.active || Boolean(activeFilter.matchedTraceIds[node.traceId]);
  }


  function isKeptMethod(key) {
    return !activeFilter || !activeFilter.active || Boolean(activeFilter.keptMethods[key]);
  }

  function filteredStats(trace) {
    if (!activeFilter || !activeFilter.active) { return trace.stats; }
    return trace.stats.filter(function (row) { return isKeptMethod(row.key); });
  }

  function indent(depth) {
    var text = '';
    for (var i = 0; i < depth; i++) { text += '  '; }
    return text;
  }

  /** "Outer+SumTotals" is how a local function is recorded; show the type that contains it. */
  function shortDeclaringType(declaringType, method) {
    var short = fmt.shortType(declaringType);
    var suffix = '+' + method;
    return short.slice(-suffix.length) === suffix ? short.slice(0, -suffix.length) : short;
  }

  function shortName(invocation) {
    var type = shortDeclaringType(invocation.declaringType, invocation.method);
    return (type ? type + '.' : '') + invocation.method;
  }

  function valueText(captured, limit) {
    if (!captured) { return '?'; }
    switch (captured.serializationStatus) {
      case 'Null': return 'null';
      case 'Redacted': return '<redacted>';
      case 'Unavailable': return '<unavailable>';
      case 'Failed': return '<unserializable>';
      default: break;
    }
    return fmt.ellipsis(fmt.inlineJson(captured.value), limit);
  }

  function argsText(invocation, level) {
    if (!invocation.parameters) { return ''; }
    var names = Object.keys(invocation.parameters);
    if (!names.length) { return ''; }

    // At the compact level the parameter *names* still carry meaning for a diagram; their values
    // do not, and they are what makes the text long.
    if (!level.valueChars) { return names.join(', '); }

    return names.map(function (name) {
      return name + '=' + valueText(invocation.parameters[name], level.valueChars);
    }).join(', ');
  }

  function returnText(invocation, level) {
    if (!level.valueChars) { return ''; }

    var parts = [];
    if (invocation.returnValue) {
      var value = valueText(invocation.returnValue, level.valueChars);
      if (value !== '?') { parts.push(value); }
    }

    // ref/out values are only knowable at exit, and are as much "output" as the return value -
    // a sequence diagram wants them on the return arrow.
    if (invocation.outParameters) {
      Object.keys(invocation.outParameters).forEach(function (name) {
        parts.push('out ' + name + '=' + valueText(invocation.outParameters[name], level.valueChars));
      });
    }

    return parts.length ? ' => ' + parts.join(', ') : '';
  }

  /**
   * Folds runs of consecutive sibling calls to the same method into one entry. This is what turns
   * "the same thing happened 40 times" into a loop a diagram can express, and it is where most of
   * the size saving comes from on real traces.
   */
  function fold(children) {
    var groups = [];

    children.forEach(function (child) {
      var last = groups[groups.length - 1];
      var key = PFT.model.methodKey(child);

      if (last && last.key === key) {
        last.count++;
        last.members.push(child);
        if (child.exceptions.length) { last.errors++; }
        return;
      }

      groups.push({
        key: key,
        count: 1,
        errors: child.exceptions.length ? 1 : 0,
        first: child,
        members: [child]
      });
    });

    return groups;
  }

  function callLine(group, depth, level, parentThreadId) {
    var invocation = group.first;
    var line = indent(depth) + shortName(invocation) + '(' + argsText(invocation, level) + ')';

    if (group.count > 1) {
      line += ' x' + group.count;
      var varied = group.members.some(function (member) {
        return argsText(member, level) !== argsText(invocation, level);
      });
      if (varied) { line += ' (args vary)'; }
    }

    // Only note the thread when it changes - a switch is an async boundary worth drawing, an
    // unchanged thread on every line is noise.
    if (invocation.threadId !== null && invocation.threadId !== parentThreadId) {
      line += ' [thread ' + invocation.threadId + (invocation.isThreadPoolThread ? ', pool' : '') + ']';
    }

    if (group.count === 1) { line += returnText(invocation, level); }

    if (group.errors) {
      var thrower = group.members.filter(function (member) { return member.exceptions.length; })[0];
      var event = thrower.exceptions[0];
      line += ' !! ' + fmt.shortType(event.exceptionType) +
        (event.message ? ': ' + fmt.ellipsis(event.message, 80) : '');
    } else if (group.count === 1 && !invocation.exit) {
      line += ' (no exit recorded)';
    }

    return line;
  }

  function renderFlow(allChildren, depth, level, parentThreadId, state, out) {
    // Under a filter, a call is described when it matches or when it is on the path to something
    // that does - the same rule the call tree uses, so the two never disagree.
    var children = activeFilter && activeFilter.active ? allChildren.filter(isKept) : allChildren;
    if (!children.length) { return; }

    if (depth > level.maxDepth) {
      out.push(indent(depth) + '... ' + fmt.count(children.length) + ' deeper call(s) not shown');
      return;
    }

    var groups = fold(children);

    for (var i = 0; i < groups.length; i++) {
      if (out.length >= level.maxLines) {
        state.omitted += groups.length - i;
        return;
      }

      var group = groups[i];
      out.push(callLine(group, depth, level, parentThreadId));

      // For a folded group only the first member's subtree is described; the others are, by
      // definition of the fold, the same call happening again.
      renderFlow(group.first.children, depth + 1, level, group.first.threadId, state, out);
      if (group.count > 1 && group.first.children.length) {
        out.push(indent(depth + 1) + '(repeats above ' + (group.count - 1) + ' more time(s))');
      }
    }
  }

  /* ------------------------------------------------------------- sections */

  function headerSection(trace) {
    var meta = trace.metadata || {};
    var lines = ['# Program execution trace' + (meta.application ? ' - ' + meta.application : '')];

    var facts = [];
    if (meta.startedAtUtc) { facts.push('started ' + meta.startedAtUtc); }
    facts.push(fmt.count(trace.invocations.length) + ' calls');
    facts.push(fmt.count(trace.stats.length) + ' distinct methods');
    facts.push(fmt.count(trace.threads.length) + ' thread(s)');
    if (meta.droppedEventCount) { facts.push(fmt.count(meta.droppedEventCount) + ' events DROPPED - trace is incomplete'); }
    lines.push(facts.join(' | '));

    lines.push('');
    lines.push('Captured at runtime by instrumenting the source, so this is what actually ran -');
    lines.push('not a static analysis of what could run.');

    if (activeFilter && activeFilter.active) {
      var description = PFT.filter.describe(activeFilter.compiled);
      lines.push('');
      lines.push('!! FILTERED VIEW - this is NOT the whole run.');
      lines.push('Only calls ' + (description || 'matching the active filter') +
        ' are included, together with their callers for context.');
      lines.push(fmt.count(activeFilter.matchCount) + ' of ' + fmt.count(trace.invocations.length) +
        ' calls matched. Anything else the program did is absent by design.');
    }

    return lines;
  }

  function notationSection() {
    return [
      '',
      '## Notation',
      '- Indentation shows call depth: an indented line was called by the line above it.',
      '- `xN` means the same call repeated N times in a row (a loop).',
      '- `=> value` is the return value. `!!` marks a thrown exception.',
      '- `[thread N]` appears only where execution moved to a different thread (async boundary).',
      '- Values are truncated; they are illustrative, not exhaustive.'
    ];
  }

  function participantsSection(trace, level) {
    var byType = Object.create(null);
    var order = [];

    filteredStats(trace).forEach(function (row) {
      var type = shortDeclaringType(row.declaringType, row.method) || '(global)';
      if (!byType[type]) { byType[type] = []; order.push(type); }
      if (byType[type].indexOf(row.method) === -1) { byType[type].push(row.method); }
    });

    var lines = ['', '## Participants (' + order.length + ' type(s))'];
    order.forEach(function (type) {
      var methods = byType[type];
      var shown = methods.slice(0, level.maxMethodsPerType);
      lines.push('- ' + type + ': ' + shown.join(', ') +
        (methods.length > shown.length ? ', +' + (methods.length - shown.length) + ' more' : ''));
    });
    return lines;
  }

  function entryPointsSection(trace, level) {
    var seen = Object.create(null);
    var entries = [];

    trace.roots.filter(isKept).forEach(function (root) {
      var key = PFT.model.methodKey(root);
      if (seen[key]) { seen[key].count++; return; }
      seen[key] = { invocation: root, count: 1 };
      entries.push(seen[key]);
    });

    var lines = ['', '## Entry points (' + entries.length + ')',
      'Nothing in this trace called these - each one starts a flow of its own.'];

    entries.forEach(function (entry) {
      lines.push('- ' + shortName(entry.invocation) + '(' + argsText(entry.invocation, level) + ')' +
        (entry.count > 1 ? ' x' + entry.count : ''));
    });

    return lines;
  }

  function flowSection(trace, level) {
    var lines = ['', '## Call flow (in execution order)'];
    var state = { omitted: 0 };
    var out = [];

    renderFlow(trace.roots.filter(isKept), 0, level, null, state, out);
    lines = lines.concat(out);

    if (state.omitted) {
      lines.push('... ' + fmt.count(state.omitted) + ' further call(s) omitted for length; ' +
        'the aggregated call graph below still covers them.');
    }

    return lines;
  }

  function callGraphSection(trace, level) {
    var graph = PFT.model.callGraph(trace);
    if (!graph.edges.length) { return []; }

    var sorted = graph.edges.slice().sort(function (a, b) { return b.calls - a.calls; });
    var shown = sorted.slice(0, level.maxEdges);

    var lines = ['', '## Call graph (aggregated caller -> callee pairs' +
      (shown.length < sorted.length ? ', busiest ' + shown.length + ' of ' + sorted.length : '') + ')'];

    shown.forEach(function (edge) {
      var from = graph.nodes[edge.from];
      var to = graph.nodes[edge.to];
      lines.push('- ' + shortDeclaringType(from.declaringType, from.method) + '.' + from.method +
        ' -> ' + shortDeclaringType(to.declaringType, to.method) + '.' + to.method +
        ' (' + edge.calls + 'x' + (edge.isSelfCall ? ', recursive' : '') +
        (edge.errors ? ', ' + edge.errors + ' threw' : '') + ')');
    });

    if (shown.length < sorted.length) {
      lines.push('- ... ' + (sorted.length - shown.length) + ' further pair(s) omitted (fewest calls)');
    }

    return lines;
  }

  function reportsType(invocation, exceptionType) {
    return invocation.exceptions.some(function (event) { return event.exceptionType === exceptionType; });
  }

  function exceptionsSection(trace) {
    var thrown = trace.invocations.filter(function (invocation) {
      return invocation.exceptions.length && isMatchedNode(invocation);
    });
    if (!thrown.length) { return ['', '## Exceptions', 'None - every call completed normally.']; }

    // Every frame an exception unwinds through records it, so the raw events show one failure as
    // several. The *origin* is the deepest frame reporting that type - the one whose callees did
    // not report it too. Everything above it is propagation, and belongs on the same entry.
    var groups = Object.create(null);
    var order = [];

    thrown.forEach(function (invocation) {
      invocation.exceptions.forEach(function (event) {
        var isOrigin = !invocation.children.some(function (child) {
          return reportsType(child, event.exceptionType);
        });
        if (!isOrigin) { return; }

        var key = event.exceptionType + '|' + PFT.model.methodKey(invocation);
        var group = groups[key];

        if (!group) {
          group = groups[key] = {
            type: event.exceptionType,
            message: event.message,
            at: invocation,
            count: 0,
            propagatedThrough: []
          };
          order.push(key);

          var parent = invocation.parent;
          while (parent) {
            if (reportsType(parent, event.exceptionType)) {
              group.propagatedThrough.push(shortName(parent));
            }
            parent = parent.parent;
          }
        }

        group.count++;
      });
    });

    var lines = ['', '## Exceptions (' + order.length + ')'];
    order.forEach(function (key) {
      var group = groups[key];
      lines.push('- ' + fmt.shortType(group.type) + (group.count > 1 ? ' x' + group.count : '') +
        (group.message ? ': "' + fmt.ellipsis(group.message, 120) + '"' : ''));
      lines.push('  thrown in ' + shortName(group.at));
      if (group.propagatedThrough.length) {
        lines.push('  propagated out through ' + group.propagatedThrough.join(' -> '));
      } else {
        lines.push('  did not propagate to a traced caller');
      }
    });

    return lines;
  }

  function concurrencySection(trace) {
    if (trace.threads.length < 2) { return []; }

    var counts = Object.create(null);
    var pool = Object.create(null);

    trace.invocations.forEach(function (invocation) {
      if (invocation.threadId === null) { return; }
      counts[invocation.threadId] = (counts[invocation.threadId] || 0) + 1;
      if (invocation.isThreadPoolThread) { pool[invocation.threadId] = true; }
    });

    var lines = ['', '## Concurrency',
      'Work crossed ' + trace.threads.length + ' threads, so some of these calls ran ' +
      'asynchronously and may overlap in time.'];

    trace.threads.forEach(function (id) {
      lines.push('- thread ' + id + (pool[id] ? ' (thread pool)' : '') + ': ' +
        fmt.count(counts[id] || 0) + ' calls');
    });

    return lines;
  }

  function hotspotsSection(trace) {
    var rows = filteredStats(trace).slice().sort(function (a, b) { return b.selfUs - a.selfUs; }).slice(0, 5);
    if (!rows.length) { return []; }

    var lines = ['', '## Where the time went (top 5 by self time)'];
    rows.forEach(function (row) {
      lines.push('- ' + shortDeclaringType(row.declaringType, row.method) + '.' + row.method +
        ': ' + fmt.duration(row.selfUs) + ' self across ' + fmt.count(row.calls) + ' call(s)');
    });
    return lines;
  }

  /** Assembles the whole brief. Takes a trace and the filter in force, returns text. */
  brief.build = function (trace, levelName, filterResult) {
    var level = LEVELS[levelName] || LEVELS.standard;
    activeFilter = filterResult || null;

    return []
      .concat(headerSection(trace))
      .concat(notationSection())
      .concat(participantsSection(trace, level))
      .concat(entryPointsSection(trace, level))
      .concat(flowSection(trace, level))
      .concat(callGraphSection(trace, level))
      .concat(exceptionsSection(trace))
      .concat(concurrencySection(trace))
      .concat(hotspotsSection(trace))
      .join('\n') + '\n';
  };

  /* ----------------------------------------------------------- split brief */

  /**
   * A run big enough to need splitting gets an overview part plus one part per group.
   *
   * The overview is what someone reads (or pastes) first: how big the run is, which subsystems
   * exist, how they call each other, and what went wrong. It deliberately stays module-level -
   * no method-by-method detail - so it fits in a prompt no matter how large the run is.
   */
  function overviewPart(trace, split, level) {
    var lines = []
      .concat(headerSection(trace))
      .concat([
        '',
        'This run is too large for a single brief, so it is split into ' + split.parts.length +
          ' further part(s) by ' + describeStrategy(split.strategy) + '.',
        'This part is the map; each following part covers one group in detail.'
      ])
      .concat(notationSection());

    lines.push('', '## Groups (' + split.parts.length + ')');
    split.parts.forEach(function (part, index) {
      lines.push('- Part ' + (index + 2) + '. ' + part.title + ': ' +
        fmt.count(part.methodCount) + ' methods, ' + fmt.count(part.callCount) + ' calls' +
        (part.errorCount ? ', ' + fmt.count(part.errorCount) + ' threw' : '') +
        (part.entryKeys.length ? ', ' + part.entryKeys.length + ' entry point(s)' : ''));
    });

    lines.push('', '## How the groups call each other');
    var crossings = 0;
    split.parts.forEach(function (part) {
      part.outbound.forEach(function (group) {
        crossings++;
        lines.push('- ' + part.title + ' -> ' + group.other + ' (' + fmt.count(group.calls) + ' calls)');
      });
    });
    if (!crossings) { lines.push('- none: every group runs independently'); }

    return lines
      .concat(entryPointsSection(trace, level))
      .concat(exceptionsSection(trace))
      .concat(concurrencySection(trace))
      .concat(hotspotsSection(trace))
      .join('\n') + '\n';
  }

  function describeStrategy(strategy) {
    switch (strategy) {
      case 'namespace': return 'namespace';
      case 'entry': return 'entry point';
      case 'component': return 'independent flow';
      default: return 'size';
    }
  }

  /** One group's detail: its methods, its own call graph, and what crosses its boundary. */
  function groupPart(trace, split, part, index, level) {
    var lines = [
      '# Part ' + (index + 2) + ': ' + part.title,
      fmt.count(part.methodCount) + ' methods | ' + fmt.count(part.callCount) + ' calls' +
        (part.errorCount ? ' | ' + fmt.count(part.errorCount) + ' threw' : '') +
        ' | ' + fmt.duration(part.selfUs) + ' self time',
      '',
      'One group of a larger run. Calls crossing in or out are listed below, so treat this as a',
      'component view rather than the whole program.'
    ];

    if (part.subtitle) { lines.push('', part.subtitle); }

    lines.push('', '## Methods in this group');
    part.keys.slice(0, level.maxMethodsPerType * 4).forEach(function (key) {
      var node = split.graph.nodes[key];
      lines.push('- ' + key + ' (' + fmt.count(node.calls) + ' calls, ' +
        fmt.duration(node.selfUs) + ' self' + (node.errors ? ', ' + node.errors + ' threw' : '') +
        (node.isRoot ? ', entry point' : '') + ')');
    });
    if (part.keys.length > level.maxMethodsPerType * 4) {
      lines.push('- ... ' + (part.keys.length - level.maxMethodsPerType * 4) + ' more');
    }

    lines.push('', '## Calls within this group');
    if (!part.internalEdges.length) {
      lines.push('- none: these methods do not call each other in this run');
    } else {
      part.internalEdges
        .slice()
        .sort(function (a, b) { return b.calls - a.calls; })
        .slice(0, level.maxEdges)
        .forEach(function (edge) {
          lines.push('- ' + edge.from + ' -> ' + edge.to + ' (' + edge.calls + 'x' +
            (edge.isSelfCall ? ', recursive' : '') +
            (edge.errors ? ', ' + edge.errors + ' threw' : '') + ')');
        });
      if (part.internalEdges.length > level.maxEdges) {
        lines.push('- ... ' + (part.internalEdges.length - level.maxEdges) + ' further pair(s) omitted');
      }
    }

    lines.push('', '## Boundary');
    if (!part.inbound.length && !part.outbound.length) {
      lines.push('- nothing crosses: this group is self-contained in this run');
    }
    part.inbound.forEach(function (group) {
      lines.push('- called from ' + group.other + ' (' + fmt.count(group.calls) + ' calls)');
    });
    part.outbound.forEach(function (group) {
      lines.push('- calls out to ' + group.other + ' (' + fmt.count(group.calls) + ' calls)');
    });

    return lines.join('\n') + '\n';
  }

  /**
   * Builds every part of the brief. Small runs get exactly one part, identical to what
   * brief.build produces - splitting only kicks in when a single brief would be unusable.
   */
  brief.buildParts = function (trace, levelName, strategyName, filterResult) {
    var level = LEVELS[levelName] || LEVELS.standard;
    var strategy = strategyName || 'auto';

    activeFilter = filterResult || null;

    // Split only what the filter left, so the parts describe the filtered run rather than
    // pointing at groups that are now empty.
    var graph = PFT.model.callGraph(trace);
    if (activeFilter && activeFilter.active) {
      var kept = Object.create(null);
      var order = [];
      graph.order.forEach(function (key) {
        if (!isKeptMethod(key)) { return; }
        kept[key] = graph.nodes[key];
        order.push(key);
      });
      graph = {
        nodes: kept,
        order: order,
        edges: graph.edges.filter(function (edge) { return kept[edge.from] && kept[edge.to]; })
      };
    }

    var split = PFT.partition.split(trace, {
      strategy: strategy,
      budget: level.maxMethodsPerType * 3,
      graph: graph
    });

    if (split.strategy === 'none' || split.parts.length < 2) {
      return {
        strategy: 'none',
        parts: [{ title: 'Whole run', text: brief.build(trace, levelName, filterResult) }]
      };
    }

    activeFilter = filterResult || null;
    var parts = [{ title: 'Overview', text: overviewPart(trace, split, level) }];
    split.parts.forEach(function (part, index) {
      parts.push({ title: part.title, text: groupPart(trace, split, part, index, level) });
    });

    return { strategy: split.strategy, parts: parts };
  };

  /* ---------------------------------------------------------------- view */

  function BriefView(container, options) {
    this.container = container;
    this.options = options || {};
    this.trace = null;
    this.level = 'standard';
    this.text = '';
    this.split = null;
    this.partIndex = 0;

    var self = this;

    container.addEventListener('click', function (event) {
      if (event.target.closest('[data-copy-all]')) { self.copy(true); return; }
      if (event.target.closest('[data-copy]')) { self.copy(false); return; }
      if (event.target.closest('[data-select]')) { self.selectAll(); return; }

      var step = event.target.closest('[data-part]');
      if (step) { self.step(step.getAttribute('data-part') === 'next' ? 1 : -1); }
    });

    container.addEventListener('change', function (event) {
      if (event.target.closest('[data-level]')) {
        self.level = event.target.value;
        self.partIndex = 0;
        self.render();
        return;
      }

      if (event.target.closest('[data-part-select]')) {
        self.partIndex = Number(event.target.value) || 0;
        self.render();
      }
    });
  }

  BriefView.prototype.setFilter = function (result) {
    this.filterResult = result;
    this.partIndex = 0;
    this.render();
  };

  BriefView.prototype.step = function (delta) {
    if (!this.split || this.split.parts.length < 2) { return; }
    var count = this.split.parts.length;
    this.partIndex = (this.partIndex + delta + count) % count;
    this.render();
  };

  BriefView.prototype.setTrace = function (trace) {
    this.trace = trace;
    this.render();
  };

  BriefView.prototype.allPartsText = function () {
    if (this.cachedAllText) { return this.cachedAllText; }

    var parts = this.split.parts;
    this.cachedAllText = parts.map(function (part, index) {
      return '===== Part ' + (index + 1) + ' of ' + parts.length + ': ' + part.title + ' =====\n\n' + part.text;
    }).join('\n');

    return this.cachedAllText;
  };

  BriefView.prototype.copy = function (everything) {
    var button = this.container.querySelector(everything ? '[data-copy-all]' : '[data-copy]');
    PFT.clipboard.writeFromButton(button, everything ? this.allPartsText() : this.text);
  };

  BriefView.prototype.selectAll = function () {
    var pre = this.container.querySelector('.brief-text');
    if (!pre) { return; }
    var range = document.createRange();
    range.selectNodeContents(pre);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  };

  /**
   * The toolbar is built once and left alone. Rebuilding it on every level change would detach
   * the very <select> that fired the change - taking the user's focus with it, and leaving a
   * stale node behind for anything still holding a reference.
   */
  BriefView.prototype.buildChrome = function () {
    if (this.container.querySelector('.brief-text')) { return; }

    var options = Object.keys(LEVELS).map(function (name) {
      return '<option value="' + name + '">' + LEVELS[name].label + '</option>';
    }).join('');

    this.container.innerHTML =
      '<div class="brief-bar">' +
        '<button type="button" class="btn btn-primary" data-copy>Copy this part</button>' +
        '<button type="button" class="btn" data-copy-all hidden>Copy all parts</button>' +
        '<button type="button" class="btn btn-quiet" data-select>Select all</button>' +
        '<label class="field field-inline"><span>Detail</span>' +
          '<select data-level>' + options + '</select>' +
        '</label>' +
        '<span class="brief-size"></span>' +
      '</div>' +
      '<div class="part-bar" data-part-bar hidden>' +
        '<button type="button" class="btn btn-quiet" data-part="prev" title="Previous part">&lsaquo;</button>' +
        '<select data-part-select></select>' +
        '<button type="button" class="btn btn-quiet" data-part="next" title="Next part">&rsaquo;</button>' +
        '<span class="part-summary" data-part-summary></span>' +
      '</div>' +
      '<p class="brief-hint">' +
        'Paste this into any AI assistant and ask for a <strong>sequence diagram</strong>, ' +
        '<strong>activity diagram</strong>, <strong>use-case diagram</strong>, or a written ' +
        'walkthrough. It is self-describing, so no other context is needed. Drop to ' +
        '<em>Compact</em> if the run is large, or raise to <em>Full</em> for more argument detail.' +
      '</p>' +
      '<pre class="brief-text"></pre>';
  };

  BriefView.prototype.render = function () {
    if (!this.trace) { this.container.innerHTML = ''; return; }

    this.buildChrome();

    // Building every part costs real time on a large run, and only the trace, the detail level
    // or the filter can change what they contain - moving between parts must not pay for it.
    if (!this.split || this.cachedTrace !== this.trace || this.cachedLevel !== this.level ||
        this.cachedFilter !== this.filterResult) {
      this.split = brief.buildParts(this.trace, this.level, null, this.filterResult);
      this.cachedTrace = this.trace;
      this.cachedLevel = this.level;
      this.cachedFilter = this.filterResult;
      this.cachedAllText = null;
    }

    if (this.partIndex >= this.split.parts.length) { this.partIndex = 0; }

    var parts = this.split.parts;
    var multi = parts.length > 1;
    this.text = parts[this.partIndex].text;

    var bar = this.container.querySelector('[data-part-bar]');
    bar.hidden = !multi;
    this.container.querySelector('[data-copy-all]').hidden = !multi;
    this.container.querySelector('[data-copy]').textContent = multi ? 'Copy this part' : 'Copy to clipboard';

    if (multi) {
      var select = this.container.querySelector('[data-part-select]');
      select.innerHTML = parts.map(function (part, index) {
        return '<option value="' + index + '">' + fmt.escape(part.title) + '</option>';
      }).join('');
      select.value = String(this.partIndex);

      var whole = this.allPartsText();
      this.container.querySelector('[data-part-summary]').textContent =
        'Part ' + (this.partIndex + 1) + ' of ' + parts.length + ' · split by ' +
        describeStrategy(this.split.strategy) + ' · all parts ~' +
        fmt.count(Math.round(whole.length / 4)) + ' tokens';
    }

    var lineCount = this.text.split('\n').length;
    // Rough enough to answer "will this fit in a chat window?" - real tokenizers vary.
    var approxTokens = Math.round(this.text.length / 4);

    this.container.querySelector('[data-level]').value = this.level;
    this.container.querySelector('.brief-size').textContent =
      fmt.count(lineCount) + ' lines · ~' + fmt.count(approxTokens) + ' tokens';
    // textContent, not innerHTML: the brief is full of user data and angle brackets.
    this.container.querySelector('.brief-text').textContent = this.text;
  };

  PFT.brief = brief;
  PFT.BriefView = BriefView;
})(window.PFT = window.PFT || {});
