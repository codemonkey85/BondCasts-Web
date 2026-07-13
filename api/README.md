# BondCasts API

Azure Functions (.NET 9 isolated) with two jobs:

1. **Link previews** — server-render rich landing pages for the universal-link
   paths `/episode` and `/show`, so shared links unfurl with real show/episode
   metadata (title, artwork, notes) instead of a generic card.
2. **Feed poller** — server-side new-episode discovery for the BondCasts app
   (PodcastApp#135): polls the feeds devices register in CloudKit and writes
   `NewEpisode` records whose CKQuerySubscription fan-out delivers the push.

## Why this exists

The links carry the same identifiers the app uses — `feed` (the podcast's RSS
URL) and `guid` (the RSS `<guid>`). The static site can't turn those into a rich
page: browser JS can't fetch arbitrary feeds (CORS), and link crawlers don't run
JS anyway (so Open Graph tags must be in the served HTML). These Functions fetch
and parse the feed server-side and bake the OG tags into the response.

Feed parsing mirrors the app's `RSSFeedParser.swift` so the web resolves a link
to the same show/episode the app does.

## Endpoints

| Path | Purpose |
| --- | --- |
| `GET /episode?feed=<rss>&guid=<guid>` | Rich episode page |
| `GET /show?feed=<rss>` | Rich show page |

Static Web Apps rewrites `/episode` → `/api/episode` and `/show` → `/api/show`
(see `../staticwebapp.config.json`). If the feed is unreachable or the `guid` has
aged out of the feed window, the endpoint serves a generic fallback card so the
link never looks broken.

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

## Feed poller (PodcastApp#135)

Every 10 minutes `PollFeeds` (`Functions/FeedPollerFunctions.cs`):

1. Reads `PolledFeed` registrations from the app container's public CloudKit
   database (server-to-server key auth), unions them by `feedHash`, and expires
   rows devices haven't re-touched in ~30 days. The app is the ONLY place
   `feedHash` is derived — the server reads it from the rows, never recomputes.
2. Conditionally GETs each due feed (ETag/If-Modified-Since; per-feed base
   interval `Poller__IntervalMinutes` + jitter, exponential backoff on
   failures) and head-parses guid/title/pubDate only, stopping after 250 items.
3. On discovery writes ONE `NewEpisode` record per feed per cycle (multi-episode
   discoveries collapse to "N new episodes" — every record write is one banner
   per subscribed device), then prunes its own records older than ~30 days.
   First sight of a feed seeds state without announcing; new-to-us items with
   pubDates older than 14 days are treated as backfill, not news.

Per-feed state (ETag, known-episode window, backoff) lives in Table Storage
(`bondcastsfeedpollstate`) in the Functions storage account.

### Configuration

Without these settings the poller no-ops and link previews work as before:

| Setting | Value |
| --- | --- |
| `CloudKit__Container` | `iCloud.com.bondcodes.PodcastApp` |
| `CloudKit__Environment` | `development` or `production` |
| `CloudKit__KeyId` | Server-to-server key ID for THAT environment (keys are env-scoped; same keypair, one key per env) |
| `CloudKit__PrivateKeyPemBase64` | `base64 < eckey.pem` of the EC P-256 private key |
| `Poller__IntervalMinutes` | Optional; per-feed base poll interval (default 20) |

### Hosting caveat

Static Web Apps **managed** Functions support HTTP triggers only — the timer
will not run there. Deploy this project as a standalone Azure Functions app
(Consumption is fine; Table Storage rides the required storage account), either
linked to the SWA as bring-your-own-functions (Standard tier) or alongside it
with the SWA keeping the managed link-preview endpoints.

## Notes

- `og:image` points at the podcast's own artwork URL, so image bandwidth stays
  on the podcast host — our egress is just small HTML responses.
- Episode notes are rendered as plain text (feed HTML is stripped) to avoid XSS.
  Swap in a sanitizer (e.g. `Ganss.Xss`) if you want to preserve safe formatting.
- Parsed feeds are cached in-memory for 15 minutes to absorb crawler bursts.
