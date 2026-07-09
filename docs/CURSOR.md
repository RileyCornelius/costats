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
  can diverge from the dashboard bars. `autoPercentUsed` is the dashboard's
  **First-party models** bar, `apiPercentUsed` its **API** bar, and `totalPercentUsed` a
  weighted blend of the two (not an average — e.g. auto 2% + api 12% has been observed as
  total 4%).
- `POST https://cursor.com/api/dashboard/get-filtered-usage-events` — per-request usage
  events, used to estimate token cost. Requires the same session cookie plus an `Origin:
  https://cursor.com` header. Request body (all dates epoch **milliseconds as strings**):

  ```json
  { "teamId": 0, "startDate": "1751000000000", "endDate": "1753600000000", "page": 1, "pageSize": 100 }
  ```

  Verified response shape (2026-07):

  ```json
  {
    "totalUsageEventsCount": 34,
    "usageEventsDisplay": [
      {
        "timestamp": "1783635383682",
        "model": "gpt-5.6-sol-high",
        "kind": "USAGE_EVENT_KIND_INCLUDED_IN_PRO",
        "isTokenBasedCall": true,
        "tokenUsage": {
          "inputTokens": 124067, "outputTokens": 17787, "cacheReadTokens": 2636480,
          "totalCents": 247.2185
        },
        "chargedCents": 247.2185
      }
    ]
  }
  ```

  `timestamp` is epoch ms as a string. `tokenUsage.cacheWriteTokens` appears for models
  with cache-write billing. `tokenUsage.totalCents` is Cursor's own pre-discount API-value
  estimate for the call; `chargedCents` is after discounts (`discountPercentOff`). The
  fetcher paginates until `totalUsageEventsCount` is reached (page size 100, hard cap 50
  pages) and tolerates numeric fields encoded as strings.
- `GET https://cursor.com/api/auth/me` — email/name. **Best-effort only**: observed to return
  404 ("User not found") for some account types even when `usage-summary` succeeds, so
  identity primarily comes from the `cursorAuth/cachedEmail` DB key.
- HTTP 401/403 from any endpoint means the session is invalid/expired.

## Field → widget mapping

- **First-party models** bar (session slot): `individualUsage.plan.autoPercentUsed`,
  rendered as `SessionUsed`/`SessionLimit = 100`. Fallbacks when absent (payload shape
  varies by account type): `totalPercentUsed`, then `used/limit`.
- **API** bar (week slot): `individualUsage.plan.apiPercentUsed`, rendered the same way.
  No fallback — the bar is hidden when the field is absent.
- On-demand spend (`onDemand.used`/`limit`) is still parsed but no longer displayed; the
  two visible bars mirror the dashboard's First-party models / API split.
- Reset window: `billingCycleEnd` (monthly billing cycle) for both bars.
- Plan label: `membershipType` ("pro" → "Pro").

## Estimated cost (Cost section)

The Cost section shows Today / Last 30 days token counts and estimated cost, built from
`get-filtered-usage-events` the same way Codex and Claude costs are built from local logs:

- Events are aggregated per local day and model; `tokenUsage` maps input, cache-read,
  cache-write, and output tokens into the shared `TokenLedger`.
- Models with a pricing-catalog entry (OpenAI/Anthropic ids the model matcher can resolve)
  are priced at **provider list rates**, matching Codex/Claude semantics.
- Cursor-proprietary models (`composer-*`, mode-suffixed ids like `gpt-5.6-sol-high` when
  unmatched) fall back to Cursor's own `tokenUsage.totalCents` estimate instead of
  contributing $0. Events with no tokens are skipped.
- The paginated events call is heavier than `usage-summary`, so the digest is cached for
  30 minutes (`CursorUsageSource.ConsumptionCacheTtl`); quota bars keep refreshing at the
  normal cadence, and a failed events fetch falls back to the last good digest. Costs are
  **estimates of API value**, not what Cursor bills the subscription.

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
