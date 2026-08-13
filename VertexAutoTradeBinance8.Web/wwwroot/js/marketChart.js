// Vertex Market Chart — TradingView Lightweight Charts
window.VertexMarketChart = {
  chart: null,
  candleSeries: null,
  volumeSeries: null,
  ema21: null,
  ema55: null,
  markers: [],

  init(containerId) {
    const el = document.getElementById(containerId);
    if (!el || typeof LightweightCharts === 'undefined') return false;
    if (this.chart) {
      try { this.chart.remove(); } catch (_) {}
      this.chart = null;
    }
    this.chart = LightweightCharts.createChart(el, {
      layout: {
        background: { color: '#0a0d10' },
        textColor: '#848e9c',
        fontFamily: 'JetBrains Mono, Consolas, monospace',
        fontSize: 11,
      },
      grid: {
        vertLines: { color: '#1a1f27' },
        horzLines: { color: '#1a1f27' },
      },
      crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
      rightPriceScale: { borderColor: '#2a313c' },
      timeScale: { borderColor: '#2a313c', timeVisible: true, secondsVisible: false },
      width: el.clientWidth,
      height: Math.max(el.clientHeight || 0, 320),
    });

    this.candleSeries = this.chart.addCandlestickSeries({
      upColor: '#0ecb81',
      downColor: '#f6465d',
      borderUpColor: '#0ecb81',
      borderDownColor: '#f6465d',
      wickUpColor: '#0ecb81',
      wickDownColor: '#f6465d',
    });
    this.volumeSeries = this.chart.addHistogramSeries({
      priceFormat: { type: 'volume' },
      priceScaleId: '',
      scaleMargins: { top: 0.8, bottom: 0 },
    });
    this.ema21 = this.chart.addLineSeries({ color: '#1e90ff', lineWidth: 1, priceLineVisible: false, lastValueVisible: false });
    this.ema55 = this.chart.addLineSeries({ color: '#9b59ff', lineWidth: 1, priceLineVisible: false, lastValueVisible: false });

    const ro = new ResizeObserver(() => {
      if (!this.chart || !el) return;
      this.chart.applyOptions({ width: el.clientWidth, height: el.clientHeight });
    });
    ro.observe(el);
    return true;
  },

  setData(candles) {
    if (!this.candleSeries || !candles || !candles.length) return;
    this.candleSeries.setData(candles);
    const vols = candles.map(c => ({
      time: c.time,
      value: c.volume || 0,
      color: c.close >= c.open ? 'rgba(14,203,129,0.35)' : 'rgba(246,70,93,0.35)',
    }));
    this.volumeSeries.setData(vols);
    this.ema21.setData(this._ema(candles, 21));
    this.ema55.setData(this._ema(candles, 55));
    this.chart.timeScale().fitContent();
  },

  setMarkers(markers) {
    if (!this.candleSeries) return;
    this.candleSeries.setMarkers(markers || []);
  },

  setLevels(entry, sl, tp) {
    if (!this.candleSeries) return;
    // clear by recreating price lines via apply - lightweight-charts v4 uses createPriceLine
    if (this._lines) {
      this._lines.forEach(l => { try { this.candleSeries.removePriceLine(l); } catch (_) {} });
    }
    this._lines = [];
    const add = (price, color, title) => {
      if (!price || price <= 0) return;
      const line = this.candleSeries.createPriceLine({
        price: price,
        color: color,
        lineWidth: 1,
        lineStyle: 2,
        axisLabelVisible: true,
        title: title,
      });
      this._lines.push(line);
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
      if (i >= period - 1) out.push({ time: candles[i].time, value: v });
    }
    return out;
  }
};
