# BondCasts feed poller

Azure Functions (.NET 9 isolated) doing server-side new-episode discovery for
the BondCasts app (PodcastApp#135): polls the feeds devices register in
CloudKit and writes `NewEpisode` records whose CKQuerySubscription fan-out
delivers the push.

This is a SEPARATE project from `../api` because Static Web Apps managed
Functions only run HTTP triggers - the deploy pipeline hard-rejects a
`timerTrigger`. Deploy this as a standalone Azure Functions app (Consumption
is fine; Table Storage rides the required storage account), or link it to the
SWA as bring-your-own-functions (Standard tier).

## What it does

Every 10 minutes `PollFeeds` (`Functions/FeedPollerFunctions.cs`):

1. Reads `PolledFeed` registrations from the app container's public CloudKit
   database (server-to-server key auth), unions them by `feedHash`, and expires
   rows devices haven't re-touched in ~30 days. The app is the ONLY place
   `feedHash` is derived - the server reads it from the rows, never recomputes.
2. Conditionally GETs each due feed (ETag/If-Modified-Since; per-feed base
   interval `Poller__IntervalMinutes` + jitter, exponential backoff on
   failures) and head-parses guid/title/pubDate only, stopping after 250 items.
3. On discovery writes ONE `NewEpisode` record per feed per cycle (multi-episode
   discoveries collapse to "N new episodes" - every record write is one banner
   per subscribed device), then prunes its own records older than ~30 days.
   First sight of a feed seeds state without announcing; new-to-us items with
   pubDates older than 14 days are treated as backfill, not news.

Per-feed state (ETag, known-episode window, backoff) lives in Table Storage
(`bondcastsfeedpollstate`) in the Functions storage account.

## Configuration

Without these settings the poller no-ops (safe to deploy unconfigured):

| Setting | Value |
| --- | --- |
| `CloudKit__Container` | `iCloud.com.bondcodes.PodcastApp` |
| `CloudKit__Environment` | `development` or `production` |
| `CloudKit__KeyId` | Server-to-server key ID for THAT environment (keys are env-scoped; same keypair, one key per env) |
| `CloudKit__PrivateKeyPemBase64` | `base64 < eckey.pem` of the EC P-256 private key |
| `Poller__IntervalMinutes` | Optional; per-feed base poll interval (default 20) |

## Production telemetry

The production Function App is connected to Application Insights, but
`host.json` disables `Host.Results` request telemetry and automatic dependency
tracking. Custom traces and exceptions remain available for operational failures
and are retained for 30 days. Log messages omit public feed URLs, public
registration record names, and podcast titles; a feed hash and exception details
can remain. Previously collected request and dependency records have four-day
retention configured, although Azure can preserve data affected by a retention
reduction for up to 30 additional days as a recovery safeguard. App Service HTTP
logging, detailed errors, failed-request tracing, and Azure Monitor diagnostic
exports are disabled. Keep this configuration and `../privacy.html` in sync.

## Local development

```bash
cd poller && func start
```

Timers need `AzureWebJobsStorage`; the default `local.settings.json` points at
Azurite (`UseDevelopmentStorage=true`), so start Azurite first or point it at a
real storage account. Add the `CloudKit__*` values (development environment)
to `local.settings.json` to run a real cycle against dev.
