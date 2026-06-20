// ============================================================
// VERTEX TRADING CHART v5 — Professional Exchange Style
// ============================================================
(function () {
'use strict';

// ── STATE ─────────────────────────────────────────────────
// generationId is THE single source of truth for "is this call still
// relevant". Every time init() runs (new component mount), it bumps
// this counter. Any in-flight async work (retries, delays, callbacks)
// captures the generation it started with and checks it again before
// touching shared module state or calling back into Blazor. If the
// generation changed in the meantime (component was disposed and a
// new one mounted), the stale call is a no-op — full stop, no
// exceptions, no partial writes, nothing to race against.
let generationId = 0;

let K = [];                   // klines array
let derived = {};             // computed indicators
let view = {
    offset: 0,                // candles from right edge (float)
    candleW: 10,              // px per candle including gap
    yMin: null, yMax: null,   // null = auto
};
let drag = { active: false, startX: 0, startOffset: 0 };
let resizeY = { active: false, startY: 0, startH: 0 };
let resizeX = { active: false, startX: 0, startW: 0 };
let hoverI = -1;
let hoverY = -1;          // raw mouse Y in canvas CSS px (for price picking)
let priceLine = null;     // user-picked price line { price } or null

// ── CANVASES ──────────────────────────────────────────────
let MC, RC, VC; // canvas elements
let Mx, Rx, Vx; // 2d contexts
let dpr = window.devicePixelRatio || 1;

// ── DOM-LEVEL SAFETY NET ──────────────────────────────────
// Completely independent of Blazor's component lifecycle (which has
// been confirmed, via diagnostic logging, to NOT reliably re-trigger
// OnAfterRenderAsync — neither firstRender=true nor even a plain
// re-render — when navigating back to /market through the sidebar).
// This MutationObserver watches the whole document body for ANY DOM
// change and, on every mutation batch, cheaply checks whether the
// #priceChart element currently in the document is still the one this
// module is bound to (MC). If a NEW #priceChart node appears (Blazor
// re-rendered the page content even without calling our C# lifecycle
// hooks) and it doesn't match MC, this re-runs init() directly —
// no waiting for any Blazor callback at all. Started once, the first
// time init() ever runs, and kept running for the lifetime of the
// page/tab.
//
// NOTE: this MUST live inside this IIFE (not at module top-level
// outside it) because it references MC, which is only declared in
// this scope — putting it outside caused a ReferenceError every time
// the observer fired.
let _domWatcherStarted = false;
let _lastInitIds = null;
let _curMainId = 'priceChart', _curRsiId = 'rsiChart', _curVolId = 'volumeChart';
function ensureDomWatcher() {
    if (_domWatcherStarted) return;
    _domWatcherStarted = true;

    let debounceTimer = null;
    const observer = new MutationObserver(() => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            const live = document.getElementById(_curMainId);
            if (live && live !== MC && _lastInitIds) {
                console.log('[chart] MutationObserver detected new #priceChart element, re-initializing');
                window.marketChart.init(..._lastInitIds);
            }
        }, 80);
    });
    observer.observe(document.body, { childList: true, subtree: true });
}

// ── THEME ─────────────────────────────────────────────────
const T = {
    bg:'#070a0f', grid:'rgba(30,37,53,0.7)',
    G:'#22c55e', R:'#ef4444',
    blue:'#3b82f6', purple:'#a855f7',
    yellow:'#eab308', cyan:'#38bdf8',
    dim:'#64748b', txt:'#94a3b8',
    cross:'rgba(100,116,139,0.5)',
};

// ── MATH ──────────────────────────────────────────────────
function calcEma(arr, p) {
    if (arr.length === 0) return [];
    const k = 2/(p+1); let v = arr[0];
    return arr.map(x => (v = x*k + v*(1-k)));
}
function calcVwap(c, vols) {
    let pv=0,v=0;
    return c.map((x,i)=>{pv+=x*vols[i];v+=vols[i];return v?pv/v:x;});
}
function calcBB(c, p=20) {
    return c.map((_,i)=>{
        const sl=c.slice(Math.max(0,i-p+1),i+1);
        const m=sl.reduce((a,b)=>a+b,0)/sl.length;
        const s=Math.sqrt(sl.reduce((a,b)=>a+(b-m)**2,0)/sl.length);
        return {mid:m,up:m+2*s,dn:m-2*s};
    });
}
function calcRsi(c, p=14) {
    const out=new Array(p).fill(null);
    if(c.length<p+1) return out;
    let ag=0,al=0;
    for(let i=1;i<=p;i++){const d=c[i]-c[i-1];d>0?ag+=d:al-=d;}
    ag/=p;al/=p;
    out.push(al===0?100:100-100/(1+ag/al));
    for(let i=p+1;i<c.length;i++){
        const d=c[i]-c[i-1];
        ag=(ag*(p-1)+Math.max(d,0))/p;
        al=(al*(p-1)+Math.max(-d,0))/p;
        out.push(al===0?100:100-100/(1+ag/al));
    }
    return out;
}

function derive() {
    const c = K.map(k=>k.close);
    const vol = K.map(k=>k.volume);
    derived = {
        c, vol,
        ema21: calcEma(c,21),
        ema55: calcEma(c,55),
        vwap:  calcVwap(c,vol),
        bb:    calcBB(c,20),
        rsi:   calcRsi(c,14),
    };
}

// ── CANVAS SETUP ──────────────────────────────────────────
let _lastW = 0, _lastH = 0; // track last known size

function setupCanvas(el) {
    if (!el) return null;
    if (!document.contains(el)) {
        console.warn('[chart] setupCanvas called with a detached element (should not happen — drawAll refreshes references first):', el.id);
        return null;
    }
    dpr = window.devicePixelRatio || 1;
    const parent = el.parentElement || el;
    const w = Math.max(parent.clientWidth || 0, parent.offsetWidth || 0, 1);
    const h = Math.max(parent.clientHeight || 0, parent.offsetHeight || 0, 1);
    // Only resize if dimensions actually changed (prevents flicker)
    if (el.width !== Math.round(w * dpr) || el.height !== Math.round(h * dpr)) {
        el.width  = Math.round(w * dpr);
        el.height = Math.round(h * dpr);
    }
    const ctx = el.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return ctx;
}

// Always read dimensions from parent (canvas fills parent via CSS)
function W(c)  { return (c.parentElement || c).clientWidth  || c.offsetWidth  || 800; }
function H(c)  { return (c.parentElement || c).clientHeight || c.offsetHeight || 300; }

// ── LAYOUT ────────────────────────────────────────────────
const PL=8, PR=70, PT=20, PB=20;

function visRange() {
    const n = K.length;
    const chartW = W(MC) - PL - PR;
    const nVis = Math.ceil(chartW / view.candleW) + 2;
    const end = Math.min(n, Math.max(1, n - Math.floor(view.offset)));
    const start = Math.max(0, end - nVis);
    return { start, end };
}

function xFor(i) {
    const { end } = visRange();
    const chartW = W(MC) - PL - PR;
    return PL + chartW - (end - i - 0.5) * view.candleW;
}

function autoY() {
    if (view.yMin !== null) return { yMin:view.yMin, yMax:view.yMax };
    const { start, end } = visRange();
    const vis = K.slice(start, end);
    if (!vis.length) return { yMin:0, yMax:1 };
    let lo = Math.min(...vis.map(k=>k.low));
    let hi = Math.max(...vis.map(k=>k.high));
    const bbSlice = derived.bb.slice(start, end);
    bbSlice.forEach(b => { if(b.up>hi) hi=b.up; if(b.dn<lo) lo=b.dn; });
    if (!isFinite(lo) || !isFinite(hi) || hi <= lo) {
        // Degenerate range (flat price or bad data) - use a sane fallback
        const mid = isFinite(lo) ? lo : 1;
        return { yMin: mid * 0.98, yMax: mid * 1.02 || 1 };
    }
    const pad = (hi-lo)*0.07;
    return { yMin:lo-pad, yMax:hi+pad };
}

function yFor(p, yMin, yMax, cH, cPT=PT, cPB=PB) {
    const ch = cH - cPT - cPB;
    const range = yMax - yMin;
    if (!range || !isFinite(range)) return cPT + ch / 2; // fallback: center
    return cPT + ch - (p - yMin) / range * ch;
}

// Inverse of yFor — converts a mouse Y pixel back to a real price.
// Needed for "click chart to pick a price" trading UX.
function priceForY(y, yMin, yMax, cH, cPT=PT, cPB=PB) {
    const ch = cH - cPT - cPB;
    const range = yMax - yMin;
    if (!range || !isFinite(range)) return yMin;
    return yMin + (cPT + ch - y) / ch * range;
}

// ── DRAW ──────────────────────────────────────────────────
function drawAll(retriesLeft = 8) {
    // Always re-fetch the live canvas elements right before drawing.
    // This is the single source of truth for "is our reference stale" —
    // no dependency on render()'s timing, no dependency on Blazor's
    // firstRender/OnAfterRenderAsync sequencing being whatever we assume
    // it is. getElementById is cheap enough to do on every draw call.
    const livePrice = document.getElementById(_curMainId);
    const liveRsi   = document.getElementById(_curRsiId);
    const liveVol   = document.getElementById(_curVolId);

    if (livePrice && livePrice !== MC) { MC = livePrice; }
    if (liveRsi   && liveRsi   !== RC) { RC = liveRsi; }
    if (liveVol   && liveVol   !== VC) { VC = liveVol; }

    if (!K.length) return;

    if (!MC || !document.contains(MC)) {
        // Canvas not in the DOM yet (Blazor still finishing its render
        // pass after a navigation) — retry shortly instead of silently
        // giving up. A few rAF retries comfortably covers this window.
        if (retriesLeft > 0) {
            requestAnimationFrame(() => drawAll(retriesLeft - 1));
        }
        return;
    }

    try {
        const { yMin, yMax } = autoY();
        drawMain(yMin, yMax);
        if (RC && document.contains(RC)) drawRsi();
        if (VC && document.contains(VC)) drawVol();
    } catch (e) {
        console.error('[market-chart] draw error:', e);
    }
}

function drawMain(yMin, yMax) {
    Mx = setupCanvas(MC);
    const ctx = Mx;
    const cW = W(MC), cH = H(MC);
    ctx.clearRect(0,0,cW,cH);
    ctx.fillStyle = T.bg; ctx.fillRect(0,0,cW,cH);

    const { start, end } = visRange();
    const chartH = cH - PT - PB;
    const chartW = cW - PL - PR;

    const Y = (p) => yFor(p, yMin, yMax, cH);

    // ── Grid ──
    ctx.strokeStyle = T.grid; ctx.lineWidth = 1;
    const ticks = 6;
    for (let t=0; t<=ticks; t++) {
        const y = PT + t * chartH / ticks;
        ctx.beginPath(); ctx.moveTo(PL, y); ctx.lineTo(cW-PR, y); ctx.stroke();
        const p = yMax - t*(yMax-yMin)/ticks;
        ctx.fillStyle = T.dim; ctx.font = '10px JetBrains Mono,monospace';
        ctx.textAlign = 'left';
        ctx.fillText(fmtP(p), cW-PR+4, y+4);
    }
    // Vertical grid every ~80px
    const vStep = Math.max(1, Math.round(80/view.candleW));
    ctx.strokeStyle = 'rgba(30,37,53,0.4)';
    for (let i=start; i<end; i+=vStep) {
        const x = xFor(i);
        ctx.beginPath(); ctx.moveTo(x,PT); ctx.lineTo(x,cH-PB); ctx.stroke();
    }

    // ── BB fill ──
    ctx.beginPath();
    let first=true;
    for(let i=start;i<end;i++){
        const x=xFor(i), y=Y(derived.bb[i].up);
        first?ctx.moveTo(x,y):ctx.lineTo(x,y); first=false;
    }
    for(let i=end-1;i>=start;i--) ctx.lineTo(xFor(i), Y(derived.bb[i].dn));
    ctx.closePath();
    ctx.fillStyle='rgba(59,130,246,0.04)'; ctx.fill();

    // ── Lines ──
    function line(arr, color, w=1.2, dash=[]) {
        ctx.beginPath(); ctx.strokeStyle=color; ctx.lineWidth=w; ctx.setLineDash(dash);
        let f=true;
        for(let i=start;i<end;i++){
            if(arr[i]==null)continue;
            const x=xFor(i),y=Y(arr[i]);
            f?ctx.moveTo(x,y):ctx.lineTo(x,y); f=false;
        }
        ctx.stroke(); ctx.setLineDash([]);
    }
    line(derived.bb.map(b=>b.up),'rgba(59,130,246,0.35)',1,[4,4]);
    line(derived.bb.map(b=>b.dn),'rgba(59,130,246,0.35)',1,[4,4]);
    line(derived.ema21, T.cyan);
    line(derived.ema55, T.purple);
    line(derived.vwap,  T.yellow, 1, [6,3]);

    // ── Candles ──
    const bw = Math.max(1, view.candleW - 2);
    for(let i=start;i<end;i++){
        const k=K[i], x=xFor(i);
        const oY=Y(k.open), cY=Y(k.close), hY=Y(k.high), lY=Y(k.low);
        const bull = k.close >= k.open;
        const col  = bull ? T.G : T.R;
        // Wick
        ctx.strokeStyle=col; ctx.lineWidth=1;
        ctx.beginPath(); ctx.moveTo(x,hY); ctx.lineTo(x,lY); ctx.stroke();
        // Body
        const top = Math.min(oY,cY), bh=Math.max(1,Math.abs(cY-oY));
        ctx.fillStyle=col; ctx.fillRect(x-bw/2, top, bw, bh);
    }

    // ── Live price line ──
    const last = derived.c[derived.c.length-1];
    const lY = Y(last);
    ctx.strokeStyle=T.blue; ctx.lineWidth=1; ctx.setLineDash([4,3]);
    ctx.beginPath(); ctx.moveTo(PL,lY); ctx.lineTo(cW-PR,lY); ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle=T.blue;
    ctx.fillRect(cW-PR+1, lY-9, PR-2, 18);
    ctx.fillStyle='#fff'; ctx.font='bold 10px JetBrains Mono,monospace'; ctx.textAlign='left';
    ctx.fillText(fmtP(last), cW-PR+4, lY+4);

    // ── Time labels ──
    ctx.fillStyle=T.dim; ctx.font='10px JetBrains Mono,monospace'; ctx.textAlign='center';
    const tStep = Math.max(1, Math.round(80/view.candleW));
    for(let i=start;i<end;i+=tStep){
        if(!K[i])continue;
        const d=new Date(K[i].openTime);
        ctx.fillText(d.getHours().toString().padStart(2,'0')+':'+d.getMinutes().toString().padStart(2,'0'),
            xFor(i), cH-4);
    }

    // ── Price-pick line: follows mouse Y, shows the real price there ──
    // (separate from the candle-close crosshair above — this one
    // reflects whatever price is literally under the cursor)
    if (hoverY >= PT && hoverY <= cH - PB) {
        const pickedPrice = priceForY(hoverY, yMin, yMax, cH);
        ctx.strokeStyle = 'rgba(234,179,8,0.6)'; ctx.lineWidth = 1; ctx.setLineDash([2,2]);
        ctx.beginPath(); ctx.moveTo(PL, hoverY); ctx.lineTo(cW-PR, hoverY); ctx.stroke();
        ctx.setLineDash([]);
        ctx.fillStyle = '#eab308';
        ctx.fillRect(cW-PR+1, hoverY-9, PR-2, 18);
        ctx.fillStyle = '#0a0d12'; ctx.font = 'bold 10px JetBrains Mono,monospace'; ctx.textAlign = 'left';
        ctx.fillText(fmtP(pickedPrice), cW-PR+4, hoverY+4);
    }

    // ── Persistent picked price line (after click/double-click) ──
    if (priceLine !== null) {
        const py = Y(priceLine);
        if (py >= PT && py <= cH - PB) {
            ctx.strokeStyle = '#22c55e'; ctx.lineWidth = 1.5; ctx.setLineDash([6,3]);
            ctx.beginPath(); ctx.moveTo(PL, py); ctx.lineTo(cW-PR, py); ctx.stroke();
            ctx.setLineDash([]);
            ctx.fillStyle = '#22c55e';
            ctx.fillRect(cW-PR+1, py-9, PR-2, 18);
            ctx.fillStyle = '#06170d'; ctx.font = 'bold 10px JetBrains Mono,monospace'; ctx.textAlign = 'left';
            ctx.fillText(fmtP(priceLine), cW-PR+4, py+4);
        }
    }

    // ── Crosshair + tooltip ──
    if (hoverI>=0 && hoverI<K.length) {
        const k=K[hoverI], hx=xFor(hoverI), hy=Y(k.close);
        ctx.strokeStyle=T.cross; ctx.lineWidth=1; ctx.setLineDash([4,3]);
        ctx.beginPath(); ctx.moveTo(hx,PT); ctx.lineTo(hx,cH-PB); ctx.stroke();
        ctx.beginPath(); ctx.moveTo(PL,hy); ctx.lineTo(cW-PR,hy); ctx.stroke();
        ctx.setLineDash([]);
        // Y label on right axis
        ctx.fillStyle='#334155';
        ctx.fillRect(cW-PR+1, hy-9, PR-2, 18);
        ctx.fillStyle='#e2e8f0'; ctx.font='10px JetBrains Mono,monospace'; ctx.textAlign='left';
        ctx.fillText(fmtP(k.close), cW-PR+4, hy+4);
        // Tooltip
        const d=new Date(k.openTime);
        const chg=((k.close-k.open)/k.open*100).toFixed(2);
        const lines=[
            d.toLocaleDateString()+' '+d.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'}),
            'O: '+fmtP(k.open)+'  H: '+fmtP(k.high),
            'L: '+fmtP(k.low)+'  C: '+fmtP(k.close),
            'Chg: '+(k.close>=k.open?'+':'')+chg+'%  V: '+fmtV(k.volume),
        ];
        const tw=200, th=lines.length*17+14;
        let tx=hx+14; if(tx+tw>cW-PR) tx=hx-tw-14;
        let ty=Math.max(PT,hy-th/2); if(ty+th>cH-PB) ty=cH-PB-th;
        ctx.fillStyle='#111520'; ctx.strokeStyle='#1e2535'; ctx.lineWidth=1;
        rRect(ctx,tx,ty,tw,th,5);
        ctx.fillStyle='#e2e8f0'; ctx.font='11px JetBrains Mono,monospace'; ctx.textAlign='left';
        lines.forEach((l,i)=>ctx.fillText(l,tx+8,ty+13+i*17));
    }
}

function drawRsi() {
    Rx = setupCanvas(RC);
    const ctx=Rx, cW=W(RC), cH=H(RC);
    ctx.clearRect(0,0,cW,cH); ctx.fillStyle=T.bg; ctx.fillRect(0,0,cW,cH);
    const {start,end}=visRange();
    const Y=(v)=>v==null?null:PT/2+(cH-PT)*(1-v/100);
    // Lines
    [[70,'rgba(239,68,68,0.4)'],[50,'rgba(30,37,53,0.8)'],[30,'rgba(34,197,94,0.4)']].forEach(([v,c])=>{
        const y=Y(v);
        ctx.strokeStyle=c; ctx.lineWidth=1; ctx.setLineDash(v===50?[]:[3,3]);
        ctx.beginPath(); ctx.moveTo(PL,y); ctx.lineTo(cW-PR,y); ctx.stroke();
        ctx.setLineDash([]);
        ctx.fillStyle=T.dim; ctx.font='9px JetBrains Mono,monospace'; ctx.textAlign='left';
        ctx.fillText(v, cW-PR+3, y+3);
    });
    // RSI line
    ctx.lineWidth=1.5; let f=true;
    for(let i=start;i<end;i++){
        const v=derived.rsi[i]; if(v==null)continue;
        const x=xFor(i), y=Y(v);
        ctx.strokeStyle=v>=70?T.R:v<=30?T.G:T.blue;
        if(f){ctx.beginPath();ctx.moveTo(x,y);f=false;}
        else ctx.lineTo(x,y);
        // Break stroke on color change
        if(i+1<end && derived.rsi[i+1]!=null){
            const nv=derived.rsi[i+1];
            const nc=nv>=70?T.R:nv<=30?T.G:T.blue;
            if(nc!==ctx.strokeStyle){ctx.stroke();ctx.beginPath();ctx.moveTo(x,y);}
        }
    }
    ctx.stroke();
    // Crosshair
    if(hoverI>=0&&hoverI<K.length){
        const x=xFor(hoverI);
        ctx.strokeStyle=T.cross; ctx.lineWidth=1; ctx.setLineDash([4,3]);
        ctx.beginPath(); ctx.moveTo(x,0); ctx.lineTo(x,cH); ctx.stroke(); ctx.setLineDash([]);
        const v=derived.rsi[hoverI];
        if(v!=null){
            ctx.fillStyle=T.txt; ctx.font='10px JetBrains Mono,monospace'; ctx.textAlign='right';
            ctx.fillText('RSI '+v.toFixed(1), cW-PR-4, 12);
        }
    }
    // Label
    ctx.fillStyle=T.dim; ctx.font='9px JetBrains Mono,monospace'; ctx.textAlign='left';
    ctx.fillText('RSI(14)', PL+2, 10);
}

function drawVol() {
    Vx = setupCanvas(VC);
    const ctx=Vx, cW=W(VC), cH=H(VC);
    ctx.clearRect(0,0,cW,cH); ctx.fillStyle=T.bg; ctx.fillRect(0,0,cW,cH);
    const {start,end}=visRange();
    const vis=derived.vol.slice(start,end);
    const maxV=Math.max(...vis.filter(v=>v>0),1);
    const avg=vis.reduce((a,b)=>a+b,0)/Math.max(vis.length,1);
    const bw=Math.max(1,view.candleW-2);
    for(let i=start;i<end;i++){
        const v=derived.vol[i]; if(!v)continue;
        const x=xFor(i), k=K[i];
        const h=Math.max(1,(v/maxV)*(cH-4));
        const spike=v>avg*2.2;
        ctx.fillStyle=k.close>=k.open
            ?(spike?'rgba(34,197,94,0.9)':'rgba(34,197,94,0.45)')
            :(spike?'rgba(239,68,68,0.9)':'rgba(239,68,68,0.4)');
        ctx.fillRect(x-bw/2, cH-h, bw, h);
    }
    // Avg line
    ctx.strokeStyle='rgba(100,116,139,0.3)'; ctx.lineWidth=1; ctx.setLineDash([3,3]);
    const avgY=cH-(avg/maxV)*(cH-4);
    ctx.beginPath(); ctx.moveTo(PL,avgY); ctx.lineTo(cW-PR,avgY); ctx.stroke(); ctx.setLineDash([]);
    // Crosshair
    if(hoverI>=0){
        const x=xFor(hoverI);
        ctx.strokeStyle=T.cross; ctx.lineWidth=1; ctx.setLineDash([4,3]);
        ctx.beginPath(); ctx.moveTo(x,0); ctx.lineTo(x,cH); ctx.stroke(); ctx.setLineDash([]);
    }
    ctx.fillStyle=T.dim; ctx.font='9px JetBrains Mono,monospace'; ctx.textAlign='left';
    ctx.fillText('VOL', PL+2, 10);
}

function rRect(ctx,x,y,w,h,r){
    ctx.beginPath();
    ctx.moveTo(x+r,y);ctx.lineTo(x+w-r,y);ctx.quadraticCurveTo(x+w,y,x+w,y+r);
    ctx.lineTo(x+w,y+h-r);ctx.quadraticCurveTo(x+w,y+h,x+w-r,y+h);
    ctx.lineTo(x+r,y+h);ctx.quadraticCurveTo(x,y+h,x,y+h-r);
    ctx.lineTo(x,y+r);ctx.quadraticCurveTo(x,y,x+r,y);
    ctx.closePath();ctx.fill();ctx.stroke();
}

// ── INTERACTION ───────────────────────────────────────────
function xToIdx(mx) {
    const {end}=visRange(), chartW=W(MC)-PL-PR;
    const i=end-1-Math.round((W(MC)-PR-mx)/view.candleW);
    return Math.max(0,Math.min(K.length-1,i));
}

function onWheel(e) {
    e.preventDefault();
    const mx = e.clientX - MC.getBoundingClientRect().left;
    const idxUnder = xToIdx(mx);
    const distFromRight = K.length - 1 - idxUnder;

    if (e.shiftKey) {
        // Shift+Wheel → zoom Y
        if (view.yMin === null) { const {yMin,yMax}=autoY(); view.yMin=yMin; view.yMax=yMax; }
        const range=view.yMax-view.yMin, mid=(view.yMin+view.yMax)/2;
        const f=e.deltaY>0?1.12:0.88;
        view.yMin=mid-range*f/2; view.yMax=mid+range*f/2;
    } else {
        // Wheel → zoom X candle width
        const oldW=view.candleW;
        const delta=e.deltaY>0?-1:1;
        view.candleW=Math.max(2,Math.min(50,view.candleW+delta));
        // Anchor to candle under cursor
        view.offset=Math.max(0,view.offset+distFromRight*(view.candleW-oldW)/view.candleW);
    }
    drawAll();
}

function onMouseDown(e) {
    drag.active=true;
    drag.startX=e.clientX;
    drag.startOffset=view.offset;
    MC.style.cursor='grabbing';
}
function onMouseMove(e) {
    const rect=MC.getBoundingClientRect();
    const mx=e.clientX-rect.left;
    const my=e.clientY-rect.top;
    hoverY = my;
    if(mx>=PL&&mx<=W(MC)-PR) hoverI=xToIdx(mx); else hoverI=-1;
    if(drag.active){
        const dx=e.clientX-drag.startX;
        view.offset=Math.max(0,Math.min(K.length-3,drag.startOffset-dx/view.candleW));
        view.yMin=null; // re-auto-fit Y while panning
    }
    drawAll();
}
function onMouseUp(){drag.active=false;MC.style.cursor='crosshair';}
function onMouseLeave(){hoverI=-1;drag.active=false;MC.style.cursor='crosshair';drawAll();}

// ── RESIZE HANDLES ────────────────────────────────────────
function initResizeY(handleEl, wrapEl) {
    if(!handleEl||!wrapEl) return;
    function start(y){resizeY={active:true,startY:y,startH:wrapEl.offsetHeight};}
    function move(y){
        if(!resizeY.active)return;
        const h=Math.max(150,Math.min(900,resizeY.startH+(y-resizeY.startY)));
        wrapEl.style.height=h+'px';
        setTimeout(()=>{resize();drawAll();},0);
    }
    function end(){resizeY.active=false;localStorage.setItem('vtx_cH',wrapEl.offsetHeight);}
    handleEl.addEventListener('mousedown',e=>{e.preventDefault();start(e.clientY);});
    document.addEventListener('mousemove',e=>{if(resizeY.active)move(e.clientY);});
    document.addEventListener('mouseup',end);
    handleEl.addEventListener('touchstart',e=>{e.preventDefault();start(e.touches[0].clientY);},{passive:false});
    document.addEventListener('touchmove',e=>{if(resizeY.active)move(e.touches[0].clientY);},{passive:false});
    document.addEventListener('touchend',end);
    const saved=localStorage.getItem('vtx_cH');
    if(saved) wrapEl.style.height=saved+'px';
}

// Touch pan/pinch on main canvas
let _t0x=0, _t0off=0, _pinchD=0;
function onTouchStart(e){
    e.preventDefault();
    if(e.touches.length===1){drag.active=true;_t0x=e.touches[0].clientX;_t0off=view.offset;}
    if(e.touches.length===2){_pinchD=pDist(e);}
}
function onTouchMove(e){
    e.preventDefault();
    if(e.touches.length===1&&drag.active){
        const dx=e.touches[0].clientX-_t0x;
        view.offset=Math.max(0,Math.min(K.length-3,_t0off-dx/view.candleW));
        view.yMin=null;drawAll();
    }
    if(e.touches.length===2){
        const d=pDist(e),ratio=d/_pinchD;
        view.candleW=Math.max(2,Math.min(50,view.candleW*ratio));
        _pinchD=d;view.yMin=null;drawAll();
    }
}
function onTouchEnd(){drag.active=false;}
function pDist(e){const dx=e.touches[0].clientX-e.touches[1].clientX,dy=e.touches[0].clientY-e.touches[1].clientY;return Math.sqrt(dx*dx+dy*dy);}

// ── FORMAT ────────────────────────────────────────────────
function fmtP(v){if(!v||v<=0)return '—';if(v>=1000)return v.toFixed(2);if(v>=1)return v.toFixed(4);return v.toPrecision(4);}
function fmtV(v){if(v>=1e6)return (v/1e6).toFixed(2)+'M';if(v>=1e3)return (v/1e3).toFixed(1)+'K';return v.toFixed(2);}

// ── RESIZE OBSERVER ───────────────────────────────────────
function resize(){
    dpr = window.devicePixelRatio || 1;
    [MC,RC,VC].forEach(c=>{
        if(!c) return;
        const parent = c.parentElement || c;
        const w = Math.round(Math.max(parent.clientWidth||0, parent.offsetWidth||0, 1) * dpr);
        const h = Math.round(Math.max(parent.clientHeight||0, parent.offsetHeight||0, 1) * dpr);
        c.width = w; c.height = h;
        c.getContext('2d').setTransform(dpr,0,0,dpr,0,0);
    });
}

// ── PUBLIC API ────────────────────────────────────────────
let _activeRO = null;          // current ResizeObserver instance
let _activeResizeListener = null;
let _lastSym = null, _lastTf = null;

window.marketChart = {
    // Is the module's current MC reference still the SAME DOM node that
    // is actually in the document right now under id="priceChart"?
    // This is the cheap check OnAfterRenderAsync calls on every non-first
    // render to detect "Blazor reused this component instance across a
    // navigation without re-running firstRender logic" — confirmed via
    // diagnostic logging to actually happen for this component. If MC
    // no longer matches the live element, the caller knows it needs to
    // re-run init() right now instead of assuming the original
    // initialization is still valid.
    isBoundToLiveCanvas(expectedId) {
        const live = document.getElementById(expectedId);
        return !!live && live === MC && document.contains(MC);
    },

    // Simple synchronous check - does the element exist with real size?
    elementReady(mainId) {
        const el = document.getElementById(mainId);
        if (!el) return false;
        const parent = el.parentElement || el;
        const w = parent.clientWidth || parent.offsetWidth || 0;
        const h = parent.clientHeight || parent.offsetHeight || 0;
        return w > 10 && h > 10;
    },

    // Clears the green persistent price-pick line (e.g. after the
    // order panel closes or the price field is edited manually).
    clearPriceLine() {
        priceLine = null;
        if (K.length) drawAll();
    },

    init(mainId, rsiId, volId, resizeHandleId, resizeWrapId) {
        _lastInitIds = [mainId, rsiId, volId, resizeHandleId, resizeWrapId];
        _curMainId = mainId; _curRsiId = rsiId; _curVolId = volId;
        ensureDomWatcher();

        // Bump the generation FIRST, before anything else. Any async
        // work already in flight from a previous init() call (e.g. a
        // requestAnimationFrame callback queued below) captured the
        // OLD generation number and will see this new one when it
        // finally runs — making it a guaranteed no-op. This single
        // counter replaces every previous attempt at this problem
        // (cancellation tokens, detached-element checks, etc) with one
        // simple, impossible-to-race rule: every deferred callback
        // checks "is my generation still the current one?" before
        // touching anything.
        generationId++;
        const myGen = generationId;

        // Disconnect previous observer/listener if init() called again
        // (happens on Blazor component re-mount without full page reload)
        if (_activeRO) { try { _activeRO.disconnect(); } catch{} _activeRO = null; }
        if (_activeResizeListener) { try { window.removeEventListener('resize', _activeResizeListener); } catch{} _activeResizeListener = null; }

        // Reset module state — this is a fresh chart instance
        K = [];
        derived = {};
        view.offset = 0;
        view.yMin = null;
        view.candleW = 10;
        _lastSym = null;
        _lastTf  = null;
        priceLine = null;
        hoverY = -1;

        MC=document.getElementById(mainId);
        RC=document.getElementById(rsiId);
        VC=document.getElementById(volId);
        console.log('[chart] init() called, gen=', myGen, 'MC found=', !!MC,
            'parent size=', MC ? MC.parentElement.clientWidth + 'x' + MC.parentElement.clientHeight : 'n/a');
        if(!MC) return;

        // Drag-scroll for ticker bar
        const ticker = document.querySelector('.mk-ticker-bar');
        if (ticker && !ticker._dragBound) {
            ticker._dragBound = true;
            let isDown=false, startX, scrollLeft;
            ticker.addEventListener('mousedown', e=>{isDown=true;startX=e.pageX-ticker.offsetLeft;scrollLeft=ticker.scrollLeft;ticker.style.cursor='grabbing';});
            ticker.addEventListener('mouseleave',()=>{isDown=false;ticker.style.cursor='grab';});
            ticker.addEventListener('mouseup',  ()=>{isDown=false;ticker.style.cursor='grab';});
            ticker.addEventListener('mousemove', e=>{if(!isDown)return;e.preventDefault();const x=e.pageX-ticker.offsetLeft;ticker.scrollLeft=scrollLeft-(x-startX);});
        }

        // Debounced resize handler — guarded by generation check so a
        // resize event firing after this chart instance was replaced
        // can't touch the new instance's state.
        let _roTimer = null;
        const debouncedResize = () => {
            clearTimeout(_roTimer);
            _roTimer = setTimeout(() => {
                if (myGen !== generationId) return;
                if (K.length === 0) return;
                resize();
                drawAll();
            }, 40);
        };

        _activeRO = new ResizeObserver(debouncedResize);
        _activeRO.observe(MC.parentElement);
        _activeResizeListener = debouncedResize;
        window.addEventListener('resize', debouncedResize);

        // Initial layout after 2 frames — generation-guarded so a slow
        // double-rAF callback from a PREVIOUS init() can never fire
        // against whatever the CURRENT chart instance is doing.
        requestAnimationFrame(()=>requestAnimationFrame(()=>{
            if (myGen !== generationId) return;
            resize();
            if(K.length > 0) drawAll();
        }));
        // Main canvas events
        MC.addEventListener('mousedown',onMouseDown);
        MC.addEventListener('mousemove',onMouseMove);
        MC.addEventListener('mouseup',onMouseUp);
        MC.addEventListener('mouseleave',onMouseLeave);
        MC.addEventListener('wheel',onWheel,{passive:false});
        MC.addEventListener('dblclick',()=>{view.offset=0;view.yMin=null;view.candleW=10;drawAll();});
        // Right-click on the chart = pick the price under the cursor for trading.
        // (left-click/drag stays reserved for pan, double-click for zoom reset)
        MC.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            if (!K.length) return;
            const rect = MC.getBoundingClientRect();
            const my = e.clientY - rect.top;
            const cH = H(MC);
            const { yMin, yMax } = autoY();
            const price = priceForY(my, yMin, yMax, cH);
            priceLine = price;
            drawAll();
            if (window._onPricePicked) window._onPricePicked(price);
        });
        MC.addEventListener('touchstart',onTouchStart,{passive:false});
        MC.addEventListener('touchmove',onTouchMove,{passive:false});
        MC.addEventListener('touchend',onTouchEnd);
        MC.style.cursor='crosshair';
        // Resize handle
        initResizeY(document.getElementById(resizeHandleId), document.getElementById(resizeWrapId));
    },
    render(sym, tf, klines) {
        const myGen = generationId;
        console.log('[chart] render() called, gen=', myGen, 'klines.length=', klines ? klines.length : 'null', 'MC=', !!MC);

        // Refresh canvas references up front so resize()/auto-fit below
        // operate on the actual live DOM elements, not stale ones from
        // a previous page visit (drawAll() does this again right before
        // drawing as a second safety net, but doing it here too avoids
        // wasted resize() calls against a detached element).
        const livePrice = document.getElementById(_curMainId);
        const liveRsi   = document.getElementById(_curRsiId);
        const liveVol   = document.getElementById(_curVolId);
        if (livePrice && livePrice !== MC) MC = livePrice;
        if (liveRsi   && liveRsi   !== RC) RC = liveRsi;
        if (liveVol   && liveVol   !== VC) VC = liveVol;

        K = klines;
        derive();

        const w = W(MC);
        const chartW = (w || 800) - PL - PR;

        // Reset view on symbol/tf change
        if (sym !== _lastSym || tf !== _lastTf) {
            view.offset = 0;
            view.yMin = null;
            _lastSym = sym;
            _lastTf  = tf;
            // Auto-fit: choose candleW so all candles fill the visible area
            if (K.length > 0) {
                const ideal = chartW / Math.min(K.length, 120);
                view.candleW = Math.max(3, Math.min(20, ideal));
            } else {
                view.candleW = 8;
            }
        }

        resize();

        if (w >= 50) {
            drawAll();
        } else {
            let a = 0;
            const retry = () => {
                if (myGen !== generationId) return; // a newer init() superseded this render
                resize();
                if(W(MC)>=50){drawAll();}else if(++a<10){requestAnimationFrame(retry);}
            };
            requestAnimationFrame(retry);
        }
    }
};

// On-page visible debug badge — updates every 500ms with the chart
// module's real internal state. This exists so diagnosis doesn't
// require opening DevTools at all: the person can just look at the
// top-left corner of the chart and read it directly off the screen.
setInterval(() => {
    const badge = document.getElementById('chartDebugBadge');
    if (!badge) return;
    const liveEl = document.getElementById(_curMainId);
    const boundMatch = liveEl && liveEl === MC;
    const inDoc = MC ? document.contains(MC) : false;
    const parentSize = MC ? `${MC.parentElement.clientWidth}x${MC.parentElement.clientHeight}` : 'n/a';
    badge.textContent =
        `gen=${generationId} K=${K.length}\n` +
        `MC bound=${boundMatch} inDOM=${inDoc}\n` +
        `parent=${parentSize}`;
}, 500);

})();

// Smoothly scrolls the LIVE SIGNALS card strip to the given page.
// Independent of the chart IIFE above — purely a UI convenience for the
// horizontal-scroll signal panel, added without touching chart logic.
window.vertexScrollSignalsToPage = function (el, page, pageSize) {
    if (!el || !el.children || el.children.length === 0) return;
    const idx = Math.max(0, page * pageSize);
    const target = el.children[Math.min(idx, el.children.length - 1)];
    if (!target) return;
    const targetLeft = target.offsetLeft - el.offsetLeft;
    el.scrollTo({ left: targetLeft, behavior: "smooth" });
};
