window.alPulse = (() => {
    let ctx, canvas, W, H;
    const history = { score: [], conf: [], vol: [] };
    const MAX = 80;
    let raf;

    function init(id) {
        canvas = document.getElementById(id);
        if (!canvas) return;
        ctx = canvas.getContext('2d');
        W = canvas.offsetWidth; H = canvas.offsetHeight;
        canvas.width = W; canvas.height = H;
        window.addEventListener('resize', () => {
            W = canvas.offsetWidth; H = canvas.offsetHeight;
            canvas.width = W; canvas.height = H;
        });
        // seed
        for (let i = 0; i < MAX; i++) {
            history.score.push(50 + Math.random() * 10 - 5);
            history.conf.push(60 + Math.random() * 10 - 5);
            history.vol.push(30 + Math.random() * 10 - 5);
        }
        loop();
    }

    function push(score, conf, vol) {
        const add = (arr, v) => { arr.push(Math.max(0, Math.min(100, v))); if (arr.length > MAX) arr.shift(); };
        add(history.score, score + (Math.random() * 4 - 2));
        add(history.conf,  conf  + (Math.random() * 4 - 2));
        add(history.vol,   vol   + (Math.random() * 4 - 2));
    }

    function drawLine(data, color, glow) {
        if (!data.length) return;
        const step = W / (MAX - 1);
        ctx.beginPath();
        ctx.shadowColor = color;
        ctx.shadowBlur  = glow ? 12 : 0;
        ctx.strokeStyle = color;
        ctx.lineWidth   = 1.5;
        ctx.lineJoin    = 'round';
        data.forEach((v, i) => {
            const x = i * step;
            const y = H - (v / 100) * H;
            i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
        });
        ctx.stroke();
        ctx.shadowBlur = 0;
    }

    function drawGrid() {
        ctx.strokeStyle = '#1e2535';
        ctx.lineWidth = 1;
        for (let i = 1; i < 4; i++) {
            const y = (H / 4) * i;
            ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke();
        }
    }

    function loop() {
        ctx.clearRect(0, 0, W, H);
        drawGrid();
        drawLine(history.vol,   '#eab308', false);
        drawLine(history.conf,  '#22c55e', true);
        drawLine(history.score, '#3b82f6', true);

        // Glow dot at end
        [history.score, history.conf, history.vol].forEach((arr, i) => {
            if (!arr.length) return;
            const colors = ['#3b82f6', '#22c55e', '#eab308'];
            const v = arr[arr.length - 1];
            const x = W - 1;
            const y = H - (v / 100) * H;
            ctx.beginPath();
            ctx.arc(x, y, 3, 0, Math.PI * 2);
            ctx.fillStyle = colors[i];
            ctx.shadowColor = colors[i]; ctx.shadowBlur = 10;
            ctx.fill();
            ctx.shadowBlur = 0;
        });

        raf = requestAnimationFrame(loop);
    }

    return { init, push };
})();
