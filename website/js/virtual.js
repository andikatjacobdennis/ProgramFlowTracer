/*
 * A minimal virtual list: render only the rows that are on screen.
 *
 * The viewer's row lists are the one place where cost scales with the size of the trace rather
 * than with the size of the screen. A 50,000-event run puts ~25,000 calls in the tree; rendering
 * even a capped 4,000 of them cost ~68 ms of DOM work on every keystroke, and the raw-event table
 * put 82,000 nodes on the page. Neither number has anything to do with what a person can actually
 * see, which is a few dozen rows.
 *
 * This keeps a spacer of the full height so the scrollbar stays honest, and moves a small window
 * of real rows around inside it. Rows must be a fixed, known height - which they are, from the
 * --row-height custom property.
 */
(function (PFT) {
  'use strict';

  var OVERSCAN = 12;

  /**
   * @param scroller  the element that scrolls (must be position:relative or static)
   * @param options   { rowHeight, renderRange(start, end) -> html, onRendered? }
   */
  function VirtualList(scroller, options) {
    this.scroller = scroller;
    this.rowHeight = options.rowHeight;
    this.renderRange = options.renderRange;
    this.onRendered = options.onRendered || null;
    this.total = 0;
    this.start = -1;
    this.end = -1;

    this.spacer = document.createElement('div');
    this.spacer.className = 'vlist';
    this.window = document.createElement('div');
    this.window.className = 'vlist-window';
    this.spacer.appendChild(this.window);

    var self = this;
    this.onScroll = function () { self.update(false); };
    // Passive: this listener never calls preventDefault, and saying so keeps scrolling off the
    // main thread's critical path.
    scroller.addEventListener('scroll', this.onScroll, { passive: true });
  }

  VirtualList.prototype.mount = function () {
    if (this.spacer.parentNode !== this.scroller) {
      this.scroller.innerHTML = '';
      this.scroller.appendChild(this.spacer);
    }
  };

  VirtualList.prototype.setTotal = function (total) {
    this.total = total;
    this.spacer.style.height = (total * this.rowHeight) + 'px';
    this.start = -1;                 // force the next update to redraw
    this.update(true);
  };

  VirtualList.prototype.update = function (force) {
    var viewport = this.scroller.clientHeight || 400;
    var first = Math.max(0, Math.floor(this.scroller.scrollTop / this.rowHeight) - OVERSCAN);
    var visible = Math.ceil(viewport / this.rowHeight) + OVERSCAN * 2;
    var last = Math.min(this.total, first + visible);

    // Scrolling within the overscan margin needs no new DOM at all.
    if (!force && first === this.start && last === this.end) { return; }

    this.start = first;
    this.end = last;
    this.window.style.transform = 'translateY(' + (first * this.rowHeight) + 'px)';
    this.window.innerHTML = this.total ? this.renderRange(first, last) : '';

    if (this.onRendered) { this.onRendered(first, last); }
  };

  /** Brings a row index into view, then redraws the window around it. */
  VirtualList.prototype.scrollToIndex = function (index) {
    if (index < 0 || index >= this.total) { return; }

    var top = index * this.rowHeight;
    var viewport = this.scroller.clientHeight || 400;

    if (top < this.scroller.scrollTop || top + this.rowHeight > this.scroller.scrollTop + viewport) {
      this.scroller.scrollTop = Math.max(0, top - viewport / 3);
    }

    this.update(true);
  };

  VirtualList.prototype.destroy = function () {
    this.scroller.removeEventListener('scroll', this.onScroll);
    if (this.spacer.parentNode) { this.spacer.parentNode.removeChild(this.spacer); }
  };

  PFT.VirtualList = VirtualList;

  /** Reads --row-height once so the JS and the stylesheet cannot drift apart. */
  PFT.rowHeightPx = function () {
    var value = parseInt(
      getComputedStyle(document.documentElement).getPropertyValue('--row-height'), 10);
    return value || 26;
  };
})(window.PFT = window.PFT || {});
