// =======================================================================
//  VERTEX AUTOTRADE AI — Learning Chart (PRO Tooltip 2.0, Smooth Update)
// =======================================================================

window.VertexLearningChart = {
    chart: null,

    /**
     * points: массив объектов AiLearningPointModel
     * {
     *   time: "2025-12-09T11:22:33Z",
     *   symbol: "BTCUSDT",
     *   score: 75,
     *   slope: 0.0123,          // 0.01 = 1%
     *   volatility: 0.0245,     // 0.02 = 2%
     *   liquidityDanger: true,
     *   confidence: 0.63        // 0–1
     * }
     */
    renderData: function (points) {
        if (!points || points.length === 0) return;

        const labels = points.map(p => new Date(p.time).toLocaleString());
        const score = points.map(p => p.score);
        const conf = points.map(p => p.confidence * 100);      // → %
        const slope = points.map(p => p.slope * 8000);         // усиление наклона
        const vol = points.map(p => p.volatility * 100);       // → %
        const liq = points.map(p => p.liquidityDanger ? 100 : 0);

        const canvas = document.getElementById("learningChart");
        if (!canvas) return;


        // Если график уже есть — просто обновляем данные без destroy (без мигания)
        //if (this.chart) {
        //    const c = this.chart;
        //    c.data.labels = labels;
        //    c.data.datasets[0].data = score;
        //    c.data.datasets[1].data = conf;
        //    c.data.datasets[2].data = slope;
        //    c.data.datasets[3].data = vol;
        //    c.data.datasets[4].data = liq;

        //    // Без анимации — максимально плавно
        //    c.update('none');
        //    return;
        //}
        if (this.chart) {
            try { this.chart.destroy(); } catch (e) { }
            this.chart = null;
        }

        // Инициализация нового графика
        this.chart = new Chart(canvas, {
            type: "line",
            data: {
                labels,
                datasets: [
                    {
                        label: "Score",
                        data: score,
                        borderColor: "#4db8ff",
                        borderWidth: 2,
                        tension: 0.25,
                        pointRadius: 0,
                        yAxisID: "yScore"
                    },
                    {
                        label: "Confidence %",
                        data: conf,
                        borderColor: "#a2ffce",
                        borderDash: [4, 3],
                        tension: 0.25,
                        pointRadius: 0,
                        yAxisID: "yScore"
                    },
                    {
                        label: "Slope ×8000",
                        data: slope,
                        borderColor: "#d099ff",
                        tension: 0.25,
                        pointRadius: 0,
                        yAxisID: "ySlope"
                    },
                    {
                        label: "Volatility %",
                        data: vol,
                        borderColor: "#26ffe0",
                        tension: 0.25,
                        pointRadius: 0,
                        yAxisID: "ySlope"
                    },
                    {
                        label: "Liquidity Danger",
                        data: liq,
                        type: "bar",
                        backgroundColor: "rgba(255,80,80,0.45)",
                        yAxisID: "yLiq"
                    }
                ]
            },

            options: {
                responsive: true,
                maintainAspectRatio: false,

                interaction: {
                    mode: "index",
                    intersect: false
                },

                plugins: {
                    legend: {
                        labels: { color: "#ddd" }
                    },

                    // 🔥 Используем только СВОЙ tooltip
                    tooltip: {
                        enabled: false,
                        external: customTooltipHandler
                    }
                },

                // Обновления без моргания
                animation: {
                    duration: 0
                },

                scales: {
                    yScore: {
                        type: "linear",
                        position: "left",
                        min: 0,
                        max: 100,
                        ticks: { color: "#ccc" }
                    },
                    ySlope: {
                        type: "linear",
                        position: "right",
                        ticks: { color: "#999" },
                        grid: { display: false }
                    },
                    yLiq: {
                        display: false,
                        min: 0,
                        max: 100
                    },
                    x: {
                        ticks: { color: "#999", maxRotation: 0 },
                        grid: { color: "rgba(255,255,255,0.03)" }
                    }
                }
            }
        });
    }
};

// =======================================================================
// 🔥 AI LOGIC — Probability / Summary / Notes
// =======================================================================

function analyzePointAI(score, conf, slope, vol, liq) {
    let p = 0;
    const notes = [];

    // Score
    if (score >= 70) {
        p += 25;
        notes.push("высокий Score → рынок стабильный");
    } else if (score >= 50) {
        p += 10;
        notes.push("средний Score → нейтрально");
    } else {
        notes.push("низкий Score → слабая структура");
    }

    // Confidence
    if (conf >= 60) {
        p += 20;
        notes.push("высокий Confidence → меньше манипуляций");
    } else if (conf < 40) {
        notes.push("низкий Confidence → хаос / шум");
    }

    // Slope (уже ×8000)
    if (slope > 300) {
        p += 20;
        notes.push("ускорение тренда → импульс вероятен");
    } else if (slope < -200) {
        notes.push("сильный откат → повышенный риск");
    }

    // Volatility
    if (vol < 1.5) {
        p += 15;
        notes.push("низкая волатильность → рынок готовится к движению");
    } else if (vol > 4) {
        p -= 10;
        notes.push("высокая волатильность → вероятны выбросы");
    }

    // Liquidity danger
    if (liq > 0) {
        p -= 40;
        notes.push("⚠ опасная ликвидность / охота за стопами");
    }

    p = Math.max(0, Math.min(100, p));

    const summary =
        p >= 70 ? "🔥 Высокая вероятность сигнала 5–10 мин" :
            p >= 50 ? "🟡 Средняя вероятность сигнала" :
                "⚫ Низкая вероятность сигнала";

    return {
        probability: p,
        summary,
        details: notes
    };
}

// =======================================================================
// 🔥 Custom Tooltip 2.0 — закрепление по клику, скролл, автопозиция
// =======================================================================

let tooltipLock = false;       // мышка внутри tooltip
let tooltipPinned = false;     // tooltip закреплён по клику
let tooltipHideTimer = null;

// context: { chart, tooltip }
function customTooltipHandler(context) {
    const { chart, tooltip } = context;

    let el = document.getElementById("chartjs-tooltip");
    if (!el) {
        el = document.createElement("div");
        el.id = "chartjs-tooltip";

        Object.assign(el.style, {
            position: "absolute",
            background: "rgba(0,0,0,0.92)",
            color: "#fff",
            padding: "12px",
            border: "1px solid #444",
            borderRadius: "8px",
            pointerEvents: "auto",
            fontSize: "13px",
            lineHeight: "1.45",
            maxWidth: "260px",
            maxHeight: "220px",
            overflowY: "auto",
            zIndex: 9999,
            opacity: 0,
            transition: "opacity 0.12s ease"
        });

        el.style.scrollbarWidth = "thin";

        // Hover — не исчезает
        el.addEventListener("mouseenter", () => {
            tooltipLock = true;
            if (tooltipHideTimer) {
                clearTimeout(tooltipHideTimer);
                tooltipHideTimer = null;
            }
            el.style.opacity = 1;
        });

        el.addEventListener("mouseleave", () => {
            tooltipLock = false;
            if (!tooltipPinned) {
                tooltipHideTimer = setTimeout(() => {
                    if (!tooltipLock && !tooltipPinned) el.style.opacity = 0;
                }, 200);
            }
        });

        // 📌 Клик по tooltip — закрепить / открепить
        el.addEventListener("click", () => {
            tooltipPinned = !tooltipPinned;

            if (tooltipPinned) {
                el.style.border = "1px solid #ffa400";
                el.style.boxShadow = "0 0 10px rgba(255,164,0,0.5)";
            } else {
                el.style.border = "1px solid #444";
                el.style.boxShadow = "none";
                el.style.opacity = 0;
            }
        });

        document.body.appendChild(el);
    }

    if (tooltip.opacity === 0) {
        if (!tooltipPinned && !tooltipLock) {
            if (tooltipHideTimer) clearTimeout(tooltipHideTimer);
            tooltipHideTimer = setTimeout(() => {
                if (!tooltipPinned && !tooltipLock) {
                    el.style.opacity = 0;
                }
            }, 200);
        }
        return;
    }

    const point = tooltip.dataPoints && tooltip.dataPoints[0];
    if (!point) return;

    const i = point.dataIndex;

    const score = chart.data.datasets[0].data[i];
    const conf = chart.data.datasets[1].data[i];
    const slope = chart.data.datasets[2].data[i];
    const vol = chart.data.datasets[3].data[i];
    const liq = chart.data.datasets[4].data[i];

    const ai = analyzePointAI(score, conf, slope, vol, liq);

    el.innerHTML = `
        <b>⏱ ${point.label}</b><br><br>

        <b>Score:</b> ${score}<br>
        <b>Confidence:</b> ${conf}%<br>
        <b>Slope ×8000:</b> ${slope}<br>
        <b>Volatility:</b> ${vol}%<br>
        <b>Liquidity:</b> ${liq > 0 ? "⚠ Danger" : "OK"}<br><br>

        <b>AI прогноз:</b> ${ai.summary}<br>
        <b>Вероятность сигнала:</b> ${ai.probability}%<br><br>

        ${ai.details.map(x => "• " + x).join("<br>")}
    `;

    if (!tooltipPinned) {
        el.style.opacity = 1;
        autoPositionTooltip(el, chart, tooltip);
    } else {
        el.style.opacity = 1;
    }
}



function customTooltipHandler(context) {
    return; // ⛔ временно отключено
}


// =============================
// 📌 Автопозиционирование
// =============================

function autoPositionTooltip(el, chart, tooltip) {
    const rect = chart.canvas.getBoundingClientRect();

    let left = rect.left + window.scrollX + tooltip.caretX + 20;
    let top = rect.top + window.scrollY + tooltip.caretY + 20;

    const tWidth = el.offsetWidth;
    const tHeight = el.offsetHeight;

    if (left + tWidth > window.innerWidth - 20) {
        left = rect.left + window.scrollX + tooltip.caretX - tWidth - 20;
    }

    if (top + tHeight > window.innerHeight - 20) {
        top = rect.top + window.scrollY + tooltip.caretY - tHeight - 20;
    }

    el.style.left = left + "px";
    el.style.top = top + "px";
}
