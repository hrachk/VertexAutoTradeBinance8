// vertex-auth.js — cookie helpers for Blazor auth persistence
// Called via JS Interop from AuthSessionService.cs
window.vertexAuth = {
    /**
     * Get cookie value by name.
     * Returns null if not found.
     */
    getCookie(name) {
        const prefix = name + '=';
        const parts  = document.cookie.split(';');
        for (let i = 0; i < parts.length; i++) {
            const p = parts[i].trim();
            if (p.startsWith(prefix))
                return decodeURIComponent(p.slice(prefix.length));
        }
        return null;
    },

    /**
     * Set cookie.
     * days=0 → session cookie (cleared when browser closes).
     * days=-1 → delete cookie immediately.
     * days=30 → persist 30 days (Remember Me).
     */
    setCookie(name, value, days) {
        let expires = '';
        if (days > 0) {
            const d = new Date();
            d.setTime(d.getTime() + (days * 24 * 60 * 60 * 1000));
            expires = '; expires=' + d.toUTCString();
        } else if (days < 0) {
            expires = '; expires=Thu, 01 Jan 1970 00:00:00 UTC';
        }
        document.cookie = name + '=' + encodeURIComponent(value)
            + expires
            + '; path=/';
        // SameSite=Strict for security (same origin only)
            // + '; SameSite=Strict';
    },

    /**
     * Delete cookie by name.
     */
    deleteCookie(name) {
        this.setCookie(name, '', -1);
    }
};
