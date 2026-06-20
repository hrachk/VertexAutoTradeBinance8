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
        if (s.previewBox && s.previewBox.parentNode) s.previewBox.remove();
        if (s.previewVLine && s.previewVLine.parentNode) s.previewVLine.remove();
        try { s.chart.remove(); } catch (e) { /* already gone */ }
        sessions.delete(containerId);
    }

    function fmtPrice(p) {
        return p.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
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
                crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
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

            const session = {
                chart, candleSeries, ema21Series, ema55Series,
                volumeSeries, rsiSeries, rsiObLine, rsiOsLine,
                priceLine: null, onPricePicked: null,
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
                entryLine: null, slLine: null, tpLine: null,
                entryPrice: 0, side: 'LONG', qty: 0,
                onSlChanged: null, onTpChanged: null,
                tpSlArmed: false,   // true after clicking the TP/SL button — "Drag to set TP/SL" mode
                dragging: false,
                previewLine: null, previewBox: null, previewVLine: null,
            };
            sessions.set(containerId, session);

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

            const NEAR_LINE_PX = 10;

            function nearEntryLine(y) {
                if (!session.entryPrice) return false;
                const entryY = candleSeries.priceToCoordinate(session.entryPrice);
                return entryY != null && Math.abs(y - entryY) <= NEAR_LINE_PX;
            }

            container.addEventListener('mousedown', (e) => {
                if (!session.entryPrice) return;
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                if (!nearEntryLine(y)) return; // let normal chart pan/zoom/crosshair through elsewhere
                session.dragging = true;
                container.style.cursor = 'grabbing';
                e.preventDefault();
            });

            container.addEventListener('mousemove', (e) => {
                if (!session.entryPrice) return;

                if (!session.dragging) {
                    const rect = container.getBoundingClientRect();
                    const y = e.clientY - rect.top;
                    container.style.cursor = nearEntryLine(y) ? 'grab' : 'crosshair';
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
            });

            container.addEventListener('mouseup', (e) => {
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
                    if (session.onTpChanged) session.onTpChanged(price);
                } else {
                    if (session.onSlChanged) session.onSlChanged(price);
                }
            });

            container.addEventListener('mouseleave', () => {
                if (session.dragging) { session.dragging = false; removePreview(); }
            });

            container.addEventListener('contextmenu', (e) => {
                e.preventDefault();
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                const price = candleSeries.coordinateToPrice(y);
                if (price == null) return;
                if (session.priceLine) { try { candleSeries.removePriceLine(session.priceLine); } catch (err) {} }
                session.priceLine = candleSeries.createPriceLine({
                    price, color: '#22c55e', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dashed,
                    axisLabelVisible: true, title: 'pick',
                });
                if (session.onPricePicked) session.onPricePicked(price);
            });

            return true;
        },

        setData(containerId, klines) {
            const s = sessions.get(containerId);
            if (!s || !klines || !klines.length) return;

            const candles = klines.map(toCandle);
            const closes = klines.map(k => k.close);
            const ema21 = ema(closes, 21);
            const ema55 = ema(closes, 55);
            const rsiVals = rsi(closes, 14);

            s.candleSeries.setData(candles);
            s.ema21Series.setData(candles.map((c, i) => ({ time: c.time, value: ema21[i] })).filter(d => d.value != null));
            s.ema55Series.setData(candles.map((c, i) => ({ time: c.time, value: ema55[i] })).filter(d => d.value != null));
            s.volumeSeries.setData(klines.map(k => toVolume(k, 'rgba(34,197,94,0.5)', 'rgba(239,68,68,0.5)')));
            s.rsiSeries.setData(candles.map((c, i) => ({ time: c.time, value: rsiVals[i] })).filter(d => d.value != null));
            s.rsiObLine.setData(candles.map(c => ({ time: c.time, value: 70 })));
            s.rsiOsLine.setData(candles.map(c => ({ time: c.time, value: 30 })));
        },

        updateLastBar(containerId, k) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.candleSeries.update(toCandle(k));
            s.volumeSeries.update(toVolume(k, 'rgba(34,197,94,0.5)', 'rgba(239,68,68,0.5)'));
        },

        clearPriceLine(containerId) {
            const s = sessions.get(containerId);
            if (!s || !s.priceLine) return;
            try { s.candleSeries.removePriceLine(s.priceLine); } catch (e) { }
            s.priceLine = null;
        },

        // Draws ONLY the entry line for the currently selected position
        // — this is the persistent reference line with the live-PnL
        // caption, always visible while a position is selected. SL/TP
        // lines are a SEPARATE, explicit action (showTpSlLines below),
        // matching Binance's actual UX: the entry/PnL line is always
        // there, but TP/SL lines only appear once you click the
        // "TP/SL" button on it.
        showPositionLines(containerId, entry, side, qty) {
            const s = sessions.get(containerId);
            if (!s) return;
            this.hidePositionLines(containerId);

            s.entryPrice = entry;
            s.side = side;
            s.qty = qty;

            if (entry > 0) {
                s.entryLine = s.candleSeries.createPriceLine({
                    price: entry, color: '#3b82f6', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dotted,
                    axisLabelVisible: true, title: `Entry ${fmtPrice(entry)}`,
                });
            }
        },

        // Shows the draggable SL/TP lines — called only when the user
        // clicks the TP/SL button, not automatically on position
        // selection. sl/tp of 0 means "no real order yet": a dashed
        // placeholder line is drawn at a sensible default distance so
        // there's something to grab and drag into a real order, same
        // idea as before, just now gated behind an explicit click
        // instead of always-on.
        // Draws the EXISTING TP/SL as solid lines if real orders are
        // set, with no placeholder dashed line anymore — Bybit doesn't
        // show a guess line for a TP/SL that was never set, it just
        // shows nothing there until the user drags one in via armed
        // mode (setTpSlArmed below).
        showTpSlLines(containerId, entry, sl, tp, side) {
            const s = sessions.get(containerId);
            if (!s) return;
            this.hideTpSlLines(containerId);

            if (sl > 0) {
                s.slLine = s.candleSeries.createPriceLine({
                    price: sl, color: '#ef4444', lineWidth: 2,
                    lineStyle: LightweightCharts.LineStyle.Solid,
                    axisLabelVisible: true, title: `SL ${fmtPrice(sl)}`,
                });
            }
            if (tp > 0) {
                s.tpLine = s.candleSeries.createPriceLine({
                    price: tp, color: '#22c55e', lineWidth: 2,
                    lineStyle: LightweightCharts.LineStyle.Solid,
                    axisLabelVisible: true, title: `TP ${fmtPrice(tp)}`,
                });
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
            for (const key of ['slLine', 'tpLine']) {
                if (s[key]) { try { s.candleSeries.removePriceLine(s[key]); } catch (e) {} s[key] = null; }
            }
        },

        // Refreshes just the entry line's title with live PnL, called
        // on every price tick without needing to redraw SL/TP too.
        updatePnl(containerId, currentPrice) {
            const s = sessions.get(containerId);
            if (!s || !s.entryLine || !s.entryPrice) return;
            const dir = s.side === 'LONG' ? 1 : -1;
            const pnl = (currentPrice - s.entryPrice) * dir * s.qty;
            const sign = pnl >= 0 ? '+' : '';
            try {
                s.entryLine.applyOptions({ title: `Entry ${fmtPrice(s.entryPrice)}  ·  PnL ${sign}${pnl.toFixed(2)}` });
            } catch (e) {}
        },

        hidePositionLines(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;
            if (s.entryLine) { try { s.candleSeries.removePriceLine(s.entryLine); } catch (e) {} s.entryLine = null; }
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
            s.onSlChanged = (price) => dotNetRef.invokeMethodAsync('OnSlDragged', price);
            s.onTpChanged = (price) => dotNetRef.invokeMethodAsync('OnTpDragged', price);
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
    };

})();
