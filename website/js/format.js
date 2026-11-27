/*
 * Small, dependency-free formatting helpers shared by every view.
 *
 * Everything here is pure: given a trace record it returns a string (or a plain object of
 * strings). No DOM, no state - so the tree, the details pane and the methods table always
 * render the same value the same way.
 */
(function (PFT) {
  'use strict';

  var fmt = {};

  /** Escapes text for safe interpolation into innerHTML. */
  fmt.escape = function (value) {
    if (value === null || value === undefined) { return ''; }
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  };

  /**
   * Durations arrive as microseconds. Pick a unit that keeps roughly 3 significant digits so a
   * column of timings stays scannable instead of turning into a wall of zeroes.
   */
  fmt.duration = function (microseconds) {
    if (microseconds === null || microseconds === undefined || isNaN(microseconds)) { return '—'; }
    var us = Number(microseconds);
    if (us < 1) { return us.toFixed(2) + ' µs'; }
    if (us < 1000) { return (us < 10 ? us.toFixed(2) : us.toFixed(1)) + ' µs'; }
    var ms = us / 1000;
    if (ms < 1000) { return (ms < 10 ? ms.toFixed(2) : ms.toFixed(1)) + ' ms'; }
    return (ms / 1000).toFixed(2) + ' s';
  };

  fmt.count = function (n) {
    return Number(n || 0).toLocaleString();
  };

  /** "System.Collections.Generic.List`1[System.Int32]" -> "List<Int32>". */
  fmt.shortType = function (typeName) {
    if (!typeName) { return ''; }
    var text = String(typeName);
    // Strip assembly-qualified tails before doing anything else.
    text = text.split(', ')[0];
    text = text.replace(/`\d+/g, '');
    text = text.replace(/\[([^\[\]]+)\]/g, function (_, inner) {
      return '<' + inner.split(',').map(function (part) { return fmt.shortType(part.trim()); }).join(', ') + '>';
    });
    return text.replace(/[A-Za-z0-9_]+\./g, '');
  };

  /** Last path segment of a source file, so rows show "OrderService.cs" not the full path. */
  fmt.fileName = function (path) {
    if (!path) { return ''; }
    var parts = String(path).split(/[\\/]/);
    return parts[parts.length - 1] || String(path);
  };

  fmt.sourceLocation = function (invocation) {
    if (!invocation.file) { return ''; }
    var text = fmt.fileName(invocation.file);
    if (invocation.line) {
      text += ':' + invocation.line;
      if (invocation.column) { text += ':' + invocation.column; }
    }
    return text;
  };

  fmt.timestamp = function (iso) {
    if (!iso) { return '—'; }
    var date = new Date(iso);
    if (isNaN(date.getTime())) { return String(iso); }
    return date.toISOString().replace('T', ' ').replace('Z', 'Z');
  };

  /**
   * One-line preview of a captured value, for tree rows and table cells. Non-Success statuses
   * describe themselves ("<redacted>", "<null>") rather than showing a misleading empty value.
   */
  fmt.valuePreview = function (captured, maxLength) {
    var limit = maxLength || 60;
    if (!captured) { return '—'; }

    switch (captured.serializationStatus) {
      case 'Null': return 'null';
      case 'Redacted': return '●●● redacted';
      case 'Unavailable': return '<unavailable>';
      case 'Failed': return '<failed: ' + (captured.errorType ? fmt.shortType(captured.errorType) : 'serialization error') + '>';
      default: break;
    }

    if (captured.objectId && (captured.value === null || captured.value === undefined)) {
      return '<spilled to objects/' + captured.objectId + '.json>';
    }

    var text = fmt.inlineJson(captured.value);
    if (captured.serializationStatus === 'Truncated') { text += ' …(truncated)'; }
    return fmt.ellipsis(text, limit);
  };

  fmt.ellipsis = function (text, limit) {
    var value = String(text === null || text === undefined ? '' : text);
    return value.length > limit ? value.slice(0, limit - 1) + '…' : value;
  };

  /** Compact single-line JSON - objects and arrays collapse to a readable summary. */
  fmt.inlineJson = function (value) {
    if (value === null || value === undefined) { return 'null'; }
    if (typeof value === 'string') { return JSON.stringify(value); }
    if (typeof value === 'number' || typeof value === 'boolean') { return String(value); }
    try {
      return JSON.stringify(value);
    } catch (err) {
      return String(value);
    }
  };

  fmt.prettyJson = function (value) {
    try {
      return JSON.stringify(value, null, 2);
    } catch (err) {
      return String(value);
    }
  };

  /** CSS modifier suffix for a capture status, so styling stays in the stylesheet. */
  fmt.statusClass = function (status) {
    switch (status) {
      case 'Success': return 'ok';
      case 'Null': return 'null';
      case 'Redacted': return 'redacted';
      case 'Truncated': return 'truncated';
      case 'Unavailable': return 'unavailable';
      case 'Failed': return 'failed';
      case 'Partial': return 'truncated';
      default: return 'unknown';
    }
  };

  PFT.format = fmt;
})(window.PFT = window.PFT || {});
