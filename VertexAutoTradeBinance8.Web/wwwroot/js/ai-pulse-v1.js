window.AiPulseV1 = (function () {

    const MAX_POINTS = 420;

    let buffer = [];

    let canvas;
    let ctx;

    let width;
    let height;

    let dpr = window.devicePixelRatio || 1;

    let dotnetRef;

    function resize() {

        width = canvas.clientWidth * dpr;
        height = canvas.clientHeight * dpr;

        canvas.width = width;
        canvas.height = height;
    }

    function push(value) {

        buffer.push(value);

        if (buffer.length > MAX_POINTS)
            buffer.shift();
    }

    function getColor(mode) {

        switch (mode) {

            case "Aggressive":
                return "#ff9f00";

            case "Alert":
                return "#ffd54f";

            default:
                return "#4dd0ff";
        }
    }

    function draw(mode) {

        ctx.clearRect(0, 0, width, height);

        if (buffer.length < 2)
            return;

        ctx.beginPath();

        for (let i = 0; i < buffer.length; i++) {

            const v = buffer[i];

            const x =
                i / (MAX_POINTS - 1) * width;

            const y =
                height
                - v * height * 0.85
                - height * 0.08;

            if (i === 0)
                ctx.moveTo(x, y);
            else
                ctx.lineTo(x, y);
        }

        const color = getColor(mode);

        ctx.strokeStyle = color;

        ctx.lineWidth = 2.2 * dpr;

        ctx.shadowBlur = 18 * dpr;

        ctx.shadowColor = color;

        ctx.stroke();
    }

    async function loop() {

        try {

            const pulse =
                await dotnetRef.invokeMethodAsync("GetPulse");

            push(pulse.value);

            draw(pulse.mode);

        }
        catch {
            // ignore disconnect
        }

        requestAnimationFrame(loop);
    }

    return {

        start: function (dotnet) {

            dotnetRef = dotnet;

            canvas =
                document.getElementById("aiPulseCanvasV1");

            if (!canvas)
                return;

            ctx = canvas.getContext("2d");

            resize();

            window.addEventListener("resize", resize);

            ctx.lineJoin = "round";

            ctx.lineCap = "round";

            requestAnimationFrame(loop);
        }
    };

})();
