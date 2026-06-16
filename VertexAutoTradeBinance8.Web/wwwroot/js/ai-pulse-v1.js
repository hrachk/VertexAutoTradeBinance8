window.AiPulseV1 = {

    start: function(canvasId, dotnetRef) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) return null;
        const ctx = canvas.getContext('2d');
        const MAX = 420;
        let buffer = [];
        let dpr = window.devicePixelRatio || 1;
        let running = true;

        function resize() {
            dpr = window.devicePixelRatio || 1;
            canvas.width  = canvas.clientWidth  * dpr;
            canvas.height = canvas.clientHeight * dpr;
        }

        function push(v) { buffer.push(v); if (buffer.length > MAX) buffer.shift(); }

        function getColor(mode) {
            if (mode === 'Aggressive') return '#ff9f00';
            if (mode === 'Alert')      return '#ffd54f';
            return '#4dd0ff';
        }

        function draw(mode) {
            const w = canvas.width, h = canvas.height;
            ctx.clearRect(0, 0, w, h);
            if (buffer.length < 2) return;
            ctx.beginPath();
            for (let i = 0; i < buffer.length; i++) {
                const x = (i / (MAX - 1)) * w;
                const y = h - buffer[i] * h * 0.85 - h * 0.08;
                i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
            }
            const color = getColor(mode);
            ctx.strokeStyle = color;
            ctx.lineWidth = 2.2 * dpr;
            ctx.shadowBlur = 18 * dpr;
            ctx.shadowColor = color;
            ctx.stroke();
        }

        resize();
        window.addEventListener('resize', resize);

        async function loop() {
            if (!running) return;
            try {
                const pulse = await dotnetRef.invokeMethodAsync('GetPulse');
                push(pulse.value);
                draw(pulse.mode);
            } catch { }
            if (running) requestAnimationFrame(loop);
        }

        loop();

        return { dispose: function() { running = false; window.removeEventListener('resize', resize); } };
    }
};
