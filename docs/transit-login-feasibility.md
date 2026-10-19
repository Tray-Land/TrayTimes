# Sign-in feasibility (Phase 2 plan)

Phase 1 ([transit-app-migration.md](transit-app-migration.md)) has no sign-in. This plan looks at what "user login" could mean for Tray Times, how feasible each option is, what it would enable, and what it would cost, so a decision can be made after Phase 1 is tested. Nothing here is implemented.

Created: 2026-10-15.

## What Transit offers

- **The public API has no user accounts.** The v4 OpenAPI spec defines one security scheme, an `apiKey` request header, and every endpoint is anonymous: departures, nearby, plans, routes, vehicles, alerts. There are no endpoints for a rider's saved places, favorite lines, trip history, or Royale subscription.
- **Transit accounts exist, but only for partners.** Transit's [Mobile Ticketing API guidelines](https://resources.transitapp.com/article/396-mobile-ticketing-api-guidelines) describe Transit acting as an identity provider: it issues JWTs, which the partner validates against Transit's JSON Web Key Set and exchanges for its own access token. That flow lets a *ticketing partner's* backend recognize a Transit user. It doesn't let a third-party client sign in to Transit and read that user's Transit data, and it's offered only under a partnership agreement.
- **Quotas are per key.** The free tier is 5 calls a minute and 1,500 a month per key. Higher limits are negotiated with partners@transit.app.

So "log in with Transit and see my Transit favorites" isn't possible with what's published. Every option below is something Tray Times would build itself, or a key-management choice.

## Options

| | Option | What the rider does | Feasibility | Effort |
|---|--------|---------------------|-------------|--------|
| A | **Transit account sign-in** | Signs in with their Transit account; Tray Times shows their Transit app favorites | **Not feasible** without a new partnership: there's no public endpoint for user data | Unknown; depends on Transit |
| B | **Bring your own key** | Pastes their own free Transit key into Settings (stored in Windows Credential Locker) | **High.** No backend, a few days of work | S |
| C | **Microsoft account sync** | Signs in with a Microsoft account; favorites, places, and settings sync through their OneDrive app folder | **High.** MSAL + Microsoft Graph, no backend of ours | M |
| D | **Tray Times service** | Signs in to a Tray Times account; the app talks to our backend, which holds one negotiated Transit key | **Medium.** Needs Transit's agreement to proxy and cache, plus hosting, costs, and operations | L–XL |

### A. Transit account sign-in: not feasible today

Ask Transit (partners@transit.app) whether a read-only, user-consented API for saved places and favorite lines is available or planned. If one appears, it would most likely be an OpenID Connect sign-in (the ticketing flow already uses JWTs and a JWKS), and the work would look like option C, with the data source swapped. Don't schedule work on it until Transit says yes.

### B. Bring your own key: the cheapest way to distribute

**Why it matters:** with one key compiled in, every install shares 1,500 calls a month. That's fine for one developer and unworkable for a Store release. With per-user keys, each rider gets the full free tier.

**Design:**
- Settings → Transit API: a "Use my own key" field with a link to the request form. Store the key in `Windows.Security.Credentials.PasswordVault`, never in LocalSettings or logs. The compiled-in key becomes optional, for developer builds only.
- Validate a new key with one cheap request (`available_networks` for the home location) and show "Key works" or "Key rejected". A 401/403 already pauses requests for 30 minutes; a new key should clear that.
- The budget and the tally move with the key: per-key counts, so switching keys starts a fresh month. `TransitBudget` already takes its limits and usage as inputs.
- First run without a key: the existing "API key needed" states point to the Settings field instead of OAuth.resw.

**Enables:** Store or GitHub distribution within the free tier; per-rider limits, so one heavy user can't exhaust everyone's month.
**Costs:** onboarding friction. Transit approves keys by form, which may take days, so the README and first-run text need to set expectations.
**Risks:** Transit's terms for end-user keys aren't published, so confirm with partners@transit.app that riders may each request a key for a client app.

### C. Microsoft account sync: favorites on every PC

**Design:**
- Sign in with MSAL using the `Files.ReadWrite.AppFolder` scope (personal Microsoft accounts and work/school accounts), with token caching through MSAL's Windows broker (WAM), so there's no password UI of our own.
- Store `favorites.json` in the app's OneDrive special folder (`/me/drive/special/approot`) with saved stops, favorite places, head-out minutes, and API limits. Sync on launch and after a change, with last-writer-wins per item, using the drive item's eTag to avoid overwriting newer data.
- Optional: keep the rider's own Transit key (option B) in the same account, encrypted with DPAPI per device rather than synced in plain text. Or don't sync it, and ask for it once per PC.
- Signed out, the app works exactly as Phase 1 does.

**Enables:** the same favorites and places on a desktop and a laptop; a backup that survives reinstalls; "set up once" for riders with several PCs.
**Costs:** an app registration in Entra ID, MSAL and Microsoft Graph package dependencies, a privacy note (home and work addresses would live in the rider's own OneDrive), and accessibility and globalization work for the sign-in UI.
**Transit calls:** none. Sync goes to Microsoft Graph, so the Transit budget is unaffected.
**Note:** Windows' own app-data roaming isn't an alternative; Microsoft stopped roaming it for packaged apps. OneDrive through Graph is the supported path.

### D. Tray Times service: scale and shared caching

**Design:** a small backend (for example Azure Functions plus Table Storage) holds one negotiated Transit key. The app signs in (Microsoft account or Entra External ID) and asks *our* API for departures, discovery, and plans. The server applies the same coalescing and caching as Phase 1, but **across all riders**: one `stop_departures` call per stop per minute serves everyone watching that stop, and discovery and plans are cached per area.

**Enables:**
- Distribution without asking riders for keys.
- Far fewer Transit calls per rider in shared areas (a downtown stop watched by 50 riders costs the same as one).
- **Server-side ride tracking:** the server polls the tracked stop and pushes head-out and change alerts through the Windows Push Notification Service, so the PC can sleep and alerts still arrive, replacing "keep awake while tracking".
- Sync (as in C) as a by-product of the account.

**Costs:** hosting, monitoring, abuse protection (per-user rate limits), privacy obligations (riders' locations and places would pass through our servers, which needs a privacy policy and data retention rules), and a negotiated Transit agreement that explicitly allows proxying and shared caching. It's the largest effort by far, and it means running a service.

## Recommendation

1. **Finish and test Phase 1** with the developer key.
2. **Phase 2a: option B** (bring your own key), before any public release. It's the smallest change that makes distribution viable, and it fits the existing budget and gate design.
3. **Phase 2b: option C** (Microsoft account sync), if riders use more than one PC. It's independent of B and costs no Transit calls.
4. **Option D only if** Tray Times grows past what per-user keys can serve, or server-side tracking alerts become a priority, and only after Transit agrees in writing to proxying and caching.
5. **Option A:** ask Transit, and revisit if they offer a user-data API.

## Questions for Transit (partners@transit.app)

1. May individual riders each request a free key for use in a third-party Windows client (option B)?
2. Is there, or will there be, a user-consented API for a rider's saved places and favorite lines (option A)?
3. Do the terms allow a backend to proxy the API and share cached responses across riders (option D)? What are the attribution requirements?
4. When does the monthly quota reset, and what does an over-quota response look like?

## Effort and testing notes

| Phase | Main changes | Tests |
|-------|--------------|-------|
| 2a (B) | Settings key field and validation; `Secrets` reads Credential Locker first; per-key tally | Core: key validation and per-key budget; app: manual first-run and key-change checks |
| 2b (C) | MSAL sign-in, Graph app-folder client, merge logic, sign-in UI | Core: merge and conflict rules (pure functions); manual sign-in and offline checks |
| D | Backend API, auth, server cache, WNS alerts; app swaps `TransitAppService` for a `TrayTimesService` client behind `ITransitService` | Server unit and integration tests; load test of shared caching |

Because the app depends on `ITransitService`, option D's client could replace `TransitAppService` without UI changes, and the budget would then govern calls to our service instead.
