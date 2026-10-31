/*
 * Opening a trace by folder *location* rather than by a packaged copy of it.
 *
 * The plain <input webkitdirectory> picker hands over a one-shot snapshot of files: to see a
 * newer run, or events appended since, you have to pick the folder all over again. The File
 * System Access API instead hands over a *handle* to the directory, which can be:
 *
 *   - re-read on demand, so "Reload" picks up new runs and appended events;
 *   - stored in IndexedDB, so the viewer can offer to reopen the same location next time.
 *
 * Handles are stored, not paths - the browser deliberately never exposes the absolute path, and
 * re-granting read permission needs a user gesture. Everything here degrades to the classic
 * picker when the API is missing (Firefox, Safari), which app.js falls back to.
 */
(function (PFT) {
  'use strict';

  var DB_NAME = 'programflowtracer-viewer';
  var STORE = 'handles';
  var LAST_KEY = 'lastRunDirectory';

  // A trace directory holds a handful of files. If a user picks their whole source tree by
  // mistake we want to give up quickly instead of walking a node_modules.
  var MAX_DEPTH = 5;
  var MAX_ENTRIES = 20000;
  var SKIP_DIRECTORIES = { bin: 1, obj: 1, '.git': 1, '.vs': 1, node_modules: 1, packages: 1 };

  var fsaccess = {};

  fsaccess.isSupported = function () {
    return typeof window.showDirectoryPicker === 'function';
  };

  fsaccess.pick = function () {
    return window.showDirectoryPicker({ id: 'pft-run', mode: 'read', startIn: 'documents' });
  };

  /* ------------------------------------------------------------- permission */

  /**
   * Permission for a restored handle is not automatic. queryPermission tells us whether we still
   * have it; requestPermission re-prompts, and only works inside a user gesture - which is why
   * "Reopen" is a button rather than something that happens on load.
   */
  fsaccess.ensureReadable = async function (handle) {
    if (!handle || typeof handle.queryPermission !== 'function') { return true; }

    var options = { mode: 'read' };
    if ((await handle.queryPermission(options)) === 'granted') { return true; }
    return (await handle.requestPermission(options)) === 'granted';
  };

  /* ------------------------------------------------------------------ reading */

  function isTraceFile(name) {
    var lower = name.toLowerCase();
    return lower === 'events.jsonl' || lower === 'run.json' || lower.slice(-5) === '.json';
  }

  /**
   * Walks the picked directory and returns loader-shaped items for every trace file found.
   *
   * The picked folder can legitimately be any level of the layout - a single run directory, the
   * "runs" directory holding many of them, or the whole ".flowtrace" folder - so this just walks
   * down and lets loader.assembleRuns() group whatever turns up by directory.
   */
  fsaccess.read = async function (rootHandle) {
    var items = [];
    var scanned = 0;
    var truncated = false;

    async function walk(handle, prefix, depth) {
      if (depth > MAX_DEPTH || truncated) { return; }

      for await (var entry of handle.values()) {
        if (scanned >= MAX_ENTRIES) { truncated = true; return; }
        scanned++;

        if (entry.kind === 'directory') {
          if (SKIP_DIRECTORIES[entry.name.toLowerCase()]) { continue; }
          await walk(entry, prefix + entry.name + '/', depth + 1);
          continue;
        }

        if (!isTraceFile(entry.name)) { continue; }

        var path = prefix + entry.name;
        var isObject = /(^|\/)objects\/[^/]+\.json$/i.test(path);
        var lower = entry.name.toLowerCase();
        if (lower !== 'events.jsonl' && lower !== 'run.json' && !isObject) { continue; }

        var file = await entry.getFile();
        items.push({
          path: path,
          name: lower,
          isObject: isObject,
          text: await file.text()
        });
      }
    }

    await walk(rootHandle, rootHandle.name + '/', 0);
    return { items: items, truncated: truncated };
  };

  /* ------------------------------------------------------- remembering it */

  function openDb() {
    return new Promise(function (resolve, reject) {
      if (!window.indexedDB) { reject(new Error('IndexedDB unavailable')); return; }

      var request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = function () {
        if (!request.result.objectStoreNames.contains(STORE)) {
          request.result.createObjectStore(STORE);
        }
      };
      request.onsuccess = function () { resolve(request.result); };
      request.onerror = function () { reject(request.error); };
    });
  }

  function withStore(mode, action) {
    return openDb().then(function (db) {
      return new Promise(function (resolve, reject) {
        var tx = db.transaction(STORE, mode);
        var request = action(tx.objectStore(STORE));
        request.onsuccess = function () { resolve(request.result); };
        request.onerror = function () { reject(request.error); };
        tx.oncomplete = function () { db.close(); };
      });
    });
  }

  /** Best-effort: never let a storage failure stop a trace from opening. */
  fsaccess.remember = function (handle) {
    return withStore('readwrite', function (store) {
      return store.put(handle, LAST_KEY);
    }).catch(function () { return null; });
  };

  fsaccess.recall = function () {
    if (!fsaccess.isSupported()) { return Promise.resolve(null); }
    return withStore('readonly', function (store) {
      return store.get(LAST_KEY);
    }).catch(function () { return null; });
  };

  fsaccess.forget = function () {
    return withStore('readwrite', function (store) {
      return store.delete(LAST_KEY);
    }).catch(function () { return null; });
  };

  /**
   * A dropped folder can also carry a handle, so drag-and-drop gets the same reload/remember
   * behaviour as the picker instead of being a second-class path.
   */
  fsaccess.handleFromDrop = function (dataTransfer) {
    if (!fsaccess.isSupported() || !dataTransfer.items || !dataTransfer.items.length) {
      return Promise.resolve(null);
    }

    var item = dataTransfer.items[0];
    if (typeof item.getAsFileSystemHandle !== 'function') { return Promise.resolve(null); }

    return item.getAsFileSystemHandle().then(function (handle) {
      return handle && handle.kind === 'directory' ? handle : null;
    }, function () { return null; });
  };

  PFT.fsaccess = fsaccess;
})(window.PFT = window.PFT || {});
