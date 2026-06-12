// ============================================================
// VERTEX MARKET CHART v2 — Professional Trading Terminal
// Charts: Candlestick price + Volume + RSI
// Indicators: EMA21, EMA55, VWAP, Bollinger Bands
// ============================================================

let priceChart = null, volumeChart = null, rsiChart = null;

// ── MATH HELPERS ──────────────────────────────────────────
function ema(arr, period) {
    const k = 2 / (period + 1);
    const out = []; let prev = arr[0]; out.push(prev);
    for (let i = 1; i < arr.length; i++) {
        prev = arr[i] * k + prev * (1 - k); out.push(prev);
    }
    return out;
}

function vwap(closes, volumes) {
    let cpv = 0, cv = 0;
    return closes.map((c, i) => {
        cpv += c * (volumes[i] || 0); cv += (volumes[i] || 0);
        return cv ? cpv / cv : c;
    });
}

function bollinger(closes, period = 20, mult = 2) {
    const mid = [], upper = [], lower = [];
    for (let i = 0; i < closes.length; i++) {
        const slice = closes.slice(Math.max(0, i - period + 1), i + 1);
        const m = slice.reduce((a, b) => a + b, 0) / slice.length;
        const std = Math.sqrt(slice.reduce((s, v) => s + (v - m) ** 2, 0) / slice.length);
        mid.push(m); upper.push(m + mult * std); lower.push(m - mult * std);
    }
    return { mid, upper, lower };
}

function rsi(closes, period = 14) {
    const out = new Array(period).fill(null);
    let gains = 0, losses = 0;
    for (let i = 1; i <= period; i++) {
        const d = closes[i] - closes[i - 1];
        d > 0 ? gains += d : losses -= d;
    }
    let ag = gains / period, al = losses / period;
    out.push(al === 0 ? 100 : 100 - 100 / (1 + ag / al));
    for (let i = period + 1; i < closes.length; i++) {
        const d = closes[i] - closes[i - 1];
        ag = (ag * (period - 1) + Math.max(d, 0)) / period;
        al = (al * (period - 1) + Math.max(-d, 0)) / period;
        out.push(al === 0 ? 100 : 100 - 100 / (1 + ag / al));
    }
    return out;
}

function liquiditySpikes(volumes, mult = 2.2) {
    const avg = volumes.reduce((a, b) => a + b, 0) / volumes.length;
    return volumes.map((v, i) => v > avg * mult ? { x: i, y: v } : null).filter(Boolean);
}

function safeDestroy(c) { try { if (c) c.destroy(); } catch {} }

// ── THEME ──────────────────────────────────────────────────
const T = {
    bg:    '#0a0d12',
    bg2:   '#111520',
    grid:  'rgba(30,37,53,0.8)',
    green: '#22c55e',
    red:   '#ef4444',
    blue:  '#3b82f6',
    purple:'#a855f7',
    yellow:'#eab308',
    cyan:  '#38bdf8',
    dim:   '#64748b',
    text:  '#94a3b8',
};

const baseOpts = {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
        legend: { display: false },
        tooltip: {
            backgroundColor: '#1a1f2e',
            borderColor: '#1e2535',
            borderWidth: 1,
            titleColor: '#e2e8f0',
            bodyColor: '#94a3b8',
        }
    },
    scales: {
        x: { display: false },
        y: {
            position: 'right',
            ticks: { color: T.text, font: { family: 'JetBrains Mono, monospace', size: 11 } },
            grid: { color: T.grid }
        }
    }
};

// ── CANDLESTICK PLUGIN ────────────────────────────────────
// Custom bar renderer for OHLC candles
function buildCandleDataset(klines) {
    return klines.map((k, i) => ({
        x: i,
        o: k.open, h: k.high, l: k.low, c: k.close,
        color: k.close >= k.open ? T.green : T.red
    }));
}

const candlePlugin = {
    id: 'candles',
    afterDatasetsDraw(chart) {
        const ds = chart.data.datasets[0];
        if (!ds || !ds._candles) return;
        const ctx = chart.ctx;
        const meta = chart.getDatasetMeta(0);
        const xScale = chart.scales.x;
        const yScale = chart.scales.y;

        ds._candles.forEach((c, i) => {
            const x = xScale.getPixelForValue(i);
            const open  = yScale.getPixelForValue(c.o);
            const close = yScale.getPixelForValue(c.c);
            const high  = yScale.getPixelForValue(c.h);
            const low   = yScale.getPixelForValue(c.l);
            const w = Math.max(1, xScale.getPixelForValue(1) - xScale.getPixelForValue(0) - 1);

            ctx.strokeStyle = c.color;
            ctx.fillStyle = c.color;

            // Wick
            ctx.beginPath();
            ctx.lineWidth = 1;
            ctx.moveTo(x, high);
            ctx.lineTo(x, low);
            ctx.stroke();

            // Body
            const top = Math.min(open, close);
            const h = Math.max(1, Math.abs(close - open));
            ctx.fillRect(x - w / 2, top, w, h);
        });
    }
};

// ── MAIN API ──────────────────────────────────────────────
window.marketChart = {
    render(symbol, tf, klines) {
        if (typeof Chart === 'undefined') return;
        if (!klines || klines.length < 5) return;

        const closes  = klines.map(k => k.close);
        const opens   = klines.map(k => k.open);
        const highs   = klines.map(k => k.high);
        const lows    = klines.map(k => k.low);
        const volumes = klines.map(k => k.volume);
        const labels  = klines.map((_, i) => i);

        const ema21  = ema(closes, 21);
        const ema55  = ema(closes, 55);
        const vwapArr = vwap(closes, volumes);
        const boll   = bollinger(closes, 20, 2);
        const rsiArr = rsi(closes, 14);
        const spikes = liquiditySpikes(volumes);
        const candles = klines.map((k, i) => ({
            o: k.open, h: k.high, l: k.low, c: k.close,
            color: k.close >= k.open ? T.green : T.red
        }));

        // ── PRICE CHART ────────────────────────────────────
        const priceCanvas = document.getElementById('priceChart');
        if (priceCanvas) {
            safeDestroy(priceChart);

            // Dummy dataset to hold candle data
            const dummyDs = {
                label: 'Candles',
                data: closes,
                _candles: candles,
                borderColor: 'transparent',
                backgroundColor: 'transparent',
                pointRadius: 0,
                borderWidth: 0,
            };

            priceChart = new Chart(priceCanvas, {
                type: 'line',
                plugins: [candlePlugin],
                data: {
                    labels,
                    datasets: [
                        dummyDs,
                        {
                            label: 'BB Upper',
                            data: boll.upper,
                            borderColor: 'rgba(59,130,246,0.3)',
                            borderWidth: 1,
                            borderDash: [4, 4],
                            pointRadius: 0,
                            fill: false,
                        },
                        {
                            label: 'BB Lower',
                            data: boll.lower,
                            borderColor: 'rgba(59,130,246,0.3)',
                            borderWidth: 1,
                            borderDash: [4, 4],
                            pointRadius: 0,
                            fill: 1,
                            backgroundColor: 'rgba(59,130,246,0.03)',
                        },
                        {
                            label: 'EMA21',
                            data: ema21,
                            borderColor: T.cyan,
                            borderWidth: 1.5,
                            pointRadius: 0,
                        },
                        {
                            label: 'EMA55',
                            data: ema55,
                            borderColor: T.purple,
                            borderWidth: 1.5,
                            pointRadius: 0,
                        },
                        {
                            label: 'VWAP',
                            data: vwapArr,
                            borderColor: T.yellow,
                            borderWidth: 1,
                            pointRadius: 0,
                            borderDash: [6, 3],
                        },
                    ]
                },
                options: { ...baseOpts }
            });
        }

        // ── VOLUME CHART ──────────────────────────────────
        const volCanvas = document.getElementById('volumeChart');
        if (volCanvas) {
            safeDestroy(volumeChart);

            const volColors = klines.map(k =>
                k.close >= k.open
                    ? 'rgba(34,197,94,0.5)'
                    : 'rgba(239,68,68,0.4)'
            );

            volumeChart = new Chart(volCanvas, {
                data: {
                    labels,
                    datasets: [
                        {
                            type: 'bar',
                            label: 'Volume',
                            data: volumes,
                            backgroundColor: volColors,
                            borderWidth: 0,
                        },
                        {
                            type: 'scatter',
                            label: 'Spike',
                            data: spikes,
                            pointRadius: 4,
                            pointBackgroundColor: T.cyan,
                            pointBorderColor: T.bg2,
                            pointBorderWidth: 1,
                        }
                    ]
                },
                options: {
                    ...baseOpts,
                    scales: {
                        x: { display: false },
                        y: {
                            position: 'right',
                            ticks: { color: T.text, maxTicksLimit: 3,
                                font: { family: 'JetBrains Mono, monospace', size: 10 } },
                            grid: { color: T.grid }
                        }
                    }
                }
            });
        }

        // ── RSI CHART ─────────────────────────────────────
        const rsiCanvas = document.getElementById('rsiChart');
        if (rsiCanvas) {
            safeDestroy(rsiChart);

            const rsiColors = rsiArr.map(v =>
                v === null ? 'transparent' :
                v >= 70 ? T.red :
                v <= 30 ? T.green : T.blue
            );

            rsiChart = new Chart(rsiCanvas, {
                type: 'line',
                data: {
                    labels,
                    datasets: [
                        {
                            label: 'RSI',
                            data: rsiArr,
                            borderColor: T.blue,
                            borderWidth: 1.5,
                            pointRadius: 0,
                            segment: {
                                borderColor: ctx => {
                                    const v = ctx.p1.parsed.y;
                                    return v >= 70 ? T.red : v <= 30 ? T.green : T.blue;
                                }
                            }
                        },
                        // Overbought line
                        {
                            label: 'OB',
                            data: new Array(klines.length).fill(70),
                            borderColor: 'rgba(239,68,68,0.3)',
                            borderWidth: 1,
                            borderDash: [4, 4],
                            pointRadius: 0,
                        },
                        // Oversold line
                        {
                            label: 'OS',
                            data: new Array(klines.length).fill(30),
                            borderColor: 'rgba(34,197,94,0.3)',
                            borderWidth: 1,
                            borderDash: [4, 4],
                            pointRadius: 0,
                        }
                    ]
                },
                options: {
                    ...baseOpts,
                    scales: {
                        x: { display: false },
                        y: {
                            position: 'right',
                            min: 0, max: 100,
                            ticks: {
                                color: T.text,
                                maxTicksLimit: 4,
                                font: { family: 'JetBrains Mono, monospace', size: 10 }
                            },
                            grid: { color: T.grid }
                        }
                    }
                }
            });
        }
    }
};

window.marketChartReady = true;
