// Share-card renderer — draws a trade-result card matching the
// reference style (dark background, big bold ROI%, symbol/side/
// leverage row, entry/close prices, a decorative accent curve) onto a
// canvas, then offers it as a downloadable PNG. Drawn natively rather
// than via html2canvas since the content is simple and fully known in
// advance, not arbitrary DOM — a direct canvas draw avoids that
// library's font/gradient rendering quirks entirely.
window.shareCard = {
    // data: { symbol, side ('LONG'/'SHORT'), leverage, roiPct, pnl,
    //         entryPrice, exitPrice, isOpen (bool — still-open position
    //         vs a closed trade changes the header wording) }
    render(canvasEl, data) {
        const ctx = canvasEl.getContext('2d');
        const W = canvasEl.width, H = canvasEl.height;
        const isUp = data.roiPct >= 0;
        const accent = isUp ? '#34d399' : '#f87171';

        // Background
        const bg = ctx.createLinearGradient(0, 0, W, H);
        bg.addColorStop(0, '#0d1117');
        bg.addColorStop(1, '#000000');
        ctx.fillStyle = bg;
        ctx.fillRect(0, 0, W, H);

        const padX = 64;

        // Header label
        ctx.fillStyle = '#e2e8f0';
        ctx.font = '700 30px Inter, "Segoe UI", sans-serif';
        ctx.textBaseline = 'alphabetic';
        ctx.fillText(data.isOpen ? 'Unrealized P/L' : 'Realized P/L', padX, 110);

        // Symbol · Side · Leverage row
        let x = padX;
        const y2 = 200;
        ctx.font = '700 34px Inter, "Segoe UI", sans-serif';
        ctx.fillStyle = '#ffffff';
        ctx.fillText(data.symbol, x, y2);
        x += ctx.measureText(data.symbol).width + 24;

        ctx.fillStyle = '#64748b';
        ctx.fillText('|', x, y2);
        x += 30;

        ctx.fillStyle = isUp ? '#34d399' : '#f87171';
        const sideLabel = data.side === 'LONG' ? 'Long' : 'Short';
        ctx.fillText(sideLabel, x, y2);
        x += ctx.measureText(sideLabel).width + 24;

        ctx.fillStyle = '#64748b';
        ctx.fillText('|', x, y2);
        x += 30;

        ctx.fillStyle = '#ffffff';
        ctx.fillText(data.leverage + 'X', x, y2);

        // Big ROI%
        const sign = data.roiPct >= 0 ? '+' : '';
        ctx.fillStyle = accent;
        ctx.font = '800 96px Inter, "Segoe UI", sans-serif';
        ctx.fillText(`${sign}${data.roiPct.toFixed(2)}%`, padX, 340);

        // Entry/Exit price rows
        ctx.font = '400 26px Inter, "Segoe UI", sans-serif';
        const rowLabelColor = '#64748b';
        const rowValueColor = '#e2e8f0';
        const exitLabel = data.isOpen ? 'Mark Price' : 'Close Price';

        ctx.fillStyle = rowLabelColor;
        ctx.fillText(exitLabel, padX, 430);
        ctx.fillStyle = rowValueColor;
        ctx.font = '700 26px Inter, "Segoe UI", sans-serif';
        ctx.fillText(String(data.exitPrice), padX + 280, 430);

        ctx.font = '400 26px Inter, "Segoe UI", sans-serif';
        ctx.fillStyle = rowLabelColor;
        ctx.fillText('Entry Price', padX, 480);
        ctx.fillStyle = rowValueColor;
        ctx.font = '700 26px Inter, "Segoe UI", sans-serif';
        ctx.fillText(String(data.entryPrice), padX + 280, 480);

        // Decorative accent curve + glowing dot, top-right — purely
        // visual, matching the reference card's style.
        const curveX = W - 140;
        ctx.strokeStyle = accent;
        ctx.lineWidth = 4;
        ctx.beginPath();
        ctx.moveTo(curveX - 90, H - 130);
        ctx.bezierCurveTo(curveX - 60, H - 230, curveX + 40, H - 230, curveX + 10, H - 320);
        ctx.bezierCurveTo(curveX - 20, H - 400, curveX + 30, H - 420, curveX, H - 480);
        ctx.stroke();

        ctx.save();
        ctx.shadowColor = accent;
        ctx.shadowBlur = 24;
        ctx.fillStyle = accent;
        ctx.beginPath();
        ctx.arc(curveX, H - 480, 26, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();

        // Footer branding
        ctx.font = '600 20px Inter, "Segoe UI", sans-serif';
        ctx.fillStyle = '#475569';
        ctx.fillText('Vertex AutoTrade', padX, H - 50);
    },

    download(canvasEl, filename) {
        const link = document.createElement('a');
        link.download = filename || 'trade-result.png';
        link.href = canvasEl.toDataURL('image/png');
        link.click();
    },
};
