window.learningChartRender = (canvasId, labels, data) => {
  const ctx = document.getElementById(canvasId).getContext('2d');

  new Chart(ctx, {
    type: "line",
    data: {
      labels: labels,
      datasets: [
        {
          label: "Confidence %",
          data: data,
          borderWidth: 2,
          borderColor: "#4FC3F7",
          backgroundColor: "rgba(79,195,247,0.25)",
          pointRadius: 1,
          tension: 0.3
        }
      ]
    },
    options: {
      responsive: true,
      plugins: {
        legend: {
          position: "top"
        }
      },
      scales: {
        y: {
          beginAtZero: true,
          max: 100
        }
      }
    }
  });
};
