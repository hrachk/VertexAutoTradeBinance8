/**
 * vertex-oauth.js
 * Handles OAuth flows for Google One Tap, Telegram Login Widget, Apple Sign In.
 * Configured via window.vertexOAuthConfig (set in _Host.cshtml from appsettings).
 *
 * Config structure (set in _Host.cshtml):
 *   window.vertexOAuthConfig = {
 *     google:   { clientId: "..." },           // Google OAuth Client ID
 *     telegram: { botName: "VertexAIBot" },    // Telegram bot username (without @)
 *     apple:    { clientId: "...", redirectUri: "..." }  // Apple Service ID
 *   };
 */
window.vertexOAuth = {

    // ── Check which providers are configured ──────────────────────
    isGoogleConfigured() {
        return !!(window.vertexOAuthConfig?.google?.clientId);
    },
    isTelegramConfigured() {
        return !!(window.vertexOAuthConfig?.telegram?.botName);
    },
    isAppleConfigured() {
        return !!(window.vertexOAuthConfig?.apple?.clientId);
    },

    // ── Google One Tap / GSI ─────────────────────────────────────
    startGoogle(dotNet) {
        const cfg = window.vertexOAuthConfig?.google;
        if (!cfg?.clientId) { console.warn('[OAUTH] Google not configured'); return; }

        // Load Google Identity Services script once
        if (!window._googleGsiLoaded) {
            const script = document.createElement('script');
            script.src = 'https://accounts.google.com/gsi/client';
            script.async = true;
            script.defer = true;
            script.onload = () => {
                window._googleGsiLoaded = true;
                this._initGoogle(cfg, dotNet);
            };
            document.head.appendChild(script);
        } else {
            this._initGoogle(cfg, dotNet);
        }
    },

    _initGoogle(cfg, dotNet) {
        google.accounts.id.initialize({
            client_id: cfg.clientId,
            callback: (response) => {
                dotNet.invokeMethodAsync('OnGoogleToken', response.credential);
            },
            auto_select: false,
            cancel_on_tap_outside: true,
        });
        google.accounts.id.prompt((notification) => {
            if (notification.isNotDisplayed() || notification.isSkippedMoment()) {
                // One Tap not shown — open popup instead
                google.accounts.id.renderButton(
                    document.getElementById('lm-google-btn') || document.body,
                    { theme: 'filled_black', size: 'large', type: 'standard' }
                );
            }
        });
    },

    // ── Telegram Login Widget ─────────────────────────────────────
    startTelegram(dotNet) {
        const cfg = window.vertexOAuthConfig?.telegram;
        if (!cfg?.botName) { console.warn('[OAUTH] Telegram not configured'); return; }

        // Create a temporary container and inject Telegram widget
        const container = document.createElement('div');
        container.id = 'tg-widget-container';
        container.style.cssText = 'position:fixed;top:-9999px;left:-9999px;';
        document.body.appendChild(container);

        const script = document.createElement('script');
        script.src = 'https://telegram.org/js/telegram-widget.js?22';
        script.setAttribute('data-telegram-login', cfg.botName);
        script.setAttribute('data-size', 'large');
        script.setAttribute('data-radius', '8');
        script.setAttribute('data-request-access', 'write');
        script.setAttribute('data-onauth', 'window._tgAuthCallback(user)');
        script.async = true;
        container.appendChild(script);

        // Telegram widget opens a popup — callback fires when user approves
        window._tgAuthCallback = (user) => {
            document.body.removeChild(container);
            delete window._tgAuthCallback;
            // Convert to Dictionary<string, string> for .NET
            const data = {};
            Object.entries(user).forEach(([k, v]) => { data[k] = String(v); });
            dotNet.invokeMethodAsync('OnTelegramData', data);
        };

        // Click the hidden widget button to open Telegram login popup
        setTimeout(() => {
            const btn = container.querySelector('iframe');
            if (btn) {
                // Widget renders as iframe — click it
                const clickEvent = new MouseEvent('click', { bubbles: true });
                btn.dispatchEvent(clickEvent);
            } else {
                // Fallback: open Telegram OAuth URL directly
                const width  = 550, height = 470;
                const left   = (screen.width  - width)  / 2;
                const top    = (screen.height - height) / 2;
                const url    = `https://oauth.telegram.org/auth?bot_id=${cfg.botId || ''}&origin=${encodeURIComponent(location.origin)}&request_access=write`;
                window.open(url, 'tgAuth',
                    `width=${width},height=${height},left=${left},top=${top}`);
            }
        }, 500);
    },

    // ── Apple Sign In ─────────────────────────────────────────────
    startApple(dotNet) {
        const cfg = window.vertexOAuthConfig?.apple;
        if (!cfg?.clientId) { console.warn('[OAUTH] Apple not configured'); return; }

        // Load Apple JS SDK once
        if (!window._appleLoaded) {
            const script = document.createElement('script');
            script.src = 'https://appleid.cdn-apple.com/appleauth/static/jsapi/appleid/1/en_US/appleid.auth.js';
            script.async = true;
            script.onload = () => {
                window._appleLoaded = true;
                this._initApple(cfg, dotNet);
            };
            document.head.appendChild(script);
        } else {
            this._initApple(cfg, dotNet);
        }
    },

    _initApple(cfg, dotNet) {
        AppleID.auth.init({
            clientId:    cfg.clientId,
            scope:       'email name',
            redirectURI: cfg.redirectUri || location.origin + '/auth/apple/callback',
            usePopup:    true,
        });

        AppleID.auth.signIn()
            .then(response => {
                const token    = response.authorization?.id_token || '';
                const userJson = response.user ? JSON.stringify(response.user) : null;
                dotNet.invokeMethodAsync('OnAppleToken', token, userJson);
            })
            .catch(err => {
                console.warn('[APPLE] Sign in cancelled or failed:', err);
            });
    },
};
