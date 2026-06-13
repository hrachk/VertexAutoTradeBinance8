// ============================================================
// VERTEX TRADING CHART v4
// Professional exchange-style candlestick chart
// Features:
//   - Smooth scroll to pan (drag or trackpad)
//   - Ctrl+Wheel / pinch to zoom X (candle width)
//   - Shift+Wheel to zoom Y (price range)
//   - Auto-fit Y to visible candles
//   - Live price line with label
//   - Crosshair + OHLC tooltip on hover
//   - EMA21, EMA55, VWAP, Bollinger Bands
//   - RSI(14) sub-chart
//   - Volume bars (color matched to candle)
// ============================================================

(function() {

// ── MATH ──────────────────────────────────────────────────
function ema(arr, p) {
    const k = 2/(p+1); let v = arr[0];
    return arr.map(x => (v = x*k + v*(1-k)));
}
function vwap(closes, vols) {
    let cpv=0, cv=0;
    return closes.map((c,i)=>{ cpv+=c*vols[i]; cv+=vols[i]; return cv?cpv/cv:c; });
}
function bb(closes, p=20) {
    return closes.map((_,i)=>{
        const sl=closes.slice(Math.max(0,i-p+1),i+1);
        const m=sl.reduce((a,b)=>a+b,0)/sl.length;
        const s=Math.sqrt(sl.reduce((a,b)=>a+(b-m)**2,0)/sl.length);
        return {mid:m,up:m+2*s,dn:m-2*s};
    });
}
function rsi(closes, p=14) {
    const out=new Array(p).fill(null);
    let ag=0,al=0;
    for(let i=1;i<=p;i++){const d=closes[i]-closes[i-1]; d>0?ag+=d:al-=d;}
    ag/=p; al/=p;
    out.push(al===0?100:100-100/(1+ag/al));
    for(let i=p+1;i<closes.length;i++){
        const d=closes[i]-closes[i-1];
        ag=(ag*(p-1)+Math.max(d,0))/p; al=(al*(p-1)+Math.max(-d,0))/p;
        out.push(al===0?100:100-100/(1+ag/al));
    }
    return out;
}

// ── STATE ─────────────────────────────────────────────────
let klines=[], closes=[], volumes=[];
let ema21=[], ema55=[], vwapA=[], bbA=[], rsiA=[];

// View state
let candleW = 10;      // candle body width px
let gap     = 2;       // gap between candles
let offsetX = 0;       // pan offset in candles from right edge
let manualYMin = null, manualYMax = null;  // null = auto-fit

// Canvases
let mainCanvas, rsiCanvas, volCanvas;
let mainCtx, rsiCtx, volCtx;
let W=0, H=0, rsiH=80, volH=70;

// Hover
let hoverIdx = -1;
let isDragging = false, dragStartX=0, dragOffsetStart=0;

// Layout
const PAD_L=8, PAD_R=72, PAD_T=20, PAD_B=20;

const C = {
    bg:'#070a0f', grid:'rgba(30,37,53,0.7)',
    green:'#22c55e', red:'#ef4444',
    blue:'#3b82f6', purple:'#a855f7',
    yellow:'#eab308', cyan:'#38bdf8',
    text:'#64748b', txt2:'#94a3b8',
    cross:'rgba(100,116,139,0.6)',
    tooltip:'#111520', tooltipBdr:'#1e2535',
    priceLine:'#3b82f6',
};

// ── INIT ──────────────────────────────────────────────────
function init(mainId, rsiId, volId) {
    mainCanvas = document.getElementById(mainId);
    rsiCanvas  = document.getElementById(rsiId);
    volCanvas  = document.getElementById(volId);
    if (!mainCanvas) return;

    mainCtx = mainCanvas.getContext('2d');
    if (rsiCanvas) rsiCtx = rsiCanvas.getContext('2d');
    if (volCanvas) volCtx = volCanvas.getContext('2d');

    resize();
    window.addEventListener('resize', resize);

    // Mouse events on main canvas
    mainCanvas.addEventListener('mousedown', onMouseDown);
    mainCanvas.addEventListener('mousemove', onMouseMove);
    mainCanvas.addEventListener('mouseup',   onMouseUp);
    mainCanvas.addEventListener('mouseleave',()=>{ hoverIdx=-1; isDragging=false; drawAll(); });
    mainCanvas.addEventListener('wheel', onWheel, { passive: false });

    // Touch
    mainCanvas.addEventListener('touchstart', onTouchStart, {passive:false});
    mainCanvas.addEventListener('touchmove',  onTouchMove,  {passive:false});
    mainCanvas.addEventListener('touchend',   onTouchEnd);
}

function resize() {
    const dpr = window.devicePixelRatio||1;
    [mainCanvas, rsiCanvas, volCanvas].forEach(c=>{
        if(!c) return;
        const rect = c.getBoundingClientRect();
        c.width  = rect.width * dpr;
        c.height = rect.height * dpr;
        c.getContext('2d').scale(dpr, dpr);
    });
    if(mainCanvas){ W=mainCanvas.getBoundingClientRect().width; H=mainCanvas.getBoundingClientRect().height; }
    drawAll();
}

// ── RENDER DATA ───────────────────────────────────────────
function render(data) {
    klines  = data;
    closes  = data.map(k=>k.close);
    volumes = data.map(k=>k.volume);
    if (closes.length < 2) return;

    ema21 = ema(closes, 21);
    ema55 = ema(closes, 55);
    vwapA = vwap(closes, volumes);
    bbA   = bb(closes, 20);
    rsiA  = rsi(closes, 14);

    // Start showing last ~80 candles
    const candleW_default = Math.max(4, Math.min(12, Math.floor((W - PAD_L - PAD_R) / 80)));
    candleW = candleW_default;
    offsetX = 0;
    manualYMin = null; manualYMax = null;
    drawAll();
}

// ── COORDINATE HELPERS ────────────────────────────────────
function totalWidth() { return candleW + gap; }

function visibleRange() {
    const chartW = W - PAD_L - PAD_R;
    const total  = klines.length;
    const nVisible = Math.floor(chartW / totalWidth()) + 2;
    const endIdx   = Math.max(1, total - offsetX);
    const startIdx = Math.max(0, endIdx - nVisible);
    return { startIdx, endIdx: Math.min(endIdx, total) };
}

function xForIdx(i) {
    const { endIdx } = visibleRange();
    const chartW = W - PAD_L - PAD_R;
    return PAD_L + chartW - (endIdx - i) * totalWidth() + candleW/2;
}

function yForPrice(p, yMin, yMax) {
    const chartH = H - PAD_T - PAD_B;
    return PAD_T + chartH - (p - yMin) / (yMax - yMin) * chartH;
}

function priceForY(y, yMin, yMax) {
    const chartH = H - PAD_T - PAD_B;
    return yMin + (1 - (y - PAD_T) / chartH) * (yMax - yMin);
}

// ── AUTO-FIT Y ────────────────────────────────────────────
function getYRange() {
    if (manualYMin !== null) return { yMin: manualYMin, yMax: manualYMax };
    const { startIdx, endIdx } = visibleRange();
    const vis = klines.slice(startIdx, endIdx);
    if (!vis.length) return { yMin: 0, yMax: 1 };
    let lo = Math.min(...vis.map(k=>k.low));
    let hi = Math.max(...vis.map(k=>k.high));
    // Also include BB
    bbA.slice(startIdx, endIdx).forEach(b=>{ if(b.up>hi)hi=b.up; if(b.dn<lo)lo=b.dn; });
    const pad = (hi - lo) * 0.06;
    return { yMin: lo - pad, yMax: hi + pad };
}

// ── DRAW ALL ──────────────────────────────────────────────
function drawAll() {
    if (!mainCtx || !klines.length) return;
    const { yMin, yMax } = getYRange();
    drawMain(yMin, yMax);
    if (rsiCtx)  drawRsi();
    if (volCtx)  drawVol();
}

// ── MAIN CHART ────────────────────────────────────────────
function drawMain(yMin, yMax) {
    const ctx = mainCtx;
    ctx.clearRect(0, 0, W, H);
    ctx.fillStyle = C.bg;
    ctx.fillRect(0, 0, W, H);

    const { startIdx, endIdx } = visibleRange();
    const chartH = H - PAD_T - PAD_B;
    const chartW = W - PAD_L - PAD_R;

    // Grid
    ctx.strokeStyle = C.grid; ctx.lineWidth = 1;
    for (let t=0; t<=5; t++) {
        const y = PAD_T + t * chartH / 5;
        ctx.beginPath(); ctx.moveTo(PAD_L, y); ctx.lineTo(W-PAD_R, y); ctx.stroke();
        const p = yMax - t*(yMax-yMin)/5;
        ctx.fillStyle = C.text;
        ctx.font = '10px JetBrains Mono,monospace';
        ctx.textAlign = 'left';
        ctx.fillText(fmtP(p), W-PAD_R+4, y+4);
    }

    // BB fill
    ctx.beginPath();
    for(let i=startIdx;i<endIdx;i++) {
        const x=xForIdx(i), y=yForPrice(bbA[i].up, yMin, yMax);
        i===startIdx?ctx.moveTo(x,y):ctx.lineTo(x,y);
    }
    for(let i=endIdx-1;i>=startIdx;i--) ctx.lineTo(xForIdx(i), yForPrice(bbA[i].dn, yMin, yMax));
    ctx.closePath();
    ctx.fillStyle = 'rgba(59,130,246,0.04)';
    ctx.fill();

    // Lines helper
    function drawLine(arr, color, dash=[]) {
        ctx.beginPath(); ctx.strokeStyle=color; ctx.lineWidth=1.2; ctx.setLineDash(dash);
        let first=true;
        for(let i=startIdx;i<endIdx;i++) {
            if(arr[i]==null) continue;
            const x=xForIdx(i), y=yForPrice(arr[i],yMin,yMax);
            first?ctx.moveTo(x,y):ctx.lineTo(x,y); first=false;
        }
        ctx.stroke(); ctx.setLineDash([]);
    }

    drawLine(bbA.map(b=>b.up), 'rgba(59,130,246,0.35)', [4,4]);
    drawLine(bbA.map(b=>b.dn), 'rgba(59,130,246,0.35)', [4,4]);
    drawLine(ema21, C.cyan);
    drawLine(ema55, C.purple);
    drawLine(vwapA, C.yellow, [6,3]);

    // Candles
    for (let i=startIdx; i<endIdx; i++) {
        const k = klines[i];
        const x  = xForIdx(i);
        const oY = yForPrice(k.open,  yMin, yMax);
        const cY = yForPrice(k.close, yMin, yMax);
        const hY = yForPrice(k.high,  yMin, yMax);
        const lY = yForPrice(k.low,   yMin, yMax);
        const bull = k.close >= k.open;
        const color = bull ? C.green : C.red;
        const halfW = candleW / 2;

        // Wick
        ctx.strokeStyle = color; ctx.lineWidth = 1;
        ctx.beginPath(); ctx.moveTo(x, hY); ctx.lineTo(x, lY); ctx.stroke();

        // Body
        const top = Math.min(oY, cY);
        const bh  = Math.max(1, Math.abs(cY-oY));
        if (bull) {
            ctx.strokeStyle = color; ctx.lineWidth = 1;
            if (bh >= 2) { ctx.fillStyle = color; ctx.fillRect(x-halfW, top, candleW, bh); }
            else { ctx.beginPath(); ctx.moveTo(x-halfW,top); ctx.lineTo(x+halfW,top); ctx.stroke(); }
        } else {
            ctx.fillStyle = color;
            ctx.fillRect(x-halfW, top, candleW, Math.max(1, bh));
        }
    }

    // Live price line
    const lastClose = closes[closes.length-1];
    const lpY = yForPrice(lastClose, yMin, yMax);
    ctx.strokeStyle = C.priceLine; ctx.lineWidth = 1;
    ctx.setLineDash([4,3]);
    ctx.beginPath(); ctx.moveTo(PAD_L, lpY); ctx.lineTo(W-PAD_R, lpY); ctx.stroke();
    ctx.setLineDash([]);
    // Price label
    ctx.fillStyle = C.priceLine;
    ctx.fillRect(W-PAD_R+1, lpY-9, PAD_R-2, 18);
    ctx.fillStyle = '#fff';
    ctx.font = 'bold 10px JetBrains Mono,monospace';
    ctx.textAlign = 'left';
    ctx.fillText(fmtP(lastClose), W-PAD_R+4, lpY+4);

    // Time axis
    const step = Math.max(1, Math.floor(80 / Math.floor((W-PAD_L-PAD_R)/totalWidth())));
    ctx.fillStyle = C.text; ctx.font = '10px JetBrains Mono,monospace'; ctx.textAlign='center';
    for(let i=startIdx;i<endIdx;i+=step) {
        if(!klines[i]) continue;
        const t = new Date(klines[i].openTime);
        const lbl = t.getHours().toString().padStart(2,'0')+':'+t.getMinutes().toString().padStart(2,'0');
        ctx.fillText(lbl, xForIdx(i), H-4);
    }

    // Crosshair + tooltip
    if (hoverIdx >= 0 && hoverIdx < klines.length) {
        const k = klines[hoverIdx];
        const x = xForIdx(hoverIdx);
        const y = yForPrice(k.close, yMin, yMax);

        ctx.strokeStyle = C.cross; ctx.lineWidth = 1; ctx.setLineDash([4,3]);
        ctx.beginPath(); ctx.moveTo(x, PAD_T); ctx.lineTo(x, H-PAD_B); ctx.stroke();
        ctx.beginPath(); ctx.moveTo(PAD_L, y); ctx.lineTo(W-PAD_R, y); ctx.stroke();
        ctx.setLineDash([]);

        // Tooltip
        const lines = [
            fmtTime(k.openTime),
            `O: ${fmtP(k.open)}  H: ${fmtP(k.high)}`,
            `L: ${fmtP(k.low)}   C: ${fmtP(k.close)}`,
            `V: ${fmtV(k.volume)}`,
        ];
        const tw = 190, th = lines.length * 17 + 12;
        let tx = x + 12;
        if (tx + tw > W-PAD_R) tx = x - tw - 12;
        let ty = Math.max(PAD_T, y - th/2);
        if (ty + th > H-PAD_B) ty = H-PAD_B-th;

        ctx.fillStyle = C.tooltip;
        ctx.strokeStyle = C.tooltipBdr; ctx.lineWidth=1;
        roundRect(ctx, tx, ty, tw, th, 4);

        ctx.fillStyle = '#e2e8f0'; ctx.font = '11px JetBrains Mono,monospace'; ctx.textAlign='left';
        lines.forEach((l,i) => ctx.fillText(l, tx+8, ty+14+i*17));
    }
}

function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x+r,y); ctx.lineTo(x+w-r,y); ctx.quadraticCurveTo(x+w,y,x+w,y+r);
    ctx.lineTo(x+w,y+h-r); ctx.quadraticCurveTo(x+w,y+h,x+w-r,y+h);
    ctx.lineTo(x+r,y+h); ctx.quadraticCurveTo(x,y+h,x,y+h-r);
    ctx.lineTo(x,y+r); ctx.quadraticCurveTo(x,y,x+r,y);
    ctx.closePath(); ctx.fill(); ctx.stroke();
}

// ── RSI ───────────────────────────────────────────────────
function drawRsi() {
    const ctx = rsiCtx;
    const cW = rsiCanvas.getBoundingClientRect().width;
    const cH = rsiCanvas.getBoundingClientRect().height;
    ctx.clearRect(0,0,cW,cH);
    ctx.fillStyle = C.bg; ctx.fillRect(0,0,cW,cH);

    const { startIdx, endIdx } = visibleRange();
    const chartH = cH - 4;
    const toY = v => v==null ? null : (1-v/100)*chartH;

    // OB/OS lines
    [70,50,30].forEach(v=>{
        ctx.strokeStyle = v===50?'rgba(30,37,53,0.8)':'rgba(100,116,139,0.3)';
        ctx.lineWidth=1; ctx.setLineDash(v===50?[]:[4,4]);
        const y=toY(v);
        ctx.beginPath(); ctx.moveTo(PAD_L,y); ctx.lineTo(cW-PAD_R,y); ctx.stroke();
        ctx.setLineDash([]);
        ctx.fillStyle=C.text; ctx.font='9px JetBrains Mono,monospace'; ctx.textAlign='left';
        ctx.fillText(v, cW-PAD_R+2, y+3);
    });

    ctx.beginPath(); ctx.lineWidth=1.5;
    let first=true;
    for(let i=startIdx;i<endIdx;i++) {
        const v=rsiA[i]; if(v==null) continue;
        const x=xForIdx(i), y=toY(v);
        if(first){ctx.moveTo(x,y);first=false;}else ctx.lineTo(x,y);
        ctx.strokeStyle = v>=70?C.red:v<=30?C.green:C.blue;
    }
    ctx.stroke();

    // Hover line
    if(hoverIdx>=0) {
        ctx.strokeStyle=C.cross; ctx.lineWidth=1; ctx.setLineDash([4,3]);
        const x=xForIdx(hoverIdx);
        ctx.beginPath(); ctx.moveTo(x,0); ctx.lineTo(x,cH); ctx.stroke();
        ctx.setLineDash([]);
        const v=rsiA[hoverIdx];
        if(v!=null){
            ctx.fillStyle=C.blue; ctx.font='10px JetBrains Mono,monospace'; ctx.textAlign='left';
            ctx.fillText('RSI '+v.toFixed(1), cW-PAD_R+2, 10);
        }
    }
}

// ── VOLUME ────────────────────────────────────────────────
function drawVol() {
    const ctx = volCtx;
    const cW = volCanvas.getBoundingClientRect().width;
    const cH = volCanvas.getBoundingClientRect().height;
    ctx.clearRect(0,0,cW,cH);
    ctx.fillStyle = C.bg; ctx.fillRect(0,0,cW,cH);

    const { startIdx, endIdx } = visibleRange();
    const vis = volumes.slice(startIdx, endIdx);
    const maxV = Math.max(...vis.filter(v=>v>0), 1);
    const avg  = vis.reduce((a,b)=>a+b,0)/Math.max(vis.length,1);

    for(let i=startIdx;i<endIdx;i++) {
        const v=volumes[i]; if(!v) continue;
        const k=klines[i];
        const x=xForIdx(i);
        const h=Math.max(1, v/maxV*(cH-4));
        const spike=v>avg*2.2;
        ctx.fillStyle = k.close>=k.open
            ? (spike?'rgba(34,197,94,0.85)':'rgba(34,197,94,0.45)')
            : (spike?'rgba(239,68,68,0.85)':'rgba(239,68,68,0.4)');
        ctx.fillRect(x-candleW/2, cH-h, candleW, h);
    }

    if(hoverIdx>=0){
        ctx.strokeStyle=C.cross; ctx.lineWidth=1; ctx.setLineDash([4,3]);
        const x=xForIdx(hoverIdx);
        ctx.beginPath(); ctx.moveTo(x,0); ctx.lineTo(x,cH); ctx.stroke();
        ctx.setLineDash([]);
    }
}

// ── INTERACTION ───────────────────────────────────────────
function xToIdx(mouseX) {
    const { endIdx } = visibleRange();
    const chartW = W-PAD_L-PAD_R;
    const dist = W-PAD_R-mouseX;
    const i = endIdx-1 - Math.round(dist/totalWidth());
    return Math.max(0, Math.min(klines.length-1, i));
}

function onMouseDown(e) {
    isDragging=true; dragStartX=e.clientX;
    dragOffsetStart=offsetX;
    mainCanvas.style.cursor='grabbing';
}

function onMouseMove(e) {
    const rect=mainCanvas.getBoundingClientRect();
    const mx=e.clientX-rect.left;
    if(mx>=PAD_L && mx<=W-PAD_R) hoverIdx=xToIdx(mx); else hoverIdx=-1;

    if(isDragging) {
        const dx=e.clientX-dragStartX;
        offsetX = Math.max(0, Math.min(klines.length-5, dragOffsetStart - dx/totalWidth()));
        manualYMin=null; // re-fit Y on pan
    }
    drawAll();
}

function onMouseUp() {
    isDragging=false;
    mainCanvas.style.cursor='crosshair';
}

function onWheel(e) {
    e.preventDefault();
    const rect=mainCanvas.getBoundingClientRect();
    const mx=e.clientX-rect.left;
    const idxUnderMouse=xToIdx(mx);

    if(e.shiftKey) {
        // Shift+wheel → zoom Y (price range)
        const { yMin, yMax } = getYRange();
        const range = yMax-yMin;
        const factor = e.deltaY>0 ? 1.1 : 0.9;
        const mid = (yMin+yMax)/2;
        manualYMin = mid - range*factor/2;
        manualYMax = mid + range*factor/2;
    } else {
        // Normal wheel → zoom X (candle width)
        const oldW = candleW;
        if(e.deltaY>0) candleW=Math.max(2, candleW-1);
        else            candleW=Math.min(40, candleW+1);

        // Keep candle under cursor fixed
        const dW = candleW-oldW;
        const distFromRight = klines.length-1-idxUnderMouse;
        offsetX = Math.max(0, offsetX + distFromRight*dW/totalWidth());
        manualYMin=null;
    }
    drawAll();
}

// Double-click to reset view
mainCanvas && mainCanvas.addEventListener('dblclick', ()=>{
    candleW=10; offsetX=0; manualYMin=null; manualYMax=null; drawAll();
});

// Touch
let _t1x=0, _t1y=0, _t2dist=0;
function onTouchStart(e) {
    e.preventDefault();
    if(e.touches.length===1){ isDragging=true; dragStartX=e.touches[0].clientX; dragOffsetStart=offsetX; }
    if(e.touches.length===2){ _t2dist=touchDist(e); }
}
function onTouchMove(e) {
    e.preventDefault();
    if(e.touches.length===1 && isDragging){
        const dx=e.touches[0].clientX-dragStartX;
        offsetX=Math.max(0,Math.min(klines.length-5,dragOffsetStart-dx/totalWidth()));
        manualYMin=null; drawAll();
    }
    if(e.touches.length===2){
        const d=touchDist(e), ratio=d/_t2dist;
        candleW=Math.max(2,Math.min(40,Math.round(candleW*ratio)));
        _t2dist=d; manualYMin=null; drawAll();
    }
}
function onTouchEnd(e){ isDragging=false; }
function touchDist(e){ const dx=e.touches[0].clientX-e.touches[1].clientX, dy=e.touches[0].clientY-e.touches[1].clientY; return Math.sqrt(dx*dx+dy*dy); }

// ── FORMAT ────────────────────────────────────────────────
function fmtP(v){ if(!v||v<=0)return '—'; if(v>=1000)return v.toFixed(2); if(v>=1)return v.toFixed(4); return v.toPrecision(4); }
function fmtV(v){ if(v>=1e6)return (v/1e6).toFixed(2)+'M'; if(v>=1e3)return (v/1e3).toFixed(1)+'K'; return v.toFixed(2); }
function fmtTime(ms){ const d=new Date(ms); return d.toLocaleDateString()+' '+d.toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}); }

// ── PUBLIC ────────────────────────────────────────────────
window.marketChart = {
    init,
    render(sym, tf, klines) { render(klines); }
};

})();
