# BondCasts link-preview API

Azure Functions (.NET 9 isolated) that server-render rich landing pages for the
universal-link paths `/episode`, `/show`, `/e/<token>`, and `/s/<token>`, so
shared links unfurl with real show/episode metadata (title, artwork, notes)
instead of a generic card.

## Why this exists

The links carry the same identifiers the app uses — `feed` (the podcast's RSS
URL) and `guid` (the RSS `<guid>`). The static site can't turn those into a rich
page: browser JS can't fetch arbitrary feeds (CORS), and link crawlers don't run
JS anyway (so Open Graph tags must be in the served HTML). These Functions fetch
and parse the feed server-side and bake the OG tags into the response.

New app-generated links use encrypted path tokens so Messages and screenshots do
not expose raw feed URLs. The app encrypts `{ feed, guid }` or `{ feed }` with a
public key; the Functions app decrypts with the matching private key and then
uses the same feed-rendering path as the legacy query links.

Feed parsing mirrors the app's `RSSFeedParser.swift` so the web resolves a link
to the same show/episode the app does.

## Endpoints

| Path | Purpose |
| --- | --- |
| `GET /episode?feed=<rss>&guid=<guid>` | Rich episode page |
| `GET /show?feed=<rss>` | Rich show page |
| `GET /e/<encrypted-token>` | Rich encrypted episode page |
| `GET /s/<encrypted-token>` | Rich encrypted show page |
| `GET /api/share/episode/<encrypted-token>` | JSON resolver for the app |
| `GET /api/share/show/<encrypted-token>` | JSON resolver for the app |
| `POST /api/podcasts/search` with `{ "term": ..., "limit": ... }` | Public Apple podcast-directory search |
| `POST /api/podcasts/resolve` with `{ "url": ..., "itunesID": ... }` | Resolve redirects and return verified RSS metadata |
| `GET /api/companion/config` | Public, environment-backed CloudKit JS configuration |

Static Web Apps rewrites `/episode` → `/api/episode`, `/show` → `/api/show`,
`/e/*` → `/api/e/*`, and `/s/*` → `/api/s/*` (see
`../staticwebapp.config.json`). If the feed is unreachable, the token cannot be
decrypted, or the `guid` has aged out of the feed window, the endpoint serves a
generic fallback card so the link never looks broken.

## Encrypted share-link keys

Encrypted tokens have this shape:

```text
v1.<kid>.<rsa-encrypted-aes-key>.<nonce>.<ciphertext-and-tag>
```

The app ships the current public key and key id. The Functions app needs a
private-key ring in environment variables:

| Setting | Value |
| --- | --- |
| `ShareLinks__KeyIds` | Comma-separated key ids, for example `2026-07` |
| `ShareLinks__PrivateKeys__2026_07` | Base64-encoded PEM private key for key id `2026-07` |

Rotation is manual and low-frequency:

1. Generate a new RSA keypair.
2. Add the new private key to the web environment under a new key id.
3. Deploy web with both old and new private keys.
4. Update the app public-key constant and key id.
5. Ship the app.
6. Keep old private keys as long as old shared links should keep resolving.

Never put the private key in the app. The web resolver also rejects decrypted
feed URLs with query strings, fragments, or userinfo so encrypted links cannot be
used to render signed/private feeds.

## Local development

```bash
# from repo root — runs the static site + the API together
swa start . --api-location api
# or run just the Functions host
cd api && func start
```

Test: `http://localhost:4280/episode?feed=<rss-url>&guid=<guid>`

## Deploy

Deployed by `.github/workflows/azure-static-web-apps.yml` on push to `main`.

One-time setup:
1. Create a **Static Web App** in Azure (Standard tier if you want an SLA; Free
   tier is fine to start — managed Functions are included on both).
2. Copy its **deployment token** into the repo secret
   `AZURE_STATIC_WEB_APPS_API_TOKEN`.
3. Add `bondcasts.com` as a **custom domain** on the Static Web App and update
   DNS. Once verified, **remove `.github/workflows/deploy.yml`** — GitHub Pages
   and SWA can't both serve the apex domain.
4. Confirm `https://bondcasts.com/.well-known/apple-app-site-association` still
   returns the AASA (as `application/json`) after the cutover, or universal
   links stop opening the app.

## Related

The feed poller for BondCasts new-episode push (PodcastApp#135) lives in
`../poller` as a separate Functions project - SWA managed Functions only run
HTTP triggers, so its timer can't be deployed from here. See `../poller/README.md`.

## Notes

- `og:image` points at the podcast's own artwork URL, so image bandwidth stays
  on the podcast host — our egress is just small HTML responses.
- Episode notes are rendered as plain text (feed HTML is stripped) to avoid XSS.
  Swap in a sanitizer (e.g. `Ganss.Xss`) if you want to preserve safe formatting.
- Parsed feeds are cached in-memory for 15 minutes to absorb crawler bursts.
- Companion responses use `Cache-Control: no-store`; locked feeds and feed URLs
  containing query strings or fragments are also excluded from the in-memory
  parsed-feed cache because those components can contain capability tokens.
- Feed resolution rejects credentials and private, loopback, link-local, and
  documentation-only network destinations on the initial URL and every redirect.
- The CloudKit configuration endpoint may expose only an origin-restricted web
  API token. Never place a server-to-server CloudKit key in this Functions app.
- `host.json` disables `Host.Results`, suppressing managed Functions request
  telemetry at its source. Application Insights sampling exclusions do not turn
  collection off. Do not enable or export HTTP telemetry without redacting
  search terms, feed URLs, and request bodies.
