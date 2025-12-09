// wwwroot/js/learningchart.js

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

        if (this.chart) {
            this.chart.destroy();
        }

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
                    }
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
