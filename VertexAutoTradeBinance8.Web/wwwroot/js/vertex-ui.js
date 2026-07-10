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

  window.vertexUi.makeDraggable = function(elementId, handleId, _retries) {
    const el     = document.getElementById(elementId);
    const handle = document.getElementById(handleId);
    if (!el || !handle) {
      // Blazor DOM may not be ready yet — retry up to 10 times × 100ms
      const retries = (_retries || 0);
      if (retries < 10) {
        setTimeout(() => window.vertexUi.makeDraggable(elementId, handleId, retries + 1), 100);
      }
      return;
    }

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

// ── vertexUi.makeCdtToolbarDraggable ──────────────────────────────
// Makes the toolbar injected by chart-drawing-tools.js draggable.
// CDT appends a div (the toolbar) as the LAST child of chartContainer.
// We find it, add a drag handle (grip dots) at top, and make it movable.
window.vertexUi.makeCdtToolbarDraggable = function(containerId, _retries) {
  const container = document.getElementById(containerId);
  if (!container) {
    if ((_retries || 0) < 15) setTimeout(() => window.vertexUi.makeCdtToolbarDraggable(containerId, (_retries||0)+1), 150);
    return;
  }

  // CDT toolbar is a positioned div appended to the container
  // It has position:absolute top:10px left:10px set inline
  const bar = Array.from(container.children).find(el =>
    el.tagName === 'DIV' &&
    el.style.position === 'absolute' &&
    el.style.flexDirection === 'column'
  );

  if (!bar) {
    if ((_retries || 0) < 15) setTimeout(() => window.vertexUi.makeCdtToolbarDraggable(containerId, (_retries||0)+1), 150);
    return;
  }

  // Already made draggable
  if (bar._vxDrag) return;
  bar._vxDrag = true;

  // Add grip handle at top
  const grip = document.createElement('div');
  grip.style.cssText = [
    'display:flex','flex-direction:column','justify-content:center',
    'align-items:center','gap:3px','height:18px','cursor:grab',
    'opacity:0.35','transition:opacity 0.15s','margin:0 auto 2px',
    'padding:2px 4px','border-bottom:1px solid rgba(30,45,71,0.6)',
    'width:100%',
  ].join(';');
  for (let i = 0; i < 3; i++) {
    const dot = document.createElement('span');
    dot.style.cssText = 'display:block;width:16px;height:1.5px;background:#94a3b8;border-radius:1px;';
    grip.appendChild(dot);
  }
  grip.addEventListener('mouseenter', () => grip.style.opacity = '0.9');
  grip.addEventListener('mouseleave', () => grip.style.opacity = '0.35');

  bar.insertBefore(grip, bar.firstChild);
  bar.style.userSelect  = 'none';
  bar.style.touchAction = 'none';

  // Restore saved position
  const storageKey = 'vx_cdt_toolbar_' + containerId;
  const saved = sessionStorage.getItem(storageKey);
  if (saved) {
    try {
      const { left, top } = JSON.parse(saved);
      bar.style.left = left; bar.style.top = top;
    } catch(e) {}
  }

  let startX, startY, startLeft, startTop;

  grip.addEventListener('mousedown', onDown);
  grip.addEventListener('touchstart', onDown, { passive: false });

  function onDown(e) {
    e.stopPropagation(); e.preventDefault();
    grip.style.cursor = 'grabbing';
    const pt = e.touches ? e.touches[0] : e;
    startX    = pt.clientX;
    startY    = pt.clientY;
    startLeft = parseInt(bar.style.left) || 10;
    startTop  = parseInt(bar.style.top)  || 10;
    bar.style.transition = 'none';
    document.addEventListener('mousemove', onMove, { passive: false });
    document.addEventListener('mouseup',   onUp);
    document.addEventListener('touchmove', onMove, { passive: false });
    document.addEventListener('touchend',  onUp);
  }

  function onMove(e) {
    e.preventDefault();
    const pt = e.touches ? e.touches[0] : e;
    const dx = pt.clientX - startX;
    const dy = pt.clientY - startY;
    const pRect = container.getBoundingClientRect();
    const newLeft = Math.max(0, Math.min(startLeft + dx, pRect.width  - bar.offsetWidth));
    const newTop  = Math.max(0, Math.min(startTop  + dy, pRect.height - bar.offsetHeight));
    bar.style.left = newLeft + 'px';
    bar.style.top  = newTop  + 'px';
  }

  function onUp() {
    grip.style.cursor = 'grab';
    bar.style.transition = '';
    sessionStorage.setItem(storageKey, JSON.stringify({
      left: bar.style.left, top: bar.style.top
    }));
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup',   onUp);
    document.removeEventListener('touchmove', onMove);
    document.removeEventListener('touchend',  onUp);
  }
};
