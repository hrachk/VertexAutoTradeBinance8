window.VertexLearningTimeline = (() => {

    let chart;

    function render(data) {
        const ctx = document
            .getElementById("learningTimelineChart")
            .getContext("2d");

        const labels = data.map(x => new Date(x.time));
        const scores = data.map(x => x.score);

        if (chart) chart.destroy();

        chart = new Chart(ctx, {
            type: "line",
            data: {
                labels,
                datasets: [{
                    data: scores,
                    borderColor: "#00ffc8",
                    borderWidth: 2,
                    tension: 0.25,
                    pointRadius: 0
                }]
            },
            options: {
                responsive: true,
                plugins: { legend: { display: false } },
                scales: {
                    x: { display: false },
                    y: { min: 0, max: 100 }
                }
            }
        });
    }

    return { render };
})();
