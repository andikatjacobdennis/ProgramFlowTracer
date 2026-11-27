/*
 * Gets trace files off the user's disk and into memory.
 *
 * Everything here works from a plain file:// page - no server, no fetch. The browser will only
 * hand us files the user explicitly picked or dropped, so the three entry points are the file
 * picker, the directory picker, and a drag-and-drop of either.
 *
 * A single drop can contain several runs (people usually drag the whole .flowtrace/runs
 * directory), so files are grouped back into runs by their directory path.
 */
(function (PFT) {
  'use strict';

  var loader = {};

  function pathOf(file) {
    return file.webkitRelativePath || file.__path || file.name;
  }

  function directoryOf(path) {
    var parts = String(path).split('/');
    parts.pop();
    return parts.join('/');
  }

  /**
   * The run a file belongs to. events.jsonl and run.json sit directly in the run directory;
   * spilled values sit one level deeper under objects/.
   */
  function runKeyFor(path) {
    var dir = directoryOf(path);
    return dir.replace(/\/objects$/, '');
  }

  function readAsText(file) {
    return new Promise(function (resolve, reject) {
      var reader = new FileReader();
      reader.onload = function () { resolve(String(reader.result)); };
      reader.onerror = function () { reject(reader.error || new Error('Could not read ' + file.name)); };
      reader.readAsText(file);
    });
  }

  /** Walks a dropped directory entry, which the browser only exposes one level at a time. */
  function readEntry(entry, prefix, collected) {
    return new Promise(function (resolve, reject) {
      if (entry.isFile) {
        entry.file(function (file) {
          file.__path = prefix + file.name;
          collected.push(file);
          resolve();
        }, reject);
        return;
      }

      if (!entry.isDirectory) { resolve(); return; }

      var reader = entry.createReader();
      var children = [];

      // readEntries() returns at most ~100 entries per call and signals "done" with an empty
      // batch, so it has to be drained in a loop.
      var readBatch = function () {
        reader.readEntries(function (batch) {
          if (!batch.length) {
            Promise.all(children.map(function (child) {
              return readEntry(child, prefix + entry.name + '/', collected);
            })).then(resolve, reject);
            return;
          }
          children = children.concat(Array.prototype.slice.call(batch));
          readBatch();
        }, reject);
      };

      readBatch();
    });
  }

  /** Flattens a DataTransfer from a drop event into a flat list of files with paths. */
  loader.filesFromDataTransfer = function (dataTransfer) {
    var items = dataTransfer.items;
    var collected = [];

    if (!items || !items.length || typeof items[0].webkitGetAsEntry !== 'function') {
      return Promise.resolve(Array.prototype.slice.call(dataTransfer.files));
    }

    var entries = [];
    for (var i = 0; i < items.length; i++) {
      var entry = items[i].webkitGetAsEntry();
      if (entry) { entries.push(entry); }
    }

    if (!entries.length) {
      return Promise.resolve(Array.prototype.slice.call(dataTransfer.files));
    }

    return Promise.all(entries.map(function (entry) {
      return readEntry(entry, '', collected);
    })).then(function () { return collected; });
  };

  /**
   * Reads a flat list of files into one or more runs. Files that are not part of a trace are
   * ignored rather than treated as an error - dropping a whole project directory should work.
   */
  loader.loadFiles = function (files) {
    var relevant = [];

    files.forEach(function (file) {
      var name = file.name.toLowerCase();
      var path = pathOf(file);
      var isObject = /(^|\/)objects\/[^/]+\.json$/i.test(path);
      if (name === 'events.jsonl' || name === 'run.json' || isObject) {
        relevant.push({ file: file, path: path, name: name, isObject: isObject });
      }
    });

    if (!relevant.some(function (item) { return item.name === 'events.jsonl'; })) {
      return Promise.reject(new Error(
        'No events.jsonl found. Open a run directory such as ' +
        '<app>.instrumented/.flowtrace/runs/<run-guid>/.'
      ));
    }

    return Promise.all(relevant.map(function (item) {
      return readAsText(item.file).then(function (text) {
        item.text = text;
        return item;
      });
    })).then(function (loaded) {
      return loader.assembleRuns(loaded);
    });
  };

  /** Groups already-read files into runs and builds a trace model for each. */
  loader.assembleRuns = function (loaded) {
    var groups = Object.create(null);

    loaded.forEach(function (item) {
      var key = runKeyFor(item.path);
      var group = groups[key] || (groups[key] = { key: key, eventsText: null, metadata: null, objects: {}, parseErrors: [] });

      if (item.name === 'events.jsonl') {
        group.eventsText = item.text;
      } else if (item.name === 'run.json') {
        try { group.metadata = JSON.parse(item.text); } catch (err) { /* metadata is optional */ }
      } else if (item.isObject) {
        try {
          var record = JSON.parse(item.text);
          if (record && record.objectId) { group.objects[record.objectId] = record; }
        } catch (err) { /* one unreadable spilled value must not sink the run */ }
      }
    });

    var runs = [];
    Object.keys(groups).forEach(function (key) {
      var group = groups[key];
      if (!group.eventsText) { return; }

      var parsed = PFT.model.parseEvents(group.eventsText);
      var trace = PFT.model.build(parsed.events, group.metadata, group.objects);
      trace.parseErrors = parsed.errors;
      trace.sourceLabel = loader.labelFor(key, trace);
      runs.push(trace);
    });

    runs.sort(function (a, b) {
      var left = (a.metadata && a.metadata.startedAtUtc) || '';
      var right = (b.metadata && b.metadata.startedAtUtc) || '';
      return left < right ? -1 : (left > right ? 1 : 0);
    });

    return runs;
  };

  loader.labelFor = function (key, trace) {
    var meta = trace.metadata;
    var name = (meta && meta.application) || '';
    var runId = (meta && meta.runId) || (trace.events[0] && trace.events[0].runId) || key || 'run';
    var shortId = String(runId).slice(0, 8);
    var started = meta && meta.startedAtUtc ? new Date(meta.startedAtUtc) : null;
    var when = started && !isNaN(started.getTime()) ? started.toLocaleString() : '';
    return [name || 'run', shortId, when].filter(Boolean).join(' · ');
  };

  PFT.loader = loader;
})(window.PFT = window.PFT || {});
