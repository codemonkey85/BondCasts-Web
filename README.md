# bondcasts-web

Marketing site and web companion for **BondCasts**, served at
[bondcasts.com](https://bondcasts.com) through Azure Static Web Apps. The
frontend remains plain HTML/CSS and native JavaScript modules with no build step;
the companion's public podcast APIs use the existing .NET Azure Functions app.

## Contents

| File | Purpose |
| --- | --- |
| `index.html` | Landing page |
| `privacy.html` | Privacy policy (required for App Store Connect) |
| `support.html` | Support page (required for App Store Connect) |
| `styles.css` | Shared styles (dark/light aware) |
| `discover/` | Public podcast discovery and private iCloud follow companion |
| `scripts/companion/` | Native modules for directory, feed identity, and CloudKit adapter V1 |
| `assets/logo.svg` | Brand mark (chain-link "B" + equalizer; navy→cyan) |
| `assets/favicon.svg` | Favicon (same mark) |
| `assets/apple-touch-icon.png` | 180×180 touch icon |
| `assets/og-image.png` | 1200×630 social share image |
| `.well-known/apple-app-site-association` | Universal Links association file |
| `CNAME` | Custom domain for GitHub Pages (`bondcasts.com`) |

The brand mark is a **placeholder direction**, not the final app icon — a clean
play + broadcast-waves glyph on the app's navy→cyan gradient. Regenerate the PNGs
from the SVG with the snippet in git history if you tweak `logo.svg`.

## Legacy GitHub Pages setup

The production site is moving to Azure Static Web Apps so the companion and
Functions API share one origin. These settings document the previous static-only
host and remain useful only until DNS cutover is complete.

1. Push this repo to GitHub.
2. **Settings → Pages** → Source: *Deploy from a branch* → `main` / root.
3. Under **Custom domain**, enter `bondcasts.com` (the `CNAME` file already sets
   this) and enable **Enforce HTTPS** once the cert provisions.

## DNS (GoDaddy)

Point `bondcasts.com` at GitHub Pages. For an apex domain, add these **A**
records (and optionally the IPv6 **AAAA** records):

```
A    @    185.199.108.153
A    @    185.199.109.153
A    @    185.199.110.153
A    @    185.199.111.153
```

Add a `CNAME` record for `www` → `codemonkey85.github.io` if you want the www
subdomain to work too.

`bondcasts.app` can keep forwarding to `bondcasts.com` at GoDaddy.

## Web companion configuration

`/discover` keeps search and verified feed previews public. Followed state and
mutations run directly between CloudKit JS and the signed-in user's private
iCloud database; the API never receives CloudKit user credentials or private
records.

Configure these Azure Functions application settings:

| Setting | Purpose |
| --- | --- |
| `BONDCASTS_CLOUDKIT_WEB_API_TOKEN` | Origin-restricted CloudKit web API token; its presence enables sign-in |
| `BONDCASTS_CLOUDKIT_ENVIRONMENT` | `development` or `production` (defaults to `production`) |
| `BONDCASTS_CLOUDKIT_WRITES_ENABLED` | Must be exactly `true` to expose follow/unfollow mutations |

Restrict the Production token to the exact production origin in CloudKit
Console. Keep writes disabled until Production create/import/delete validation
has passed. For local development, put settings in `api/local.settings.json`,
which is ignored by Git; never copy the spike's `config.*.local.json` files into
this repository.

CloudKit web authentication currently fails in Safari on macOS when Safari's
default cross-site tracking protection is enabled. The companion avoids opening
an authentication flow that cannot complete there and keeps public search
available; iPhone, iPad, and other supported desktop browsers can still connect
to iCloud.

Run the native-module contract tests with `npm run test:web`.

## Universal Links

`.well-known/apple-app-site-association` is set up for **`bondcasts.com`**
(App ID `YLT8FWVXBN.com.bondcodes.PodcastApp`). GitHub Pages serves it over
HTTPS with the correct `application/json` content type and **no redirect**,
which is what Apple's CDN requires.

> Note: `bondcasts.app` currently 301-redirects to `.com` via GoDaddy
> forwarding. Apple does **not** follow redirects when fetching the AASA file,
> so Universal Links must use `bondcasts.com`, not `bondcasts.app`.

To finish enabling Universal Links in the app, add the associated-domains
entitlement:

```
applinks:bondcasts.com
```

## TODO

- [ ] Replace the App Store `href="#"` in `index.html` once the app is live.
- [ ] Add `assets/favicon.svg` and `assets/og-image.png`.
- [ ] Confirm the support/privacy email addresses resolve.

## Privacy: keep the site, label, and manifest in sync

The App Store nutrition label is **"Data Not Linked to You"** as of 2026-07-13
(re-audited for PodcastApp#135): the new-episode push service registers the
public feed URLs of notification-enabled shows in the public CloudKit database
under a random per-device ID, which the poller reads with server-to-server
keys (~30-day retention). Declared: Identifiers > Device ID and Usage Data >
Other Usage Data, purpose App Functionality, not linked to identity, no
tracking. Everything else is unchanged: no third-party analytics/ad/crash
SDKs, sync via the user's private CloudKit database (which the developer
can't read), feeds fetched directly from podcast hosts the user chose.
Apple's own platform metrics (App Store + opt-in App Analytics) are Apple's
collection, not ours.

If you ever add any of the following, you must revisit **all three** - the
nutrition label in App Store Connect, `NSPrivacyCollectedDataTypes` in
`PrivacyInfo.xcprivacy`, and `privacy.html` here:

- [ ] A crash/analytics/attribution SDK (Sentry, TelemetryDeck, Firebase, etc.)
- [ ] An account system (Sign in with Apple, email login) or your own server
      that stores user data
- [x] A **public** CloudKit database, or any data shared beyond the user's own
      private iCloud (2026-07-13, the #135 push registrations - label, manifest,
      and privacy.html all updated)
- [ ] Anything that phones home (remote config, feature flags, server logging)
