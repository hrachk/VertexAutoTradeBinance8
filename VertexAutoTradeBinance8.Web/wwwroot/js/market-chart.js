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
            };
            sessions.set(containerId, session);

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

        bindPricePicked(containerId, dotNetRef) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.onPricePicked = (price) => dotNetRef.invokeMethodAsync('OnPricePicked', price);
        },

        dispose(containerId) {
            disposeSession(containerId);
        },
    };

})();
