// mini-chart.js — LightweightCharts v5 mini chart for position modal
// Called from MarketMiniChart.razor via JS interop.
// Each chart lives in a Map keyed by containerId.

'use strict';

(function () {

    const _sessions = new Map(); // containerId → { chart, series }

    // ── miniChartInit ───────────────────────────────────────────────────
    // Creates (or replaces) a candlestick chart inside the given container.
    // bars: [{time (unix seconds), open, high, low, close}]
    window.miniChartInit = function (containerId, bars) {
        // Destroy any existing chart in this container first
        miniChartDestroy(containerId);

        const container = document.getElementById(containerId);
        if (!container) {
            console.warn('[MiniChart] container not found:', containerId);
            return;
        }

        // LightweightCharts v5 API
        const LWC = window.LightweightCharts;
        if (!LWC) {
            console.error('[MiniChart] LightweightCharts not loaded');
            return;
        }

        const chart = LWC.createChart(container, {
            width:  container.clientWidth  || 680,
            height: container.clientHeight || 260,
            layout: {
                background: { color: '#07090f' },
                textColor:  '#2a3a50',
                fontSize:   10,
            },
            grid: {
                vertLines: { color: '#0d1520', style: 1 },
                horzLines: { color: '#0d1520', style: 1 },
            },
            crosshair: {
                mode: LWC.CrosshairMode ? LWC.CrosshairMode.Normal : 1,
                vertLine: { color: '#1e2d47', width: 1, style: 2 },
                horzLine: { color: '#1e2d47', width: 1, style: 2 },
            },
            rightPriceScale: {
                borderColor: '#0d1520',
                textColor:   '#2a3a50',
                scaleMargins: { top: 0.08, bottom: 0.08 },
            },
            timeScale: {
                borderColor:     '#0d1520',
                timeVisible:     true,
                secondsVisible:  false,
                fixLeftEdge:     false,
                fixRightEdge:    false,
            },
            handleScroll:  true,
            handleScale:   true,
        });

        const series = chart.addCandlestickSeries({
            upColor:        '#00d68f',
            downColor:      '#ff4d6a',
            borderUpColor:  '#00d68f',
            borderDownColor:'#ff4d6a',
            wickUpColor:    '#00d68f',
            wickDownColor:  '#ff4d6a',
        });

        if (bars && bars.length > 0) {
            // Sort by time ascending (required by LW Charts)
            const sorted = [...bars].sort((a, b) => a.time - b.time);
            series.setData(sorted);
            chart.timeScale().fitContent();
        }

        // Auto-resize when container size changes
        const ro = new ResizeObserver(entries => {
            for (const entry of entries) {
                const { width, height } = entry.contentRect;
                if (width > 0 && height > 0) {
                    chart.applyOptions({ width, height });
                }
            }
        });
        ro.observe(container);

        _sessions.set(containerId, { chart, series, ro });
        console.log('[MiniChart] init OK:', containerId, bars?.length, 'bars');
    };

    // ── miniChartDestroy ────────────────────────────────────────────────
    window.miniChartDestroy = function (containerId) {
        const s = _sessions.get(containerId);
        if (!s) return;
        try { s.ro?.disconnect(); }    catch (_) {}
        try { s.chart?.remove(); }     catch (_) {}
        _sessions.delete(containerId);
        console.log('[MiniChart] destroyed:', containerId);
    };

})();
