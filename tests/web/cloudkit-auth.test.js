import assert from "node:assert/strict";
import test from "node:test";
import {
  consumeCloudKitWebAuthToken,
  isTrustedAppleSignInURL,
  requestCloudKitSignInURL
} from "../../scripts/companion/cloudkit-auth.js";

test("redirect callback consumes the web token and removes it from browser history", () => {
  let replacement = null;
  const browserWindow = {
    location: {
      href: "https://bondcasts.com/discover/?q=example&ckWebAuthToken=session%2Btoken%3D#results"
    },
    history: {
      state: { source: "test" },
      replaceState(state, title, url) {
        replacement = { state, title, url };
      }
    }
  };

  assert.equal(consumeCloudKitWebAuthToken(browserWindow), "session+token=");
  assert.deepEqual(replacement, {
    state: { source: "test" },
    title: "",
    url: "/discover/?q=example#results"
  });
});

test("ordinary Discover URLs do not modify browser history", () => {
  let replaced = false;
  const browserWindow = {
    location: { href: "https://bondcasts.com/discover/?q=example" },
    history: { replaceState() { replaced = true; } }
  };

  assert.equal(consumeCloudKitWebAuthToken(browserWindow), null);
  assert.equal(replaced, false);
});

test("sign-in request returns Apple's trusted redirect destination", async () => {
  let requestedURL = null;
  const redirectURL = "https://idmsa.apple.com/appleauth/auth/signin?widgetKey=example";
  const result = await requestCloudKitSignInURL({
    containerIdentifier: "iCloud.com.bondcodes.PodcastApp",
    environment: "production",
    apiToken: "browser-token"
  }, {
    async fetch(url, options) {
      requestedURL = url;
      assert.equal(options.cache, "no-store");
      return {
        async json() {
          return {
            serverErrorCode: "AUTHENTICATION_REQUIRED",
            redirectURL
          };
        }
      };
    }
  });

  assert.equal(result, redirectURL);
  assert.equal(requestedURL.pathname, "/database/1/iCloud.com.bondcodes.PodcastApp/production/public/users/current");
  assert.equal(requestedURL.searchParams.get("ckAPIToken"), "browser-token");
});

test("sign-in request rejects invalid-token and untrusted redirect responses", async () => {
  const configuration = {
    containerIdentifier: "iCloud.com.bondcodes.PodcastApp",
    environment: "production",
    apiToken: "browser-token"
  };

  await assert.rejects(() => requestCloudKitSignInURL(configuration, {
    async fetch() {
      return { async json() { return { serverErrorCode: "AUTHENTICATION_FAILED", reason: "Wrong token" }; } };
    }
  }), { kind: "unavailable", message: "Wrong token" });

  await assert.rejects(() => requestCloudKitSignInURL(configuration, {
    async fetch() {
      return {
        async json() {
          return { serverErrorCode: "AUTHENTICATION_REQUIRED", redirectURL: "https://evil.example/sign-in" };
        }
      };
    }
  }), { kind: "unavailable" });
});

test("trusted sign-in destinations are HTTPS Apple authentication hosts", () => {
  assert.equal(isTrustedAppleSignInURL("https://idmsa.apple.com/signin"), true);
  assert.equal(isTrustedAppleSignInURL("https://idmsa.apple.com.cn/signin"), true);
  assert.equal(isTrustedAppleSignInURL("http://idmsa.apple.com/signin"), false);
  assert.equal(isTrustedAppleSignInURL("https://apple.com.evil.example/signin"), false);
});
