window.vertex = window.vertex || {};
 

window.vertex.scroll = {
    save() {
        return window.scrollY || document.documentElement.scrollTop || 0;
    },
    restore(y) {
        window.scrollTo({ top: y, behavior: "auto" });
    }
};
window.vertex.tradePermission = {
    _cache: {},

    update(symbol, tf, events) {
        this._cache[`${symbol}:${tf}`] = events || [];
    },

    get(symbol, tf) {
        return this._cache[`${symbol}:${tf}`] || [];
    }
};