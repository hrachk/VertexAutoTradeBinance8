// ============================================================
// Bridges market-chart.js's plain-JS price-pick callback
// (window._onPricePicked) to a Blazor DotNetObjectReference,
// without coupling market-chart.js itself to Blazor APIs.
// ============================================================
window.marketChartBridge = {
    bindPricePicked(dotNetRef) {
        window._onPricePicked = (price) => {
            try {
                dotNetRef.invokeMethodAsync('OnPricePicked', price);
            } catch (e) {
                console.error('[market-chart-bridge] OnPricePicked failed', e);
            }
        };
    }
};
