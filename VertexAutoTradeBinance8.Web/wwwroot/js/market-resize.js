// Corner drag for the market chart. The handle lives in the chart wrap.
// Height is an inline style so it wins over the stylesheet, and it is
// restored after maximize is turned off.
window.marketResize = (() => {
    const MIN_H = 220;
    let wrap = null;
    let dragging = false;
    let startY = 0;
    let startH = 0;

    function maxH() {
        return Math.max(680, Math.round(window.innerHeight * 0.92));
    }

    function chartId() {
        const node = wrap && wrap.querySelector('[id^="chartContainer-"]');
        return node ? node.id : '';
    }

    function paint(h) {
        if (!wrap || wrap.classList.contains('mk-chart-maximized')) return;
        const next = Math.max(MIN_H, Math.min(maxH(), Math.round(h)));
        wrap.style.flex = '0 0 auto';
        wrap.style.height = next + 'px';
        const id = chartId();
        if (id && window.marketChart && typeof window.marketChart.resize === 'function') {
            window.marketChart.resize(id);
        }
    }

    function onMove(e) {
        if (!dragging) return;
        const y = e.touches ? e.touches[0].clientY : e.clientY;
        paint(startH + (y - startY));
    }

    function onUp() {
        if (!dragging) return;
        dragging = false;
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        document.removeEventListener('touchmove', onMove);
        document.removeEventListener('touchend', onUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
        if (wrap && !wrap.classList.contains('mk-chart-maximized')) {
            localStorage.setItem('vtx_chart_h', String(wrap.offsetHeight));
        }
        const id = chartId();
        if (id && window.marketChart) window.marketChart.resize(id);
    }

    function onDown(e) {
        if (!wrap || wrap.classList.contains('mk-chart-maximized')) return;
        e.preventDefault();
        e.stopPropagation();
        dragging = true;
        startY = e.touches ? e.touches[0].clientY : e.clientY;
        startH = wrap.offsetHeight;
        document.body.style.cursor = 'nwse-resize';
        document.body.style.userSelect = 'none';
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
        document.addEventListener('touchmove', onMove, { passive: false });
        document.addEventListener('touchend', onUp);
    }

    function bindHandle(handle) {
        if (!handle || handle.dataset.bound === '1') return;
        handle.dataset.bound = '1';
        handle.addEventListener('mousedown', onDown);
        handle.addEventListener('touchstart', onDown, { passive: false });
    }

    function sync() {
        wrap = document.querySelector('.mk-chart-container-wrap');
        if (!wrap) return;
        bindHandle(wrap.querySelector('.mk-chart-grip'));
        if (wrap.classList.contains('mk-chart-maximized')) {
            wrap.dataset.sized = '';
            return;
        }
        if (dragging || wrap.dataset.sized === '1') return;
        const saved = parseInt(localStorage.getItem('vtx_chart_h') || '', 10);
        paint(Number.isFinite(saved) && saved > 0 ? saved : (wrap.offsetHeight || 560));
        wrap.dataset.sized = '1';
    }

    return { init: sync, sync: sync };
})();
