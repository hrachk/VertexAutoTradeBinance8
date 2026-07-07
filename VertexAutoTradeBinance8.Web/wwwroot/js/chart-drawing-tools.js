// ============================================================
// VERTEX CHART — DRAWING TOOLS
// chart-drawing-tools.js  (companion to market-chart.js)
// ============================================================
// Professional drawing tools integrated with the existing
// LightweightCharts session system from market-chart.js.
//
// Tools:
//   H  — Horizontal line (support/resistance)
//   T  — Trend line (two-point)
//   R  — Rectangle (range / demand-supply zone)
//   F  — Fibonacci retracement
//   X  — Delete last drawing (Esc also works)
//   Esc — Cancel active drawing
//
// All drawings are stored per-session in session.drawings[].
// They render as SVG overlays positioned absolutely over the
// chart container, updated on every scroll/zoom via
// subscribeVisibleLogicalRangeChange + subscribeCrosshairMove.
// Drawings persist in localStorage keyed by symbol+timeframe.
//
// Load this file AFTER market-chart.js and BEFORE chart init:
//   <script src="~/js/market-chart.js"></script>
//   <script src="~/js/chart-drawing-tools.js"></script>
//
// Usage from Blazor (JS interop):
//   await JS.InvokeVoidAsync("chartDrawingTools.setActiveTool", containerId, "H");
//   await JS.InvokeVoidAsync("chartDrawingTools.clearAll", containerId);
//   await JS.InvokeVoidAsync("chartDrawingTools.saveDrawings", containerId, storageKey);
//   await JS.InvokeVoidAsync("chartDrawingTools.loadDrawings", containerId, storageKey);
//
// The toolbar HTML is injected by injectToolbar(containerId) —
// call it once after marketChart.init(containerId).
// ============================================================

'use strict';

(function () {

    // ── Constants ──────────────────────────────────────────────────────
    const TOOL_NONE   = null;
    const TOOL_HLINE  = 'H';   // horizontal line
    const TOOL_TLINE  = 'T';   // trend line
    const TOOL_RECT   = 'R';   // rectangle / zone
    const TOOL_FIB    = 'F';   // fibonacci retracement

    // Color palette — matches the chart's dark theme
    const COLORS = {
        hline:   '#94a3b8',   // neutral gray — support/resistance
        tline:   '#f59e0b',   // amber — trend lines
        rect:    'rgba(59,130,246,0.12)',  // blue zone fill
        rectBorder: '#3b82f6',
        fib:     '#a855f7',   // purple — fibonacci
        fibZone: 'rgba(168,85,247,0.06)',
        preview: 'rgba(148,163,184,0.45)',
        labelBg: 'rgba(10,13,18,0.85)',
        labelFg: '#e2e8f0',
    };

    // Fibonacci ratios — standard professional set
    const FIB_LEVELS = [
        { r: 0,     label: '0%',     color: '#ef4444' },
        { r: 0.236, label: '23.6%',  color: '#f97316' },
        { r: 0.382, label: '38.2%',  color: '#eab308' },
        { r: 0.5,   label: '50%',    color: '#a855f7' },
        { r: 0.618, label: '61.8%',  color: '#3b82f6' },
        { r: 0.786, label: '78.6%',  color: '#22c55e' },
        { r: 1,     label: '100%',   color: '#ef4444' },
    ];

    // ── Per-session drawing state ──────────────────────────────────────
    // Each session gets:
    //   .drawings       — committed Drawing[] objects
    //   .activeTool     — currently selected tool string or null
    //   .drawState      — in-progress click state
    //   .svgOverlay     — <svg> element over the chart
    //   .toolbarEl      — toolbar DOM element
    //   .drawingsSub    — range-change subscription fn
    //   .drawingsChSub  — crosshair-move subscription fn
    const drawingSessions = new Map();   // containerId → drawing session

    function getDS(containerId) {
        if (!drawingSessions.has(containerId)) {
            drawingSessions.set(containerId, {
                drawings: [],
                activeTool: TOOL_NONE,
                drawState: null,
                svgOverlay: null,
                toolbarEl: null,
                drawingsSub: null,
                drawingsChSub: null,
            });
        }
        return drawingSessions.get(containerId);
    }

    // ── Price ↔ pixel helpers ──────────────────────────────────────────
    // We borrow the candleSeries from the existing marketChart session
    // (same containerId key) — priceToCoordinate / coordinateToPrice
    // are the same functions used by the pill system.
    function getChartSession(containerId) {
        // marketChart stores sessions in a module-private Map exposed
        // indirectly — we reach it via the global marketChart object
        // which already has all helpers. We only need priceToCoordinate
        // and timeToCoordinate which are accessible via the series/chart
        // references. The cleanest approach: expose a thin accessor on
        // the marketChart namespace (added at the bottom of this file).
        return window._vertexChartSessions && window._vertexChartSessions.get(containerId);
    }

    function priceToY(cs, containerId, price) {
        const cs2 = cs || (getChartSession(containerId) || {}).candleSeries;
        if (!cs2) return null;
        return cs2.priceToCoordinate(price);
    }

    function yToPrice(cs, containerId, y) {
        const cs2 = cs || (getChartSession(containerId) || {}).candleSeries;
        if (!cs2) return null;
        return cs2.coordinateToPrice(y);
    }

    function timeToX(chart, ts) {
        try { return chart.timeScale().timeToCoordinate(ts); } catch (e) { return null; }
    }

    function xToTime(chart, x) {
        try { return chart.timeScale().coordinateToTime(x); } catch (e) { return null; }
    }

    // ── SVG overlay management ─────────────────────────────────────────
    function ensureSvgOverlay(containerId) {
        const ds = getDS(containerId);
        if (ds.svgOverlay) return ds.svgOverlay;

        const container = document.getElementById(containerId);
        if (!container) return null;
        if (getComputedStyle(container).position === 'static') container.style.position = 'relative';

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.style.position  = 'absolute';
        svg.style.inset     = '0';
        svg.style.width     = '100%';
        svg.style.height    = '100%';
        svg.style.pointerEvents = 'none';   // drawing clicks handled on container
        svg.style.zIndex    = '4';          // below pills (z:6) but above chart (z:0)
        svg.style.overflow  = 'visible';
        container.appendChild(svg);
        ds.svgOverlay = svg;
        return svg;
    }

    function svgEl(tag, attrs) {
        const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
        for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
        return el;
    }

    // ── Redraw all committed drawings ──────────────────────────────────
    function redrawAll(containerId) {
        const ds  = getDS(containerId);
        const svg = ensureSvgOverlay(containerId);
        if (!svg) return;

        const cs = getChartSession(containerId);
        if (!cs) return;

        const container = document.getElementById(containerId);
        if (!container) return;
        const W = container.clientWidth;
        const H = container.clientHeight;

        // Clear everything except the preview group (last child if present)
        while (svg.firstChild) svg.removeChild(svg.firstChild);

        for (const d of ds.drawings) {
            renderDrawing(svg, cs, d, W, H, false);
        }

        // Render in-progress preview
        if (ds.drawState && ds.drawState.previewData) {
            renderDrawing(svg, cs, ds.drawState.previewData, W, H, true);
        }
    }

    // ── Render one Drawing object ──────────────────────────────────────
    // Drawing shapes: { type, price1, price2, time1, time2, color, id }
    function renderDrawing(svg, cs, d, W, H, isPreview) {
        const alpha = isPreview ? 0.55 : 1;

        switch (d.type) {

            case TOOL_HLINE: {
                const y = priceToY(cs.candleSeries, null, d.price1);
                if (y == null || y < 0 || y > H) return;
                const g = svgEl('g', {});

                // Dashed line
                const line = svgEl('line', {
                    x1: 0, y1: y, x2: W, y2: y,
                    stroke: d.color || COLORS.hline,
                    'stroke-width': isPreview ? 1 : 1.5,
                    'stroke-dasharray': '6 3',
                    opacity: alpha,
                });
                g.appendChild(line);

                // Price label pill on the right
                const label = makeSvgLabel(d.price1, W - 70, y - 9, 64, 18,
                    d.color || COLORS.hline, isPreview);
                g.appendChild(label);

                if (!isPreview) {
                    // Delete button on hover — we use a wider transparent hitbox
                    const hit = svgEl('line', {
                        x1: 0, y1: y, x2: W, y2: y,
                        stroke: 'transparent', 'stroke-width': 12,
                        style: 'pointer-events:stroke; cursor:pointer;',
                    });
                    hit.addEventListener('click', (e) => {
                        e.stopPropagation();
                        removeDrawing(d._containerId, d.id);
                    });
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }

            case TOOL_TLINE: {
                if (!d.time1 || !d.time2 || !d.price1 || !d.price2) return;
                const x1 = timeToX(cs.chart, d.time1);
                const x2 = timeToX(cs.chart, d.time2);
                const y1 = priceToY(cs.candleSeries, null, d.price1);
                const y2 = priceToY(cs.candleSeries, null, d.price2);
                if (x1 == null || x2 == null || y1 == null || y2 == null) return;

                const g = svgEl('g', {});

                // Extend the line to screen edges (infinite line style)
                const { ex1, ey1, ex2, ey2 } = extendLine(x1, y1, x2, y2, W, H);

                const line = svgEl('line', {
                    x1: ex1, y1: ey1, x2: ex2, y2: ey2,
                    stroke: d.color || COLORS.tline,
                    'stroke-width': isPreview ? 1 : 1.5,
                    opacity: alpha,
                });
                g.appendChild(line);

                // Anchor dots at the two clicked points
                [{ x: x1, y: y1 }, { x: x2, y: y2 }].forEach(pt => {
                    const dot = svgEl('circle', {
                        cx: pt.x, cy: pt.y, r: isPreview ? 2.5 : 3.5,
                        fill: d.color || COLORS.tline, opacity: alpha,
                    });
                    g.appendChild(dot);
                });

                if (!isPreview) {
                    const hit = svgEl('line', {
                        x1: ex1, y1: ey1, x2: ex2, y2: ey2,
                        stroke: 'transparent', 'stroke-width': 12,
                        style: 'pointer-events:stroke; cursor:pointer;',
                    });
                    hit.addEventListener('click', (e) => {
                        e.stopPropagation();
                        removeDrawing(d._containerId, d.id);
                    });
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }

            case TOOL_RECT: {
                if (!d.time1 || !d.time2 || !d.price1 || !d.price2) return;
                const x1 = timeToX(cs.chart, d.time1);
                const x2 = timeToX(cs.chart, d.time2);
                const y1 = priceToY(cs.candleSeries, null, d.price1);
                const y2 = priceToY(cs.candleSeries, null, d.price2);
                if (x1 == null || x2 == null || y1 == null || y2 == null) return;

                const rx = Math.min(x1, x2), ry = Math.min(y1, y2);
                const rw = Math.abs(x2 - x1), rh = Math.abs(y2 - y1);

                const g = svgEl('g', {});

                const fill = svgEl('rect', {
                    x: rx, y: ry, width: rw, height: rh,
                    fill: d.fill || COLORS.rect,
                    opacity: isPreview ? 0.5 : 1,
                });
                g.appendChild(fill);

                const border = svgEl('rect', {
                    x: rx, y: ry, width: rw, height: rh,
                    fill: 'none',
                    stroke: d.color || COLORS.rectBorder,
                    'stroke-width': 1,
                    opacity: alpha,
                });
                g.appendChild(border);

                // Price labels on right edge
                const higher = Math.max(d.price1, d.price2);
                const lower  = Math.min(d.price1, d.price2);
                const yHigh  = priceToY(cs.candleSeries, null, higher);
                const yLow   = priceToY(cs.candleSeries, null, lower);
                if (yHigh != null) g.appendChild(makeSvgLabel(higher, W - 70, yHigh - 9, 64, 18, d.color || COLORS.rectBorder, isPreview));
                if (yLow  != null) g.appendChild(makeSvgLabel(lower,  W - 70, yLow  - 9, 64, 18, d.color || COLORS.rectBorder, isPreview));

                if (!isPreview) {
                    const hit = svgEl('rect', {
                        x: rx, y: ry, width: rw, height: rh,
                        fill: 'transparent',
                        style: 'pointer-events:all; cursor:pointer;',
                    });
                    hit.addEventListener('click', (e) => {
                        e.stopPropagation();
                        removeDrawing(d._containerId, d.id);
                    });
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }

            case TOOL_FIB: {
                if (!d.time1 || !d.time2 || !d.price1 || !d.price2) return;
                const x1 = timeToX(cs.chart, d.time1);
                const x2 = timeToX(cs.chart, d.time2);
                if (x1 == null || x2 == null) return;

                const high  = Math.max(d.price1, d.price2);
                const low   = Math.min(d.price1, d.price2);
                const range = high - low;

                const g = svgEl('g', {});
                const xLeft  = Math.min(x1, x2);
                const xRight = W - 70;

                FIB_LEVELS.forEach((lvl, i) => {
                    const price = high - lvl.r * range;
                    const y = priceToY(cs.candleSeries, null, price);
                    if (y == null) return;

                    // Zone fill between adjacent levels
                    if (i < FIB_LEVELS.length - 1) {
                        const priceLow = high - FIB_LEVELS[i + 1].r * range;
                        const yLow = priceToY(cs.candleSeries, null, priceLow);
                        if (yLow != null) {
                            const zone = svgEl('rect', {
                                x: xLeft, y: Math.min(y, yLow),
                                width: xRight - xLeft,
                                height: Math.abs(yLow - y),
                                fill: COLORS.fibZone,
                                opacity: isPreview ? 0.4 : 1,
                            });
                            g.appendChild(zone);
                        }
                    }

                    // Level line
                    const line = svgEl('line', {
                        x1: xLeft, y1: y, x2: xRight, y2: y,
                        stroke: lvl.color,
                        'stroke-width': lvl.r === 0.5 || lvl.r === 0.618 ? 1.5 : 1,
                        'stroke-dasharray': lvl.r === 0 || lvl.r === 1 ? 'none' : '4 2',
                        opacity: isPreview ? 0.5 : 0.85,
                    });
                    g.appendChild(line);

                    // Label
                    const fmt = p => p.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
                    const lbl = svgEl('text', {
                        x: xRight + 3, y: y + 4,
                        fill: lvl.color, 'font-size': '9.5',
                        'font-family': 'monospace', 'font-weight': '700',
                        opacity: isPreview ? 0.5 : 0.9,
                    });
                    lbl.textContent = `${lvl.label} ${fmt(price)}`;
                    g.appendChild(lbl);
                });

                // Anchor dots
                const y1cs = priceToY(cs.candleSeries, null, d.price1);
                const y2cs = priceToY(cs.candleSeries, null, d.price2);
                [{ x: x1, y: y1cs }, { x: x2, y: y2cs }].forEach(pt => {
                    if (pt.y == null) return;
                    const dot = svgEl('circle', {
                        cx: pt.x, cy: pt.y, r: 3,
                        fill: COLORS.fib, opacity: alpha,
                    });
                    g.appendChild(dot);
                });

                svg.appendChild(g);
                break;
            }
        }
    }

    // ── SVG label pill helper ──────────────────────────────────────────
    function makeSvgLabel(price, x, y, w, h, color, isPreview) {
        const fmt = p => {
            const abs = Math.abs(p);
            if (abs >= 1000) return p.toFixed(2);
            if (abs >= 1)    return p.toFixed(4);
            if (abs >= 0.01) return p.toFixed(5);
            return p.toFixed(6);
        };
        const g = svgEl('g', { opacity: isPreview ? 0.5 : 1 });
        const bg = svgEl('rect', {
            x, y, width: w, height: h, rx: 3,
            fill: color || COLORS.hline,
        });
        const txt = svgEl('text', {
            x: x + w / 2, y: y + h / 2 + 3.5,
            fill: '#0a0d12', 'font-size': '9.5',
            'font-family': 'monospace', 'font-weight': '700',
            'text-anchor': 'middle',
        });
        txt.textContent = fmt(price);
        g.appendChild(bg);
        g.appendChild(txt);
        return g;
    }

    // ── Extend line to screen edges ────────────────────────────────────
    function extendLine(x1, y1, x2, y2, W, H) {
        if (x1 === x2) return { ex1: x1, ey1: 0, ex2: x2, ey2: H };
        const slope = (y2 - y1) / (x2 - x1);
        const intercept = y1 - slope * x1;
        const yAtX0 = intercept;
        const yAtXW = slope * W + intercept;
        let points = [
            { x: 0, y: yAtX0 },
            { x: W, y: yAtXW },
        ];
        return { ex1: points[0].x, ey1: points[0].y, ex2: points[1].x, ey2: points[1].y };
    }

    // ── Drawing lifecycle ──────────────────────────────────────────────
    let _drawingIdCounter = 1;

    function addDrawing(containerId, drawing) {
        const ds = getDS(containerId);
        drawing.id = _drawingIdCounter++;
        drawing._containerId = containerId;
        ds.drawings.push(drawing);
        redrawAll(containerId);
        updateToolbarBadge(containerId);
    }

    function removeDrawing(containerId, id) {
        const ds = getDS(containerId);
        ds.drawings = ds.drawings.filter(d => d.id !== id);
        redrawAll(containerId);
        updateToolbarBadge(containerId);
    }

    function removeLastDrawing(containerId) {
        const ds = getDS(containerId);
        if (ds.drawings.length === 0) return;
        ds.drawings.pop();
        redrawAll(containerId);
        updateToolbarBadge(containerId);
    }

    // ── Mouse event handling on container ─────────────────────────────
    // We attach separate listeners (via a dedicated AbortController per
    // drawing session) so they don't conflict with marketChart's own
    // mousedown/mousemove/mouseup which handles TP/SL dragging.
    function attachContainerListeners(containerId) {
        const ds = getDS(containerId);
        if (ds._abortController) ds._abortController.abort();
        ds._abortController = new AbortController();
        const opts = { signal: ds._abortController.signal };

        const container = document.getElementById(containerId);
        if (!container) return;

        container.addEventListener('mousedown', (e) => {
            const ds = getDS(containerId);
            if (!ds.activeTool) return;
            if (e.button !== 0) return;   // left click only

            const cs = getChartSession(containerId);
            if (!cs) return;

            const rect = container.getBoundingClientRect();
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;
            const price = yToPrice(cs.candleSeries, null, y);
            const time  = xToTime(cs.chart, x);
            if (price == null) return;

            e.stopPropagation();   // prevent TP/SL drag from activating
            e.preventDefault();

            switch (ds.activeTool) {
                case TOOL_HLINE:
                    // Single click completes a horizontal line immediately
                    addDrawing(containerId, { type: TOOL_HLINE, price1: price });
                    ds.drawState = null;
                    break;

                case TOOL_TLINE:
                case TOOL_RECT:
                case TOOL_FIB:
                    if (!ds.drawState) {
                        // First point
                        ds.drawState = { step: 1, x1: x, y1: y, price1: price, time1: time };
                    } else {
                        // Second point — commit
                        const d = ds.drawState;
                        addDrawing(containerId, {
                            type: ds.activeTool,
                            price1: d.price1, price2: price,
                            time1: d.time1,   time2: time,
                        });
                        ds.drawState = null;
                    }
                    break;
            }
        }, opts);

        container.addEventListener('mousemove', (e) => {
            const ds = getDS(containerId);
            if (!ds.activeTool) return;

            const cs = getChartSession(containerId);
            if (!cs) return;

            const rect = container.getBoundingClientRect();
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;
            const price = yToPrice(cs.candleSeries, null, y);
            const time  = xToTime(cs.chart, x);
            if (price == null) return;

            // Build a live preview drawing for 2-point tools
            if (ds.activeTool === TOOL_HLINE) {
                ds.drawState = {
                    previewData: { type: TOOL_HLINE, price1: price }
                };
            } else if (ds.drawState && ds.drawState.step === 1) {
                ds.drawState.previewData = {
                    type: ds.activeTool,
                    price1: ds.drawState.price1, price2: price,
                    time1: ds.drawState.time1,   time2: time,
                };
            } else if (!ds.drawState) {
                ds.drawState = {
                    previewData: { type: ds.activeTool, price1: price }
                };
            }

            redrawAll(containerId);
        }, opts);

        container.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                cancelActiveDraw(containerId);
                e.stopPropagation();
            }
            if ((e.key === 'Delete' || e.key === 'Backspace') && !ds.activeTool) {
                removeLastDrawing(containerId);
            }
        }, opts);
    }

    // ── Subscribe to chart scroll/zoom for redraw ──────────────────────
    function subscribeRedraw(containerId) {
        const ds = getDS(containerId);
        const cs = getChartSession(containerId);
        if (!cs) return;

        if (ds.drawingsSub) {
            try { cs.chart.timeScale().unsubscribeVisibleLogicalRangeChange(ds.drawingsSub); } catch (_) {}
        }
        if (ds.drawingsChSub) {
            try { cs.chart.unsubscribeCrosshairMove(ds.drawingsChSub); } catch (_) {}
        }

        ds.drawingsSub = () => redrawAll(containerId);
        ds.drawingsChSub = () => redrawAll(containerId);

        cs.chart.timeScale().subscribeVisibleLogicalRangeChange(ds.drawingsSub);
        cs.chart.subscribeCrosshairMove(ds.drawingsChSub);
    }

    // ── Cancel active draw / reset state ──────────────────────────────
    function cancelActiveDraw(containerId) {
        const ds = getDS(containerId);
        ds.drawState = null;
        redrawAll(containerId);
    }

    // ── Toolbar injection ──────────────────────────────────────────────
    function injectToolbar(containerId) {
        const ds = getDS(containerId);
        const container = document.getElementById(containerId);
        if (!container || ds.toolbarEl) return;

        if (getComputedStyle(container).position === 'static') container.style.position = 'relative';

        const toolbar = document.createElement('div');
        toolbar.className = 'vx-draw-toolbar';
        toolbar.style.cssText = [
            'position:absolute',
            'top:8px',
            'left:8px',
            'z-index:8',
            'display:flex',
            'flex-direction:column',
            'gap:4px',
            'background:rgba(10,13,18,0.88)',
            'border:1px solid rgba(30,37,54,0.9)',
            'border-radius:8px',
            'padding:6px 5px',
            'box-shadow:0 4px 16px rgba(0,0,0,0.45)',
            'backdrop-filter:blur(6px)',
        ].join(';');

        const tools = [
            { key: TOOL_HLINE, icon: '—', title: 'Horizontal Line  (H)', label: 'H' },
            { key: TOOL_TLINE, icon: '↗', title: 'Trend Line  (T)',       label: 'T' },
            { key: TOOL_RECT,  icon: '▭', title: 'Rectangle / Zone  (R)', label: 'R' },
            { key: TOOL_FIB,   icon: '〜', title: 'Fibonacci  (F)',        label: 'F' },
        ];

        tools.forEach(t => {
            const btn = document.createElement('button');
            btn.dataset.tool = t.key;
            btn.title  = t.title;
            btn.innerHTML = `<span class="vx-tool-icon">${t.icon}</span><span class="vx-tool-lbl">${t.label}</span>`;
            btn.style.cssText = [
                'display:flex', 'flex-direction:column', 'align-items:center',
                'justify-content:center', 'gap:1px',
                'width:32px', 'height:32px',
                'background:transparent',
                'border:1px solid transparent',
                'border-radius:6px',
                'color:#64748b',
                'font-size:14px',
                'cursor:pointer',
                'transition:all .12s',
                'padding:0',
            ].join(';');
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                window.chartDrawingTools.setActiveTool(
                    containerId,
                    ds.activeTool === t.key ? null : t.key
                );
            });
            toolbar.appendChild(btn);
        });

        // Divider
        const div = document.createElement('div');
        div.style.cssText = 'height:1px;background:rgba(30,37,54,0.8);margin:2px 2px;';
        toolbar.appendChild(div);

        // Undo last button
        const undoBtn = document.createElement('button');
        undoBtn.title = 'Delete last drawing  (Del)';
        undoBtn.innerHTML = '<span style="font-size:13px">⌫</span>';
        undoBtn.style.cssText = [
            'display:flex', 'align-items:center', 'justify-content:center',
            'width:32px', 'height:28px',
            'background:transparent', 'border:1px solid transparent',
            'border-radius:6px', 'color:#64748b',
            'cursor:pointer', 'transition:all .12s',
        ].join(';');
        undoBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            removeLastDrawing(containerId);
        });
        toolbar.appendChild(undoBtn);

        // Clear all button
        const clearBtn = document.createElement('button');
        clearBtn.title = 'Clear all drawings';
        clearBtn.innerHTML = '<span style="font-size:11px">✕</span>';
        clearBtn.style.cssText = undoBtn.style.cssText;
        clearBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            window.chartDrawingTools.clearAll(containerId);
        });
        toolbar.appendChild(clearBtn);

        // Drawing count badge (shown when there are drawings)
        const badge = document.createElement('div');
        badge.className = 'vx-draw-badge';
        badge.style.cssText = [
            'display:none',
            'font-size:9px', 'font-weight:700', 'font-family:monospace',
            'color:#3b82f6', 'text-align:center', 'margin-top:2px',
        ].join(';');
        toolbar.appendChild(badge);
        toolbar._badge = badge;

        container.appendChild(toolbar);
        ds.toolbarEl = toolbar;

        // Keyboard shortcuts (when chart container is focused or doc-wide)
        const kbHandler = (e) => {
            // Only fire if no text input is focused
            if (document.activeElement && ['INPUT','TEXTAREA','SELECT'].includes(document.activeElement.tagName)) return;
            switch (e.key.toUpperCase()) {
                case 'H': window.chartDrawingTools.setActiveTool(containerId, TOOL_HLINE); break;
                case 'T': window.chartDrawingTools.setActiveTool(containerId, TOOL_TLINE); break;
                case 'R': window.chartDrawingTools.setActiveTool(containerId, TOOL_RECT);  break;
                case 'F': window.chartDrawingTools.setActiveTool(containerId, TOOL_FIB);   break;
                case 'ESCAPE': cancelActiveDraw(containerId); break;
            }
        };
        document.addEventListener('keydown', kbHandler);
        getDS(containerId)._kbHandler = kbHandler;

        updateToolbarState(containerId);
    }

    function updateToolbarState(containerId) {
        const ds = getDS(containerId);
        if (!ds.toolbarEl) return;
        const btns = ds.toolbarEl.querySelectorAll('button[data-tool]');
        btns.forEach(btn => {
            const isActive = btn.dataset.tool === ds.activeTool;
            btn.style.background = isActive ? 'rgba(59,130,246,0.18)' : 'transparent';
            btn.style.borderColor = isActive ? 'rgba(59,130,246,0.5)' : 'transparent';
            btn.style.color = isActive ? '#60a5fa' : '#64748b';
        });

        // Update cursor on container
        const container = document.getElementById(containerId);
        if (container) container.style.cursor = ds.activeTool ? 'crosshair' : '';
    }

    function updateToolbarBadge(containerId) {
        const ds = getDS(containerId);
        if (!ds.toolbarEl || !ds.toolbarEl._badge) return;
        const n = ds.drawings.length;
        ds.toolbarEl._badge.style.display = n > 0 ? 'block' : 'none';
        ds.toolbarEl._badge.textContent = `${n} line${n !== 1 ? 's' : ''}`;
    }

    // ── Expose session map to drawing tools (bridge with market-chart.js)
    // market-chart.js uses a module-private `sessions` Map. We expose
    // a reference via window._vertexChartSessions so drawing tools can
    // call priceToCoordinate / timeToCoordinate without duplicating the
    // sessions map.  Add this one line inside market-chart.js's IIFE at
    // the END (before the closing `})();` ):
    //
    //   window._vertexChartSessions = sessions;
    //
    // If that line is not added, we fall back to document.getElementById
    // + a stored reference via the init hook below.
    function tryExposeSessionMap() {
        // Called once per tool init to check if the bridge is available
        return typeof window._vertexChartSessions !== 'undefined';
    }

    // ── Public API ─────────────────────────────────────────────────────
    window.chartDrawingTools = {

        // Call once after marketChart.init(containerId) to set up the
        // drawing tools overlay, toolbar, and event listeners.
        init(containerId) {
            const ds = getDS(containerId);
            ensureSvgOverlay(containerId);
            injectToolbar(containerId);
            attachContainerListeners(containerId);
            subscribeRedraw(containerId);
            // Focus the container so keyboard shortcuts work
            const container = document.getElementById(containerId);
            if (container && !container.hasAttribute('tabindex')) {
                container.setAttribute('tabindex', '0');
            }
        },

        // Set or clear the active tool. Pass null/'' to deactivate all.
        setActiveTool(containerId, tool) {
            const ds = getDS(containerId);
            ds.activeTool = tool || null;
            ds.drawState  = null;    // cancel any in-progress draw
            updateToolbarState(containerId);
            redrawAll(containerId);
        },

        getActiveTool(containerId) {
            return getDS(containerId).activeTool;
        },

        // Clears all drawings for this chart.
        clearAll(containerId) {
            const ds = getDS(containerId);
            ds.drawings  = [];
            ds.drawState = null;
            redrawAll(containerId);
            updateToolbarBadge(containerId);
        },

        // Save drawings to localStorage. Call with a key like
        // "drawings:BTCUSDT:FifteenMinutes".
        saveDrawings(containerId, storageKey) {
            const ds = getDS(containerId);
            try {
                const data = ds.drawings.map(d => ({
                    type: d.type, price1: d.price1, price2: d.price2,
                    time1: d.time1, time2: d.time2,
                    color: d.color, fill: d.fill,
                }));
                localStorage.setItem('vxDraw:' + storageKey, JSON.stringify(data));
            } catch (e) { console.warn('[DrawTools] save failed', e); }
        },

        // Load previously saved drawings from localStorage.
        loadDrawings(containerId, storageKey) {
            const ds = getDS(containerId);
            try {
                const raw = localStorage.getItem('vxDraw:' + storageKey);
                if (!raw) return;
                const data = JSON.parse(raw);
                ds.drawings = data.map(d => ({
                    ...d,
                    id: _drawingIdCounter++,
                    _containerId: containerId,
                }));
                redrawAll(containerId);
                updateToolbarBadge(containerId);
            } catch (e) { console.warn('[DrawTools] load failed', e); }
        },

        // Trigger a full redraw — call after setData / chart resize.
        redraw(containerId) {
            redrawAll(containerId);
        },

        // Clean up everything for this container.
        dispose(containerId) {
            const ds = getDS(containerId);
            if (ds._abortController) ds._abortController.abort();
            if (ds._kbHandler) document.removeEventListener('keydown', ds._kbHandler);
            if (ds.svgOverlay && ds.svgOverlay.parentNode) ds.svgOverlay.remove();
            if (ds.toolbarEl && ds.toolbarEl.parentNode) ds.toolbarEl.remove();
            const cs = getChartSession(containerId);
            if (cs) {
                if (ds.drawingsSub)  try { cs.chart.timeScale().unsubscribeVisibleLogicalRangeChange(ds.drawingsSub); } catch(_) {}
                if (ds.drawingsChSub) try { cs.chart.unsubscribeCrosshairMove(ds.drawingsChSub); } catch(_) {}
            }
            drawingSessions.delete(containerId);
        },
    };

})();

// ================================================================
// INTEGRATION INSTRUCTIONS
// ================================================================
//
// 1. Add one line at the END of market-chart.js (inside the IIFE,
//    just before the closing `})();` ):
//
//       window._vertexChartSessions = sessions;
//
//    This exposes the session Map so drawing tools can call
//    priceToCoordinate / timeToCoordinate without re-implementing them.
//
// 2. In _Host.cshtml, load this file after market-chart.js:
//
//       <script src="~/js/market-chart.js"></script>
//       <script src="~/js/chart-drawing-tools.js"></script>
//
// 3. In MarketSnapshot.razor, after calling marketChart.init():
//
//       // Init drawing tools
//       await JS.InvokeVoidAsync("chartDrawingTools.init", _containerId);
//
//       // Load any previously saved drawings for this symbol/TF
//       var key = $"{_activeSym}:{_activeTf}";
//       await JS.InvokeVoidAsync("chartDrawingTools.loadDrawings", _containerId, key);
//
// 4. When symbol or timeframe changes (before calling setData again):
//
//       // Save current drawings
//       await JS.InvokeVoidAsync("chartDrawingTools.saveDrawings", _containerId, oldKey);
//       // Clear and reload for new symbol/TF
//       await JS.InvokeVoidAsync("chartDrawingTools.clearAll", _containerId);
//       await JS.InvokeVoidAsync("chartDrawingTools.loadDrawings", _containerId, newKey);
//
// 5. Optionally expose tool selection to Blazor buttons:
//
//       <button @onclick='() => SetDrawTool("H")'>H-Line</button>
//
//       private async Task SetDrawTool(string tool)
//           => await JS.InvokeVoidAsync("chartDrawingTools.setActiveTool", _containerId, tool);
//
// 6. On chart dispose (component DisposeAsync):
//
//       await JS.InvokeVoidAsync("chartDrawingTools.dispose", _containerId);
//
// ================================================================
