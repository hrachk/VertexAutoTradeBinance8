// =======================================================
// AI PULSE ENGINE — VERTEX AUTOTRADE AI
// Canvas-based, realtime, no Blazor re-render
// =======================================================

window.AiPulse = (() => {

    const CANVAS_ID = "aiPulseCanvas";
    const MAX_POINTS = 220;
    const BG_FADE_ALPHA = 0.18;

    let canvas, ctx;
    let w = 0, h = 0;
    let running = false;

    // --- DATA BUFFERS ---
    const decision = [];
    const noise = [];
    const confidence = [];

    // ===================================================
    // INIT
    // ===================================================
    function init() {
        canvas = document.getElementById(CANVAS_ID);
        if (!canvas) {
            console.warn("[AI-PULSE] Canvas not found");
            return;
        }

        ctx = canvas.getContext("2d");
        resize();
        window.addEventListener("resize", resize);

        running = true;
        requestAnimationFrame(loop);
    }

    function resize() {
        const dpr = window.devicePixelRatio || 1;
        w = canvas.clientWidth;
        h = canvas.clientHeight;

        canvas.width = w * dpr;
        canvas.height = h * dpr;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    // ===================================================
    // DATA PUSH
    // ===================================================
    function push(arr, v) {
        arr.push(v);
        if (arr.length > MAX_POINTS) arr.shift();
    }

    function pushData(dec, noi, conf) {
        push(decision, clamp(dec));
        push(noise, clamp(noi));
        push(confidence, clamp(conf));
    }

    function clamp(v) {
        if (v > 50) return 50;
        if (v < -50) return -50;
        return v;
    }

    // ===================================================
    // DRAWING
    // ===================================================
    function loop() {
        if (!running) return;

        fadeBackground();

        drawLine(
            noise,
            "#3b6cff",
            "rgba(80,120,255,0.55)",
            18
        );

        drawLine(
            decision,
            "#ffd24a",
            "rgba(255,210,80,0.75)",
            30
        );

        drawLine(
            confidence,
            "#00ffc8",
            "rgba(0,255,200,0.85)",
            22
        );

        requestAnimationFrame(loop);
    }

    function fadeBackground() {
        ctx.fillStyle = `rgba(5,6,11,${BG_FADE_ALPHA})`;
        ctx.fillRect(0, 0, w, h);
    }

    function drawLine(data, color, glow, amp) {
        if (data.length < 2) return;

        ctx.beginPath();
        ctx.lineWidth = 1.6;
        ctx.strokeStyle = color;
        ctx.shadowColor = glow;
        ctx.shadowBlur = 16;

        const mid = h / 2;

        for (let i = 0; i < data.length; i++) {
            const x = (i / (MAX_POINTS - 1)) * w;
            const y = mid - data[i] * amp;
            if (i === 0) ctx.moveTo(x, y);
            else ctx.lineTo(x, y);
        }

        ctx.stroke();
        ctx.shadowBlur = 0;
    }

    // ===================================================
    // PUBLIC API
    // ===================================================
    return {
        init,
        push: pushData,
        stop() { running = false; },
        resume() {
            if (!running) {
                running = true;
                requestAnimationFrame(loop);
            }
        }
    };

})();
