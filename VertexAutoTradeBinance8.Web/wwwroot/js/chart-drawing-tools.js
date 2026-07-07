// ================================================================
// VERTEX CHART — DRAWING TOOLS  v2.0
// chart-drawing-tools.js
// ================================================================
// TradingView-style interaction model:
//
//   1. Click a toolbar button → cursor changes to crosshair
//   2. Draw on chart (1 click for H-line, 2 clicks for others)
//   3. Tool auto-deactivates → back to cursor mode
//   4. Hover any drawing → it highlights + shows move cursor
//   5. Drag any drawing to reposition it
//   6. Click a drawing to select it (shows delete button)
//   7. Press Delete or click X to remove selected drawing
//
// Tools:
//   H  Horizontal line      — 1 click
//   T  Trend line           — 2 clicks, extendable to screen edges
//   R  Rectangle / zone     — 2 clicks (diagonal corners)
//   F  Fibonacci retracement— 2 clicks, all 7 standard levels
//
// All drawings stored per-session, persisted to localStorage by
// symbol+timeframe key so they survive page refresh and sym changes.
// ================================================================

'use strict';

(function () {

    // ── Tool IDs ────────────────────────────────────────────────
    const T_NONE  = null;
    const T_HLINE = 'H';
    const T_TLINE = 'T';
    const T_RECT  = 'R';
    const T_FIB   = 'F';

    // ── Theme colours (dark trading terminal) ───────────────────
    const C = {
        hline      : '#94a3b8',
        hlineHover : '#e2e8f0',
        tline      : '#f59e0b',
        tlineHover : '#fcd34d',
        rect       : 'rgba(59,130,246,0.10)',
        rectBorder : '#3b82f6',
        rectHover  : 'rgba(59,130,246,0.22)',
        fib        : '#a855f7',
        fibHover   : '#c084fc',
        fibZone    : 'rgba(168,85,247,0.055)',
        selected   : '#38bdf8',
        preview    : 'rgba(148,163,184,0.5)',
        labelBg    : '#0a0d12',
        labelFg    : '#e2e8f0',
        deleteBg   : '#ef4444',
        deleteFg   : '#fff',
    };

    // Fibonacci standard levels
    const FIB_LEVELS = [
        { r: 0,     label: '0%',    color: '#ef4444' },
        { r: 0.236, label: '23.6%', color: '#f97316' },
        { r: 0.382, label: '38.2%', color: '#eab308' },
        { r: 0.5,   label: '50%',   color: '#a855f7' },
        { r: 0.618, label: '61.8%', color: '#3b82f6' },
        { r: 0.786, label: '78.6%', color: '#22c55e' },
        { r: 1,     label: '100%',  color: '#ef4444' },
    ];

    // ── Per-container state ──────────────────────────────────────
    const S = new Map(); // containerId → state object

    function state(id) {
        if (!S.has(id)) S.set(id, {
            drawings    : [],       // Drawing[]
            activeTool  : T_NONE,  // tool being drawn
            draft       : null,     // in-progress drawing (not committed)
            selected    : null,     // selected drawing id
            hovered     : null,     // hovered drawing id
            drag        : null,     // { id, kind, startX, startY, snapshot }
            svg         : null,     // <svg> overlay element
            toolbar     : null,     // toolbar DOM element
            rangeSub    : null,     // chart range subscription
            crossSub    : null,     // crosshair subscription
            _nextId     : 1,
            _ac         : null,     // AbortController for listeners
            _kbHandler  : null,
        });
        return S.get(id);
    }

    // ── Bridge to market-chart.js sessions ──────────────────────
    function cs(id) {
        return window._vertexChartSessions && window._vertexChartSessions.get(id);
    }

    function toY(id, price) {
        const s = cs(id);
        return s ? s.candleSeries.priceToCoordinate(price) : null;
    }

    function toPrice(id, y) {
        const s = cs(id);
        return s ? s.candleSeries.coordinateToPrice(y) : null;
    }

    function toX(id, time) {
        const s = cs(id);
        if (!s) return null;
        try { return s.chart.timeScale().timeToCoordinate(time); } catch { return null; }
    }

    function toTime(id, x) {
        const s = cs(id);
        if (!s) return null;
        try { return s.chart.timeScale().coordinateToTime(x); } catch { return null; }
    }

    function containerRect(id) {
        const el = document.getElementById(id);
        return el ? el.getBoundingClientRect() : null;
    }

    function containerSize(id) {
        const el = document.getElementById(id);
        return el ? { W: el.clientWidth, H: el.clientHeight } : { W: 0, H: 0 };
    }

    // ── SVG overlay ──────────────────────────────────────────────
    function ensureSvg(id) {
        const st = state(id);
        if (st.svg) return st.svg;
        const el = document.getElementById(id);
        if (!el) return null;
        if (getComputedStyle(el).position === 'static') el.style.position = 'relative';

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none;z-index:4;overflow:visible;';
        el.appendChild(svg);
        st.svg = svg;
        return svg;
    }

    function mk(tag, attrs) {
        const el = document.createElementNS('http://www.w3.org/2000/svg', tag);
        for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
        return el;
    }

    // ── Hit-test: returns drawing id or null ─────────────────────
    // We keep it simple: check each drawing's bounding region
    const HIT_TOLERANCE = 8; // px — tolerance for line hit

    function hitTest(id, x, y) {
        const st = state(id);
        const { W, H } = containerSize(id);
        // Iterate in reverse so topmost (newest) wins
        for (let i = st.drawings.length - 1; i >= 0; i--) {
            const d = st.drawings[i];
            if (hitDrawing(id, d, x, y, W, H)) return d.id;
        }
        return null;
    }

    function hitDrawing(id, d, mx, my, W, H) {
        switch (d.type) {
            case T_HLINE: {
                const y = toY(id, d.price1);
                return y != null && Math.abs(my - y) < HIT_TOLERANCE;
            }
            case T_TLINE: {
                const x1 = toX(id, d.time1), y1 = toY(id, d.price1);
                const x2 = toX(id, d.time2), y2 = toY(id, d.price2);
                if (x1 == null || x2 == null) return false;
                return distToSegment(mx, my, x1, y1, x2, y2) < HIT_TOLERANCE;
            }
            case T_RECT: {
                const x1 = toX(id, d.time1), y1 = toY(id, d.price1);
                const x2 = toX(id, d.time2), y2 = toY(id, d.price2);
                if (x1 == null || x2 == null) return false;
                const rx = Math.min(x1,x2), ry = Math.min(y1,y2);
                const rw = Math.abs(x2-x1), rh = Math.abs(y2-y1);
                // hit border OR interior
                return mx >= rx - HIT_TOLERANCE && mx <= rx + rw + HIT_TOLERANCE &&
                       my >= ry - HIT_TOLERANCE && my <= ry + rh + HIT_TOLERANCE;
            }
            case T_FIB: {
                // Hit if within the vertical band
                const x1 = toX(id, d.time1), x2 = toX(id, d.time2);
                if (x1 == null || x2 == null) return false;
                const xLeft = Math.min(x1,x2), xRight = W;
                if (mx < xLeft - HIT_TOLERANCE || mx > xRight + HIT_TOLERANCE) return false;
                // Check any fib level line
                const high = Math.max(d.price1, d.price2);
                const low  = Math.min(d.price1, d.price2);
                for (const lvl of FIB_LEVELS) {
                    const p = high - lvl.r * (high - low);
                    const yy = toY(id, p);
                    if (yy != null && Math.abs(my - yy) < HIT_TOLERANCE) return true;
                }
                return false;
            }
        }
        return false;
    }

    function distToSegment(px, py, ax, ay, bx, by) {
        const dx = bx - ax, dy = by - ay;
        const lenSq = dx*dx + dy*dy;
        if (lenSq === 0) return Math.hypot(px-ax, py-ay);
        let t = ((px-ax)*dx + (py-ay)*dy) / lenSq;
        t = Math.max(0, Math.min(1, t));
        return Math.hypot(px - (ax + t*dx), py - (ay + t*dy));
    }

    // ── Drag helpers ─────────────────────────────────────────────
    // When drag starts we snapshot the drawing's key values so we
    // can offset them by (Δprice, Δtime) as the mouse moves.
    function startDrag(id, drawingId, clientX, clientY) {
        const st = state(id);
        const d  = st.drawings.find(x => x.id === drawingId);
        if (!d) return;
        const rect = containerRect(id);
        const x = clientX - rect.left;
        const y = clientY - rect.top;
        st.drag = {
            id,
            drawingId,
            startX    : x,
            startY    : y,
            startPrice1: d.price1,
            startPrice2: d.price2,
            startTime1 : d.time1,
            startTime2 : d.time2,
        };
    }

    function applyDrag(id, clientX, clientY) {
        const st = state(id);
        if (!st.drag || st.drag.id !== id) return;
        const rect = containerRect(id);
        const x = clientX - rect.left;
        const y = clientY - rect.top;
        const dx = x - st.drag.startX;
        const dy = y - st.drag.startY;

        const d = st.drawings.find(x => x.id === st.drag.drawingId);
        if (!d) return;

        const p1 = toPrice(id, toY(id, st.drag.startPrice1) + dy);
        const p2 = d.price2 != null ? toPrice(id, toY(id, st.drag.startPrice2) + dy) : null;
        const t1 = d.time1 != null ? toTime(id, toX(id, st.drag.startTime1) + dx) : null;
        const t2 = d.time2 != null ? toTime(id, toX(id, st.drag.startTime2) + dx) : null;

        if (p1 != null) d.price1 = p1;
        if (p2 != null) d.price2 = p2;
        if (t1 != null) d.time1 = t1;
        if (t2 != null) d.time2 = t2;

        redraw(id);
    }

    function endDrag(id) {
        const st = state(id);
        st.drag = null;
    }

    // ── Commit drawing ────────────────────────────────────────────
    function commit(id) {
        const st = state(id);
        if (!st.draft) return;
        const d = { ...st.draft, id: st._nextId++ };
        st.drawings.push(d);
        st.draft = null;
        st.activeTool = T_NONE;        // auto-deactivate like TradingView
        updateToolbarState(id);
        setCursor(id, '');
        redraw(id);
        updateBadge(id);
    }

    function cancelDraft(id) {
        const st = state(id);
        st.draft = null;
        redraw(id);
    }

    function removeDrawing(id, drawingId) {
        const st = state(id);
        st.drawings = st.drawings.filter(d => d.id !== drawingId);
        if (st.selected === drawingId) st.selected = null;
        redraw(id);
        updateBadge(id);
    }

    // ── Cursor management ─────────────────────────────────────────
    function setCursor(id, cur) {
        const el = document.getElementById(id);
        if (el) el.style.cursor = cur;
    }

    // ── Main redraw ───────────────────────────────────────────────
    function redraw(id) {
        const svg = ensureSvg(id);
        if (!svg) return;
        while (svg.firstChild) svg.removeChild(svg.firstChild);

        const { W, H } = containerSize(id);
        const st = state(id);

        // Draw committed drawings
        for (const d of st.drawings) {
            const isHov = st.hovered === d.id;
            const isSel = st.selected === d.id;
            renderDrawing(svg, id, d, W, H, false, isHov || isSel, isSel);
        }

        // Draw in-progress draft
        if (st.draft) {
            renderDrawing(svg, id, st.draft, W, H, true, false, false);
        }

        // Delete button for selected drawing
        if (st.selected != null) {
            const sel = st.drawings.find(d => d.id === st.selected);
            if (sel) renderDeleteBtn(svg, id, sel, W, H);
        }
    }

    // ── Render one drawing ────────────────────────────────────────
    function renderDrawing(svg, id, d, W, H, isDraft, isHighlit, isSelected) {
        const baseAlpha = isDraft ? 0.55 : 1;
        const strokeW   = isDraft ? 1 : (isHighlit ? 2 : 1.5);

        switch (d.type) {

            case T_HLINE: {
                const y = toY(id, d.price1);
                if (y == null) return;
                const color = isSelected ? C.selected : isHighlit ? C.hlineHover : C.hline;
                const g = mk('g', {});

                // Main line
                g.appendChild(mk('line', {
                    x1: 0, y1: y, x2: W, y2: y,
                    stroke: color, 'stroke-width': strokeW,
                    'stroke-dasharray': '7 3',
                    opacity: baseAlpha,
                    style: 'pointer-events:none',
                }));

                // Price label pill
                g.appendChild(priceLabel(id, d.price1, W - 72, y, color, isDraft));

                // Wide invisible hit area (pointer-events on, so mouse events fire)
                if (!isDraft) {
                    const hit = mk('line', {
                        x1: 0, y1: y, x2: W - 75, y2: y,
                        stroke: 'transparent', 'stroke-width': 16,
                        style: 'pointer-events:stroke; cursor:grab;',
                    });
                    hit.dataset.did = d.id;
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }

            case T_TLINE: {
                if (!d.time1 || !d.time2) return;
                const x1 = toX(id, d.time1), y1 = toY(id, d.price1);
                const x2 = toX(id, d.time2), y2 = toY(id, d.price2);
                if (x1 == null || x2 == null || y1 == null || y2 == null) return;

                const color = isSelected ? C.selected : isHighlit ? C.tlineHover : C.tline;
                const { ex1, ey1, ex2, ey2 } = extendLine(x1, y1, x2, y2, W, H);
                const g = mk('g', {});

                g.appendChild(mk('line', {
                    x1: ex1, y1: ey1, x2: ex2, y2: ey2,
                    stroke: color, 'stroke-width': strokeW,
                    opacity: baseAlpha,
                    style: 'pointer-events:none',
                }));

                // Anchor dots at real click points
                for (const pt of [{x:x1,y:y1},{x:x2,y:y2}]) {
                    g.appendChild(mk('circle', {
                        cx: pt.x, cy: pt.y, r: isHighlit ? 4.5 : 3.5,
                        fill: color, opacity: baseAlpha,
                        style: 'pointer-events:none',
                    }));
                }

                if (!isDraft) {
                    const hit = mk('line', {
                        x1: ex1, y1: ey1, x2: ex2, y2: ey2,
                        stroke: 'transparent', 'stroke-width': 16,
                        style: 'pointer-events:stroke; cursor:grab;',
                    });
                    hit.dataset.did = d.id;
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }

            case T_RECT: {
                if (!d.time1 || !d.time2) return;
                const x1 = toX(id, d.time1), y1 = toY(id, d.price1);
                const x2 = toX(id, d.time2), y2 = toY(id, d.price2);
                if (x1 == null || x2 == null || y1 == null || y2 == null) return;

                const rx = Math.min(x1,x2), ry = Math.min(y1,y2);
                const rw = Math.abs(x2-x1), rh = Math.abs(y2-y1);
                const borderColor = isSelected ? C.selected : isHighlit ? C.rectHover : C.rectBorder;
                const g = mk('g', {});

                g.appendChild(mk('rect', {
                    x: rx, y: ry, width: rw, height: rh,
                    fill: isHighlit ? C.rectHover : C.rect,
                    opacity: isDraft ? 0.5 : 1,
                    style: 'pointer-events:none',
                }));
                g.appendChild(mk('rect', {
                    x: rx, y: ry, width: rw, height: rh,
                    fill: 'none', stroke: borderColor, 'stroke-width': strokeW,
                    opacity: baseAlpha, style: 'pointer-events:none',
                }));

                // Price labels
                const hi = Math.max(d.price1, d.price2), lo = Math.min(d.price1, d.price2);
                const yHi = toY(id, hi), yLo = toY(id, lo);
                if (yHi != null) g.appendChild(priceLabel(id, hi, W-72, yHi, borderColor, isDraft));
                if (yLo != null) g.appendChild(priceLabel(id, lo, W-72, yLo, borderColor, isDraft));

                if (!isDraft) {
                    const hit = mk('rect', {
                        x: rx, y: ry, width: rw, height: rh,
                        fill: 'transparent',
                        style: 'pointer-events:all; cursor:grab;',
                    });
                    hit.dataset.did = d.id;
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }

            case T_FIB: {
                if (!d.time1 || !d.time2) return;
                const x1 = toX(id, d.time1), x2 = toX(id, d.time2);
                const y1 = toY(id, d.price1), y2 = toY(id, d.price2);
                if (x1 == null || x2 == null) return;

                const color  = isSelected ? C.selected : isHighlit ? C.fibHover : C.fib;
                const xLeft  = Math.min(x1, x2);
                const xRight = W - 75;
                const high   = Math.max(d.price1, d.price2);
                const low    = Math.min(d.price1, d.price2);
                const g      = mk('g', {});

                FIB_LEVELS.forEach((lvl, i) => {
                    const price = high - lvl.r * (high - low);
                    const yy = toY(id, price);
                    if (yy == null) return;

                    // Zone fill between adjacent levels
                    if (i < FIB_LEVELS.length - 1) {
                        const p2 = high - FIB_LEVELS[i+1].r * (high - low);
                        const y2 = toY(id, p2);
                        if (y2 != null) {
                            g.appendChild(mk('rect', {
                                x: xLeft, y: Math.min(yy,y2),
                                width: Math.max(xRight-xLeft, 0),
                                height: Math.abs(y2-yy),
                                fill: C.fibZone,
                                opacity: isDraft ? 0.4 : 1,
                                style: 'pointer-events:none',
                            }));
                        }
                    }

                    // Level line
                    const lc = isSelected ? C.selected : isHighlit ? C.fibHover : lvl.color;
                    g.appendChild(mk('line', {
                        x1: xLeft, y1: yy, x2: xRight, y2: yy,
                        stroke: lc,
                        'stroke-width': (lvl.r===0.5||lvl.r===0.618) ? (isHighlit?2:1.5) : (isHighlit?1.5:1),
                        'stroke-dasharray': (lvl.r===0||lvl.r===1) ? 'none' : '5 2',
                        opacity: isDraft ? 0.5 : 0.9,
                        style: 'pointer-events:none',
                    }));

                    // Label
                    const fmt = p => {
                        const a = Math.abs(p);
                        return a>=1000?p.toFixed(2):a>=1?p.toFixed(4):p.toFixed(5);
                    };
                    const txt = mk('text', {
                        x: xRight+4, y: yy+4,
                        fill: lc, 'font-size': '9.5',
                        'font-family': 'monospace', 'font-weight': '700',
                        opacity: isDraft ? 0.5 : 0.9,
                        style: 'pointer-events:none; user-select:none',
                    });
                    txt.textContent = `${lvl.label}  ${fmt(price)}`;
                    g.appendChild(txt);
                });

                // Anchor dots
                if (y1!=null) g.appendChild(mk('circle',{cx:x1,cy:y1,r:3,fill:color,opacity:baseAlpha,style:'pointer-events:none'}));
                if (y2!=null) g.appendChild(mk('circle',{cx:x2,cy:y2,r:3,fill:color,opacity:baseAlpha,style:'pointer-events:none'}));

                // Hit area — wide invisible rect over the full fib band
                if (!isDraft && y1!=null && y2!=null) {
                    const hRy = Math.min(y1,y2)-10, hRh = Math.abs(y2-y1)+20;
                    const hit = mk('rect',{
                        x:xLeft, y:hRy,
                        width: Math.max(xRight-xLeft,0), height: hRh,
                        fill:'transparent',
                        style:'pointer-events:all; cursor:grab;',
                    });
                    hit.dataset.did = d.id;
                    g.appendChild(hit);
                }
                svg.appendChild(g);
                break;
            }
        }
    }

    // ── Price label pill (right side) ─────────────────────────────
    function priceLabel(id, price, x, y, color, isDraft) {
        const fmt = p => {
            const a = Math.abs(p);
            return a>=10000?p.toFixed(2):a>=1?p.toFixed(4):p.toFixed(5);
        };
        const g = mk('g', { opacity: isDraft ? 0.5 : 1, style:'pointer-events:none' });
        g.appendChild(mk('rect', { x, y: y-9, width: 70, height: 18, rx: 3, fill: color }));
        const t = mk('text', {
            x: x+35, y: y+4.5,
            fill: '#0a0d12', 'font-size': '9.5',
            'font-family': 'monospace', 'font-weight': '700',
            'text-anchor': 'middle',
            style: 'user-select:none',
        });
        t.textContent = fmt(price);
        g.appendChild(t);
        return g;
    }

    // ── Delete button for selected drawing ────────────────────────
    function renderDeleteBtn(svg, id, d, W, H) {
        // Compute a good position (top-center-ish of the drawing)
        let bx = W / 2, by = 30;
        switch (d.type) {
            case T_HLINE: {
                const y = toY(id, d.price1);
                if (y != null) { bx = W/2; by = y - 18; }
                break;
            }
            case T_TLINE:
            case T_FIB: {
                const x1=toX(id,d.time1),y1=toY(id,d.price1);
                const x2=toX(id,d.time2),y2=toY(id,d.price2);
                if (x1!=null&&y1!=null&&x2!=null&&y2!=null) {
                    bx=(x1+x2)/2; by=Math.min(y1,y2)-18;
                }
                break;
            }
            case T_RECT: {
                const x1=toX(id,d.time1),y1=toY(id,d.price1);
                const x2=toX(id,d.time2),y2=toY(id,d.price2);
                if (x1!=null&&y1!=null&&x2!=null&&y2!=null) {
                    bx=(x1+x2)/2; by=Math.min(y1,y2)-18;
                }
                break;
            }
        }
        by = Math.max(18, Math.min(H-10, by));
        bx = Math.max(20, Math.min(W-20, bx));

        // Background pill: "DRAG  ✕"
        const g = mk('g', { style:'pointer-events:all; cursor:pointer;' });
        g.appendChild(mk('rect', { x:bx-26, y:by-12, width:52, height:22, rx:5,
            fill:'rgba(10,13,18,0.9)', stroke:C.selected, 'stroke-width':1 }));

        // ✕ text
        const delTxt = mk('text', {
            x: bx+8, y: by+4.5,
            fill: C.deleteBg, 'font-size': '12', 'font-weight': '800',
            'text-anchor': 'middle', style: 'user-select:none',
        });
        delTxt.textContent = '✕';
        g.appendChild(delTxt);

        // "DEL" label
        const delLbl = mk('text', {
            x: bx-12, y: by+4.5,
            fill: '#94a3b8', 'font-size': '8.5', 'font-weight': '700',
            'text-anchor': 'middle', style: 'user-select:none',
        });
        delLbl.textContent = 'DEL';
        g.appendChild(delLbl);

        g.addEventListener('mousedown', (e) => {
            e.stopPropagation();
            removeDrawing(id, d.id);
        });
        svg.appendChild(g);
    }

    // ── Extend line to canvas edges ───────────────────────────────
    function extendLine(x1, y1, x2, y2, W, H) {
        if (Math.abs(x2 - x1) < 0.001) return { ex1:x1, ey1:0, ex2:x2, ey2:H };
        const m = (y2-y1)/(x2-x1);
        const b = y1 - m*x1;
        return { ex1:0, ey1:b, ex2:W, ey2:m*W+b };
    }

    // ── Event listeners on the SVG (pointer-events on hit areas) ──
    function attachListeners(id) {
        const st = state(id);
        if (st._ac) st._ac.abort();
        st._ac = new AbortController();
        const opts = { signal: st._ac.signal };

        const el = document.getElementById(id);
        if (!el) return;

        // ── MOUSEMOVE ─────────────────────────────────────────────
        el.addEventListener('mousemove', (e) => {
            const st = state(id);
            const rect = containerRect(id);
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;

            // ── Drag active ──
            if (st.drag) {
                applyDrag(id, e.clientX, e.clientY);
                setCursor(id, 'grabbing');
                return;
            }

            // ── Drawing in progress ──
            if (st.activeTool) {
                const price = toPrice(id, y);
                const time  = toTime(id, x);
                if (price == null) return;

                if (st.activeTool === T_HLINE) {
                    st.draft = { type: T_HLINE, price1: price };
                } else if (st.draft && st.draft._step === 1) {
                    // Second point preview
                    st.draft.price2 = price;
                    st.draft.time2  = time;
                }
                redraw(id);
                return;
            }

            // ── Hover detection ──
            const hit = hitTest(id, x, y);
            const prev = st.hovered;
            st.hovered = hit;
            if (hit !== prev) redraw(id);
            setCursor(id, hit != null ? 'grab' : '');
        }, opts);

        // ── MOUSEDOWN ─────────────────────────────────────────────
        el.addEventListener('mousedown', (e) => {
            if (e.button !== 0) return;
            const st = state(id);
            const rect = containerRect(id);
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;
            const price = toPrice(id, y);
            const time  = toTime(id, x);

            // ── Hit area elements (SVG) forward their did ──
            const did = e.target && e.target.dataset && e.target.dataset.did
                ? parseInt(e.target.dataset.did)
                : null;

            // ── Drawing mode ──
            if (st.activeTool) {
                e.stopPropagation();
                e.preventDefault();

                if (st.activeTool === T_HLINE) {
                    st.draft = { type: T_HLINE, price1: price };
                    commit(id);
                    return;
                }

                if (!st.draft || !st.draft._step) {
                    // First click
                    st.draft = {
                        type: st.activeTool,
                        price1: price, time1: time,
                        price2: price, time2: time,  // preview same point
                        _step: 1,
                    };
                } else {
                    // Second click → commit
                    st.draft.price2 = price;
                    st.draft.time2  = time;
                    delete st.draft._step;
                    commit(id);
                }
                return;
            }

            // ── Selection / drag mode ──
            if (did != null) {
                e.stopPropagation();
                // Select it
                st.selected = (st.selected === did) ? null : did;
                redraw(id);
                // Start drag
                startDrag(id, did, e.clientX, e.clientY);
                setCursor(id, 'grabbing');
                return;
            }

            // Clicked empty space → deselect
            if (st.selected != null) {
                st.selected = null;
                redraw(id);
            }
        }, opts);

        // ── MOUSEUP ───────────────────────────────────────────────
        window.addEventListener('mouseup', () => {
            const st = state(id);
            if (st.drag) {
                endDrag(id);
                setCursor(id, st.hovered != null ? 'grab' : '');
            }
        }, opts);

        // ── DBLCLICK on a drawing → delete immediately ────────────
        el.addEventListener('dblclick', (e) => {
            const st = state(id);
            if (st.activeTool) return;
            const rect = containerRect(id);
            const x = e.clientX - rect.left;
            const y = e.clientY - rect.top;
            const hit = hitTest(id, x, y);
            if (hit != null) {
                e.stopPropagation();
                removeDrawing(id, hit);
            }
        }, opts);

        // ── KEYBOARD ──────────────────────────────────────────────
        const kb = (e) => {
            if (['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName)) return;
            switch (e.key.toUpperCase()) {
                case 'H': window.chartDrawingTools.setActiveTool(id, T_HLINE); break;
                case 'T': window.chartDrawingTools.setActiveTool(id, T_TLINE); break;
                case 'R': window.chartDrawingTools.setActiveTool(id, T_RECT);  break;
                case 'F': window.chartDrawingTools.setActiveTool(id, T_FIB);   break;
                case 'ESCAPE':
                    cancelDraft(id);
                    const s = state(id);
                    s.activeTool = T_NONE;
                    s.selected = null;
                    updateToolbarState(id);
                    setCursor(id, '');
                    redraw(id);
                    break;
                case 'DELETE':
                case 'BACKSPACE': {
                    const s = state(id);
                    if (s.selected != null) {
                        removeDrawing(id, s.selected);
                    } else if (s.drawings.length > 0) {
                        // Delete last if nothing selected
                        removeDrawing(id, s.drawings[s.drawings.length-1].id);
                    }
                    break;
                }
            }
        };
        document.addEventListener('keydown', kb, opts);
        st._kbHandler = kb;
    }

    // ── Subscribe to chart scroll/zoom for redraw ─────────────────
    function subscribeChartEvents(id) {
        const st = state(id);
        const c  = cs(id);
        if (!c) return;

        if (st.rangeSub) {
            try { c.chart.timeScale().unsubscribeVisibleLogicalRangeChange(st.rangeSub); } catch {}
        }
        if (st.crossSub) {
            try { c.chart.unsubscribeCrosshairMove(st.crossSub); } catch {}
        }

        st.rangeSub = () => redraw(id);
        st.crossSub = () => redraw(id);
        c.chart.timeScale().subscribeVisibleLogicalRangeChange(st.rangeSub);
        c.chart.subscribeCrosshairMove(st.crossSub);
    }

    // ── Toolbar ───────────────────────────────────────────────────
    const TOOLS = [
        { key: T_HLINE, svg: `<line x1="3" y1="12" x2="21" y2="12" stroke="currentColor" stroke-width="1.8" stroke-dasharray="5 2"/>`, label: 'H', title: 'Horizontal Line  [H]' },
        { key: T_TLINE, svg: `<line x1="4" y1="18" x2="20" y2="6" stroke="currentColor" stroke-width="1.8"/><circle cx="4" cy="18" r="1.8" fill="currentColor"/><circle cx="20" cy="6" r="1.8" fill="currentColor"/>`, label: 'T', title: 'Trend Line  [T]' },
        { key: T_RECT,  svg: `<rect x="4" y="7" width="16" height="10" rx="1" stroke="currentColor" stroke-width="1.8" fill="none"/>`, label: 'R', title: 'Rectangle / Zone  [R]' },
        { key: T_FIB,   svg: `<line x1="3" y1="5" x2="21" y2="5" stroke="#ef4444" stroke-width="1.2"/><line x1="3" y1="9" x2="21" y2="9" stroke="#eab308" stroke-width="1.2"/><line x1="3" y1="13" x2="21" y2="13" stroke="#a855f7" stroke-width="1.5"/><line x1="3" y1="17" x2="21" y2="17" stroke="#3b82f6" stroke-width="1.2"/><line x1="3" y1="21" x2="21" y2="21" stroke="#22c55e" stroke-width="1.2"/>`, label: 'F', title: 'Fibonacci Retracement  [F]' },
    ];

    function injectToolbar(id) {
        const st = state(id);
        if (st.toolbar) return;
        const el = document.getElementById(id);
        if (!el) return;

        const bar = document.createElement('div');
        bar.style.cssText = [
            'position:absolute', 'top:10px', 'left:10px', 'z-index:9',
            'display:flex', 'flex-direction:column', 'gap:3px',
            'background:rgba(10,13,18,0.92)',
            'border:1px solid rgba(30,45,71,0.9)',
            'border-radius:9px',
            'padding:6px 5px',
            'box-shadow:0 4px 20px rgba(0,0,0,0.5)',
            'backdrop-filter:blur(8px)',
            'user-select:none',
        ].join(';');

        for (const t of TOOLS) {
            const btn = document.createElement('button');
            btn.dataset.tool = t.key;
            btn.title = t.title;
            btn.style.cssText = [
                'display:flex', 'align-items:center', 'justify-content:center',
                'width:34px', 'height:34px',
                'background:transparent',
                'border:1px solid transparent',
                'border-radius:7px',
                'color:#64748b',
                'cursor:pointer',
                'transition:all 0.12s',
                'padding:0',
                'outline:none',
            ].join(';');
            btn.innerHTML = `<svg width="22" height="22" viewBox="0 0 24 24" fill="none">${t.svg}</svg>`;
            btn.addEventListener('mouseenter', () => {
                if (btn.dataset.tool !== state(id).activeTool)
                    btn.style.background = 'rgba(59,130,246,0.08)';
            });
            btn.addEventListener('mouseleave', () => {
                if (btn.dataset.tool !== state(id).activeTool)
                    btn.style.background = 'transparent';
            });
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                const cur = state(id).activeTool;
                window.chartDrawingTools.setActiveTool(id, cur === t.key ? T_NONE : t.key);
            });
            bar.appendChild(btn);
        }

        // Divider
        const div = document.createElement('div');
        div.style.cssText = 'height:1px;background:rgba(30,45,71,0.8);margin:3px 3px;';
        bar.appendChild(div);

        // Undo button
        const undo = mkTbBtn('⌫', 'Remove last drawing  [Del]');
        undo.addEventListener('click', (e) => {
            e.stopPropagation();
            const s = state(id);
            if (s.selected != null) {
                removeDrawing(id, s.selected);
            } else if (s.drawings.length > 0) {
                removeDrawing(id, s.drawings[s.drawings.length-1].id);
            }
        });
        bar.appendChild(undo);

        // Clear all button
        const clr = mkTbBtn('✕', 'Clear all drawings');
        clr.addEventListener('click', (e) => {
            e.stopPropagation();
            window.chartDrawingTools.clearAll(id);
        });
        bar.appendChild(clr);

        // Count badge
        const badge = document.createElement('div');
        badge.style.cssText = [
            'display:none', 'font-size:9px', 'font-weight:700',
            'color:#3b82f6', 'text-align:center',
            'font-family:monospace', 'margin-top:2px', 'letter-spacing:0.3px',
        ].join(';');
        bar.appendChild(badge);
        bar._badge = badge;

        el.appendChild(bar);
        st.toolbar = bar;
        updateToolbarState(id);
    }

    function mkTbBtn(icon, title) {
        const btn = document.createElement('button');
        btn.title = title;
        btn.textContent = icon;
        btn.style.cssText = [
            'display:flex', 'align-items:center', 'justify-content:center',
            'width:34px', 'height:28px',
            'background:transparent', 'border:1px solid transparent',
            'border-radius:7px', 'color:#475569',
            'font-size:13px', 'cursor:pointer',
            'transition:all 0.12s', 'outline:none',
        ].join(';');
        return btn;
    }

    function updateToolbarState(id) {
        const st = state(id);
        if (!st.toolbar) return;
        st.toolbar.querySelectorAll('button[data-tool]').forEach(btn => {
            const active = btn.dataset.tool === st.activeTool;
            btn.style.background  = active ? 'rgba(59,130,246,0.18)' : 'transparent';
            btn.style.borderColor = active ? 'rgba(59,130,246,0.55)' : 'transparent';
            btn.style.color       = active ? '#60a5fa' : '#64748b';
        });
    }

    function updateBadge(id) {
        const st = state(id);
        if (!st.toolbar?._badge) return;
        const n = st.drawings.length;
        st.toolbar._badge.style.display = n > 0 ? 'block' : 'none';
        st.toolbar._badge.textContent   = `${n}`;
    }

    // ── Persistence ───────────────────────────────────────────────
    function saveDrawings(id, key) {
        try {
            const st = state(id);
            const data = st.drawings.map(d => {
                const { id: _, _step, ...rest } = d;
                return rest;
            });
            localStorage.setItem('vxd:' + key, JSON.stringify(data));
        } catch {}
    }

    function loadDrawings(id, key) {
        try {
            const raw = localStorage.getItem('vxd:' + key);
            if (!raw) return;
            const st = state(id);
            const data = JSON.parse(raw);
            // Avoid duplicates on repeated calls
            const existing = new Set(st.drawings.map(d =>
                `${d.type}:${d.price1}:${d.time1}`));
            for (const d of data) {
                const k = `${d.type}:${d.price1}:${d.time1}`;
                if (!existing.has(k)) {
                    st.drawings.push({ ...d, id: st._nextId++ });
                    existing.add(k);
                }
            }
            redraw(id);
            updateBadge(id);
        } catch {}
    }

    // ── Public API ────────────────────────────────────────────────
    window.chartDrawingTools = {

        init(id) {
            ensureSvg(id);
            injectToolbar(id);
            attachListeners(id);
            subscribeChartEvents(id);
            const el = document.getElementById(id);
            if (el && !el.hasAttribute('tabindex')) el.setAttribute('tabindex', '0');
        },

        setActiveTool(id, tool) {
            const st = state(id);
            cancelDraft(id);
            st.activeTool = tool || T_NONE;
            st.selected   = null;
            updateToolbarState(id);
            setCursor(id, tool ? 'crosshair' : '');
            redraw(id);
        },

        getActiveTool(id) { return state(id).activeTool; },

        clearAll(id) {
            const st = state(id);
            st.drawings  = [];
            st.draft     = null;
            st.selected  = null;
            st.hovered   = null;
            redraw(id);
            updateBadge(id);
        },

        saveDrawings,
        loadDrawings,

        redraw(id) { redraw(id); },

        dispose(id) {
            const st = state(id);
            if (st._ac) st._ac.abort();
            const c = cs(id);
            if (c) {
                try { c.chart.timeScale().unsubscribeVisibleLogicalRangeChange(st.rangeSub); } catch {}
                try { c.chart.unsubscribeCrosshairMove(st.crossSub); } catch {}
            }
            if (st.svg?.parentNode) st.svg.remove();
            if (st.toolbar?.parentNode) st.toolbar.remove();
            S.delete(id);
        },
    };

})();
