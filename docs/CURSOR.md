# Cursor reference notes

This file captures Cursor-related implementation notes and how authentication works.

## How costats authenticates with Cursor

Cursor has no official individual usage API — the official Admin API (`api.cursor.com`) is
Team/Enterprise-admin only. costats reuses the logged-in Cursor session against the
`cursor.com/api/*` web endpoints, authenticated purely by the cookie:

```
Cookie: WorkosCursorSessionToken=<userId>%3A%3A<accessToken>
```

Two credential sources, tried in order:

### 1. Automatic (local Cursor install)

costats reads the session token straight from Cursor's local state database, so no setup is
needed beyond being signed in to the Cursor app.

- Database: SQLite file `state.vscdb`, table `ItemTable` (key/value).
  - Windows: `%APPDATA%\Cursor\User\globalStorage\state.vscdb`
  - macOS: `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`
  - Linux: `~/.config/Cursor/User/globalStorage/state.vscdb`
  - Override the data dir with the `CURSOR_DATA_DIR` environment variable.
- Keys read:
  - `cursorAuth/accessToken` — the session JWT.
  - `cursorAuth/cachedEmail` / `cursorAuth/stripeMembershipType` — identity (email + plan)
    without any extra API call.
  - `workbench.experiments.statsigBootstrap` — fallback source for the user id.
- The user id comes from the JWT `sub` claim, e.g. `github|54592152` → `54592152`
  (the part after the last `|`; the prefix varies by sign-in method).
- The DB is opened read-only; if Cursor holds a write lock, costats copies
  `state.vscdb` (+ `-wal`/`-shm`) to a temp folder and reads the copy.

### 2. Manual session token (fallback)

Only needed if auto-detection fails (Cursor not installed on this machine, signed out, etc.):

1. Sign in at [cursor.com](https://cursor.com) in your browser.
2. Open DevTools (`F12`) → **Application** → **Cookies** → `https://cursor.com`.
3. Copy the value of the `WorkosCursorSessionToken` cookie.
4. In costats: **Settings → Cursor → Session token** → paste → **Save token**.

The paste box accepts a bare cookie value, a `WorkosCursorSessionToken=...` pair, or a full
`Cookie:` header — costats normalizes all three. The token is stored in the Windows
Credential Vault (`cursor.token`) and validated against the API on save.

## Endpoints used

- `GET https://cursor.com/api/usage-summary` — primary usage source. Verified response shape:

  ```json
  {
    "billingCycleStart": "2026-07-03T18:57:21.000Z",
    "billingCycleEnd": "2026-08-03T18:57:21.000Z",
    "membershipType": "pro",
    "individualUsage": {
      "plan": {
        "enabled": true, "used": 0, "limit": 2000, "remaining": 2000,
        "autoPercentUsed": 0, "apiPercentUsed": 0, "totalPercentUsed": 0
      },
      "onDemand": { "enabled": false, "used": 0, "limit": null, "remaining": null }
    }
  }
  ```

  `used`/`limit`/`remaining` are **cents**; the `*PercentUsed` fields are already 0–100
  percentages. `plan.limit` is often just the subscription price in cents, so `used/limit`
  can diverge from the dashboard bars — `totalPercentUsed` is authoritative.
- `GET https://cursor.com/api/auth/me` — email/name. **Best-effort only**: observed to return
  404 ("User not found") for some account types even when `usage-summary` succeeds, so
  identity primarily comes from the `cursorAuth/cachedEmail` DB key.
- HTTP 401/403 from either endpoint means the session is invalid/expired.

## Field → widget mapping

- Primary bar (session slot): `individualUsage.plan.totalPercentUsed`, rendered as
  `SessionUsed`/`SessionLimit = 100`. Fallbacks when absent: average of
  `autoPercentUsed`/`apiPercentUsed`, then `used/limit`.
- Secondary bar (week slot): on-demand spend, `onDemand.used`/`onDemand.limit` in cents —
  only shown when a spending limit is configured (`limit > 0`).
- Reset window: `billingCycleEnd` (monthly billing cycle).
- Plan label: `membershipType` ("pro" → "Pro").

## Troubleshooting

- **"Cursor session expired"** — re-open Cursor (it refreshes the token) or sign in again;
  alternatively paste a fresh browser token in Settings.
- **"Cursor session not found"** — Cursor isn't installed/signed in on this machine; use the
  manual token.
- **Locked database** — handled automatically via the temp-copy read; no action needed.
- The `cursor.com/api/*` endpoints are unofficial and can change without notice. The parser
  tolerates missing fields, and the manual-token path provides a workaround if the local
  token format changes.

## References

- Reference implementation: `packages/CodexBar-main/Sources/CodexBarCore/Providers/Cursor/CursorStatusProbe.swift`
  and `packages/CodexBar-main/docs/cursor.md` (endpoint + schema source; CodexBar imports
  browser cookies on macOS instead of reading `state.vscdb`).
