// vertex-ui.js
window.vertex = {
    getPinnedAllowed: function () {
        return localStorage.getItem("pin_allowed") === "1";
    },

    setPinnedAllowed: function (value) {
        localStorage.setItem("pin_allowed", value ? "1" : "0");
    }
};

// ── vertexUi.makeDraggable ─────────────────────────────────────────
// Makes any absolutely-positioned element draggable by a handle.
//   elementId  — the element to move
//   handleId   — the element to grab (drag initiator)
// Position is persisted in sessionStorage so panels remember their spot.
(function(){
  const _drags = {};

  if (!window.vertexUi) window.vertexUi = {};

  window.vertexUi.makeDraggable = function(elementId, handleId) {
    const el     = document.getElementById(elementId);
    const handle = document.getElementById(handleId);
    if (!el || !handle) return;

    // Restore saved position
    const saved = sessionStorage.getItem('vx_drag_' + elementId);
    if (saved) {
      try {
        const { left, top } = JSON.parse(saved);
        el.style.left = left;
        el.style.top  = top;
        el.style.right  = 'auto';
        el.style.bottom = 'auto';
      } catch(e) {}
    }

    if (_drags[elementId]) return; // already registered
    _drags[elementId] = true;

    let startX, startY, startLeft, startTop;

    function onDown(e) {
      // Prevent click events on the handle from bubbling into chart interactions
      e.stopPropagation();
      const isTouch = e.type === 'touchstart';
      const pt = isTouch ? e.touches[0] : e;

      startX = pt.clientX;
      startY = pt.clientY;

      const rect  = el.getBoundingClientRect();
      const pRect = (el.offsetParent || document.body).getBoundingClientRect();
      startLeft = rect.left - pRect.left;
      startTop  = rect.top  - pRect.top;

      el.style.transition = 'none';
      handle.style.cursor = 'grabbing';

      document.addEventListener(isTouch ? 'touchmove' : 'mousemove', onMove, { passive: false });
      document.addEventListener(isTouch ? 'touchend'  : 'mouseup',   onUp);
    }

    function onMove(e) {
      e.preventDefault();
      const isTouch = e.type === 'touchmove';
      const pt = isTouch ? e.touches[0] : e;

      const dx = pt.clientX - startX;
      const dy = pt.clientY - startY;

      const pRect  = (el.offsetParent || document.body).getBoundingClientRect();
      const maxLeft = pRect.width  - el.offsetWidth;
      const maxTop  = pRect.height - el.offsetHeight;

      const newLeft = Math.max(0, Math.min(startLeft + dx, maxLeft));
      const newTop  = Math.max(0, Math.min(startTop  + dy, maxTop));

      el.style.left   = newLeft + 'px';
      el.style.top    = newTop  + 'px';
      el.style.right  = 'auto';
      el.style.bottom = 'auto';
    }

    function onUp(e) {
      const isTouch = e.type === 'touchend';
      handle.style.cursor = 'grab';
      el.style.transition = '';

      // Persist position
      sessionStorage.setItem('vx_drag_' + elementId, JSON.stringify({
        left: el.style.left,
        top:  el.style.top
      }));

      document.removeEventListener(isTouch ? 'touchmove' : 'mousemove', onMove);
      document.removeEventListener(isTouch ? 'touchend'  : 'mouseup',   onUp);
    }

    handle.addEventListener('mousedown',  onDown);
    handle.addEventListener('touchstart', onDown, { passive: false });
  };
})();

