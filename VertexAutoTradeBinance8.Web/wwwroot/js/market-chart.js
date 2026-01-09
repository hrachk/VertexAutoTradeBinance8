// ================= GLOBAL STATE =================
let priceChart = null;
let volumeChart = null;

// ================== HELPERS ==================
function ema(values, period) {
    if (!values || values.length === 0) return [];
    const k = 2 / (period + 1);
    const out = [];
    let prev = values[0];
    out.push(prev);
    for (let i = 1; i < values.length; i++) {
        prev = values[i] * k + prev * (1 - k);
        out.push(prev);
    }
    return out;
}

function vwap(prices, volumes) {
    let cumPV = 0, cumV = 0;
    return prices.map((p, i) => {
        const v = volumes[i] ?? 0;
        cumPV += p * v;
        cumV += v;
        return cumV === 0 ? p : cumPV / cumV;
    });
}

// ================== LIQUIDITY SPIKES ==================
function liquiditySpikes(volumes, multiplier = 2.2) {
    if (!volumes || volumes.length === 0) return [];

    const avg =
        volumes.reduce((a, b) => a + b, 0) / volumes.length;

    return volumes
        .map((v, i) => (v > avg * multiplier ? { x: i, y: v } : null))
        .filter(Boolean);
}

// ================== PERMISSION MARKERS ==================
function permissionDataset(symbol, tf, closes) {
    const events = window.vertex?.tradePermission?.get(symbol, tf) || [];
    if (!events.length) return null;

    return {
        type: "scatter",
        label: "Permission",
        data: events
            .filter(e => closes[e.index] !== undefined)
            .map(e => ({
                x: e.index,
                y: closes[e.index]
            })),
        pointRadius: 4,
        pointHoverRadius: 5,
        pointBackgroundColor: events.map(e =>
            e.allow ? "#22c55e" : "#ef4444"
        ),
        showLine: false
    };
}

// ================== SAFE DESTROY ==================
function safeDestroy(chart) {
    try {
        if (chart) chart.destroy();
    } catch { }
}

// ================== MAIN API ==================
window.marketChart = {
    render(symbol, tf, closes, volumes) {

        // ---------- HARD GUARDS ----------
        if (typeof Chart === "undefined") {
            console.warn("Chart.js not loaded");
            return;
        }

        const priceCanvas = document.getElementById("priceChart");
        const volumeCanvas = document.getElementById("volumeChart");

        if (!priceCanvas || !volumeCanvas) {
            console.warn("Canvas not ready");
            return;
        }

        if (!closes || closes.length < 5) {
            console.warn("Not enough data to render chart");
            return;
        }

        // ---------- PREP DATA ----------
        const labels = closes.map((_, i) => i);
        const ema21 = ema(closes, 21);
        const ema55 = ema(closes, 55);
        const vwapArr = vwap(closes, volumes);

        safeDestroy(priceChart);
        safeDestroy(volumeChart);

        // ---------- PRICE CHART ----------
        const priceDatasets = [
            {
                label: "Close",
                data: closes,
                borderColor: "#22c55e",
                borderWidth: 2,
                pointRadius: 0,
                tension: 0.25
            },
            {
                label: "EMA21",
                data: ema21,
                borderColor: "#38bdf8",
                borderWidth: 1,
                pointRadius: 0
            },
            {
                label: "EMA55",
                data: ema55,
                borderColor: "#a855f7",
                borderWidth: 1,
                pointRadius: 0
            },
            {
                label: "VWAP",
                data: vwapArr,
                borderColor: "#f59e0b",
                borderWidth: 1,
                pointRadius: 0
            }
        ];

        const perm = permissionDataset(symbol, tf, closes);
        if (perm) priceDatasets.push(perm);

        priceChart = new Chart(priceCanvas, {
            type: "line",
            data: { labels, datasets: priceDatasets },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                interaction: { mode: "index", intersect: false },
                plugins: {
                    legend: { display: false },
                    tooltip: { enabled: true }
                },
                scales: {
                    x: { display: false },
                    y: {
                        ticks: { color: "#94a3b8" },
                        grid: { color: "rgba(255,255,255,0.05)" }
                    }
                }
            }
        });

        // ---------- VOLUME ----------
        const spikes = liquiditySpikes(volumes);

        volumeChart = new Chart(volumeCanvas, {
            data: {
                labels,
                datasets: [
                    {
                        type: "bar",
                        data: volumes,
                        backgroundColor: "#334155"
                    },
                    {
                        type: "scatter",
                        data: spikes,
                        pointRadius: 3,
                        pointBackgroundColor: "#38bdf8"
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                plugins: { legend: { display: false } },
                scales: {
                    x: { display: false },
                    y: {
                        ticks: { color: "#94a3b8" },
                        grid: { color: "rgba(255,255,255,0.05)" }
                    }
                }
            }
        });
    }
};

// ================= READY FLAG FOR BLAZOR =================
window.marketChartReady = true;
