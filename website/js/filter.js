/*
 * The toolbar filter, in one place.
 *
 * The filter sits above the tabs, so it has to mean the same thing in all of them. It is defined
 * once here - against an *invocation* - and every view derives what it needs from that single
 * pass: the tree shows matching calls, the methods table shows methods that have one, the raw
 * event list shows the events belonging to one, the flowchart draws only those methods, and the
 * brief summarises only them.
 *
 * Compiling the text once per change rather than once per node matters: a regular expression
 * recompiled for every row of a large trace is the difference between typing smoothly and not.
 */
(function (PFT) {
  'use strict';

  var filter = {};

  /**
   * Turns the toolbar's raw state into something that can be asked about a node.
   *
   * The haystack is lowercased, so a pattern always carries the `i` flag - and therefore must NOT
   * be lowercased itself the way a plain substring is. Doing that would quietly rewrite `\S` into
   * `\s`, `\W` into `\w` and `\B` into `\b`, inverting what was asked for.
   */
  filter.compile = function (spec) {
    var compiled = {
      spec: spec,
      regex: Boolean(spec.regex),
      error: null,
      pattern: null,
      plainText: '',
      textActive: false
    };

    if (spec.text) {
      if (spec.regex) {
        try {
          compiled.pattern = new RegExp(spec.text, 'i');
          compiled.textActive = true;
        } catch (err) {
          compiled.error = err && err.message ? err.message : 'Invalid regular expression';
        }
      } else {
        compiled.plainText = spec.text.toLowerCase();
        compiled.textActive = Boolean(compiled.plainText);
      }
    }

    compiled.active = compiled.textActive || spec.errorsOnly ||
      spec.minDurationUs > 0 || spec.threadId !== '';

    compiled.matches = function (node) {
      if (spec.errorsOnly && !node.exceptions.length) { return false; }
      if (spec.minDurationUs > 0 &&
          !(typeof node.durationUs === 'number' && node.durationUs >= spec.minDurationUs)) {
        return false;
      }
      if (spec.threadId !== '' && String(node.threadId) !== String(spec.threadId)) { return false; }

      if (compiled.textActive) {
        var hit = compiled.regex
          ? compiled.pattern.test(node.searchText)
          : node.searchText.indexOf(compiled.plainText) !== -1;
        if (!hit) { return false; }
      }

      return true;
    };

    return compiled;
  };

  /**
   * Runs a compiled filter over a trace once, and returns everything the views need:
   *
   *   matchedTraceIds  - invocations that match
   *   keptTraceIds     - those plus their ancestors, so a match is never shown without its callers
   *   matchedMethods   - method keys with at least one matching invocation
   *   matchCount       - how many invocations matched
   *
   * With no active filter this returns `active: false` and the views show everything, rather than
   * every view separately re-deriving "no filter means all".
   */
  filter.apply = function (trace, compiled) {
    if (!trace || !compiled || !compiled.active) {
      return { active: false, matchCount: trace ? trace.invocations.length : 0 };
    }

    var matchedTraceIds = Object.create(null);
    var keptTraceIds = Object.create(null);
    var matchedMethods = Object.create(null);
    var keptMethods = Object.create(null);
    var matchCount = 0;

    trace.invocations.forEach(function (invocation) {
      if (!compiled.matches(invocation)) { return; }

      matchCount++;
      matchedTraceIds[invocation.traceId] = true;
      keptTraceIds[invocation.traceId] = true;

      var key = PFT.model.methodKey(invocation);
      matchedMethods[key] = true;
      keptMethods[key] = true;

      var parent = invocation.parent;
      while (parent && !keptTraceIds[parent.traceId]) {
        keptTraceIds[parent.traceId] = true;
        keptMethods[PFT.model.methodKey(parent)] = true;
        parent = parent.parent;
      }
    });

    return {
      active: true,
      compiled: compiled,
      matchedTraceIds: matchedTraceIds,
      keptTraceIds: keptTraceIds,
      // Methods that matched, versus those plus the callers that lead to one. Views that show
      // structure (the graph, the brief) need the callers or a match is left floating with no
      // route to it; views that list things (the methods table) want only the matches.
      matchedMethods: matchedMethods,
      keptMethods: keptMethods,
      matchCount: matchCount
    };
  };

  /** A short human description of what is being filtered, for headings and generated text. */
  filter.describe = function (compiled) {
    if (!compiled || !compiled.active) { return ''; }

    var parts = [];
    if (compiled.textActive) {
      parts.push((compiled.regex ? 'matching /' + compiled.spec.text + '/i' : 'containing "' + compiled.spec.text + '"'));
    }
    if (compiled.spec.errorsOnly) { parts.push('that threw'); }
    if (compiled.spec.minDurationUs > 0) {
      parts.push('lasting at least ' + PFT.format.duration(compiled.spec.minDurationUs));
    }
    if (compiled.spec.threadId !== '') { parts.push('on thread ' + compiled.spec.threadId); }

    return parts.join(', ');
  };

  PFT.filter = filter;
})(window.PFT = window.PFT || {});
