# Telegram Mini App (TWA) — Vertex Dashboard

## What you get
- Page: `https://<your-https-host>/twa/dashboard`
- API: `/api/twa/summary` · `/positions` · `/stats` · `POST /pause` · `/kill` · `/resume` · `/close?symbol=`
- Bot commands: `/dashboard` `/app` `/start` → inline **Open Dashboard** (WebApp button)

## 1. Run Web locally
```powershell
cd VertexAutoTradeBinance8.Web
dotnet run
```
Note the port (e.g. `http://127.0.0.1:5101`).

## 2. HTTPS tunnel (required by Telegram)

### Option A — Cloudflare Tunnel (recommended)
```bash
# install cloudflared, then:
cloudflared tunnel --url http://127.0.0.1:5101
```
Copy the `https://xxxx.trycloudflare.com` URL.

### Option B — ngrok
```bash
ngrok http 5101
```
Use the `https://….ngrok-free.app` URL.

## 3. Config
**Engine** (User Secrets or private config — not git):
```json
"Telegram": {
  "BotToken": "...",
  "ChatId": "...",
  "MiniAppUrl": "https://xxxx.trycloudflare.com/twa/dashboard"
}
```

**Web** must use the same `SharedData:Root` as the Engine so SQLite, kill flags, and demo account are visible.

## 4. Test
1. Start Engine + Web + tunnel.
2. In Telegram: `/dashboard` → Open Dashboard.
3. UI refreshes every **2.5s**.
4. **Pause 30m** / **Resume** / **KILL** (confirm) write `emergency_kill.flag` and flatten Demo; Engine blocks new entries while flag exists.
5. Full Live exchange flatten still runs when Engine handles Telegram `/kill` (FlattenAllService). TWA KILL prioritizes flag + Demo clear; for full Live flatten also send `/kill` to the bot or ensure Engine watches the same flag and runs flatten on change (flag is enough to block entries).

## 5. Security notes
- Tunnel URL is public while running — treat as temporary ops channel.
- Prefer Cloudflare Access or a secret query token for production.
- Never commit BotToken or MiniAppUrl with secrets.
