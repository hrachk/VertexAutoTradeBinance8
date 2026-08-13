// Vertex Market Chart — Bybit-style (Lightweight Charts v4)
window.VertexMarketChart = {
  chart: null,
  candleSeries: null,
  volumeSeries: null,
  ema21: null,
  ema55: null,
  _lines: [],
  _ro: null,

  init(containerId) {
    const el = document.getElementById(containerId);
    if (!el || typeof LightweightCharts === 'undefined') return false;

    if (this._ro) {
      try { this._ro.disconnect(); } catch (_) {}
      this._ro = null;
    }
    if (this.chart) {
      try { this.chart.remove(); } catch (_) {}
      this.chart = null;
    }

    const h = Math.max(el.clientHeight || 0, 280);
    const w = Math.max(el.clientWidth || 0, 200);

    this.chart = LightweightCharts.createChart(el, {
      layout: {
        background: { type: 'solid', color: '#0b0e11' },
        textColor: '#848e9c',
        fontFamily: "Inter, 'Segoe UI', system-ui, sans-serif",
        fontSize: 11,
      },
      grid: {
        vertLines: { color: 'rgba(42,49,60,0.45)' },
        horzLines: { color: 'rgba(42,49,60,0.45)' },
      },
      crosshair: {
        mode: LightweightCharts.CrosshairMode.Normal,
        vertLine: { color: 'rgba(240,185,11,0.35)', width: 1, style: 2, labelBackgroundColor: '#2a313c' },
        horzLine: { color: 'rgba(240,185,11,0.35)', width: 1, style: 2, labelBackgroundColor: '#2a313c' },
      },
      rightPriceScale: {
        borderColor: '#2a313c',
        scaleMargins: { top: 0.08, bottom: 0.22 },
        entireTextOnly: true,
      },
      leftPriceScale: { visible: false },
      timeScale: {
        borderColor: '#2a313c',
        timeVisible: true,
        secondsVisible: false,
        rightOffset: 4,
        barSpacing: 8,
        minBarSpacing: 3,
      },
      handleScroll: { vertTouchDrag: false },
      width: w,
      height: h,
    });

    // Candles — own right scale, room above volume
    this.candleSeries = this.chart.addCandlestickSeries({
      upColor: '#0ecb81',
      downColor: '#f6465d',
      borderVisible: false,
      wickUpColor: '#0ecb81',
      wickDownColor: '#f6465d',
      priceScaleId: 'right',
      lastValueVisible: true,
      priceLineVisible: true,
      priceLineWidth: 1,
      priceLineColor: 'rgba(132,142,156,0.5)',
      priceLineStyle: 2,
    });

    // Volume — SEPARATE scale, bottom 18% only (does not overlap candles)
    this.volumeSeries = this.chart.addHistogramSeries({
      priceFormat: { type: 'volume' },
      priceScaleId: 'vol',
      lastValueVisible: false,
      priceLineVisible: false,
    });
    this.chart.priceScale('vol').applyOptions({
      scaleMargins: { top: 0.82, bottom: 0 },
      borderVisible: false,
    });

    this.ema21 = this.chart.addLineSeries({
      color: 'rgba(30,144,255,0.9)',
      lineWidth: 1,
      priceScaleId: 'right',
      lastValueVisible: false,
      priceLineVisible: false,
      crosshairMarkerVisible: false,
    });
    this.ema55 = this.chart.addLineSeries({
      color: 'rgba(155,89,255,0.85)',
      lineWidth: 1,
      priceScaleId: 'right',
      lastValueVisible: false,
      priceLineVisible: false,
      crosshairMarkerVisible: false,
    });

    this._ro = new ResizeObserver(() => {
      if (!this.chart || !el) return;
      const nh = el.clientHeight;
      const nw = el.clientWidth;
      if (nh > 40 && nw > 40) this.chart.applyOptions({ width: nw, height: nh });
    });
    this._ro.observe(el);
    return true;
  },

  setData(candles) {
    if (!this.candleSeries || !candles || !candles.length) return;

    // Normalize numbers (API may send strings)
    const bars = candles.map(c => ({
      time: typeof c.time === 'number' ? c.time : Number(c.time),
      open: +c.open,
      high: +c.high,
      low: +c.low,
      close: +c.close,
      volume: +c.volume || 0,
    })).filter(c => c.time && c.open > 0);

    if (!bars.length) return;

    this.candleSeries.setData(bars.map(({ time, open, high, low, close }) => ({ time, open, high, low, close })));

    this.volumeSeries.setData(bars.map(c => ({
      time: c.time,
      value: c.volume,
      color: c.close >= c.open ? 'rgba(14,203,129,0.28)' : 'rgba(246,70,93,0.28)',
    })));

    this.ema21.setData(this._ema(bars, 21));
    this.ema55.setData(this._ema(bars, 55));

    // Auto-fit with a bit of padding — avoid crushing candles
    this.chart.timeScale().fitContent();
  },

  setMarkers(markers) {
    if (!this.candleSeries) return;
    this.candleSeries.setMarkers(markers || []);
  },

  setLevels(entry, sl, tp) {
    if (!this.candleSeries) return;
    if (this._lines && this._lines.length) {
      this._lines.forEach(l => { try { this.candleSeries.removePriceLine(l); } catch (_) {} });
    }
    this._lines = [];
    const add = (price, color, title) => {
      const p = +price;
      if (!p || p <= 0) return;
      this._lines.push(this.candleSeries.createPriceLine({
        price: p,
        color,
        lineWidth: 1,
        lineStyle: 2,
        axisLabelVisible: true,
        title,
      }));
    };
    add(entry, '#f0b90b', 'Entry');
    add(sl, '#f6465d', 'SL');
    add(tp, '#0ecb81', 'TP');
  },

  _ema(candles, period) {
    const out = [];
    if (!candles.length) return out;
    const k = 2 / (period + 1);
    let prev = candles[0].close;
    for (let i = 0; i < candles.length; i++) {
      const v = i === 0 ? candles[0].close : candles[i].close * k + prev * (1 - k);
      prev = v;
      if (i >= period - 1) out.push({ time: candles[i].time, value: +v.toFixed(10) });
    }
    return out;
  }
};
