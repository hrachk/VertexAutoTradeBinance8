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
        try { s.chart.remove(); } catch (e) { /* already gone */ }
        sessions.delete(containerId);
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
                dragTarget: null, // 'sl' | 'tp' | null while dragging
            };
            sessions.set(containerId, session);

            // ── Draggable SL/TP lines ──────────────────────────────
            // Lightweight Charts v5's core API does not expose a
            // built-in draggable price line (it's an open feature
            // request, github.com/tradingview/lightweight-charts/
            // issues/1086 — implemented only as third-party plugins so
            // far). This implements the same interaction manually using
            // the library's own coordinateToPrice/priceToCoordinate,
            // which is the standard documented approach for this.
            const HIT_TOLERANCE_PX = 8;

            function priceLineY(line) {
                if (!line) return null;
                try { return candleSeries.priceToCoordinate(line.options().price); }
                catch (e) { return null; }
            }

            function pnlText(currentPrice) {
                if (!session.entryPrice || !session.qty) return '';
                const dir = session.side === 'LONG' ? 1 : -1;
                const pnl = (currentPrice - session.entryPrice) * dir * session.qty;
                const sign = pnl >= 0 ? '+' : '';
                return `  PnL ${sign}${pnl.toFixed(2)}`;
            }

            container.addEventListener('mousedown', (e) => {
                if (!session.slLine && !session.tpLine) return;
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                const slY = priceLineY(session.slLine);
                const tpY = priceLineY(session.tpLine);
                if (slY != null && Math.abs(y - slY) <= HIT_TOLERANCE_PX) {
                    session.dragTarget = 'sl';
                    container.style.cursor = 'grabbing';
                    e.preventDefault();
                } else if (tpY != null && Math.abs(y - tpY) <= HIT_TOLERANCE_PX) {
                    session.dragTarget = 'tp';
                    container.style.cursor = 'grabbing';
                    e.preventDefault();
                }
            });

            container.addEventListener('mousemove', (e) => {
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;

                // Hover feedback even when not dragging, so the lines
                // feel grabbable before the user commits to a drag.
                if (!session.dragTarget) {
                    const slY = priceLineY(session.slLine);
                    const tpY = priceLineY(session.tpLine);
                    const nearLine = (slY != null && Math.abs(y - slY) <= HIT_TOLERANCE_PX) ||
                                      (tpY != null && Math.abs(y - tpY) <= HIT_TOLERANCE_PX);
                    container.style.cursor = nearLine ? 'grab' : 'crosshair';
                    return;
                }

                const price = candleSeries.coordinateToPrice(y);
                if (price == null) return;

                if (session.dragTarget === 'sl' && session.slLine) {
                    try { candleSeries.removePriceLine(session.slLine); } catch (err) {}
                    session.slLine = candleSeries.createPriceLine({
                        price, color: '#ef4444', lineWidth: 2,
                        lineStyle: LightweightCharts.LineStyle.Solid,
                        axisLabelVisible: true,
                        title: `SL ${price.toFixed(6).replace(/0+$/,'').replace(/\.$/,'')}`,
                    });
                } else if (session.dragTarget === 'tp' && session.tpLine) {
                    try { candleSeries.removePriceLine(session.tpLine); } catch (err) {}
                    session.tpLine = candleSeries.createPriceLine({
                        price, color: '#22c55e', lineWidth: 2,
                        lineStyle: LightweightCharts.LineStyle.Solid,
                        axisLabelVisible: true,
                        title: `TP ${price.toFixed(6).replace(/0+$/,'').replace(/\.$/,'')}`,
                    });
                }
            });

            container.addEventListener('mouseup', (e) => {
                if (!session.dragTarget) return;
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;
                const price = candleSeries.coordinateToPrice(y);
                const target = session.dragTarget;
                session.dragTarget = null;
                container.style.cursor = 'crosshair';
                if (price == null) return;

                if (target === 'sl' && session.onSlChanged) session.onSlChanged(price);
                if (target === 'tp' && session.onTpChanged) session.onTpChanged(price);
            });

            container.addEventListener('mouseleave', () => {
                session.dragTarget = null;
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

        // Draws the Entry/SL/TP lines for the currently selected open
        // position. entry/sl/tp are plain numbers; sl/tp of 0 means
        // "no real order yet" — a placeholder line is still drawn
        // (a sensible default distance from entry) so there's always
        // something visible to grab and drag into a real TP/SL,
        // exactly like Bybit/Binance's chart-based position editor.
        showPositionLines(containerId, entry, sl, tp, side, qty) {
            const s = sessions.get(containerId);
            if (!s) return;
            this.hidePositionLines(containerId);

            s.entryPrice = entry;
            s.side = side;
            s.qty = qty;

            const isLong = side === 'LONG';
            const fmt = (p) => p.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');

            if (entry > 0) {
                s.entryLine = s.candleSeries.createPriceLine({
                    price: entry, color: '#3b82f6', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dotted,
                    axisLabelVisible: true, title: `Entry ${fmt(entry)}`,
                });
            }

            // Placeholder distance when no real SL/TP exists yet: 2%
            // of entry price in the protective direction. Purely a
            // starting position for the user to drag from — has no
            // effect on the exchange until they actually drag it
            // (which fires onSlChanged/onTpChanged) or it stays
            // untouched (in which case nothing was ever placed).
            const placeholderDist = entry * 0.02;
            const slPrice = sl > 0 ? sl : (isLong ? entry - placeholderDist : entry + placeholderDist);
            const tpPrice = tp > 0 ? tp : (isLong ? entry + placeholderDist : entry - placeholderDist);

            s.slLine = s.candleSeries.createPriceLine({
                price: slPrice, color: '#ef4444', lineWidth: 2,
                lineStyle: sl > 0 ? LightweightCharts.LineStyle.Solid : LightweightCharts.LineStyle.Dashed,
                axisLabelVisible: true,
                title: sl > 0 ? `SL ${fmt(slPrice)}` : `SL (drag to set)`,
            });
            s.tpLine = s.candleSeries.createPriceLine({
                price: tpPrice, color: '#22c55e', lineWidth: 2,
                lineStyle: tp > 0 ? LightweightCharts.LineStyle.Solid : LightweightCharts.LineStyle.Dashed,
                axisLabelVisible: true,
                title: tp > 0 ? `TP ${fmt(tpPrice)}` : `TP (drag to set)`,
            });
        },

        // Refreshes just the entry line's title with live PnL, called
        // on every price tick without needing to redraw SL/TP too.
        updatePnl(containerId, currentPrice) {
            const s = sessions.get(containerId);
            if (!s || !s.entryLine || !s.entryPrice) return;
            const dir = s.side === 'LONG' ? 1 : -1;
            const pnl = (currentPrice - s.entryPrice) * dir * s.qty;
            const sign = pnl >= 0 ? '+' : '';
            const fmt = (p) => p.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
            try {
                s.entryLine.applyOptions({ title: `Entry ${fmt(s.entryPrice)}  ·  PnL ${sign}${pnl.toFixed(2)}` });
            } catch (e) {}
        },

        hidePositionLines(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;
            for (const key of ['entryLine', 'slLine', 'tpLine']) {
                if (s[key]) { try { s.candleSeries.removePriceLine(s[key]); } catch (e) {} s[key] = null; }
            }
            s.entryPrice = 0;
            s.qty = 0;
        },

        // Binds the C# callbacks that fire once a drag is committed
        // (mouseup), so the actual exchange order can be replaced.
        // Doesn't fire on every mousemove — only once the user lets go,
        // matching the "drag, then it actually applies" feel of Bybit's
        // chart-based SL/TP editor rather than firing an API call per
        // pixel of mouse movement.
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
