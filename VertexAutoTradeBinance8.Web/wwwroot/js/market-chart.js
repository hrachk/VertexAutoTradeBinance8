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
        if (s.tooltipEl && s.tooltipEl.parentNode) s.tooltipEl.remove();
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

            chart.subscribeCrosshairMove((param) => {
                if (!param.point || !param.time || param.point.y < 0) {
                    tooltip.style.display = 'none';
                    return;
                }
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

            container.addEventListener('mousedown', (e) => {
                if (!session.entryPrice) return;
                const rect = container.getBoundingClientRect();
                const y = e.clientY - rect.top;

                // Existing SL/TP line under the cursor — drag THAT
                // specific line directly, taking priority over the
                // entry-armed "create new TP/SL" gesture below.
                const nearby = findNearbyDraggableLine(y);
                if (nearby) {
                    session.draggingLine = nearby.line;
                    session.draggingLineKind = nearby.kind;
                    session.draggingLineIdx = nearby.index;
                    container.style.cursor = 'grabbing';
                    e.preventDefault();
                    return;
                }

                if (!nearEntryLine(y)) return; // let normal chart pan/zoom/crosshair through elsewhere
                session.dragging = true;
                container.style.cursor = 'grabbing';
                e.preventDefault();
            });

            container.addEventListener('mousemove', (e) => {
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
                            (session.tpLines.length > 1 ? `TP${session.draggingLineIdx + 1}` : 'TP');
                        try { session.draggingLine.applyOptions({ price, title: `${kind} ${fmtPrice(price)}` }); } catch (err) {}
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
            });

            container.addEventListener('mouseup', (e) => {
                // Finished dragging an EXISTING SL/TP line to a new
                // price — commit it via a dedicated callback (distinct
                // from the entry-armed gesture's onTpChanged, since this
                // needs to know WHICH TP index moved when there are
                // multiple).
                if (session.draggingLine) {
                    const rect = container.getBoundingClientRect();
                    const y = e.clientY - rect.top;
                    const price = candleSeries.coordinateToPrice(y);
                    const kind = session.draggingLineKind;
                    const idx = session.draggingLineIdx;
                    session.draggingLine = null;
                    session.draggingLineKind = null;
                    session.draggingLineIdx = null;
                    container.style.cursor = 'crosshair';
                    if (price != null) {
                        if (kind === 'sl') {
                            if (session.onSlChanged) session.onSlChanged(price);
                        } else if (kind === 'tp') {
                            if (session.onTpChangedAt) session.onTpChangedAt(idx, price);
                        }
                    }
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
                    if (session.onTpChanged) session.onTpChanged(price);
                } else {
                    if (session.onSlChanged) session.onSlChanged(price);
                }
            });

            container.addEventListener('mouseleave', () => {
                if (session.dragging) { session.dragging = false; removePreview(); }
                if (session.draggingLine) {
                    session.draggingLine = null;
                    session.draggingLineKind = null;
                    session.draggingLineIdx = null;
                }
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
            s.volumeSeries.setData(klines.map(k => toVolume(k, 'rgba(34,197,94,0.5)', 'rgba(239,68,68,0.5)')));
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
            s.volumeSeries.update(toVolume(k, 'rgba(34,197,94,0.5)', 'rgba(239,68,68,0.5)'));

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
                    axisLabelVisible: true, title: `Entry ${fmtPrice(entry)}`,
                });
            }

            if (liqPrice && liqPrice > 0) {
                s.liqLine = s.candleSeries.createPriceLine({
                    price: liqPrice, color: '#f97316', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dashed,
                    axisLabelVisible: true, title: `Liq ${fmtPrice(liqPrice)}`,
                });
            }

            if (breakEvenPrice && breakEvenPrice > 0) {
                s.beLine = s.candleSeries.createPriceLine({
                    price: breakEvenPrice, color: '#94a3b8', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Dotted,
                    axisLabelVisible: true, title: `BE ${fmtPrice(breakEvenPrice)}`,
                });
            }
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
            this.hideTpSlLines(containerId);

            if (sl > 0) {
                s.slLine = s.candleSeries.createPriceLine({
                    price: sl, color: '#ef4444', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Solid,
                    axisLabelVisible: true, title: `SL ${fmtPrice(sl)}`,
                });
            }

            const tpList = Array.isArray(tps) ? tps : (tps > 0 ? [tps] : []);
            s.tpLines = [];
            tpList.forEach((tpPrice, i) => {
                if (!tpPrice || tpPrice <= 0) return;
                const label = tpList.length > 1 ? `TP${i + 1} ${fmtPrice(tpPrice)}` : `TP ${fmtPrice(tpPrice)}`;
                const line = s.candleSeries.createPriceLine({
                    price: tpPrice, color: '#22c55e', lineWidth: 1,
                    lineStyle: LightweightCharts.LineStyle.Solid,
                    axisLabelVisible: true, title: label,
                });
                s.tpLines.push({ line, index: i, price: tpPrice });
            });
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
            if (s.slLine) { try { s.candleSeries.removePriceLine(s.slLine); } catch (e) {} s.slLine = null; }
            for (const tp of (s.tpLines || [])) {
                try { s.candleSeries.removePriceLine(tp.line); } catch (e) {}
            }
            s.tpLines = [];
        },

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

            // Position the floating PnL label near the center-left of
            // the chart's visible width, vertically aligned with the
            // current price line.
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
                s.pnlLabelEl.style.fontSize = '12px';
                s.pnlLabelEl.style.fontWeight = '700';
                s.pnlLabelEl.style.fontFamily = 'monospace';
                s.pnlLabelEl.style.transform = 'translateY(-50%)';
                if (getComputedStyle(container).position === 'static') container.style.position = 'relative';
                container.appendChild(s.pnlLabelEl);
            }
            s.pnlLabelEl.style.left = '18%';
            s.pnlLabelEl.style.top = y + 'px';
            s.pnlLabelEl.style.background = color;
            s.pnlLabelEl.style.color = '#0a0d12';
            s.pnlLabelEl.textContent = `PnL ${sign}${pnl.toFixed(2)}`;
        },

        hidePositionLines(containerId) {
            const s = sessions.get(containerId);
            if (!s) return;
            s.candleSeries.applyOptions({ priceLineVisible: true });
            if (s.entryLine) { try { s.candleSeries.removePriceLine(s.entryLine); } catch (e) {} s.entryLine = null; }
            if (s.liqLine) { try { s.candleSeries.removePriceLine(s.liqLine); } catch (e) {} s.liqLine = null; }
            if (s.beLine) { try { s.candleSeries.removePriceLine(s.beLine); } catch (e) {} s.beLine = null; }
            if (s.pnlLine) { try { s.candleSeries.removePriceLine(s.pnlLine); } catch (e) {} s.pnlLine = null; }
            if (s.pnlLabelEl) { try { s.pnlLabelEl.remove(); } catch (e) {} s.pnlLabelEl = null; }
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
            s.onTpChangedAt = (index, price) => dotNetRef.invokeMethodAsync('OnTpDraggedAt', index, price);
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
                    s.volumeSeries.setData(combined.map(k => toVolume(k, 'rgba(34,197,94,0.5)', 'rgba(239,68,68,0.5)')));
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

})();
