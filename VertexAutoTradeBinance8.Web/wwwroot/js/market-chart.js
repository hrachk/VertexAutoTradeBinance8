// ============================================================
// VERTEX TRADING CHART — powered by TradingView Lightweight Charts™ v5
// ============================================================
// Replaces the previous hand-rolled canvas-drawing engine entirely.
// That engine required hundreds of lines to reimplement candle
// rendering, EMA/BB/VWAP overlays, RSI/Volume sub-panes, zoom/pan,
// resize handling, and DPR scaling by hand — and was the source of
// a multi-day bug where the chart would go blank after SPA navigation
// because Blazor's component-reuse behavior conflicted with several
// layers of manual canvas-reference bookkeeping.
//
// Lightweight Charts is a battle-tested, professional financial
// charting library (Apache 2.0, maintained by TradingView, used by
// thousands of real trading platforms) that handles all of the above
// internally — including safely tearing down and recreating itself
// when called again against a container, which is exactly the
// "component re-mounted, DOM might be stale" scenario that broke the
// old engine repeatedly.
'use strict';

(function () {

    // One chart "session" per container id. Keyed by container id so
    // that if init() is called again with the SAME container id (the
    // component remounted but reused the same DOM), we cleanly dispose
    // the old chart instance first — Lightweight Charts' own .remove()
    // is designed for exactly this and leaves no dangling state behind,
    // unlike the previous module-level MC/RC/VC approach.
    const sessions = new Map();

    function disposeSession(containerId) {
        const s = sessions.get(containerId);
        if (!s) return;
        if (s.abortController) { try { s.abortController.abort(); } catch (e) {} }
        if (s.previewBox && s.previewBox.parentNode) s.previewBox.remove();
        if (s.previewVLine && s.previewVLine.parentNode) s.previewVLine.remove();
        if (s.tooltipEl && s.tooltipEl.parentNode) s.tooltipEl.remove();
        if (s.pnlLabelEl && s.pnlLabelEl.parentNode) s.pnlLabelEl.remove();
        if (s.entryBtnTp && s.entryBtnTp.parentNode) s.entryBtnTp.remove();
        if (s.entryBtnSl && s.entryBtnSl.parentNode) s.entryBtnSl.remove();
        if (s.slPill && s.slPill.parentNode) s.slPill.remove();
        if (s.entryPill && s.entryPill.parentNode) s.entryPill.remove();
        if (s.liqPill && s.liqPill.parentNode) s.liqPill.remove();
        if (s.bePill && s.bePill.parentNode) s.bePill.remove();
        for (const pill of (s.tpPills || [])) { if (pill && pill.parentNode) pill.remove(); }
        // Clean up ResizeObserver created in showTpSlLines —
        // must disconnect even if disposeSession runs before hidePositionLines
        if (s._pillResizeObs) { try { s._pillResizeObs.disconnect(); } catch(e) {} s._pillResizeObs = null; }
        // Clean up order pill range subscription
        if (s._orderPillRangeSub) {
            try { s.chart.timeScale().unsubscribeVisibleLogicalRangeChange(s._orderPillRangeSub); } catch(e) {}
            try { s.chart.priceScale('right').unsubscribePriceRangeChange(s._orderPillRangeSub); } catch(e) {}
            s._orderPillRangeSub = null;
        }
        // Clean up tpSlPriceScaleSub if not already cleaned by hideTpSlLines
        if (s.tpSlPriceScaleSub) {
            try { s.chart.priceScale('right').unsubscribePriceRangeChange(s.tpSlPriceScaleSub); } catch(e) {}
            s.tpSlPriceScaleSub = null;
        }
        try { s.chart.remove(); } catch (e) { /* already gone */ }
        sessions.delete(containerId);
    }

    function fmtPrice(p) {
        return p.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
    }

    // Computes the right { precision, minMove } for Lightweight Charts'
    // priceFormat based on price magnitude — mirrors the same scaling
    // already used for text price displays elsewhere on this page
    // (MarketSnapshot.razor's FmtP), so the chart's own price-axis
    // labels show consistent precision with the rest of the UI for the
    // same asset, instead of the library's fixed default (2 decimals)
    // which silently rounds cheap-coin prices into uselessness.
    function priceFormatFor(price) {
        const p = Math.abs(price);
        if (p === 0) return { precision: 2, minMove: 0.01 };
        if (p >= 1000) return { precision: 2, minMove: 0.01 };
        if (p >= 1)    return { precision: 4, minMove: 0.0001 };
        if (p >= 0.01) return { precision: 5, minMove: 0.00001 };
        if (p >= 0.0001) return { precision: 6, minMove: 0.000001 };
        // Genuinely cheap coins (sub-$0.0001) — count leading zeros after
        // the decimal point and show 5 significant digits past them,
        // capped at 12 total decimals (Lightweight Charts' own practical
        // ceiling for priceFormat precision).
        const str = p.toFixed(12);
        const afterDot = str.slice(str.indexOf('.') + 1);
        let leadingZeros = 0;
        for (const ch of afterDot) { if (ch === '0') leadingZeros++; else break; }
        const precision = Math.min(12, leadingZeros + 5);
        return { precision, minMove: Math.pow(10, -precision) };
    }

    function toCandle(k) {
        return { time: Math.floor(k.openTime / 1000), open: k.open, high: k.high, low: k.low, close: k.close };
    }
    function toVolume(k, upColor, downColor) {
        return { time: Math.floor(k.openTime / 1000), value: k.volume, color: k.close >= k.open ? upColor : downColor };
    }

    function ema(values, period) {
        const out = new Array(values.length).fill(null);
        if (values.length === 0) return out;
        const k = 2 / (period + 1);
        let prev = values[0];
        out[0] = prev;
        for (let i = 1; i < values.length; i++) {
            prev = values[i] * k + prev * (1 - k);
            out[i] = prev;
        }
        return out;
    }

    function rsi(closes, period = 14) {
        const out = new Array(closes.length).fill(null);
        if (closes.length < period + 1) return out;
        let gainSum = 0, lossSum = 0;
        for (let i = 1; i <= period; i++) {
            const diff = closes[i] - closes[i - 1];
            if (diff >= 0) gainSum += diff; else lossSum -= diff;
        }
        let avgGain = gainSum / period, avgLoss = lossSum / period;
        out[period] = avgLoss === 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        for (let i = period + 1; i < closes.length; i++) {
            const diff = closes[i] - closes[i - 1];
            const gain = diff > 0 ? diff : 0;
            const loss = diff < 0 ? -diff : 0;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
            out[i] = avgLoss === 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        }
        return out;
    }

    window.marketChart = {
        // Saves the chart's current visible range (which bars are on
        // screen, at what zoom level) to sessionStorage, keyed by
        // symbol+timeframe so switching between different charts
        // doesn't clobber each other's saved scroll position. Per
        // direct request to remember exactly where the chart was
        // scrolled to across page navigations within the same browser
        // tab - uses the same getVisibleLogicalRange primitive already
        // proven to work elsewhere in this file (the infinite-history
        // lazy-load feature).
        saveScrollPosition(containerId, key) {
            const s = sessions.get(containerId);
            if (!s || !s.chart) return;
            try {
                const range = s.chart.timeScale().getVisibleLogicalRange();
                if (!range) return;
                localStorage.setItem('vertexChartScroll:' + key, JSON.stringify({ from: range.from, to: range.to }));
            } catch (e) {}
        },

        // Restores a previously-saved visible range, if one exists for
        // this symbol+timeframe key. Safe to call even if the chart
        // doesn't have enough bars loaded yet for the saved range to
        // make sense - setVisibleLogicalRange clamps to whatever data
        // actually exists rather than erroring out.
        restoreScrollPosition(containerId, key) {
            const s = sessions.get(containerId);
            if (!s || !s.chart) return false;
            try {
                const raw = localStorage.getItem('vertexChartScroll:' + key);
                if (!raw) return false;
                const saved = JSON.parse(raw);
                if (typeof saved.from !== 'number' || typeof saved.to !== 'number') return false;
                s.chart.timeScale().setVisibleLogicalRange(saved);
                return true;
            } catch (e) {
                return false;
            }
        },

        // Creates (or re-creates) a full chart session bound to
        // containerId. Safe to call multiple times against the same id
        // — any previous session for that id is torn down first via
        // disposeSession, so there is never more than one live chart
        // instance per container, regardless of how many times Blazor
        // calls this (every fresh page visit, every reconnect, etc).
        init(containerId) {
            disposeSession(containerId);

            const container = document.getElementById(containerId);
            if (!container) return false;

            const colors = {
                bg: '#0a0d12', text: '#94a3b8', grid: '#1a1f2e',
                up: '#22c55e', down: '#ef4444',
                ema21: '#3b82f6', ema55: '#a855f7',
                rsi: '#3b82f6', rsiOb: '#ef4444', rsiOs: '#22c55e',
            };

            const chart = LightweightCharts.createChart(container, {
                layout: {
                    background: { type: 'solid', color: colors.bg },
                    textColor: colors.text,
                    panes: { separatorColor: colors.grid, separatorHoverColor: 'rgba(59,130,246,0.15)' },
                },
                grid: {
                    vertLines: { color: colors.grid },
                    horzLines: { color: colors.grid },
                },
                rightPriceScale: { borderColor: colors.grid },
                timeScale: { borderColor: colors.grid, timeVisible: true, secondsVisible: false },
                crosshair: {
                    mode: LightweightCharts.CrosshairMode.Normal,
                    vertLine: { width: 1, color: 'rgba(148,163,184,0.4)', style: LightweightCharts.LineStyle.Dashed },
                    horzLine: { width: 1, color: 'rgba(148,163,184,0.4)', style: LightweightCharts.LineStyle.Dashed },
                },
                autoSize: true,
            });

            const candleSeries = chart.addSeries(LightweightCharts.CandlestickSeries, {
                upColor: colors.up, downColor: colors.down,
                borderVisible: false,
                wickUpColor: colors.up, wickDownColor: colors.down,
            }, 0);

            const ema21Series = chart.addSeries(LightweightCharts.LineSeries, {
                color: colors.ema21, lineWidth: 1, priceLineVisible: false, lastValueVisible: false,
            }, 0);
            const ema55Series = chart.addSeries(LightweightCharts.LineSeries, {
                color: colors.ema55, lineWidth: 1, priceLineVisible: false, lastValueVisible: false,
            }, 0);

            const volumeSeries = chart.addSeries(LightweightCharts.HistogramSeries, {
                priceFormat: { type: 'volume' }, priceLineVisible: false, lastValueVisible: false,
            }, 1);

            const rsiSeries = chart.addSeries(LightweightCharts.LineSeries, {
                color: colors.rsi, lineWidth: 1.5, priceLineVisible: false, lastValueVisible: false,
            }, 2);
            const rsiObLine = chart.addSeries(LightweightCharts.LineSeries, {
                color: colors.rsiOb, lineWidth: 1, lineStyle: LightweightCharts.LineStyle.Dotted,
                priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false,
            }, 2);
            const rsiOsLine = chart.addSeries(LightweightCharts.LineSeries, {
                color: colors.rsiOs, lineWidth: 1, lineStyle: LightweightCharts.LineStyle.Dotted,
                priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false,
            }, 2);

            try {
                const panes = chart.panes();
                if (panes[0]) panes[0].setHeight(300);
                if (panes[1]) panes[1].setHeight(90);
                if (panes[2]) panes[2].setHeight(90);
            } catch (e) { /* older/edge versions may not support setHeight yet */ }

            // ── Candle tooltip ──────────────────────────────────────
            // Lightweight Charts has no built-in tooltip — this is the
            // library's own documented pattern: subscribeCrosshairMove
            // gives the hovered bar's data, and a plain HTML element
            // absolutely positioned over the container does the rest.
            const tooltip = document.createElement('div');
            tooltip.style.position = 'absolute';
            tooltip.style.display = 'none';
            tooltip.style.padding = '8px 12px';
            tooltip.style.borderRadius = '6px';
            tooltip.style.background = 'rgba(10,13,18,0.95)';
            tooltip.style.border = '1px solid rgba(255,255,255,0.1)';
            tooltip.style.color = '#e2e8f0';
            tooltip.style.fontSize = '11.5px';
            tooltip.style.fontFamily = 'monospace';
            tooltip.style.pointerEvents = 'none';
            tooltip.style.zIndex = '7';
            tooltip.style.whiteSpace = 'nowrap';
            tooltip.style.lineHeight = '1.5';
            container.style.position = container.style.position || 'relative';
            container.appendChild(tooltip);

            function fmtVol(v) {
                if (v >= 1_000_000) return (v / 1_000_000).toFixed(2) + 'M';
                if (v >= 1_000) return (v / 1_000).toFixed(2) + 'K';
                return v.toFixed(2);
            }

            let _lastTooltipX = -999, _lastTooltipTime = 0;
            chart.subscribeCrosshairMove((param) => {
                if (!param.point || !param.time || param.point.y < 0) {
                    tooltip.style.display = 'none';
                    _lastTooltipX = -999;
                    return;
                }
                // Skip re-render if same candle as last time
                if (param.time === _lastTooltipTime && Math.abs(param.point.x - _lastTooltipX) < 2) return;
                _lastTooltipTime = param.time;
                _lastTooltipX = param.point.x;
                const candleData = param.seriesData.get(candleSeries);
                const volData = param.seriesData.get(volumeSeries);
                if (!candleData) {
                    tooltip.style.display = 'none';
                    return;
                }

                const { open, high, low, close } = candleData;
                const chg = open !== 0 ? ((close - open) / open * 100) : 0;
                const chgColor = chg >= 0 ? colors.up : colors.down;
                const vol = volData ? volData.value : 0;

                // Full date/time for the hovered bar — param.time is a
                // UTCTimestamp (unix seconds) for time-based series.
                const barDate = new Date(param.time * 1000);
                const dateStr = barDate.toLocaleString(undefined, {
                    year: 'numeric', month: 'short', day: '2-digit',
                    hour: '2-digit', minute: '2-digit', hour12: false
                });

                // Buy/sell volume split — Binance's own kline data already
                // includes taker-buy volume directly (no extra API call
                // needed); sell-side is simply the remainder. Only shown
                // when this specific bar actually has the field (older
                // archived/snapshot data before this field existed won't).
                let volSplitHtml = '';
                const raw = session.rawKlineByTime && session.rawKlineByTime.get(param.time);
                if (raw && raw.takerBuyVolume != null && raw.takerBuyVolume >= 0) {
                    const buyVol = raw.takerBuyVolume;
                    const sellVol = Math.max(0, vol - buyVol);
                    volSplitHtml = ` <span style="color:${colors.up}">▲${fmtVol(buyVol)}</span>` +
                                   ` <span style="color:${colors.down}">▼${fmtVol(sellVol)}</span>`;
                }

                tooltip.innerHTML =
                    `<div style="color:#94a3b8;font-size:10.5px;margin-bottom:4px;">${dateStr}</div>` +
                    `<div style="display:flex;gap:10px;margin-bottom:4px;">` +
                    `<span>O <b style="color:${colors.text}">${fmtPrice(open)}</b></span>` +
                    `<span>H <b style="color:${colors.up}">${fmtPrice(high)}</b></span>` +
                    `<span>L <b style="color:${colors.down}">${fmtPrice(low)}</b></span>` +
                    `<span>C <b style="color:${chgColor}">${fmtPrice(close)}</b></span>` +
                    `<span style="color:${chgColor}">${chg >= 0 ? '+' : ''}${chg.toFixed(2)}%</span>` +
                    `</div>` +
                    `<div style="font-size:13px;font-weight:700;color:#eab308;">VOL ${fmtVol(vol)}${volSplitHtml}</div>`;

                tooltip.style.display = 'block';
                const rect = container.getBoundingClientRect();
                let left = param.point.x + 16;
                if (left + 220 > rect.width) left = param.point.x - 220;
                tooltip.style.left = left + 'px';
                tooltip.style.top = '8px';
            });

            const session = {
                chart, candleSeries, ema21Series, ema55Series,
                volumeSeries, rsiSeries, rsiObLine, rsiOsLine,
                priceLine: null, onPricePicked: null, tooltipEl: tooltip,
                // Bybit-style draggable position lines (entry/SL/TP).
                // entryLine is informational only (not draggable —
                // entry price of an already-open position can't be
                // changed), but its title is refreshed continuously
                // with live PnL via updatePnl(). slLine/tpLine ARE
                // draggable, and ALWAYS exist once a position is
                // selected — even with no real order behind them yet
                // (placeholder position above/below entry) — because
                // there has to be something visible to grab in order
                // to create a TP/SL that doesn't exist yet, not just
                // to move one that already does.
                entryLine: null, slLine: null, tpLines: [], liqLine: null, beLine: null,
                draggingLine: null, draggingLineKind: null, draggingLineIdx: null,
                entryPrice: 0, side: 'LONG', qty: 0,
                onSlChanged: null, onTpChanged: null,
                tpSlArmed: false,   // true after clicking the TP/SL button — "Drag to set TP/SL" mode
                dragging: false,
                previewLine: null, previewBox: null, previewVLine: null,
            };
            sessions.set(containerId, session);

            // CRITICAL FIX for a confirmed leak: the event listeners
            // attached to `container` below persisted across repeated
            // init() calls on the same containerId — disposeSession
            // correctly tore down the chart instance and its own DOM
            // elements, but never removed these, since the container
            // element itself isn't recreated on re-init. Each re-init
            // (the comment above already flagged this as a known
            // possibility - "however many times Blazor calls this")
            // silently added one more full set of mousedown/mousemove/
            // mouseup/mouseleave/contextmenu handlers on top of the
            // previous ones, making every subsequent mouse interaction
            // on the chart progressively more expensive — likely a
            // significant contributor to the reported "page gets
            // heavier over time" issue. One AbortController whose
            // signal is passed to every addEventListener call below
            // lets disposeSession remove the entire set in one call.
            const abortController = new AbortController();
            session.abortController = abortController;
            const listenerOpts = { signal: abortController.signal };

            // ── Bybit-style drag-to-set TP/SL ───────────────────────
            // Lightweight Charts v5's core API does not expose a
            // built-in draggable price line (open feature request,
            // github.com/tradingview/lightweight-charts/issues/1086),
            // so this reimplements the exact interaction observed on
            // Bybit's own chart: after arming TP/SL mode (the small
            // toggle button on the entry line), pressing anywhere on
            // the chart and dragging shows a live "Expected Profit/
            // Loss" preview with a vertical connector back to the
            // entry price — exactly matching Bybit's UI — and releases
            // into a real TP (if dragged above entry for a long) or SL
            // (if below), rather than requiring the user to grab one
            // specific pre-existing line.
            function fmt(p) { return fmtPrice(p); }

            function removePreview() {
                if (session.previewLine) { try { candleSeries.removePriceLine(session.previewLine); } catch (e) {} session.previewLine = null; }
                if (session.previewBox && session.previewBox.parentNode) session.previewBox.remove();
                session.previewBox = null;
                if (session.previewVLine && session.previewVLine.parentNode) session.previewVLine.remove();
                session.previewVLine = null;
            }

            function ensurePreviewBox() {
                if (session.previewBox) return session.previewBox;
                const box = document.createElement('div');
                box.style.position = 'absolute';
                box.style.padding = '4px 10px';
                box.style.borderRadius = '4px';
                box.style.fontSize = '11px';
                box.style.fontWeight = '600';
                box.style.fontFamily = 'monospace';
                box.style.pointerEvents = 'none';
                box.style.zIndex = '6';
                box.style.whiteSpace = 'nowrap';
                container.style.position = container.style.position || 'relative';
                container.appendChild(box);
                session.previewBox = box;
                return box;
            }

            function ensurePreviewVLine(x) {
                if (session.previewVLine) return session.previewVLine;
                const vline = document.createElement('div');
                vline.style.position = 'absolute';
                vline.style.width = '1px';
                vline.style.pointerEvents = 'none';
                vline.style.zIndex = '5';
                container.appendChild(vline);
                session.previewVLine = vline;
                return vline;
            }

            const NEAR_LINE_PX = 18; // wider grab zone — 10px was too tight

            function nearEntryLine(y) {
                if (!session.entryPrice) return false;
                const entryY = candleSeries.priceToCoordinate(session.entryPrice);
                return entryY != null && Math.abs(y - entryY) <= NEAR_LINE_PX;
            }

            // Checks proximity to any existing SL/TP line — returns a
            // descriptor for the closest match within range, or null.
            // Checked BEFORE the entry-line armed-mode logic so
            // dragging an EXISTING line moves that specific line
            // directly, rather than triggering the "drag anywhere
            // creates a new TP/SL" armed gesture meant for when no
            // line exists there yet.
            function findNearbyDraggableLine(y) {
                if (session.slLine) {
                    const slY = candleSeries.priceToCoordinate(session.slLine.options().price);
                    if (slY != null && Math.abs(y - slY) <= NEAR_LINE_PX) {
                        return { kind: 'sl', line: session.slLine, index: null };
                    }
                }
                for (const tp of (session.tpLines || [])) {
                    const tpY = candleSeries.priceToCoordinate(tp.price);
                    if (tpY != null && Math.abs(y - tpY) <= NEAR_LINE_PX) {
                        return { kind: 'tp', line: tp.line, index: tp.index };
                    }
                }
                return null;
            }

            // Shared drag-start logic (mouse and touch)
            function startLineDrag(clientY, e) {
                if (!session.entryPrice) return false;
                const rect = container.getBoundingClientRect();
                const y = clientY - rect.top;
                const nearby = findNearbyDraggableLine(y);
                if (nearby) {
                    session.draggingLine = nearby.line;
                    session.draggingLineKind = nearby.kind;
                    session.draggingLineIdx = nearby.index;
                    container.style.cursor = 'grabbing';
                    if (e) e.preventDefault();
                    return true;
                }
                return false;
            }

            container.addEventListener('mousedown', (e) => {
                const session = sessions.get(containerId) || {};
                if (!session.entryPrice) return;
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;

                // Existing SL/TP line under the cursor — drag THAT
                // specific line directly, taking priority over the
                // entry-armed "create new TP/SL" gesture below.
                const nearby = findNearbyDraggableLine(y);
                if (nearby) {
                    session.draggingLine    = nearby.line;
                    session.draggingLineKind = nearby.kind;
                    session.draggingLineIdx  = nearby.index;
                    // Save original price BEFORE drag starts — C# needs
                    // this to find the existing order on the exchange
                    // (which is still at the old price until we cancel it).
                    session.draggingLineOriginalPrice = nearby.line.options().price;
                    container.style.cursor = 'grabbing';
                    e.preventDefault();
                    return;
                }

                if (!nearEntryLine(y)) return; // let normal chart pan/zoom/crosshair through elsewhere
                session.dragging = true;
                container.style.cursor = 'grabbing';
                e.preventDefault();
            }, listenerOpts);

            container.addEventListener('mousemove', (e) => {
                const session = sessions.get(containerId) || {};
                if (!session.entryPrice) return;

                // Actively dragging an existing SL/TP line — just move
                // that one line's price as the mouse moves, with a live
                // price preview in its title; the actual order update
                // (algo order replace) happens on mouseup, not on every
                // intermediate move (that would hammer the API).
                if (session.draggingLine) {
                    const rect = container.getBoundingClientRect();
                    const y = e.clientY - rect.top;
                    const price = candleSeries.coordinateToPrice(y);
                    if (price != null) {
                        const kind = session.draggingLineKind === 'sl' ? 'SL' :
                            (session.tpLines && session.tpLines.length > 1
                                ? `TP${session.draggingLineIdx + 1}` : 'TP');
                        try { session.draggingLine.applyOptions({ price, title: `${kind} ${fmtPrice(price)}` }); } catch (err) {}
                        if (session.draggingLineKind === 'tp' && session.tpLines) {
                            const tp = session.tpLines.find(t => t.index === session.draggingLineIdx);
                            if (tp) tp.price = price;
                        }
                        // Instantly reposition the pill for this line (no subscription delay)
                        // We call repositionAllPills directly so the pill tracks the cursor.
                        const pnlFor = session._lastPnlFor || null;
                        this.repositionAllPills(containerId, pnlFor);
                    }
                    container.style.cursor = 'grabbing';
                    return;
                }

                if (!session.dragging) {
                    const rect = container.getBoundingClientRect();
                    const y = e.clientY - rect.top;
                    const nearby = findNearbyDraggableLine(y);
                    container.style.cursor = nearby ? 'grab' : (nearEntryLine(y) ? 'grab' : 'crosshair');
                    return;
                }

                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                const x = e.clientX - rect.left;
                const price = candleSeries.coordinateToPrice(y);
                if (price == null || !session.entryPrice) return;

                const isLong = session.side === 'LONG';
                // Which side of entry decides TP vs SL, same as a
                // single unified drag gesture on Bybit: above entry for
                // a long (or below for a short) = profit direction = TP;
                // the opposite = loss direction = SL.
                const isProfitSide = isLong ? price > session.entryPrice : price < session.entryPrice;
                const dir = isLong ? 1 : -1;
                const pnl = (price - session.entryPrice) * dir * session.qty;

                if (session.previewLine) { try { candleSeries.removePriceLine(session.previewLine); } catch (err) {} }
                session.previewLine = candleSeries.createPriceLine({
                    price, color: isProfitSide ? '#22c55e' : '#ef4444', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dashed,
                    axisLabelVisible: true,
                    title: `${isProfitSide ? 'TP' : 'SL'} ${fmt(price)}`,
                });

                const entryY = candleSeries.priceToCoordinate(session.entryPrice);
                const vline = ensurePreviewVLine(x);
                if (entryY != null) {
                    const top = Math.min(entryY, y);
                    const height = Math.abs(entryY - y);
                    vline.style.left = x + 'px';
                    vline.style.top = top + 'px';
                    vline.style.height = height + 'px';
                    vline.style.background = isProfitSide ? 'rgba(34,197,94,0.5)' : 'rgba(239,68,68,0.5)';
                }

                const box = ensurePreviewBox();
                box.style.left = (x + 10) + 'px';
                box.style.top = (Math.min(entryY ?? y, y) - 10) + 'px';
                box.style.background = isProfitSide ? 'rgba(34,197,94,0.15)' : 'rgba(239,68,68,0.15)';
                box.style.border = `1px solid ${isProfitSide ? '#22c55e' : '#ef4444'}`;
                box.style.color = isProfitSide ? '#22c55e' : '#ef4444';
                box.textContent = `Expected ${isProfitSide ? 'Profit' : 'Loss'} ${pnl >= 0 ? '+' : ''}${pnl.toFixed(4)}`;
            }, listenerOpts);

            container.addEventListener('mouseup', (e) => {
                const session = sessions.get(containerId) || {};
                // Finished dragging an EXISTING SL/TP line to a new
                // price — commit it via a dedicated callback (distinct
                // from the entry-armed gesture's onNewTpRequested,
                // since this needs to know WHICH TP index moved when
                // there are multiple).
                if (session.draggingLine) {
                    // Use the line's own committed price (from applyOptions during
                    // mousemove) rather than coordinateToPrice(mouseup Y) — avoids
                    // a 1-pixel slip if mouse moved between last move and up event.
                    const committedPrice = session.draggingLine.options().price;
                    const kind = session.draggingLineKind;
                    const idx  = session.draggingLineIdx;
                    session.draggingLine = null;
                    session.draggingLineKind = null;
                    session.draggingLineIdx = null;
                    container.style.cursor = 'crosshair';
                    // Freeze hideTpSlLines for 8s after drag completes.
                    // C# will cancel old order, place new one, then call
                    // showTpSlLines. Without this freeze, showTpSlLines calls
                    // hideTpSlLines first (to reset), which DELETES the line
                    // the user just moved — making it look like drag failed.
                    session._tpSlHideFreezeUntil = Date.now() + 30000; // 30s — cleared by next showTpSlLines call
                    // Apply any showTpSlLines call that was deferred during drag
                    if (session._pendingTpSlArgs) {
                        const p = session._pendingTpSlArgs;
                        session._pendingTpSlArgs = null;
                        // Small delay so the visual line stays at drag position
                        // briefly while C# places the new order (avoid flicker)
                        // Lift freeze so the deferred showTpSlLines can redraw
                        setTimeout(() => {
                            if (session._tpSlHideFreezeUntil) session._tpSlHideFreezeUntil = 0;
                            this.showTpSlLines(containerId, p.entry, p.sl, p.tps, p.side);
                        }, 800);
                    }
                    if (committedPrice != null && committedPrice > 0) {
                        if (kind === 'sl') {
                            const origPriceSl = session.draggingLineOriginalPrice || committedPrice;
                            // Restore line to original price if committed price is
                            // invalid — prevents ghost line at wrong position
                            if (origPriceSl > 0 && session.slLine) {
                                try { session.slLine.applyOptions({ price: committedPrice }); } catch(e) {}
                            }
                            if (session.onSlChanged) session.onSlChanged(committedPrice, origPriceSl);
                        } else if (kind === 'tp') {
                            // Pass BOTH new price AND original price so C# can find
                            // the existing order (still at old price on exchange)
                            const origPrice = session.draggingLineOriginalPrice || committedPrice;
                            if (session.onTpChangedAt) session.onTpChangedAt(idx, committedPrice, origPrice);
                        }
                    } else {
                        // Price is null or <=0 (mouse went outside chart area).
                        // Snap the line back to its original price — don't fire the callback.
                        const origPrice = session.draggingLineOriginalPrice;
                        if (origPrice > 0) {
                            const restoredLine = kind === 'sl' ? session.slLine
                                : (session.tpLines || []).find(t => t.index === idx)?.line;
                            if (restoredLine) {
                                try { restoredLine.applyOptions({ price: origPrice }); } catch(e) {}
                            }
                        }
                    }
                    session.draggingLineOriginalPrice = null;
                    return;
                }

                if (!session.entryPrice || !session.dragging) return;
                session.dragging = false;
                container.style.cursor = 'crosshair';

                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                const price = candleSeries.coordinateToPrice(y);
                removePreview();
                if (price == null || !session.entryPrice) return;

                const isLong = session.side === 'LONG';
                const isProfitSide = isLong ? price > session.entryPrice : price < session.entryPrice;

                if (isProfitSide) {
                    if (session.onNewTpRequested) session.onNewTpRequested(price);
                } else {
                    if (session.onSlChanged) session.onSlChanged(price);
                }
            }, listenerOpts);

            container.addEventListener('mouseleave', () => {
                const session = sessions.get(containerId) || {};
                if (session.dragging) { session.dragging = false; removePreview(); }
                if (session.draggingLine) {
                    // Cursor left chart while dragging — commit the last known
                    // price instead of silently discarding. Previously this caused
                    // the visual line to snap back but the order to stay unchanged,
                    // making the user think the drag "failed" for no reason.
                    const committedPrice = session.draggingLine.options().price;
                    const kind = session.draggingLineKind;
                    const idx  = session.draggingLineIdx;
                    session.draggingLine = null;
                    session.draggingLineKind = null;
                    session.draggingLineIdx = null;
                    container.style.cursor = 'crosshair';
                    if (committedPrice != null && committedPrice > 0) {
                        if (kind === 'sl') {
                            if (session.onSlChanged) session.onSlChanged(committedPrice);
                        } else if (kind === 'tp') {
                            if (session.onTpChangedAt) session.onTpChangedAt(idx, committedPrice);
                        }
                    }
                }
            }, listenerOpts);

            // Touch drag for SL/TP lines
            container.addEventListener('touchstart', (e) => {
                if (!e.touches.length) return;
                const touch = e.touches[0];
                if (startLineDrag(touch.clientY, e)) {
                    e.stopPropagation(); // prevent chart pan when dragging a line
                }
            }, { ...listenerOpts, passive: false });

            container.addEventListener('touchmove', (e) => {
                if (!session.draggingLine || !e.touches.length) return;
                e.preventDefault();
                const touch = e.touches[0];
                const rect = container.getBoundingClientRect();
                const y = touch.clientY - rect.top;
                const price = candleSeries.coordinateToPrice(y);
                if (price != null) {
                    const kind = session.draggingLineKind === 'sl' ? 'SL' :
                        (session.tpLines.length > 1 ? `TP${session.draggingLineIdx + 1}` : 'TP');
                    try { session.draggingLine.applyOptions({ price, title: `${kind} ${fmtPrice(price)}` }); } catch(err) {}
                    if (session.draggingLineKind === 'tp') {
                        const tp = session.tpLines.find(t => t.index === session.draggingLineIdx);
                        if (tp) tp.price = price;
                    }
                }
            }, { ...listenerOpts, passive: false });

            container.addEventListener('touchend', (e) => {
                if (!session.draggingLine) return;
                const committedPrice = session.draggingLine.options().price;
                const kind = session.draggingLineKind;
                const idx  = session.draggingLineIdx;
                session.draggingLine = null;
                session.draggingLineKind = null;
                session.draggingLineIdx = null;
                container.style.cursor = 'crosshair';
                if (committedPrice != null && committedPrice > 0) {
                    if (kind === 'sl' && session.onSlChanged) session.onSlChanged(committedPrice);
                    else if (kind === 'tp' && session.onTpChangedAt) session.onTpChangedAt(idx, committedPrice);
                }
            }, listenerOpts);

            container.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                const price = candleSeries.coordinateToPrice(y);
                if (price == null || price <= 0) return;

                const liveSession = sessions.get(containerId);
                if (!liveSession) return;

                // ── FIX 1 & 2: Always call OnPricePicked first ─────────────
                // This fills the Limit Price field in the right panel regardless
                // of whether a position is open. The panel switches to Limit mode
                // automatically. This is the primary, reliable path.
                if (liveSession.onPricePicked) {
                    try { liveSession.onPricePicked(price); } catch(ex) {
                        console.warn('[VERTEX] onPricePicked failed:', ex);
                    }
                }

                // ── Show context menu ────────────────────────────────────────
                if (!liveSession.entryPrice) {
                    // No position — show quick Limit Order popup
                    const freshSession = sessions.get(containerId) || liveSession;
                    marketChart._showLimitOrderMenu(containerId, e.clientX, e.clientY, price, freshSession);
                    return;
                }

                // Has position — show TP/SL menu (price vs entry determines side)
                const isLong = liveSession.side === 'LONG';
                const isTpSide = isLong
                    ? price > liveSession.entryPrice
                    : price < liveSession.entryPrice;
                marketChart._showRightClickMenu(containerId, e.clientX, e.clientY, price, isTpSide);
            }, listenerOpts);

            return true;
        },

        // ─── LIMIT ORDER MENU ────────────────────────────────────────
        // Right-click when no position is selected → shows limit order menu.
        // User picks: BUY LIMIT or SELL LIMIT at the clicked price.
        // Optionally set a quantity. Calls onLimitOrderRequested(side, price, qty).
        _showLimitOrderMenu(containerId, clientX, clientY, price, liveSession) {
            this._removeRightClickMenu();
            this._removeLimitOrderMenu();

            const fmt = (p) => p < 0.0001 ? p.toFixed(7)
                              : p < 0.01   ? p.toFixed(6)
                              : p < 1      ? p.toFixed(5)
                              : p < 100    ? p.toFixed(4)
                              :              p.toFixed(2);

            const menu = document.createElement('div');
            menu.id = '__vx_limit_menu';
            menu.style.cssText = `
                position:fixed; z-index:99999;
                left:${clientX}px; top:${clientY}px;
                background:#0a1422; border:1px solid #1e3050;
                border-radius:8px; box-shadow:0 8px 32px rgba(0,0,0,.7);
                min-width:230px; overflow:hidden;
                font-family:'Inter',system-ui,sans-serif;
                animation:vxMenuIn .1s ease;
            `;

            const vw = window.innerWidth, vh = window.innerHeight;
            if (clientX + 250 > vw) menu.style.left = (clientX - 250) + 'px';
            if (clientY + 200 > vh) menu.style.top  = (clientY - 200) + 'px';

            menu.innerHTML = `
                <style>
                #__vx_limit_menu * { box-sizing:border-box; }
                #__vx_limit_menu input::-webkit-inner-spin-button { display:none; }
                @keyframes vxMenuIn { from { opacity:0; transform:scale(.95) translateY(-4px); } to { opacity:1; transform:none; } }
                </style>
                <div style="padding:10px 14px 8px; border-bottom:1px solid #131f32; display:flex; align-items:center; gap:8px;">
                    <span style="font-size:15px;">📋</span>
                    <div>
                        <div style="font-size:12px; font-weight:800; color:#e8f4ff;">Place Limit Order</div>
                        <div style="font-size:10px; color:#3d5878; margin-top:1px;">Order executes when price reaches this level</div>
                    </div>
                    <button id="__vx_limit_close" style="margin-left:auto;background:transparent;border:none;color:#3d5878;font-size:16px;cursor:pointer;padding:0 2px;">×</button>
                </div>

                <!-- Price display -->
                <div style="padding:10px 14px 8px; border-bottom:1px solid #131f32;">
                    <div style="display:flex; align-items:center; justify-content:space-between; background:#0f1b30; border:1px solid #1e3050; border-radius:6px; padding:8px 12px;">
                        <div>
                            <div style="font-size:9px; font-weight:700; text-transform:uppercase; letter-spacing:.6px; color:#3d5878; margin-bottom:2px;">Limit Price</div>
                            <div style="font-family:'JetBrains Mono',monospace; font-size:16px; font-weight:700; color:#e8f4ff;" id="__vx_limit_price_display">${fmt(price)}</div>
                        </div>
                        <div style="font-size:11px; color:#3d5878; text-align:right;">
                            <div>Right-click</div>
                            <div>to change</div>
                        </div>
                    </div>
                </div>

                <!-- Qty -->
                <div style="padding:10px 14px 12px;">
                    <div style="font-size:9px; font-weight:700; text-transform:uppercase; letter-spacing:.6px; color:#3d5878; margin-bottom:6px;">Quantity (optional)</div>
                    <div style="display:flex; gap:6px; margin-bottom:10px;">
                        <div style="position:relative; flex:1;">
                            <input id="__vx_limit_qty" type="number" min="0" step="any" placeholder="auto"
                                style="width:100%; height:32px; background:#0f1b30; border:1px solid #1e3050; border-radius:6px;
                                       color:#e8f4ff; font-size:12px; font-weight:700; padding:0 10px; outline:none;
                                       font-family:'JetBrains Mono',monospace;"/>
                        </div>
                    </div>

                    <!-- Buy / Sell buttons -->
                    <div style="display:grid; grid-template-columns:1fr 1fr; gap:8px;">
                        <button id="__vx_limit_buy" style="height:36px; border-radius:6px;
                            background:rgba(34,197,94,.1); border:1.5px solid rgba(34,197,94,.35);
                            color:#22c55e; font-size:12px; font-weight:800; cursor:pointer;">
                            ↑ BUY LIMIT
                        </button>
                        <button id="__vx_limit_sell" style="height:36px; border-radius:6px;
                            background:rgba(239,68,68,.1); border:1.5px solid rgba(239,68,68,.35);
                            color:#ef4444; font-size:12px; font-weight:800; cursor:pointer;">
                            ↓ SELL LIMIT
                        </button>
                    </div>
                </div>
            `;

            document.body.appendChild(menu);
            this.__limitMenuState = { containerId, price, liveSession };

            const self = this;

            const fireOrder = (side) => {
                const st = self.__limitMenuState;
                if (!st) return;
                // Read qty BEFORE removing menu (DOM is destroyed by _removeLimitOrderMenu)
                const qtyInput = document.getElementById('__vx_limit_qty');
                const qty = qtyInput ? parseFloat(qtyInput.value) || 0 : 0;
                // Store values locally before clearing state
                const callbackFn = st.liveSession && st.liveSession.onLimitOrderRequested;
                const price      = st.price;
                self._removeLimitOrderMenu();
                if (callbackFn) {
                    try {
                        callbackFn(side, price, qty);
                    } catch (e) {
                        console.error('[VERTEX] Limit order callback failed:', e);
                    }
                } else {
                    console.warn('[VERTEX] No onLimitOrderRequested on liveSession — bindSlTpCallbacks may not have run yet');
                }
            };

            menu.querySelector('#__vx_limit_buy').addEventListener('click', () => fireOrder('BUY'));
            menu.querySelector('#__vx_limit_sell').addEventListener('click', () => fireOrder('SELL'));
            menu.querySelector('#__vx_limit_close').addEventListener('click', () => self._removeLimitOrderMenu());

            menu.addEventListener('keydown', (ev) => {
                if (ev.key === 'Escape') self._removeLimitOrderMenu();
            });

            // Auto-focus qty input
            setTimeout(() => {
                const inp = document.getElementById('__vx_limit_qty');
                if (inp) inp.focus();
            }, 50);

            // Click outside
            setTimeout(() => {
                document.addEventListener('mousedown', self._limitOutside = (ev) => {
                    if (!menu.contains(ev.target)) self._removeLimitOrderMenu();
                }, { capture: true });
            }, 100);
        },

        _removeLimitOrderMenu() {
            const el = document.getElementById('__vx_limit_menu');
            if (el) el.remove();
            this.__limitMenuState = null;
            if (this._limitOutside) {
                document.removeEventListener('mousedown', this._limitOutside, { capture: true });
                this._limitOutside = null;
            }
        },

        setData(containerId, klines) {
            const s = sessions.get(containerId);
            if (!s || !klines || !klines.length) return;

            // Dynamic price-scale precision, mirroring how a real exchange
            // shows enough decimals for the asset's actual price range —
            // the chart library's own default (precision:2, minMove:0.01)
            // is wrong for cheap coins, silently rounding e.g. 0.02018
            // down to 0.02 on the price axis labels. Based on the actual
            // minimum non-zero close price in the data being shown, not
            // a fixed assumption.
            const lastClose = klines[klines.length - 1].close;
            const { precision, minMove } = priceFormatFor(lastClose);
            s.candleSeries.applyOptions({ priceFormat: { type: 'price', precision, minMove } });

            const candles = klines.map(toCandle);
            const closes = klines.map(k => k.close);
            const ema21 = ema(closes, 21);
            const ema55 = ema(closes, 55);
            const rsiVals = rsi(closes, 14);

            s.candleSeries.setData(candles);
            s.ema21Series.setData(candles.map((c, i) => ({ time: c.time, value: ema21[i] })).filter(d => d.value != null));
            s.ema55Series.setData(candles.map((c, i) => ({ time: c.time, value: ema55[i] })).filter(d => d.value != null));
            s.volumeSeries.setData(klines.map(k => toVolume(k, 'rgba(34,197,94,0.28)', 'rgba(239,68,68,0.28)')));
            s.rsiSeries.setData(candles.map((c, i) => ({ time: c.time, value: rsiVals[i] })).filter(d => d.value != null));
            s.rsiObLine.setData(candles.map(c => ({ time: c.time, value: 70 })));
            s.rsiOsLine.setData(candles.map(c => ({ time: c.time, value: 30 })));

            // Keep the raw kline data (keyed by time) for the crosshair
            // tooltip to look up takerBuyVolume for whichever bar is
            // currently hovered — the chart's own series only carry
            // OHLCV, not this extra field.
            s.rawKlineByTime = new Map(candles.map((c, i) => [c.time, klines[i]]));
            s.lastKlinesRaw = klines;

            // Only reset the "no more history" flag when this is
            // genuinely a different series (earliest bar changed) — not
            // on every single live-tick setData call for the SAME
            // symbol+timeframe, which would otherwise let an already-
            // confirmed-exhausted lazy-load retry uselessly on the next
            // scroll near the edge.
            const newEarliestTime = candles.length > 0 ? candles[0].time : null;
            if (newEarliestTime !== s.lastSeriesEarliestTime) {
                s.historyExhausted = false;
                s.lastSeriesEarliestTime = newEarliestTime;
            }
        },

        updateLastBar(containerId, k) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.candleSeries.update(toCandle(k));
            s.volumeSeries.update(toVolume(k, 'rgba(34,197,94,0.28)', 'rgba(239,68,68,0.28)'));

            // Keep the raw-kline lookups in sync with this incremental
            // update too — without this, the tooltip/lazy-load logic
            // would see stale data for the most recent bar after a few
            // ticks (those structures are only otherwise rebuilt on a
            // full setData call, which this method exists specifically
            // to avoid doing on every single tick).
            if (s.lastKlinesRaw && s.lastKlinesRaw.length > 0) {
                const candle = toCandle(k);
                const lastIdx = s.lastKlinesRaw.length - 1;
                if (s.lastKlinesRaw[lastIdx].openTime === k.openTime) {
                    s.lastKlinesRaw[lastIdx] = k; // same bar still forming — replace
                } else {
                    s.lastKlinesRaw.push(k); // genuinely new bar closed — append
                }
                if (s.rawKlineByTime) s.rawKlineByTime.set(candle.time, k);

                // Recompute EMA21/EMA55/RSI for just the tail of the
                // series — full recalculation is cheap enough at typical
                // bar counts (hundreds to low thousands) to just redo it
                // over the whole lastKlinesRaw array each tick, rather
                // than maintaining incremental EMA/RSI state by hand.
                const closes = s.lastKlinesRaw.map(x => x.close);
                const ema21Vals = ema(closes, 21);
                const ema55Vals = ema(closes, 55);
                const rsiVals = rsi(closes, 14);
                const lastTime = candle.time;
                if (ema21Vals[lastIdx] != null) s.ema21Series.update({ time: lastTime, value: ema21Vals[lastIdx] });
                if (ema55Vals[lastIdx] != null) s.ema55Series.update({ time: lastTime, value: ema55Vals[lastIdx] });
                if (rsiVals[lastIdx] != null) {
                    s.rsiSeries.update({ time: lastTime, value: rsiVals[lastIdx] });
                    s.rsiObLine.update({ time: lastTime, value: 70 });
                    s.rsiOsLine.update({ time: lastTime, value: 30 });
                }
            }
        },

        clearPriceLine(containerId) {
            const s = sessions.get(containerId);
            if (!s || !s.priceLine) return;
            try { s.candleSeries.removePriceLine(s.priceLine); } catch (e) { }
            s.priceLine = null;
        },

        // Draws the entry line, plus liquidation and break-even
        // reference lines (Bybit-style) for the currently selected
        // position. The entry line's title is kept SHORT ('Entry
        // 61.10') — PnL used to be baked into this same title, which
        // is exactly what made it overrun the chart's right edge and
        // get visually cut off; live PnL is now shown via a separate
        // updatePnl() call that only touches the entry line's title
        // with just the PnL figure appended on each tick, kept short
        // enough to always fit.
        showPositionLines(containerId, entry, side, qty, liqPrice, breakEvenPrice) {
            const s = sessions.get(containerId);
            if (!s) return;
            this.hidePositionLines(containerId);

            s.entryPrice = entry;
            s.side = side;
            s.qty = qty;

            // The chart's own built-in last-price line would otherwise
            // sit right on top of the new PnL-tracking line at the same
            // price — disable it while a position is selected, since
            // the PnL line is a strictly more informative replacement.
            s.candleSeries.applyOptions({ priceLineVisible: false });

            if (entry > 0) {
                s.entryLine = s.candleSeries.createPriceLine({
                    price: entry, color: '#3b82f6', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dotted,
                    axisLabelVisible: false, title: '',
                });
                s.entryPill = this.makeLevelPill(containerId, '#3b82f6', 'Entry', null, null);
            }

            if (liqPrice && liqPrice > 0) {
                s.liqLine = s.candleSeries.createPriceLine({
                    price: liqPrice, color: '#f97316', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dashed,
                    axisLabelVisible: false, title: '',
                });
                s.liqPill = this.makeLevelPill(containerId, '#f97316', 'Liq', null, null);
            }

            if (breakEvenPrice && breakEvenPrice > 0) {
                s.beLine = s.candleSeries.createPriceLine({
                    price: breakEvenPrice, color: '#94a3b8', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dotted,
                    axisLabelVisible: false, title: '',
                });
                s.bePill = this.makeLevelPill(containerId, '#94a3b8', 'BE', null, null);
            }

            // Per direct confirmation: TP/SL quick-add buttons right on
            // the Entry line itself (matching the reference Bybit
            // screenshot), without Reverse. Clicking either prompts for
            // a price and adds a new protective level via the same
            // [JSInvokable] handlers the drag gesture already uses, so
            // no new C#-side plumbing is needed.
            const container = document.getElementById(containerId);
            if (container && entry > 0) {
                if (getComputedStyle(container).position === 'static') container.style.position = 'relative';

                if (!s.entryBtnTp) {
                    s.entryBtnTp = document.createElement('button');
                    s.entryBtnTp.textContent = 'TP';
                    s.entryBtnTp.style.position = 'absolute';
                    s.entryBtnTp.style.zIndex = '6';
                    s.entryBtnTp.style.padding = '2px 8px';
                    s.entryBtnTp.style.borderRadius = '4px';
                    s.entryBtnTp.style.fontSize = '10.5px';
                    s.entryBtnTp.style.fontWeight = '700';
                    s.entryBtnTp.style.border = 'none';
                    s.entryBtnTp.style.cursor = 'pointer';
                    s.entryBtnTp.style.background = '#22c55e';
                    s.entryBtnTp.style.color = '#000';
                    s.entryBtnTp.style.transform = 'translateY(-50%)';
                    s.entryBtnTp.style.boxShadow = '0 1px 4px rgba(0,0,0,0.5)';
                    s.entryBtnTp.style.letterSpacing = '0.5px';
                    s.entryBtnTp.style.transition = 'opacity .15s';
                    s.entryBtnTp.onmouseenter = () => s.entryBtnTp.style.opacity = '1';
                    s.entryBtnTp.onmouseleave = () => s.entryBtnTp.style.opacity = '0.9';
                    s.entryBtnTp.onclick = () => this.promptAddTp(containerId);
                    s.entryBtnTp.addEventListener('mousedown', (ev) => ev.stopPropagation());
                    container.appendChild(s.entryBtnTp);
                }
                if (!s.entryBtnSl) {
                    s.entryBtnSl = document.createElement('button');
                    s.entryBtnSl.textContent = 'SL';
                    s.entryBtnSl.style.position = 'absolute';
                    s.entryBtnSl.style.zIndex = '6';
                    s.entryBtnSl.style.padding = '2px 8px';
                    s.entryBtnSl.style.borderRadius = '4px';
                    s.entryBtnSl.style.fontSize = '10.5px';
                    s.entryBtnSl.style.fontWeight = '700';
                    s.entryBtnSl.style.border = 'none';
                    s.entryBtnSl.style.cursor = 'pointer';
                    s.entryBtnSl.style.background = '#ef4444';
                    s.entryBtnSl.style.color = '#000';
                    s.entryBtnSl.style.transform = 'translateY(-50%)';
                    s.entryBtnSl.style.boxShadow = '0 1px 4px rgba(0,0,0,0.5)';
                    s.entryBtnSl.style.letterSpacing = '0.5px';
                    s.entryBtnSl.style.transition = 'opacity .15s';
                    s.entryBtnSl.onmouseenter = () => s.entryBtnSl.style.opacity = '1';
                    s.entryBtnSl.onmouseleave = () => s.entryBtnSl.style.opacity = '0.9';
                    s.entryBtnSl.onclick = () => this.promptAddSl(containerId);
                    s.entryBtnSl.addEventListener('mousedown', (ev) => ev.stopPropagation());
                    container.appendChild(s.entryBtnSl);
                }
                this.repositionEntryButtons(containerId);

                if (!s.entryBtnRangeSub) {
                    // Separate subscription from the lazy-load-history
                    // one elsewhere in init() - just repositions these
                    // two buttons on every scroll/zoom so they track the
                    // Entry line's actual Y position rather than
                    // drifting out of place.
                    s.entryBtnRangeSub = () => this.repositionEntryButtons(containerId);
                    s.chart.timeScale().subscribeVisibleLogicalRangeChange(s.entryBtnRangeSub);
                }
            }

            // Per direct performance audit: the Entry/Liq/BE pills'
            // positions get finalized by the repositionAllPills call
            // inside showTpSlLines, which always runs right after this
            // (via RefreshPositionLines, now that TP/SL shows by
            // default) - calling it here too was pure redundant work,
            // immediately made stale by that second call on every
            // single position switch.
        },

        repositionEntryButtons(containerId) {
            const s = sessions.get(containerId);
            if (!s || !s.entryBtnTp || !s.entryBtnSl) return;
            // Buttons follow the LIVE PRICE line (pnlLine), not the static
            // entry line — they sit right next to the current market price
            // so the user can quickly add TP/SL relative to where price IS now.
            const trackPrice = s.lastPnlPrice || s.entryPrice;
            if (!trackPrice) return;
            const y = s.candleSeries.priceToCoordinate(trackPrice);
            if (y == null) return;

            let scaleWidth = 60;
            try { scaleWidth = s.chart.priceScale('right').width() || 60; } catch (e) {}

            // SL (red) left of TP (green), both centered on the live price line Y
            s.entryBtnTp.style.right = (scaleWidth + 50) + 'px';
            s.entryBtnTp.style.top   = y + 'px';
            s.entryBtnSl.style.right = (scaleWidth + 84) + 'px';
            s.entryBtnSl.style.top   = y + 'px';
        },

        // Per direct confirmation: a single combined prompt asking for
        // BOTH price and percent at once (format "price, percent"),
        // since clicking this button (unlike dragging) has no mouse
        // position to derive a price from - avoids showing two
        // separate prompts back to back. Calls a dedicated
        // onNewTpRequestedWithPercent callback so the percent doesn't
        // need a second round-trip prompt on the C# side.
        // ─── RIGHT-CLICK CONTEXT MENU ────────────────────────────────
        // Shows a dark, styled popup at cursor with two actions:
        //   TP side → "Set Take Profit at X.XXXX (N%)" + pct input
        //   SL side → "Set Stop Loss at X.XXXX" + confirm button
        // Clicking outside or pressing Escape dismisses it.
        _showRightClickMenu(containerId, clientX, clientY, price, isTpSide) {
            const s = sessions.get(containerId);
            if (!s) return;

            // Remove any existing menu
            this._removeRightClickMenu();

            const fmt = (p) => p < 0.0001 ? p.toFixed(7)
                              : p < 0.01   ? p.toFixed(6)
                              : p < 1      ? p.toFixed(5)
                              : p < 100    ? p.toFixed(4)
                              :              p.toFixed(2);

            const entryPrice = s.entryPrice || 0;
            const isLong = s.side === 'LONG';
            const dir = isLong ? 1 : -1;
            const qty = s.qty || 0;
            const pnl = (price - entryPrice) * dir * qty;
            const pnlPct = entryPrice > 0 ? ((price - entryPrice) / entryPrice * dir * 100) : 0;
            const pnlStr = `${pnl >= 0 ? '+' : ''}${pnl.toFixed(2)} USDT (${pnlPct >= 0 ? '+' : ''}${pnlPct.toFixed(2)}%)`;

            const color = isTpSide ? '#22c55e' : '#ef4444';
            const colorDim = isTpSide ? 'rgba(34,197,94,.12)' : 'rgba(239,68,68,.12)';
            const label = isTpSide ? 'Take Profit' : 'Stop Loss';
            const icon = isTpSide ? '🎯' : '🛡️';

            const menu = document.createElement('div');
            menu.id = '__vx_rclick_menu';
            menu.style.cssText = `
                position:fixed; z-index:99999;
                left:${clientX}px; top:${clientY}px;
                background:#0a1422; border:1px solid #1e3050;
                border-radius:8px; box-shadow:0 8px 32px rgba(0,0,0,.7);
                min-width:240px; overflow:hidden;
                font-family:'Inter',system-ui,sans-serif;
                animation:vxMenuIn .1s ease;
            `;

            // Adjust position to stay inside viewport
            const vw = window.innerWidth, vh = window.innerHeight;
            if (clientX + 260 > vw) menu.style.left = (clientX - 260) + 'px';
            if (clientY + 180 > vh) menu.style.top  = (clientY - 180) + 'px';

            menu.innerHTML = `
                <style>
                @keyframes vxMenuIn { from { opacity:0; transform:scale(.95) translateY(-4px); } to { opacity:1; transform:none; } }
                #__vx_rclick_menu * { box-sizing:border-box; }
                #__vx_rclick_menu input::-webkit-inner-spin-button { display:none; }
                </style>

                <!-- Header -->
                <div style="
                    padding:10px 14px 8px;
                    border-bottom:1px solid #131f32;
                    display:flex; align-items:center; gap:8px;
                ">
                    <span style="font-size:15px;">${icon}</span>
                    <div>
                        <div style="font-size:12px; font-weight:800; color:#e8f4ff;">
                            ${label}
                        </div>
                        <div style="font-size:10px; color:#3d5878; margin-top:1px;">
                            ${s.side} position · ${isLong ? 'LONG' : 'SHORT'}
                        </div>
                    </div>
                    <button id="__vx_rclick_close" style="
                        margin-left:auto; background:transparent; border:none;
                        color:#3d5878; font-size:16px; cursor:pointer; line-height:1;
                        padding:0 2px;
                    ">×</button>
                </div>

                <!-- Price display -->
                <div style="padding:10px 14px 8px; border-bottom:1px solid #131f32;">
                    <div style="
                        display:flex; align-items:center; justify-content:space-between;
                        background:${colorDim}; border:1px solid ${color}33;
                        border-radius:6px; padding:8px 12px;
                    ">
                        <div>
                            <div style="font-size:9px; font-weight:700; text-transform:uppercase;
                                        letter-spacing:.6px; color:#3d5878; margin-bottom:2px;">
                                Price
                            </div>
                            <div style="font-family:'JetBrains Mono',monospace; font-size:16px;
                                        font-weight:700; color:${color};">
                                ${fmt(price)}
                            </div>
                        </div>
                        <div style="text-align:right;">
                            <div style="font-size:9px; font-weight:700; text-transform:uppercase;
                                        letter-spacing:.6px; color:#3d5878; margin-bottom:2px;">
                                Expected ${isTpSide ? 'Profit' : 'Loss'}
                            </div>
                            <div style="font-family:'JetBrains Mono',monospace; font-size:11px;
                                        font-weight:700; color:${color}; white-space:nowrap;">
                                ${pnlStr}
                            </div>
                        </div>
                    </div>
                </div>

                ${isTpSide ? `
                <!-- TP: show pct input -->
                <div style="padding:10px 14px 12px;">
                    <div style="font-size:9px; font-weight:700; text-transform:uppercase;
                                letter-spacing:.6px; color:#3d5878; margin-bottom:6px;">
                        Allocation (% of position)
                    </div>
                    <div style="display:flex; gap:6px; margin-bottom:8px;">
                        ${[25,33,50,100].map(p => `
                            <button class="__vx_pct_btn" data-pct="${p}" style="
                                flex:1; height:28px; background:#0f1b30;
                                border:1px solid #1e3050; border-radius:5px;
                                color:#6e90b2; font-size:11px; font-weight:700;
                                cursor:pointer; transition:all .1s;
                            ">${p}%</button>
                        `).join('')}
                    </div>
                    <div style="display:flex; gap:8px; align-items:center;">
                        <div style="position:relative; flex:1;">
                            <input id="__vx_pct_input" type="number" min="1" max="100"
                                value="25" style="
                                width:100%; height:34px; background:#0f1b30;
                                border:1px solid #1e3050; border-radius:6px;
                                color:#e8f4ff; font-size:13px; font-weight:700;
                                padding:0 28px 0 10px; outline:none;
                                font-family:'JetBrains Mono',monospace;
                            "/>
                            <span style="
                                position:absolute; right:10px; top:50%;
                                transform:translateY(-50%);
                                color:#3d5878; font-size:12px; font-weight:700;
                            ">%</span>
                        </div>
                        <button id="__vx_rclick_confirm" style="
                            height:34px; padding:0 18px; border-radius:6px;
                            background:rgba(34,197,94,.1); border:1.5px solid rgba(34,197,94,.35);
                            color:#22c55e; font-size:12px; font-weight:800;
                            cursor:pointer; white-space:nowrap; flex-shrink:0;
                        ">Set TP →</button>
                    </div>
                </div>
                ` : `
                <!-- SL: just confirm -->
                <div style="padding:10px 14px 12px;">
                    <div style="font-size:11px; color:#6e90b2; margin-bottom:10px; line-height:1.5;">
                        Place Stop Loss at <span style="color:#ef4444; font-weight:700; font-family:'JetBrains Mono',monospace;">${fmt(price)}</span>
                        <br>This will close the full position when triggered.
                    </div>
                    <div style="display:flex; gap:8px;">
                        <button id="__vx_rclick_cancel_sl" style="
                            flex:1; height:34px; border-radius:6px;
                            background:#0f1b30; border:1px solid #1e3050;
                            color:#6e90b2; font-size:12px; font-weight:700; cursor:pointer;
                        ">Cancel</button>
                        <button id="__vx_rclick_confirm" style="
                            flex:2; height:34px; border-radius:6px;
                            background:rgba(239,68,68,.1); border:1.5px solid rgba(239,68,68,.35);
                            color:#ef4444; font-size:12px; font-weight:800;
                            cursor:pointer;
                        ">🛡️ Set Stop Loss →</button>
                    </div>
                </div>
                `}
            `;

            document.body.appendChild(menu);
            this.__rclickState = { containerId, price, isTpSide, s };

            // Focus pct input for TP
            if (isTpSide) {
                setTimeout(() => {
                    const inp = document.getElementById('__vx_pct_input');
                    if (inp) { inp.focus(); inp.select(); }
                }, 50);
            }

            // Pct preset buttons
            menu.querySelectorAll('.__vx_pct_btn').forEach(btn => {
                btn.addEventListener('mouseenter', () => {
                    btn.style.background = 'rgba(34,197,94,.1)';
                    btn.style.color = '#22c55e';
                    btn.style.borderColor = 'rgba(34,197,94,.3)';
                });
                btn.addEventListener('mouseleave', () => {
                    const inp2 = document.getElementById('__vx_pct_input');
                    const val = inp2 ? parseInt(inp2.value) : 0;
                    const isActive = String(val) === btn.dataset.pct;
                    btn.style.background = isActive ? 'rgba(34,197,94,.1)' : '#0f1b30';
                    btn.style.color = isActive ? '#22c55e' : '#6e90b2';
                    btn.style.borderColor = isActive ? 'rgba(34,197,94,.3)' : '#1e3050';
                });
                btn.addEventListener('click', () => {
                    const inp2 = document.getElementById('__vx_pct_input');
                    if (inp2) { inp2.value = btn.dataset.pct; inp2.focus(); }
                    menu.querySelectorAll('.__vx_pct_btn').forEach(b => {
                        const isThis = b === btn;
                        b.style.background = isThis ? 'rgba(34,197,94,.1)' : '#0f1b30';
                        b.style.color = isThis ? '#22c55e' : '#6e90b2';
                        b.style.borderColor = isThis ? 'rgba(34,197,94,.3)' : '#1e3050';
                    });
                });
            });

            // Confirm handler
            const self = this;
            const doConfirm = () => {
                const st = self.__rclickState;
                if (!st) return;
                self._removeRightClickMenu();

                if (st.isTpSide) {
                    const inp = document.getElementById('__vx_pct_input');
                    const pct = inp ? parseFloat(inp.value) : 25;
                    if (!pct || pct <= 0 || pct > 100) return;
                    if (st.s.onNewTpRequestedWithPercent)
                        st.s.onNewTpRequestedWithPercent(st.price, pct);
                } else {
                    if (st.s.onSlChanged)
                        st.s.onSlChanged(st.price, 0);
                }
            };

            const confirmBtn = menu.querySelector('#__vx_rclick_confirm');
            if (confirmBtn) confirmBtn.addEventListener('click', doConfirm);

            const closeBtn = menu.querySelector('#__vx_rclick_close');
            if (closeBtn) closeBtn.addEventListener('click', () => this._removeRightClickMenu());

            const cancelSlBtn = menu.querySelector('#__vx_rclick_cancel_sl');
            if (cancelSlBtn) cancelSlBtn.addEventListener('click', () => this._removeRightClickMenu());

            // Enter key confirms
            menu.addEventListener('keydown', (ev) => {
                if (ev.key === 'Enter') { ev.preventDefault(); doConfirm(); }
                if (ev.key === 'Escape') this._removeRightClickMenu();
            });

            // Click outside dismisses
            setTimeout(() => {
                document.addEventListener('mousedown', this._rclickOutside = (ev) => {
                    if (!menu.contains(ev.target)) this._removeRightClickMenu();
                }, { once: false, capture: true });
            }, 100);
        },

        _removeRightClickMenu() {
            const el = document.getElementById('__vx_rclick_menu');
            if (el) el.remove();
            this.__rclickState = null;
            if (this._rclickOutside) {
                document.removeEventListener('mousedown', this._rclickOutside, { capture: true });
                this._rclickOutside = null;
            }
        },

        promptAddTp(containerId) {
            const s = sessions.get(containerId);
            if (!s || !s.onNewTpRequestedWithPercent) return;
            const input = window.prompt('New TP — enter price, percent (e.g. "65000, 25"):');
            if (!input) return;
            const parts = input.split(',').map(p => p.trim());
            const price = parseFloat(parts[0]);
            const pct = parts.length > 1 ? parseFloat(parts[1]) : NaN;
            if (!price || price <= 0) return;
            s.onNewTpRequestedWithPercent(price, isNaN(pct) ? 0 : pct);
        },

        promptAddSl(containerId) {
            const s = sessions.get(containerId);
            if (!s || !s.onSlChanged) return;
            const input = window.prompt('Stop Loss price:');
            if (!input) return;
            const price = parseFloat(input);
            if (!price || price <= 0) return;
            s.onSlChanged(price);
        },

        // Shows the draggable SL + ALL TP lines — called only when the
        // user clicks the TP/SL button, not automatically on position
        // selection. tps is an array (multiple TP levels all get their
        // own draggable line now — previously only the first one was
        // ever shown). Each line is thin (lineWidth:1, matching the
        // Entry line's weight) and directly draggable like Entry —
        // grab the line itself and drag it to a new price, rather than
        // the old "drag anywhere on the chart" armed-mode gesture.
        showTpSlLines(containerId, entry, sl, tps, side) {
            const s = sessions.get(containerId);
            if (!s) return;
            // If user is actively dragging a line, defer this update
            // by stashing the args and applying them after mouseup.
            if (s.draggingLine) {
                s._pendingTpSlArgs = { entry, sl, tps, side };
                return;
            }
            // Clear the post-drag freeze — C# just sent us fresh data,
            // safe to redraw with confirmed order prices now.
            s._tpSlHideFreezeUntil = 0;
            this.hideTpSlLines(containerId);

            // pnlFor reads s.side/s.qty/s.entryPrice dynamically on every
            // call — not captured at closure creation. Fixes a bug where
            // hasPnlData/dir were stale after a partial-close updated s.qty,
            // causing PnL shown on pills to remain wrong until showTpSlLines
            // was fully re-called.
            const pnlFor = (price) => {
                if (!s.qty || !s.entryPrice) return null;
                const d = s.side === 'LONG' ? 1 : -1;
                return (price - s.entryPrice) * d * s.qty;
            };
            s._lastPnlFor = pnlFor; // store for drag mousemove access

            if (sl > 0) {
                s.slLine = s.candleSeries.createPriceLine({
                    price: sl, color: '#ef4444', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Solid,
                    axisLabelVisible: false, title: '',
                });
                s.slPill = this.makeLevelPill(containerId, '#ef4444', 'SL', 'sl', null);
            }

            const tpList = Array.isArray(tps) ? tps : (tps > 0 ? [tps] : []);
            s.tpLines = [];
            s.tpPills = [];
            tpList.forEach((tpPrice, i) => {
                if (!tpPrice || tpPrice <= 0) return;
                const line = s.candleSeries.createPriceLine({
                    price: tpPrice, color: '#22c55e', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Solid,
                    axisLabelVisible: false, title: '',
                });
                const label = tpList.length > 1 ? `TP${i + 1}` : 'TP';
                const pill = this.makeLevelPill(containerId, '#22c55e', label, 'tp', i);
                s.tpLines.push({ line, index: i, price: tpPrice });
                s.tpPills.push(pill);
            });

            this.repositionAllPills(containerId, pnlFor);

            if (!s.tpSlPillRangeSub) {
                const self = this;

                // PERF: dirty-flag + RAF loop.
                // Previously: subscribeVisibleLogicalRangeChange and subscribeCrosshairMove
                // called repositionAllPills() synchronously on every scroll pixel and every
                // mouse movement (~60/s each). This caused layout thrashing.
                // Now: events only set a dirty flag; actual repositioning happens in the
                // next animation frame — at most once per frame, only when needed.
                let pillDirty = true;
                const pillLoop = () => {
                    s._pillRafId = requestAnimationFrame(pillLoop);
                    if (!sessions.has(containerId)) {
                        cancelAnimationFrame(s._pillRafId); s._pillRafId = null; return;
                    }
                    if (pillDirty) {
                        pillDirty = false;
                        self.repositionAllPills(containerId, pnlFor);
                    }
                };
                s._pillRafId = requestAnimationFrame(pillLoop);

                // Scroll / zoom / crosshair → mark dirty (no work done here)
                s.tpSlPillRangeSub = () => { pillDirty = true; };
                s.chart.timeScale().subscribeVisibleLogicalRangeChange(s.tpSlPillRangeSub);

                s.tpSlPriceScaleSub = s.tpSlPillRangeSub;
                try { s.chart.priceScale('right').subscribePriceRangeChange(s.tpSlPriceScaleSub); } catch(e) {}

                s.tpSlCrosshairSub = () => { pillDirty = true; };
                s.chart.subscribeCrosshairMove(s.tpSlCrosshairSub);

                if (!s._pillResizeObs) {
                    s._pillResizeObs = new ResizeObserver(() => { pillDirty = true; });
                    s._pillResizeObs.observe(container);
                }
            }
        },

        // Creates one Bybit-style colored pill: price on top, projected
        // PnL below, positioned at the right edge of the chart (matching
        // the reference screenshot). A smooth CSS transition on `top`
        // makes repositioning during scroll/zoom feel fluid rather than
        // snapping instantly, per direct request to prioritize visual
        // polish over minimal overhead for this feature.
        makeLevelPill(containerId, color, label, cancelKind, cancelIndex) {
            const container = document.getElementById(containerId);
            if (!container) return null;
            if (getComputedStyle(container).position === 'static') container.style.position = 'relative';

            // Single thin horizontal row (label · price · PnL all on
            // one line), matching the Bybit reference style - not a
            // tall stacked block.
            const pill = document.createElement('div');
            pill.style.position = 'absolute';
            pill.style.zIndex = '6';
            pill.style.display = 'flex';
            pill.style.alignItems = 'center';
            pill.style.gap = '5px';
            pill.style.padding = '2px 6px';
            pill.style.height = '18px';
            pill.style.borderRadius = '3px';
            pill.style.fontFamily = 'monospace';
            pill.style.fontSize = '10.5px';
            pill.style.fontWeight = '700';
            pill.style.color = '#0a0d12';
            pill.style.background = color;
            pill.style.boxShadow = '0 1px 3px rgba(0,0,0,.35)';
            pill.style.transform = 'translateY(-50%)';
            pill.style.pointerEvents = 'none'; // doesn't block chart drag gestures underneath - the cancel button below re-enables it on itself specifically
            pill.style.whiteSpace = 'nowrap';

            const labelEl = document.createElement('span');
            labelEl.style.opacity = '0.75';
            labelEl.style.fontSize = '9px';
            labelEl.textContent = label;
            pill.appendChild(labelEl);

            const priceEl = document.createElement('span');
            pill.appendChild(priceEl);
            pill._priceRow = priceEl;

            const pnlEl = document.createElement('span');
            pnlEl.style.opacity = '0.85';
            pnlEl.style.fontSize = '9.5px';
            pill.appendChild(pnlEl);
            pill._pnlRow = pnlEl;

            if (cancelKind) {
                const cancelBtn = document.createElement('button');
                cancelBtn.textContent = '×';
                cancelBtn.title = `Cancel this ${cancelKind === 'sl' ? 'Stop Loss' : 'Take Profit'}`;
                cancelBtn.style.pointerEvents = 'auto'; // re-enable specifically on this button, despite the pill itself being pointer-events:none
                cancelBtn.style.border = 'none';
                cancelBtn.style.borderRadius = '2px';
                cancelBtn.style.background = 'rgba(0,0,0,.22)';
                cancelBtn.style.color = '#0a0d12';
                cancelBtn.style.cursor = 'pointer';
                cancelBtn.style.fontSize = '10px';
                cancelBtn.style.fontWeight = '700';
                cancelBtn.style.lineHeight = '1';
                cancelBtn.style.padding = '0';
                cancelBtn.style.width = '11px';
                cancelBtn.style.height = '14px';
                cancelBtn.style.flexShrink = '0';
                cancelBtn.onclick = (ev) => {
                    ev.stopPropagation();
                    this.cancelProtectiveLevel(containerId, cancelKind, cancelIndex);
                };
                cancelBtn.addEventListener('mousedown', (ev) => ev.stopPropagation());
                pill.appendChild(cancelBtn);
            }

            container.appendChild(pill);
            return pill;
        },

        // Calls the dedicated C# cancel handler for this specific
        // level - bound once via bindSlTpCallbacks, same established
        // pattern as the other pill/button callbacks.
        cancelProtectiveLevel(containerId, kind, index) {
            const s = sessions.get(containerId);
            if (!s || !s.onCancelProtectiveLevel) return;
            s.onCancelProtectiveLevel(kind, index ?? -1);
        },

        // Repositions every pill (Entry/Liq/BE/SL/TP) to track its
        // price line. Called on scroll/zoom (X and Y) so pills move
        // in lock-step with the chart without any lag.
        repositionAllPills(containerId, pnlFor) {
            const s = sessions.get(containerId);
            if (!s) return;

            // Quick dirty check: skip if no visible lines to position
            if (!s.slLine && (!s.tpLines || s.tpLines.length === 0) &&
                !s.entryLine && !s.liqLine && !s.beLine) return;

            let scaleWidth = 60;
            try { scaleWidth = s.chart.priceScale('right').width() || 60; } catch (e) {}
            const rightOffset = scaleWidth + 50;
            const MIN_GAP = 20; // px between pill centres (pill height = 18px)

            // 1. Collect all pills with a valid on-screen Y coordinate.
            const entries = [];
            const allPills = [];

            const collect = (pill, price, showPnl) => {
                if (!pill) return;
                allPills.push(pill);
                const y = s.candleSeries.priceToCoordinate(price);
                if (y == null) { pill.style.display = 'none'; return; }
                entries.push({ pill, price, showPnl, trueY: y, y });
            };

            collect(s.entryPill, s.entryLine?.options().price, false);
            collect(s.liqPill,   s.liqLine?.options().price,   false);
            collect(s.bePill,    s.beLine?.options().price,     false);
            collect(s.slPill,    s.slLine?.options().price,     true);
            for (const tp of (s.tpLines || [])) {
                collect(s.tpPills?.[tp.index], tp.price, true);
            }

            if (entries.length === 0) return;

            // 2. Sort by true Y ascending (top of screen = highest price first).
            entries.sort((a, b) => a.trueY - b.trueY);

            // 3. Single forward pass: push overlapping pills down just enough.
            for (let i = 1; i < entries.length; i++) {
                const needed = entries[i - 1].y + MIN_GAP;
                if (entries[i].y < needed) entries[i].y = needed;
            }

            // 4. Apply positions and refresh text.
            for (const e of entries) {
                e.pill.style.display  = 'flex';
                e.pill.style.right    = rightOffset + 'px';
                e.pill.style.top      = e.y + 'px';
                e.pill._priceRow.textContent = fmtPrice(e.price);
                if (e.showPnl && pnlFor) {
                    const pnl = pnlFor(e.price);
                    e.pill._pnlRow.textContent = pnl != null
                        ? (pnl >= 0 ? '+' : '') + pnl.toFixed(2)
                        : '';
                } else {
                    e.pill._pnlRow.textContent = '';
                }
            }
        },

        // Arms/disarms the "Drag to set TP/SL" mode — toggled by the
        // TP/SL button on the entry line, matching Bybit's exact
        // gesture: once armed, pressing and dragging ANYWHERE on the
        // chart creates a TP (drag toward profit) or SL (drag toward
        // loss) with a live Expected Profit/Loss preview, instead of
        // needing to grab one specific existing line.
        setTpSlArmed(containerId, armed) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.tpSlArmed = armed;
        },

        hideTpSlLines(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;
            // Guard 1: never destroy during active drag
            if (s.draggingLine) return;
            // Guard 2: freeze for 8s after drag completes.
            // C# needs time to cancel old order and place new one.
            // During this window, showTpSlLines will call us — we
            // must not erase lines or the drag looks like it failed.
            if (s._tpSlHideFreezeUntil && Date.now() < s._tpSlHideFreezeUntil) return;
            if (s.slLine) { try { s.candleSeries.removePriceLine(s.slLine); } catch (e) {} s.slLine = null; }
            for (const tp of (s.tpLines || [])) {
                try { s.candleSeries.removePriceLine(tp.line); } catch (e) {}
            }
            s.tpLines = [];
            if (s.slPill) { try { s.slPill.remove(); } catch (e) {} s.slPill = null; }
            for (const pill of (s.tpPills || [])) {
                try { pill.remove(); } catch (e) {}
            }
            s.tpPills = [];
            if (s.tpSlPillRangeSub) { try { s.chart.timeScale().unsubscribeVisibleLogicalRangeChange(s.tpSlPillRangeSub); } catch (e) {} s.tpSlPillRangeSub = null; }
            // tpSlCrosshairSub was removed from performance refactor — no unsub needed
            if (s._pillResizeObs) { s._pillResizeObs.disconnect(); s._pillResizeObs = null; }
            try { if (s.tpSlPriceScaleSub) s.chart.priceScale('right').unsubscribePriceRangeChange(s.tpSlPriceScaleSub); } catch(e) {}
            s.tpSlPriceScaleSub = null;
        },

        // ═══════════════════════════════════════════════════════════════
        // OPEN ORDER LINES — shows pending orders as price lines on chart
        // Each order gets a labelled horizontal line at its trigger price.
        // Types: LIMIT, STOP_MARKET, TAKE_PROFIT_MARKET, STOP, TAKE_PROFIT.
        // Lines are color-coded: green=buy/TP, red=sell/SL, yellow=limit.
        // Called from C# after LoadOpenOrdersAsync with current symbol orders.
        // ═══════════════════════════════════════════════════════════════
        showOpenOrderLines(containerId, orders) {
            const s = sessions.get(containerId);
            if (!s || !s.candleSeries) return;

            // Remove any existing order lines first
            this.hideOpenOrderLines(containerId);

            if (!Array.isArray(orders) || orders.length === 0) return;

            s.openOrderLines = [];
            s.openOrderPills = [];

            const container = document.getElementById(containerId);

            for (const order of orders) {
                if (!order.price || order.price <= 0) continue;

                // Normalize type for matching
                const rawType = (order.type || '').toUpperCase().replace(/_/g, '');
                const side = (order.side || '').toUpperCase();

                // SKIP STOP/TP orders — already drawn by showTpSlLines.
                // Only show LIMIT (pending entry) orders to avoid duplicate lines.
                if (rawType.includes('TAKEPROFIT') || rawType.includes('STOP')) continue;

                // Only LIMIT orders reach here
                const color = side === 'BUY' ? '#22d3ee' : '#f97316';
                const lineStyle = LightweightCharts.LineStyle.Dotted;
                const labelPrefix = side === 'BUY' ? 'LMT BUY' : 'LMT SELL';

                                const fmtQty = order.qty > 0
                    ? (order.qty < 1 ? order.qty.toFixed(4) : order.qty.toFixed(2))
                    : '';
                const lineTitle = `${labelPrefix} ${fmtQty}`.trim();

                // Create the price line
                let line;
                try {
                    line = s.candleSeries.createPriceLine({
                        price: order.price,
                        color: color,
                        lineWidth: 1,
                        lineStyle: lineStyle,
                        axisLabelVisible: false,  // pill shows price instead
                        title: '',                 // no inline title — pill is cleaner
                    });
                } catch (e) { continue; }

                // Create pill label at right edge of chart
                if (container) {
                    const pill = document.createElement('div');
                    pill.className = 'ord-line-pill';
                    pill.style.cssText = `
                        position:absolute; right:0; z-index:7;
                        display:flex; align-items:center; gap:4px;
                        height:18px; padding:0 7px; border-radius:3px 0 0 3px;
                        background:${color}18; border:1px solid ${color}44;
                        border-right:none; pointer-events:none;
                        font-size:9.5px; font-weight:700; color:${color};
                        font-family:'JetBrains Mono',monospace;
                        white-space:nowrap; line-height:1;
                        transition: top 80ms linear;
                        opacity:0.9;
                    `;
                    // Dot indicator
                    const dot = document.createElement('span');
                    dot.style.cssText = `
                        width:5px; height:5px; border-radius:50%;
                        background:${color}; flex-shrink:0;
                        box-shadow:0 0 4px ${color};
                    `;
                    pill.appendChild(dot);

                    const txt = document.createElement('span');
                    txt.textContent = lineTitle;
                    pill.appendChild(txt);

                    // Price text
                    const priceSpan = document.createElement('span');
                    priceSpan.style.opacity = '0.7';
                    priceSpan.textContent = ' ' + order.price.toFixed(
                        order.price < 0.01 ? 6 : order.price < 1 ? 4 : order.price < 100 ? 3 : 2
                    );
                    pill.appendChild(priceSpan);

                    container.appendChild(pill);
                    s.openOrderPills.push({ pill, price: order.price });
                }

                s.openOrderLines.push({ line, price: order.price, color, order });
            }

            // Position pills
            this._repositionOrderPills(containerId);

            // Subscribe to range changes to keep pills tracking
            if (s.openOrderLines.length > 0 && !s._orderPillRangeSub) {
                const self = this;
                s._orderPillRangeSub = () => self._repositionOrderPills(containerId);
                s.chart.timeScale().subscribeVisibleLogicalRangeChange(s._orderPillRangeSub);
                try {
                    s.chart.priceScale('right').subscribePriceRangeChange(s._orderPillRangeSub);
                } catch(e) {}
            }
        },

        _repositionOrderPills(containerId) {
            const s = sessions.get(containerId);
            if (!s || !s.openOrderPills || !s.candleSeries) return;

            let scaleWidth = 60;
            try { scaleWidth = s.chart.priceScale('right').width() || 60; } catch(e) {}

            const MIN_GAP = 20;
            const entries = [];

            for (const { pill, price } of s.openOrderPills) {
                const y = s.candleSeries.priceToCoordinate(price);
                if (y == null) { pill.style.display = 'none'; continue; }
                pill.style.display = 'flex';
                entries.push({ pill, price, trueY: y, y });
            }

            // Sort by Y (top first = highest price first)
            entries.sort((a, b) => a.trueY - b.trueY);

            // Collision avoidance: push down overlapping pills
            for (let i = 1; i < entries.length; i++) {
                const needed = entries[i-1].y + MIN_GAP;
                if (entries[i].y < needed) entries[i].y = needed;
            }

            for (const { pill, y } of entries) {
                pill.style.right = scaleWidth + 'px';
                pill.style.top = (y - 9) + 'px';  // center on line
            }
        },

        hideOpenOrderLines(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;

            // Remove price lines
            for (const { line } of (s.openOrderLines || [])) {
                try { s.candleSeries.removePriceLine(line); } catch(e) {}
            }
            s.openOrderLines = [];

            // Remove pills
            for (const { pill } of (s.openOrderPills || [])) {
                try { pill.remove(); } catch(e) {}
            }
            s.openOrderPills = [];

            // Unsubscribe range listener
            if (s._orderPillRangeSub) {
                try { s.chart.timeScale().unsubscribeVisibleLogicalRangeChange(s._orderPillRangeSub); } catch(e) {}
                try { s.chart.priceScale('right').unsubscribePriceRangeChange(s._orderPillRangeSub); } catch(e) {}
                s._orderPillRangeSub = null;
            }
        },

        // ═══════════════════════════════════════════════════════════════

        // Refreshes the live market-price line with the current PnL —
        // moved OFF the Entry line's title (which is what made it
        // overrun the chart edge and get visually cut off when PnL was
        // appended there) onto its own short-lived line that tracks
        // wherever price currently is, exactly like Bybit shows live
        // PnL right on the current price level.
        //
        // IMPORTANT: the price line's title text and the axisLabelVisible
        // price-scale label are two SEPARATE rendered elements - having
        // both enabled is what caused the reported overlapping/garbled
        // text near the right edge (title text colliding with the axis
        // label, on top of the built-in last-price label potentially
        // still showing too). Redesigned: the price line itself carries
        // NO text at all (just the dashed line marking the price level);
        // the PnL number is a separate HTML overlay positioned via
        // priceToCoordinate, placed near the center-left of the chart
        // per direct request — away from the crowded right edge where
        // Entry/SL/TP/Liq/BE labels and the price scale all compete.
        updatePnl(containerId, currentPrice) {
            const s = sessions.get(containerId);
            if (!s) { console.warn('[PnL] no session for', containerId); return; }
            if (!s.entryPrice) { console.warn('[PnL] session has no entryPrice set yet — showPositionLines may not have run'); return; }
            s.candleSeries.applyOptions({ priceLineVisible: false });
            s.lastPnlPrice = currentPrice; // for the scroll/zoom subscription below to reposition without waiting for the next tick

            const dir = s.side === 'LONG' ? 1 : -1;
            const pnl = (currentPrice - s.entryPrice) * dir * s.qty;
            const sign = pnl >= 0 ? '+' : '';
            const color = pnl >= 0 ? '#22c55e' : '#ef4444';

            try {
                if (s.pnlLine) {
                    s.pnlLine.applyOptions({ price: currentPrice, color });
                } else {
                    s.pnlLine = s.candleSeries.createPriceLine({
                        price: currentPrice, color, lineWidth: 1,
                        lineStyle: LightweightCharts.LineStyle.Dashed,
                        axisLabelVisible: false, title: '',
                    });
                }
            } catch (e) {}

            // Bybit-style pill positioned right on the PnL line itself
            // (per direct reference to that screenshot), rather than a
            // separate floating box off to the side as before.
            const container = document.getElementById(containerId);
            if (!container) return;
            const y = s.candleSeries.priceToCoordinate(currentPrice);
            if (y == null) return;

            if (!s.pnlLabelEl) {
                s.pnlLabelEl = document.createElement('div');
                s.pnlLabelEl.style.position = 'absolute';
                s.pnlLabelEl.style.pointerEvents = 'none';
                s.pnlLabelEl.style.zIndex = '5';
                s.pnlLabelEl.style.padding = '2px 8px';
                s.pnlLabelEl.style.borderRadius = '4px';
                s.pnlLabelEl.style.fontSize = '11px';
                s.pnlLabelEl.style.fontWeight = '700';
                s.pnlLabelEl.style.fontFamily = 'monospace';
                s.pnlLabelEl.style.transform = 'translateY(-50%)';
                s.pnlLabelEl.style.boxShadow = '0 1px 4px rgba(0,0,0,.25)';
                s.pnlLabelEl.style.opacity = '0.82';
                s.pnlLabelEl.style.transition = 'top .12s ease-out, background .15s, opacity .15s';
                if (getComputedStyle(container).position === 'static') container.style.position = 'relative';
                container.appendChild(s.pnlLabelEl);
            }
            // Per direct request: positioned at the last candle's time
            // + 3 bars further right (using the chart's own
            // timeToCoordinate for the actual last loaded bar, plus 3x
            // the current bar spacing) rather than a fixed left-edge
            // offset - this is what correctly tracks "3 candles past
            // the last one" across different timeframes/zoom levels.
            let pnlLabelX = null;
            try {
                if (s.lastKlinesRaw && s.lastKlinesRaw.length > 0) {
                    const lastBar = s.lastKlinesRaw[s.lastKlinesRaw.length - 1];
                    const lastX = s.chart.timeScale().timeToCoordinate(Math.floor(lastBar.openTime / 1000));
                    const barSpacing = s.chart.timeScale().options().barSpacing || 6;
                    if (lastX != null) pnlLabelX = lastX + barSpacing * 8;
                }
            } catch (e) {}
            if (pnlLabelX != null) {
                s.pnlLabelEl.style.left = pnlLabelX + 'px';
                s.pnlLabelEl.style.right = 'auto';
            } else {
                // Fallback if the last bar's time can't be resolved for
                // any reason - keep the label somewhere reasonable
                // rather than disappearing entirely.
                s.pnlLabelEl.style.left = '10px';
            }
            s.pnlLabelEl.style.top = y + 'px';
            s.pnlLabelEl.style.background = color;
            s.pnlLabelEl.style.color = '#0a0d12';
            s.pnlLabelEl.textContent = `${sign}${pnl.toFixed(2)} USDT`;

            // Reposition TP/SL quick-add buttons to follow the live price line Y
            this.repositionEntryButtons(containerId);

            // Per direct report: keep this label tracking scroll/zoom
            // synchronously, not only on the next price tick - subscribes
            // once per session (not per call), repositioning using the
            // last known price whenever the user scrolls/zooms the chart.
            if (!s.pnlRangeSub) {
                s.pnlRangeSub = () => {
                    if (s.lastPnlPrice == null || !s.entryPrice) return;
                    const yy = s.candleSeries.priceToCoordinate(s.lastPnlPrice);
                    if (yy != null && s.pnlLabelEl) s.pnlLabelEl.style.top = yy + 'px';
                    this.repositionEntryButtons(containerId); // keep buttons on live price during scroll/zoom

                    // Also recalculate X - barSpacing changes on zoom,
                    // so "last bar + 3 bar widths" would otherwise
                    // visibly drift from the correct spot as soon as
                    // the user zooms, since only Y was kept in sync
                    // here before this fix.
                    try {
                        if (s.lastKlinesRaw && s.lastKlinesRaw.length > 0 && s.pnlLabelEl) {
                            const lastBar = s.lastKlinesRaw[s.lastKlinesRaw.length - 1];
                            const lastX = s.chart.timeScale().timeToCoordinate(Math.floor(lastBar.openTime / 1000));
                            const barSpacing = s.chart.timeScale().options().barSpacing || 6;
                            if (lastX != null) s.pnlLabelEl.style.left = (lastX + barSpacing * 8) + 'px';
                        }
                    } catch (e) {}
                };
                s.chart.timeScale().subscribeVisibleLogicalRangeChange(s.pnlRangeSub);
            }
        },

        hidePositionLines(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.candleSeries.applyOptions({ priceLineVisible: true });
            if (s.entryLine) { try { s.candleSeries.removePriceLine(s.entryLine); } catch (e) {} s.entryLine = null; }
            if (s.liqLine) { try { s.candleSeries.removePriceLine(s.liqLine); } catch (e) {} s.liqLine = null; }
            if (s.beLine) { try { s.candleSeries.removePriceLine(s.beLine); } catch (e) {} s.beLine = null; }
            if (s.entryPill) { try { s.entryPill.remove(); } catch (e) {} s.entryPill = null; }
            if (s.liqPill) { try { s.liqPill.remove(); } catch (e) {} s.liqPill = null; }
            if (s.bePill) { try { s.bePill.remove(); } catch (e) {} s.bePill = null; }
            if (s.pnlLine) { try { s.candleSeries.removePriceLine(s.pnlLine); } catch (e) {} s.pnlLine = null; }
            if (s.pnlLabelEl) { try { s.pnlLabelEl.remove(); } catch (e) {} s.pnlLabelEl = null; }
            if (s.pnlRangeSub) { try { s.chart.timeScale().unsubscribeVisibleLogicalRangeChange(s.pnlRangeSub); } catch (e) {} s.pnlRangeSub = null; }
            if (s.entryBtnTp) { try { s.entryBtnTp.remove(); } catch (e) {} s.entryBtnTp = null; }
            if (s.entryBtnSl) { try { s.entryBtnSl.remove(); } catch (e) {} s.entryBtnSl = null; }
            if (s.entryBtnRangeSub) { try { s.chart.timeScale().unsubscribeVisibleLogicalRangeChange(s.entryBtnRangeSub); } catch (e) {} s.entryBtnRangeSub = null; }
            this.hideTpSlLines(containerId);
            this.setTpSlArmed(containerId, false);
            if (s.previewLine) { try { s.candleSeries.removePriceLine(s.previewLine); } catch (e) {} s.previewLine = null; }
            if (s.previewBox && s.previewBox.parentNode) s.previewBox.remove();
            s.previewBox = null;
            if (s.previewVLine && s.previewVLine.parentNode) s.previewVLine.remove();
            s.previewVLine = null;
            s.dragging = false;
            s.entryPrice = 0;
            s.qty = 0;
        },

        // Binds the C# callbacks that fire once a drag is committed
        // (mouseup), so the actual exchange order can be replaced.
        // Doesn't fire on every mousemove — only once the user lets go,
        // matching the "drag, then it actually applies" feel of
        // Binance/Bybit's chart-based SL/TP editor rather than firing
        // an API call per pixel of mouse movement.
        bindSlTpCallbacks(containerId, dotNetRef) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.onSlChanged    = (price, origPrice) => dotNetRef.invokeMethodAsync('OnSlDragged', price, origPrice || price);
            s.onTpChanged    = (price, origPrice) => dotNetRef.invokeMethodAsync('OnTpDragged', price, origPrice || price);
            s.onTpChangedAt  = (index, price, origPrice) => dotNetRef.invokeMethodAsync('OnTpDraggedAt', index, price, origPrice || price);
            s.onNewTpRequested = (price) => dotNetRef.invokeMethodAsync('OnNewTpRequested', price);
            s.onNewTpRequestedWithPercent = (price, pct) => dotNetRef.invokeMethodAsync('OnNewTpRequestedWithPercent', price, pct);
            s.onCancelProtectiveLevel = (kind, index) => dotNetRef.invokeMethodAsync('OnCancelProtectiveLevel', kind, index);
            s.onLimitOrderRequested = (side, price, qty) => dotNetRef.invokeMethodAsync('OnLimitOrderRequested', side, price, qty);
            // FIX 2: bind OnPricePicked so right-click fills the Limit Price field in the right panel
            s.onPricePicked = (price) => dotNetRef.invokeMethodAsync('OnPricePicked', price);
        },

        bindInfiniteHistory(containerId, dotNetRef) {
            const s = sessions.get(containerId);
            if (!s) return;

            // Avoid double-subscribing if setData/bindInfiniteHistory gets
            // called again for the same session (e.g. timeframe switch).
            if (s.infiniteHistoryBound) return;
            s.infiniteHistoryBound = true;
            s.loadingMoreHistory = false;

            const THRESHOLD_BARS = 50; // start loading when this close to the left edge
            const PAGE_SIZE = 300;     // bars requested per load-more call

            let debounceTimer = null;
            s.chart.timeScale().subscribeVisibleLogicalRangeChange((range) => {
                if (debounceTimer) clearTimeout(debounceTimer);
                debounceTimer = setTimeout(() => handleRangeChange(range), 150);
            });

            const handleRangeChange = async (range) => {
                if (!range || s.loadingMoreHistory || s.historyExhausted) return;

                const barsInfo = s.candleSeries.barsInLogicalRange(range);
                if (!barsInfo || barsInfo.barsBefore == null || barsInfo.barsBefore >= THRESHOLD_BARS) return;

                // Find the earliest bar currently held, to ask Blazor for
                // anything older than it.
                const allData = s.candleSeries.data();
                if (!allData || allData.length === 0) return;
                const earliestTime = allData[0].time;

                s.loadingMoreHistory = true;
                try {
                    const older = await dotNetRef.invokeMethodAsync('LoadMoreHistoryAsync', earliestTime * 1000, PAGE_SIZE);
                    if (!older || older.length === 0) {
                        // Server has nothing further back at all.
                        s.historyExhausted = true;
                        return;
                    }

                    // CRITICAL: the server serves from an in-memory dataset
                    // of finite size — once we've scrolled past the actual
                    // earliest bar it has, every further call would return
                    // the SAME slice again (computed relative to our
                    // request, but the underlying data never grows past
                    // what was loaded). If the oldest bar in this response
                    // isn't actually older than what we already have
                    // on-screen, there's nothing genuinely new — stop
                    // permanently instead of rebuilding the chart forever
                    // on duplicate data (which is exactly what caused the
                    // reported constant flickering on the left edge).
                    const oldestReturned = older[0].openTime / 1000;
                    if (oldestReturned >= earliestTime) {
                        s.historyExhausted = true;
                        return;
                    }

                    // Save the current scroll position before rebuilding —
                    // setData() resets the visible range by default, which
                    // would otherwise make the chart visually jump every
                    // time more history loads in.
                    const savedRange = s.chart.timeScale().getVisibleLogicalRange();
                    const addedCount = older.length;

                    // Lightweight Charts cannot update() bars older than
                    // the current earliest one (confirmed via the library's
                    // own docs/discussions) — the whole series must be
                    // rebuilt via setData with the combined, sorted set.
                    const combined = older.concat(s.lastKlinesRaw || []).sort((a, b) => a.openTime - b.openTime);
                    s.lastKlinesRaw = combined;

                    const candles = combined.map(toCandle);
                    const closes = combined.map(k => k.close);
                    const ema21 = ema(closes, 21);
                    const ema55 = ema(closes, 55);
                    const rsiVals = rsi(closes, 14);

                    s.candleSeries.setData(candles);
                    s.ema21Series.setData(candles.map((c, i) => ({ time: c.time, value: ema21[i] })).filter(d => d.value != null));
                    s.ema55Series.setData(candles.map((c, i) => ({ time: c.time, value: ema55[i] })).filter(d => d.value != null));
                    s.volumeSeries.setData(combined.map(k => toVolume(k, 'rgba(34,197,94,0.28)', 'rgba(239,68,68,0.28)')));
                    s.rsiSeries.setData(candles.map((c, i) => ({ time: c.time, value: rsiVals[i] })).filter(d => d.value != null));
                    s.rsiObLine.setData(candles.map(c => ({ time: c.time, value: 70 })));
                    s.rsiOsLine.setData(candles.map(c => ({ time: c.time, value: 30 })));
                    s.rawKlineByTime = new Map(candles.map((c, i) => [c.time, combined[i]]));
                    s.lastSeriesEarliestTime = candles.length > 0 ? candles[0].time : s.lastSeriesEarliestTime;

                    if (savedRange) {
                        s.chart.timeScale().setVisibleLogicalRange({
                            from: savedRange.from + addedCount,
                            to: savedRange.to + addedCount,
                        });
                    }
                }
                catch (e) { /* network/server hiccup — user can keep scrolling, next threshold trigger retries */ }
                finally {
                    s.loadingMoreHistory = false;
                }
            };
        },

        bindPricePicked(containerId, dotNetRef) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.onPricePicked = (price) => dotNetRef.invokeMethodAsync('OnPricePicked', price);
        },

        dispose(containerId) {
            disposeSession(containerId);
        },

        // Manually triggers a resize. autoSize's own ResizeObserver
        // normally handles this automatically when the container's CSS
        // size changes, but calling this explicitly after a deliberate
        // layout change (like the maximize toggle) guarantees there's
        // no single-frame flash of stale dimensions.
        resize(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;
            try {
                const container = document.getElementById(containerId);
                if (container) {
                    s.chart.resize(container.clientWidth, container.clientHeight);
                }
            } catch (e) { /* autoSize will catch up regardless */ }
        },

        // Clears the inline height the browser's native resize:vertical
        // handle writes directly onto the resizable wrap element. Inline
        // styles win over CSS classes — without clearing it, the wrap
        // stayed stuck at whatever height the user last manually dragged
        // it to, both when entering maximize (instead of going full-
        // height) and when restoring back out of it (instead of
        // returning to the normal 480px default).
        clearWrapInlineHeight(containerId) {
            try {
                const container = document.getElementById(containerId);
                const wrap = container && container.parentElement;
                if (wrap) wrap.style.height = '';
            } catch (e) { /* non-critical, CSS will mostly still work without this */ }
        },
    };

    // Expose the private sessions Map to companion scripts
    // (chart-drawing-tools.js) so they can access priceToCoordinate
    // and chart references without duplicating state.
    window._vertexChartSessions = sessions;

})();












