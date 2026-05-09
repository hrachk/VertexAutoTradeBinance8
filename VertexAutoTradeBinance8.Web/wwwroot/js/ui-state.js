window.uiState = {
    get: function (key, def) {
        const v = localStorage.getItem(key);
        return v === null ? def : (v === "true");
    },
    set: function (key, value) {
        localStorage.setItem(key, value ? "true" : "false");
    }
};

window.uiScrollToSymbol = function (symbol) {
    const el = document.getElementById("brain-" + symbol);
    if (el) {
        el.scrollIntoView({ behavior: "smooth", block: "center" });
    }
};
