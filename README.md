# bondcasts-web

Marketing, support, and privacy site for **BondCasts**, served at
[bondcasts.com](https://bondcasts.com) via GitHub Pages. Plain static HTML/CSS —
no build step.

## Contents

| File | Purpose |
| --- | --- |
| `index.html` | Landing page |
| `privacy.html` | Privacy policy (required for App Store Connect) |
| `support.html` | Support page (required for App Store Connect) |
| `styles.css` | Shared styles (dark/light aware) |
| `assets/logo.svg` | Brand mark (chain-link "B" + equalizer; navy→cyan) |
| `assets/favicon.svg` | Favicon (same mark) |
| `assets/apple-touch-icon.png` | 180×180 touch icon |
| `assets/og-image.png` | 1200×630 social share image |
| `.well-known/apple-app-site-association` | Universal Links association file |
| `CNAME` | Custom domain for GitHub Pages (`bondcasts.com`) |

The brand mark is a **placeholder direction**, not the final app icon — a clean
play + broadcast-waves glyph on the app's navy→cyan gradient. Regenerate the PNGs
from the SVG with the snippet in git history if you tweak `logo.svg`.

## Deploying on GitHub Pages

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

The App Store nutrition label is currently **"Data Not Collected"**, and that's
accurate: the app has no third-party analytics/ad/crash SDKs, syncs only via the
user's private CloudKit database (which the developer can't read), and fetches
feeds directly from podcast hosts the user chose. Apple's own platform metrics
(App Store + opt-in App Analytics) are Apple's collection, not ours.

If you ever add any of the following, you must revisit **all three** — the
nutrition label in App Store Connect, `NSPrivacyCollectedDataTypes` in
`PrivacyInfo.xcprivacy`, and `privacy.html` here:

- [ ] A crash/analytics/attribution SDK (Sentry, TelemetryDeck, Firebase, etc.)
- [ ] An account system (Sign in with Apple, email login) or your own server
      that stores user data
- [ ] A **public** CloudKit database, or any data shared beyond the user's own
      private iCloud
- [ ] Anything that phones home (remote config, feature flags, server logging)
