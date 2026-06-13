// ============================================================
// VERTEX MARKET CHART v3
// Pure Canvas 2D — no Chart.js for candlesticks
// RSI + Volume use Chart.js line/bar
// ============================================================

let _rsiChart = null, _volChart = null;

function safeDestroy(c) { try { if (c) c.destroy(); } catch {} }

// ── MATH ──────────────────────────────────────────────────
function calcEma(arr, p) {
    const k = 2 / (p + 1); let v = arr[0];
    return arr.map(x => (v = x * k + v * (1 - k)));
}
function calcVwap(closes, volumes) {
    let cpv = 0, cv = 0;
    return closes.map((c, i) => { cpv += c * volumes[i]; cv += volumes[i]; return cv ? cpv / cv : c; });
}
function calcBB(closes, p = 20) {
    return closes.map((_, i) => {
        const sl = closes.slice(Math.max(0, i - p + 1), i + 1);
        const m = sl.reduce((a, b) => a + b, 0) / sl.length;
        const std = Math.sqrt(sl.reduce((s, v) => s + (v - m) ** 2, 0) / sl.length);
        return { mid: m, upper: m + 2 * std, lower: m - 2 * std };
    });
}
function calcRsi(closes, p = 14) {
    const out = new Array(p).fill(null);
    let ag = 0, al = 0;
    for (let i = 1; i <= p; i++) { const d = closes[i] - closes[i-1]; d > 0 ? ag += d : al -= d; }
    ag /= p; al /= p;
    out.push(al === 0 ? 100 : 100 - 100 / (1 + ag / al));
    for (let i = p + 1; i < closes.length; i++) {
        const d = closes[i] - closes[i-1];
        ag = (ag * (p-1) + Math.max(d, 0)) / p;
        al = (al * (p-1) + Math.max(-d, 0)) / p;
        out.push(al === 0 ? 100 : 100 - 100 / (1 + ag / al));
    }
    return out;
}

// ── THEME ─────────────────────────────────────────────────
const C = {
    bg:     '#070a0f',
    grid:   'rgba(30,37,53,0.6)',
    green:  '#22c55e',
    red:    '#ef4444',
    blue:   '#3b82f6',
    purple: '#a855f7',
    yellow: '#eab308',
    cyan:   '#38bdf8',
    text:   '#64748b',
    txt2:   '#94a3b8',
};

// ── CANDLESTICK — Pure Canvas ─────────────────────────────
function drawCandles(canvasId, klines) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const dpr = window.devicePixelRatio || 1;
    const W = canvas.offsetWidth, H = canvas.offsetHeight;
    canvas.width  = W * dpr;
    canvas.height = H * dpr;
    canvas.style.width  = W + 'px';
    canvas.style.height = H + 'px';

    const ctx = canvas.getContext('2d');
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, W, H);
    ctx.fillStyle = C.bg;
    ctx.fillRect(0, 0, W, H);

    if (!klines || klines.length < 2) {
        ctx.fillStyle = C.text;
        ctx.font = '13px Inter';
        ctx.textAlign = 'center';
        ctx.fillText('No data', W / 2, H / 2);
        return;
    }

    const PAD_L = 8, PAD_R = 64, PAD_T = 16, PAD_B = 24;
    const CW = W - PAD_L - PAD_R;
    const CH = H - PAD_T - PAD_B;

    const closes  = klines.map(k => k.close);
    const highs   = klines.map(k => k.high);
    const lows    = klines.map(k => k.low);
    const volumes = klines.map(k => k.volume);

    const minP = Math.min(...lows);
    const maxP = Math.max(...highs);
    const range = maxP - minP || 1;

    const toY = v => PAD_T + CH - (v - minP) / range * CH;
    const toX = i => PAD_L + (i + 0.5) * CW / klines.length;
    const bw  = Math.max(1, CW / klines.length - 1.5);

    // Grid lines
    ctx.strokeStyle = C.grid;
    ctx.lineWidth = 1;
    for (let t = 0; t <= 4; t++) {
        const y = PAD_T + t * CH / 4;
        ctx.beginPath(); ctx.moveTo(PAD_L, y); ctx.lineTo(W - PAD_R, y); ctx.stroke();
        const price = maxP - t * range / 4;
        ctx.fillStyle = C.text;
        ctx.font = '10px JetBrains Mono, monospace';
        ctx.textAlign = 'left';
        ctx.fillText(price >= 1 ? price.toFixed(2) : price.toPrecision(4), W - PAD_R + 4, y + 4);
    }

    // BB bands
    const bb = calcBB(closes);
    const drawLine = (data, color, dash = []) => {
        ctx.beginPath(); ctx.strokeStyle = color; ctx.lineWidth = 1;
        ctx.setLineDash(dash);
        data.forEach((v, i) => { const x = toX(i), y = toY(v); i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y); });
        ctx.stroke(); ctx.setLineDash([]);
    };

    // BB fill
    ctx.beginPath();
    bb.forEach((b, i) => { const x = toX(i); i === 0 ? ctx.moveTo(x, toY(b.upper)) : ctx.lineTo(x, toY(b.upper)); });
    bb.slice().reverse().forEach((b, i) => { const x = toX(klines.length - 1 - i); ctx.lineTo(x, toY(b.lower)); });
    ctx.closePath();
    ctx.fillStyle = 'rgba(59,130,246,0.04)';
    ctx.fill();

    drawLine(bb.map(b => b.upper), 'rgba(59,130,246,0.3)', [4, 4]);
    drawLine(bb.map(b => b.lower), 'rgba(59,130,246,0.3)', [4, 4]);

    // EMA21, EMA55, VWAP
    drawLine(calcEma(closes, 21), C.cyan);
    drawLine(calcEma(closes, 55), C.purple);
    drawLine(calcVwap(closes, volumes), C.yellow, [6, 3]);

    // Candles
    klines.forEach((k, i) => {
        const x  = toX(i);
        const oY = toY(k.open);
        const cY = toY(k.close);
        const hY = toY(k.high);
        const lY = toY(k.low);
        const bull = k.close >= k.open;
        const color = bull ? C.green : C.red;

        // Wick
        ctx.strokeStyle = color;
        ctx.lineWidth = 1;
        ctx.beginPath(); ctx.moveTo(x, hY); ctx.lineTo(x, lY); ctx.stroke();

        // Body
        const bodyTop = Math.min(oY, cY);
        const bodyH   = Math.max(1, Math.abs(cY - oY));
        ctx.fillStyle = bull ? C.green : C.red;
        ctx.fillRect(x - bw / 2, bodyTop, bw, bodyH);
    });

    // Time labels
    ctx.fillStyle = C.text;
    ctx.font = '10px JetBrains Mono, monospace';
    ctx.textAlign = 'center';
    const step = Math.max(1, Math.floor(klines.length / 8));
    klines.forEach((k, i) => {
        if (i % step !== 0) return;
        const t = new Date(k.openTime);
        const label = t.getHours().toString().padStart(2,'0') + ':' + t.getMinutes().toString().padStart(2,'0');
        ctx.fillText(label, toX(i), H - 4);
    });
}

// ── RSI Chart ─────────────────────────────────────────────
function drawRsi(canvasId, closes) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const rsiArr = calcRsi(closes);
    const labels = closes.map((_, i) => i);

    safeDestroy(_rsiChart);
    _rsiChart = new Chart(canvas, {
        type: 'line',
        data: {
            labels,
            datasets: [
                {
                    data: rsiArr,
                    borderColor: C.blue,
                    borderWidth: 1.5,
                    pointRadius: 0,
                    segment: {
                        borderColor: ctx => {
                            const v = ctx.p1.parsed.y;
                            return v >= 70 ? C.red : v <= 30 ? C.green : C.blue;
                        }
                    }
                },
                {
                    data: new Array(closes.length).fill(70),
                    borderColor: 'rgba(239,68,68,0.35)',
                    borderWidth: 1, borderDash: [4,4], pointRadius: 0,
                },
                {
                    data: new Array(closes.length).fill(30),
                    borderColor: 'rgba(34,197,94,0.35)',
                    borderWidth: 1, borderDash: [4,4], pointRadius: 0,
                }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false, animation: false,
            plugins: { legend: { display: false }, tooltip: {
                backgroundColor: '#1a1f2e', borderColor: '#1e2535', borderWidth: 1,
                callbacks: { label: ctx => ctx.dataset.data[ctx.dataIndex] !== null ? 'RSI: ' + ctx.parsed.y?.toFixed(1) : '' }
            }},
            scales: {
                x: { display: false },
                y: { min: 0, max: 100, position: 'right',
                    ticks: { color: C.txt2, maxTicksLimit: 3, font: { family: 'JetBrains Mono', size: 10 } },
                    grid: { color: C.grid }
                }
            }
        }
    });
}

// ── Volume Chart ──────────────────────────────────────────
function drawVolume(canvasId, klines) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const volumes = klines.map(k => k.volume);
    const avg = volumes.reduce((a, b) => a + b, 0) / volumes.length;
    const spikes = volumes.map((v, i) => v > avg * 2.2 ? { x: i, y: v } : null).filter(Boolean);
    const colors = klines.map(k => k.close >= k.open ? 'rgba(34,197,94,0.5)' : 'rgba(239,68,68,0.4)');

    safeDestroy(_volChart);
    _volChart = new Chart(canvas, {
        data: {
            labels: klines.map((_, i) => i),
            datasets: [
                { type: 'bar', data: volumes, backgroundColor: colors, borderWidth: 0 },
                { type: 'scatter', data: spikes, pointRadius: 4,
                  pointBackgroundColor: C.cyan, pointBorderWidth: 0 }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false, animation: false,
            plugins: { legend: { display: false }, tooltip: { backgroundColor: '#1a1f2e', borderColor: '#1e2535', borderWidth: 1 } },
            scales: {
                x: { display: false },
                y: { position: 'right',
                    ticks: { color: C.txt2, maxTicksLimit: 3, font: { family: 'JetBrains Mono', size: 10 } },
                    grid: { color: C.grid }
                }
            }
        }
    });
}

// ── PUBLIC API ────────────────────────────────────────────
window.marketChart = {
    render(symbol, tf, klines) {
        if (!klines || klines.length < 5) return;
        const closes = klines.map(k => k.close);
        drawCandles('priceChart', klines);
        drawRsi('rsiChart', closes);
        drawVolume('volumeChart', klines);
    }
};

window.marketChartReady = true;
