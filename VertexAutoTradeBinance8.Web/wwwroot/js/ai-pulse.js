window.aiPulse = (() => {
    let ctx, w, h;
    let points = [];

    function init(id) {
        const c = document.getElementById(id);
        ctx = c.getContext("2d");
        w = c.width;
        h = c.height;

        for (let i = 0; i < 200; i++)
            points.push(h / 2);

        requestAnimationFrame(loop);
    }

    function loop() {
        ctx.clearRect(0, 0, w, h);

        // shift
        points.shift();
        points.push(
            h / 2 +
            Math.sin(Date.now() / 400) * 30 +
            (Math.random() - 0.5) * 20
        );

        drawLine("#FFD86A", 4, 0.15);
        drawLine("#00FFE1", 2, 0.35);

        requestAnimationFrame(loop);
    }

    function drawLine(color, width, alpha) {
        ctx.beginPath();
        ctx.strokeStyle = color;
        ctx.lineWidth = width;
        ctx.globalAlpha = alpha;
        ctx.shadowBlur = 25;
        ctx.shadowColor = color;

        points.forEach((y, i) => {
            const x = (i / points.length) * w;
            i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
        });

        ctx.stroke();
        ctx.globalAlpha = 1;
    }

    return { init };
})();
