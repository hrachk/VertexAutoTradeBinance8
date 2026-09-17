# Vertex AutoTrade — Sandbox / Demo Test Checklist

**Baseline commit:** `8b77580` (+ QA hardening)  
**Mode:** Demo / Sandbox first. Live only after all items pass.

---

## 0. Preconditions

- [ ] `git pull` + `dotnet restore` + `dotnet build` (Engine + Web)
- [ ] `Telegram:BotToken` + `Telegram:ChatId` via User Secrets (not committed)
- [ ] `SharedData:Root` same path for Engine and Web
- [ ] Binance keys optional for pure Demo; required for Live flatten tests
- [ ] Engine Worker started, then Web Blazor

**Startup log tags to confirm:**

| Tag | Expected |
|-----|----------|
| `[TIME-SYNC]` | offset ms logged within ~15 min (or first sync ~5s) |
| `[SQLITE-JOURNAL]` | `PRAGMA quick_check=ok` |
| `[TG]` / `[TG-HEALTH]` | token present or log-only |
| `[NEWS-SHADOW]` | optional, no crash |
| `[LIVESIG]` | path to live_signals.json |

---

## 1. Sanity & Health Monitoring

### 1.1 Telegram health
- [ ] With valid token: send `/status` → reply within a few seconds
- [ ] Disconnect network 90s → console shows `[TG-HEALTH] no successful Telegram API contact for …s`
- [ ] Restore network → `[TG-HEALTH]` recovers after `getMe` or next `getUpdates`

### 1.2 SQLite integrity
- [ ] On Engine start: `[SQLITE-JOURNAL] PRAGMA quick_check=ok`
- [ ] File exists: `{SharedData:Root}/vertex_journal.db`
- [ ] After several Demo closes: DB size grows; `/status` shows **SQLite: X.XX MB · integrity=ok**
- [ ] (Optional) Copy a large DB >32MB → restart → backup `vertex_journal_*.db.bak` created

### 1.3 Time sync
- [ ] `/status` shows **TimeOffset: N ms · lastSync=HH:mm:ssZ**
- [ ] Offset typically within a few hundred ms of true skew

### 1.4 Shadow KPI UI
- [ ] Open `/ai-dashboard`
- [ ] Widget **News Filter Alpha Impact** visible (ΔPF, ΔDD, RecommendEnableGate)

---

## 2. Telegram command scenarios (Demo)

### 2.1 `/status`
1. [ ] Bot replies with Engine state, day PnL, BTC vol, SQLite, TimeOffset, TG lastOk
2. [ ] No stack traces in Engine console

### 2.2 `/pause 2`
1. [ ] Reply confirms pause
2. [ ] Generate/wait for CORE signal → entry **rejected** (`KILL_SWITCH` or kill flag)
3. [ ] After ~2 minutes → auto-clear (or use `/resume`)
4. [ ] New Demo entries allowed again (subject to filters)

### 2.3 `/kill` (flatten)
**Prep:** open 1–2 Demo positions (Demo mode ON).

1. [ ] Send `/kill`
2. [ ] First reply: flattening…
3. [ ] Report lists DEMO closed counts / PnL lines
4. [ ] `demo-account.json` positions empty (or cleared)
5. [ ] `emergency_kill.flag` present under SharedData root
6. [ ] New signals do **not** open positions
7. [ ] Engine log contains `[FLATTEN]` and/or `[KILL] ACTIVATED`

### 2.4 `/resume`
1. [ ] Flag cleared
2. [ ] `/status` shows Engine: **running**
3. [ ] New Demo entries can open again

### 2.5 Live `/kill` (only if testnet or tiny size)
1. [ ] Open tiny Live position on testnet
2. [ ] `/kill` cancels open/algo orders and market-closes
3. [ ] Report includes LIVE CLOSE lines
4. [ ] Exchange UI shows flat

---

## 3. Edge cases (force majeure)

### 3.1 Network loss during `/kill`
| Step | Action | Expected |
|------|--------|----------|
| 1 | Start `/kill` with Live positions | Flatten begins |
| 2 | Cut internet mid-flight | Polly `[BINANCE-RETRY]` NETWORK attempts |
| 3 | Restore network | Remaining closes succeed on retry **or** partial report + kill flag still blocks new entries |
| 4 | Re-run `/kill` if needed | Idempotent flatten of leftovers |

**Note:** Kill flag stays active even if some closes failed — safe default.

### 3.2 Rate limit HTTP 429
| Step | Expected |
|------|----------|
| Binance returns 429 / -1003 | `[BINANCE-RETRY] reason=RATE_LIMIT` with 1s→2s→4s→8s backoff |
| After retries exhausted | Error logged; no infinite loop |
| OrderExecutor Place/Cancel | All go through `ExecBinanceAsync` |

### 3.3 Clock skew -1021
| Step | Expected |
|------|----------|
| Simulated skew / -1021 | Retry + `TimeSync.SyncOnceAsync` |
| `[TIME-SYNC]` | New offset logged |

---

## 4. Risk circuit breakers (Demo)

- [ ] **BTC vol:** simulate or wait for BTC 5m move ≥1.2% → `[BTC-VOL] LOCK` → alt entries blocked 15m
- [ ] **Daily DD:** 3 SL in a row or −3% day → `EMERGENCY_STOP` until 00:00 UTC
- [ ] **HardCap / 1R:** new Demo sizes bounded in $ risk

---

## 5. Regression smoke (15 min)

- [ ] CORE signal appears in live_signals / Signal Log
- [ ] Demo opens with SL/TP levels
- [ ] Close → journal row in SQLite (no reliance on trade-journal.json if SqliteOnly=true)
- [ ] Web History / Bot journal shows trade
- [ ] No Blazor circuit crash on Market page for 10 min of ticks

---

## 6. Sign-off

| Role | Name | Date | Pass |
|------|------|------|------|
| Operator | | | ☐ |
| QA | | | ☐ |

**Go-Live gate:** all section 1–2 + smoke section 5 green; Live `/kill` verified on testnet.
