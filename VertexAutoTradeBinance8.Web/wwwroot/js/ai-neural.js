// AI Neural Activity Canvas — live oscilloscope-style visualization
window.aiNeuralCanvas = (function () {
    const history = {};  // canvasId → {data, raf}
    const MAX = 120;
    const COLORS = ['#f97316','#a855f7','#3b82f6','#22c55e','#eab308','#38bdf8'];

    return function(id, powers) {
        const canvas = document.getElementById(id);
        if (!canvas) return;
        const ctx = canvas.getContext('2d');
        const dpr = window.devicePixelRatio || 1;
        const W = canvas.offsetWidth, H = canvas.offsetHeight || 100;
        canvas.width  = W * dpr;
        canvas.height = H * dpr;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        if (!history[id]) history[id] = { lines: [], tick: 0 };
        const h = history[id];

        // Update each "neuron line" with its power value
        const n = Math.min(powers.length, 6);
        while (h.lines.length < n) h.lines.push(new Array(MAX).fill(0));

        powers.slice(0, n).forEach((p, i) => {
            const noise = (Math.random() - 0.5) * 0.08;
            const val = Math.max(0, Math.min(1, p + noise));
            h.lines[i].push(val);
            if (h.lines[i].length > MAX) h.lines[i].shift();
        });
        h.tick++;

        // Draw
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = '#060810';
        ctx.fillRect(0, 0, W, H);

        // Grid
        ctx.strokeStyle = 'rgba(30,37,53,0.5)';
        ctx.lineWidth = 1;
        for (let y = 0; y <= 4; y++) {
            const py = (y / 4) * H;
            ctx.beginPath(); ctx.moveTo(0, py); ctx.lineTo(W, py); ctx.stroke();
        }

        // Lines
        h.lines.forEach((line, li) => {
            if (!line.length) return;
            const color = COLORS[li % COLORS.length];
            ctx.beginPath();
            ctx.strokeStyle = color;
            ctx.lineWidth = 1.5;
            ctx.shadowColor = color;
            ctx.shadowBlur = 6;
            line.forEach((v, i) => {
                const x = (i / (MAX - 1)) * W;
                const y = H - v * (H * 0.85) - H * 0.07;
                i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
            });
            ctx.stroke();
            ctx.shadowBlur = 0;

            // Dot at end
            const last = line[line.length - 1];
            const ex = W - 1, ey = H - last * (H * 0.85) - H * 0.07;
            ctx.beginPath();
            ctx.arc(ex, ey, 3, 0, Math.PI * 2);
            ctx.fillStyle = color;
            ctx.shadowColor = color; ctx.shadowBlur = 10;
            ctx.fill(); ctx.shadowBlur = 0;
        });
    };
})();
