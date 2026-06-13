// ============================================================
// VERTEX MARKET RESIZE
// Drag-to-resize chart height + mouse wheel zoom
// ============================================================

window.marketResize = (() => {

    let _wrap = null, _handle = null;
    let _dragging = false;
    let _startY = 0, _startH = 0;
    const MIN_H = 150, MAX_H = 800;

    function init(wrapId, handleId) {
        _wrap   = document.getElementById(wrapId);
        _handle = document.getElementById(handleId);
        if (!_wrap || !_handle) return;

        // ── DRAG RESIZE ───────────────────────────────────
        _handle.addEventListener('mousedown', onMouseDown);
        _handle.addEventListener('touchstart', onTouchStart, { passive: false });

        // ── MOUSE WHEEL ZOOM ──────────────────────────────
        _wrap.addEventListener('wheel', onWheel, { passive: false });

        // Restore saved height
        const saved = localStorage.getItem('vtx_chart_h');
        if (saved) setHeight(parseInt(saved));
    }

    function setHeight(h) {
        const clamped = Math.max(MIN_H, Math.min(MAX_H, h));
        _wrap.style.height = clamped + 'px';
        // Trigger chart redraw after resize
        window.dispatchEvent(new Event('resize'));
        setTimeout(() => window.dispatchEvent(new Event('resize')), 50);
    }

    // ── MOUSE DOWN ────────────────────────────────────────
    function onMouseDown(e) {
        e.preventDefault();
        _dragging = true;
        _startY = e.clientY;
        _startH = _wrap.offsetHeight;

        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        document.body.style.cursor = 'ns-resize';
        document.body.style.userSelect = 'none';
    }

    function onMouseMove(e) {
        if (!_dragging) return;
        const delta = e.clientY - _startY;
        setHeight(_startH + delta);
    }

    function onMouseUp() {
        _dragging = false;
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
        // Save height
        if (_wrap) localStorage.setItem('vtx_chart_h', _wrap.offsetHeight);
        // Final redraw
        setTimeout(() => window.dispatchEvent(new Event('resize')), 100);
    }

    // ── TOUCH ─────────────────────────────────────────────
    function onTouchStart(e) {
        e.preventDefault();
        _dragging = true;
        _startY = e.touches[0].clientY;
        _startH = _wrap.offsetHeight;
        document.addEventListener('touchmove', onTouchMove, { passive: false });
        document.addEventListener('touchend', onTouchEnd);
    }

    function onTouchMove(e) {
        if (!_dragging) return;
        e.preventDefault();
        const delta = e.touches[0].clientY - _startY;
        setHeight(_startH + delta);
    }

    function onTouchEnd() {
        _dragging = false;
        document.removeEventListener('touchmove', onTouchMove);
        document.removeEventListener('touchend', onTouchEnd);
        if (_wrap) localStorage.setItem('vtx_chart_h', _wrap.offsetHeight);
    }

    // ── MOUSE WHEEL ZOOM ──────────────────────────────────
    function onWheel(e) {
        if (!e.ctrlKey && !e.metaKey) return; // only when Ctrl held
        e.preventDefault();
        const delta = e.deltaY > 0 ? -30 : 30;
        const cur = _wrap ? _wrap.offsetHeight : 300;
        setHeight(cur + delta);
        if (_wrap) localStorage.setItem('vtx_chart_h', _wrap.offsetHeight);
    }

    return { init };
})();
