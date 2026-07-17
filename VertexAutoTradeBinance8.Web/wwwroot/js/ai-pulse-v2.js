// ═══════════════════════════════════════════════════════════════
// ai-pulse-v2.js — Canvas animations for AIPulse.razor
// Called from Blazor via JS.InvokeVoidAsync
// ═══════════════════════════════════════════════════════════════

(function () {
    'use strict';

    // ── RAF loop registry — one loop per canvas ──────────────────
    const _loops = {};

    function cancelLoop(id) {
        if (_loops[id]) { cancelAnimationFrame(_loops[id]); delete _loops[id]; }
    }

    // ── Helpers ──────────────────────────────────────────────────
    function getCtx(id) {
        const el = document.getElementById(id);
        if (!el) return null;
        const dpr = window.devicePixelRatio || 1;
        const w = el.offsetWidth || el.parentElement?.offsetWidth || 300;
        const h = el.offsetHeight || el.height || 110;
        if (el._vpW !== w || el._vpH !== h) {
            el.width  = w * dpr;
            el.height = h * dpr;
            el._vpW = w; el._vpH = h;
        }
        const ctx = el.getContext('2d');
        ctx.setTransform(1,0,0,1,0,0);
        ctx.scale(dpr, dpr);
        return { ctx, w, h };
    }

    // ── MAIN PULSE CANVAS ────────────────────────────────────────
    // pwrs = array of [0..1] neuron power values from Blazor
    window.vpDrawPulse = function (canvasId, pwrs) {
        cancelLoop(canvasId);

        let t = 0;
        const waves = [
            { freq: 0.010, amp: 0.38, speed: 0.70, color: 'rgba(255,150,30,',   phase: 0.0  },
            { freq: 0.016, amp: 0.26, speed: 1.05, color: 'rgba(255,70,30,',    phase: 1.3  },
            { freq: 0.008, amp: 0.42, speed: 0.50, color: 'rgba(0,215,175,',    phase: 2.5  },
            { freq: 0.020, amp: 0.18, speed: 1.35, color: 'rgba(100,190,255,',  phase: 0.9  },
        ];

        // Build signal from real pwrs data
        const dataPts = (pwrs && pwrs.length > 0) ? pwrs : Array.from({length: 40}, () => Math.random());

        // Particles
        const particles = Array.from({length: 60}, () => ({
            x: Math.random(), y: Math.random(),
            vx: (Math.random() - 0.5) * 0.0008,
            vy: (Math.random() - 0.5) * 0.0008,
            r: Math.random() * 1.8 + 0.4,
            a: Math.random() * 0.5 + 0.1,
        }));

        function draw() {
            const r = getCtx(canvasId);
            if (!r) return;
            const { ctx, w, h } = r;

            ctx.clearRect(0, 0, w, h);

            // Background glow
            const bg = ctx.createLinearGradient(0, 0, 0, h);
            bg.addColorStop(0, 'rgba(0,10,30,0)');
            bg.addColorStop(1, 'rgba(0,10,30,0.5)');
            ctx.fillStyle = bg;
            ctx.fillRect(0, 0, w, h);

            // Data signal from real neuron powers
            if (dataPts.length > 1) {
                ctx.beginPath();
                const step = w / (dataPts.length - 1);
                dataPts.forEach((v, i) => {
                    const x = i * step;
                    const y = h * 0.75 - v * h * 0.5
                            + Math.sin(i * 0.5 + t * 0.8) * h * 0.04;
                    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
                });
                const dataGrad = ctx.createLinearGradient(0, 0, w, 0);
                dataGrad.addColorStop(0, 'rgba(0,212,139,0.9)');
                dataGrad.addColorStop(0.5, 'rgba(34,211,238,0.7)');
                dataGrad.addColorStop(1, 'rgba(77,124,255,0.5)');
                ctx.strokeStyle = dataGrad;
                ctx.lineWidth = 2;
                ctx.shadowBlur = 14;
                ctx.shadowColor = 'rgba(0,212,139,0.8)';
                ctx.stroke();
                ctx.shadowBlur = 0;

                // Fill under data signal
                ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
                const fillG = ctx.createLinearGradient(0, 0, 0, h);
                fillG.addColorStop(0, 'rgba(0,212,139,0.15)');
                fillG.addColorStop(1, 'rgba(0,212,139,0)');
                ctx.fillStyle = fillG;
                ctx.fill();
            }

            // Decorative waves
            waves.forEach((wv, wi) => {
                ctx.beginPath();
                const mid = h * (0.40 + wi * 0.05);
                for (let x = 0; x <= w; x += 2) {
                    const n = x / w;
                    const y = mid
                        + Math.sin(n * w * wv.freq + t * wv.speed + wv.phase) * h * wv.amp * 0.35
                        + Math.sin(n * w * wv.freq * 0.5 + t * wv.speed * 0.6) * h * wv.amp * 0.18
                        + Math.cos(n * w * wv.freq * 3 + t * wv.speed * 1.2) * h * wv.amp * 0.07;
                    x === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
                }
                ctx.strokeStyle = wv.color + (wi === 2 ? '0.55)' : '0.35)');
                ctx.lineWidth = wi === 2 ? 1.5 : 1;
                ctx.shadowBlur = 8;
                ctx.shadowColor = wv.color + '0.7)';
                ctx.stroke();
                ctx.shadowBlur = 0;
            });

            // Particles
            particles.forEach(p => {
                p.x += p.vx; p.y += p.vy;
                if (p.x < 0) p.x = 1; if (p.x > 1) p.x = 0;
                if (p.y < 0) p.y = 1; if (p.y > 1) p.y = 0;
                const px = p.x * w, py = p.y * h;
                const blink = 0.4 + 0.6 * Math.sin(t * 2.5 + p.x * 8);
                const hue = (t * 15 + p.x * 280) % 360;
                ctx.beginPath();
                ctx.arc(px, py, p.r * blink, 0, Math.PI * 2);
                ctx.fillStyle = `hsla(${hue},90%,70%,${p.a * blink})`;
                ctx.fill();
            });

            t += 0.018;
            _loops[canvasId] = requestAnimationFrame(draw);
        }

        _loops[canvasId] = requestAnimationFrame(draw);
    };

    // ── SPARKLINE (symbol processing) ────────────────────────────
    window.vpDrawSparkline = function (canvasId, power, regime, isBull) {
        cancelLoop(canvasId);
        const pwr = typeof power === 'number' ? power : parseFloat(power) || 0.5;
        const reg = typeof regime === 'number' ? regime : parseInt(regime) || 0;
        // regime: StrongUpTrend=0, UpTrend=1, Range=2, DownTrend=3, StrongDownTrend=4
        const trending = reg <= 1;
        const bearish  = reg >= 3;

        // Generate organic data based on power and regime
        const n = 32;
        const data = [];
        let v = 50;
        const trend = trending ? 0.4 : bearish ? -0.3 : 0.05;
        for (let i = 0; i < n; i++) {
            v += (Math.random() - 0.48) * 2.5 + trend;
            v = Math.max(10, Math.min(90, v));
            data.push(v);
        }

        let t = 0;
        function draw() {
            const r = getCtx(canvasId);
            if (!r) return;
            const { ctx, w, h } = r;
            ctx.clearRect(0, 0, w, h);

            const min = Math.min(...data), max = Math.max(...data);
            const rng = max - min || 1;
            const pts = data.map((dv, i) => ({
                x: (i / (n - 1)) * w,
                y: h - ((dv - min) / rng) * (h - 4) - 2
                     + Math.sin(i * 0.3 + t) * 1.5
            }));

            // Choose color based on regime and power
            let c1, c2;
            if (bearish)       { c1 = 'rgba(255,51,102,0.8)';  c2 = 'rgba(255,100,130,0.6)'; }
            else if (pwr > 0.65) { c1 = 'rgba(0,212,139,0.9)';  c2 = 'rgba(0,255,136,0.7)'; }
            else if (pwr > 0.40) { c1 = 'rgba(240,180,41,0.8)'; c2 = 'rgba(255,220,100,0.6)'; }
            else                  { c1 = 'rgba(100,150,200,0.5)'; c2 = 'rgba(120,160,220,0.4)'; }

            ctx.beginPath();
            pts.forEach((p, i) => i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y));
            const grad = ctx.createLinearGradient(0, 0, w, 0);
            grad.addColorStop(0, c1); grad.addColorStop(1, c2);
            ctx.strokeStyle = grad;
            ctx.lineWidth = 1.5;
            ctx.shadowBlur = 6; ctx.shadowColor = c2;
            ctx.stroke(); ctx.shadowBlur = 0;

            // Fill
            ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
            const fg = ctx.createLinearGradient(0, 0, 0, h);
            fg.addColorStop(0, c1.replace('0.8)', '0.18)').replace('0.9)', '0.18)').replace('0.5)', '0.08)'));
            fg.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.fillStyle = fg;
            ctx.fill();

            // Live dot at end
            const last = pts[pts.length - 1];
            const blink = 0.5 + 0.5 * Math.sin(t * 4);
            ctx.beginPath();
            ctx.arc(last.x, last.y, 3 * blink, 0, Math.PI * 2);
            ctx.fillStyle = c2.replace('0.6)', '0.9)').replace('0.7)', '0.9)').replace('0.4)', '0.7)');
            ctx.fill();

            t += 0.06;
            _loops[canvasId] = requestAnimationFrame(draw);
        }
        _loops[canvasId] = requestAnimationFrame(draw);
    };

    // ── EXPOSURE CHART (exponential growth) ──────────────────────
    window.vpDrawExposure = function (canvasId, power, slope) {
        cancelLoop(canvasId);
        const pwr = typeof power === 'number' ? power : parseFloat(power) || 0.5;
        const slp = typeof slope === 'number' ? slope : parseFloat(slope) || 0;

        const n = 24;
        const data = [];
        for (let i = 0; i < n; i++) {
            const base = Math.pow(i / n, 1.4 + slp * 0.5) * 80;
            data.push(base + (Math.random() - 0.3) * 4);
        }

        let t = 0;
        function draw() {
            const r = getCtx(canvasId);
            if (!r) return;
            const { ctx, w, h } = r;
            ctx.clearRect(0, 0, w, h);

            const min = 0, max = 85, rng = max;
            const pts = data.map((v, i) => ({
                x: (i / (n - 1)) * w,
                y: h - (v / rng) * (h - 4) - 2 + Math.sin(i * 0.4 + t) * 1.2
            }));

            const bullish = slp > 0;
            const c1 = bullish ? 'rgba(0,212,139,0.9)' : slp < -0.05 ? 'rgba(255,51,102,0.8)' : 'rgba(240,180,41,0.8)';
            const c2 = bullish ? '#00ff88' : slp < -0.05 ? '#ff6688' : '#ffe066';

            ctx.beginPath();
            pts.forEach((p, i) => i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y));
            const g = ctx.createLinearGradient(0, 0, w, 0);
            g.addColorStop(0, c1); g.addColorStop(1, c1);
            ctx.strokeStyle = g;
            ctx.lineWidth = 1.5;
            ctx.shadowBlur = 8; ctx.shadowColor = c2;
            ctx.stroke(); ctx.shadowBlur = 0;

            // Gradient fill
            ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
            const fg = ctx.createLinearGradient(0, 0, 0, h);
            fg.addColorStop(0, c1.replace('0.9)', '0.25)').replace('0.8)', '0.20)'));
            fg.addColorStop(0.6, c1.replace('0.9)', '0.08)').replace('0.8)', '0.06)'));
            fg.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.fillStyle = fg;
            ctx.fill();

            // Arrow tip glow
            const tip = pts[pts.length - 1];
            const g2 = ctx.createRadialGradient(tip.x, tip.y, 0, tip.x, tip.y, 10);
            g2.addColorStop(0, c1.replace('0.9)', '0.6)').replace('0.8)', '0.5)'));
            g2.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.fillStyle = g2;
            ctx.fillRect(tip.x - 10, tip.y - 10, 20, 20);

            t += 0.04;
            _loops[canvasId] = requestAnimationFrame(draw);
        }
        _loops[canvasId] = requestAnimationFrame(draw);
    };

    // ── INSIGHT CHART (regime stats — spiky) ─────────────────────
    window.vpDrawInsight = function (canvasId, score) {
        cancelLoop(canvasId);
        const sc = typeof score === 'number' ? score : parseFloat(score) || 50;

        const n = 40;
        const data = [];
        for (let i = 0; i < n; i++) {
            const base = sc + (Math.random() - 0.45) * 20;
            const spike = Math.random() > 0.85 ? (Math.random() * 30) : 0;
            data.push(Math.max(0, Math.min(100, base + spike)));
        }

        let t = 0;
        function draw() {
            const r = getCtx(canvasId);
            if (!r) return;
            const { ctx, w, h } = r;
            ctx.clearRect(0, 0, w, h);

            const min = 0, max = 100;
            const pts = data.map((v, i) => ({
                x: (i / (n - 1)) * w,
                y: h - (v / max) * (h - 4) - 2 + Math.sin(i * 0.2 + t) * 0.8
            }));

            // Two-color gradient line
            ctx.beginPath();
            pts.forEach((p, i) => i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y));
            const lg = ctx.createLinearGradient(0, 0, w, 0);
            lg.addColorStop(0, 'rgba(240,180,41,0.8)');
            lg.addColorStop(0.5, 'rgba(0,212,139,0.7)');
            lg.addColorStop(1, 'rgba(34,211,238,0.9)');
            ctx.strokeStyle = lg;
            ctx.lineWidth = 1.5;
            ctx.shadowBlur = 8; ctx.shadowColor = 'rgba(0,212,139,0.6)';
            ctx.stroke(); ctx.shadowBlur = 0;

            // Spike dots
            pts.forEach((p, i) => {
                if (data[i] > sc + 15) {
                    ctx.beginPath();
                    ctx.arc(p.x, p.y, 2, 0, Math.PI * 2);
                    ctx.fillStyle = 'rgba(240,180,41,0.9)';
                    ctx.fill();
                    // Glow
                    const gr = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, 8);
                    gr.addColorStop(0, 'rgba(240,180,41,0.3)');
                    gr.addColorStop(1, 'rgba(0,0,0,0)');
                    ctx.fillStyle = gr;
                    ctx.fillRect(p.x - 8, p.y - 8, 16, 16);
                }
            });

            t += 0.03;
            _loops[canvasId] = requestAnimationFrame(draw);
        }
        _loops[canvasId] = requestAnimationFrame(draw);
    };

    // ── Cleanup on navigation ────────────────────────────────────
    // Blazor calls disposeSession — we expose a cleanup fn
    window.vpDisposeAll = function () {
        Object.keys(_loops).forEach(id => {
            cancelAnimationFrame(_loops[id]);
            delete _loops[id];
        });
    };

})();
