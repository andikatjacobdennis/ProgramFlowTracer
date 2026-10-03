/*
 * Copying text to the clipboard, from every context this viewer runs in.
 *
 * The async Clipboard API is unavailable on file:// pages and can be denied by permission policy
 * even where it exists, so a rejection is not the end of the road - the legacy execCommand path
 * still works in several places the modern one does not. Callers get one promise either way.
 */
(function (PFT) {
  'use strict';

  function viaTextarea(text) {
    var area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();

    var ok = false;
    try { ok = document.execCommand('copy'); } catch (err) { ok = false; }
    document.body.removeChild(area);

    return ok ? Promise.resolve() : Promise.reject(new Error('blocked'));
  }

  var clipboard = {};

  clipboard.write = function (text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).catch(function () { return viaTextarea(text); });
    }
    return viaTextarea(text);
  };

  /**
   * Runs a copy and reports the outcome on the button that triggered it, restoring its label
   * afterwards. Buttons are the only feedback surface these actions have.
   */
  clipboard.writeFromButton = function (button, text, restoreTo) {
    var original = restoreTo || button.textContent;

    return clipboard.write(text).then(function () {
      button.textContent = 'Copied';
      setTimeout(function () { button.textContent = original; }, 1600);
    }, function () {
      // Say what to do instead, rather than surfacing the browser's raw exception text.
      button.textContent = 'Blocked — select and copy';
      setTimeout(function () { button.textContent = original; }, 3200);
    });
  };

  PFT.clipboard = clipboard;
})(window.PFT = window.PFT || {});
