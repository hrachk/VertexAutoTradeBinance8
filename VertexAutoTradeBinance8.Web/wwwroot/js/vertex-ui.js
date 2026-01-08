// vertex-ui.js
window.vertex = {
    getPinnedAllowed: function () {
        return localStorage.getItem("pin_allowed") === "1";
    },

    setPinnedAllowed: function (value) {
        localStorage.setItem("pin_allowed", value ? "1" : "0");
    }
};
