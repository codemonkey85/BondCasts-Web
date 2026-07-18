import assert from "node:assert/strict";
import test from "node:test";
import { isMacSafari } from "../../scripts/companion/browser-support.js";

const macSafariUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
  + "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";

test("identifies Safari on Mac", () => {
  assert.equal(isMacSafari({
    userAgent: macSafariUserAgent,
    platform: "MacIntel",
    maxTouchPoints: 0
  }), true);
});

test("does not identify an iPad using a desktop user agent as a Mac", () => {
  assert.equal(isMacSafari({
    userAgent: macSafariUserAgent,
    platform: "MacIntel",
    maxTouchPoints: 5
  }), false);
});

test("does not identify Chrome on Mac as Safari", () => {
  assert.equal(isMacSafari({
    userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
      + "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
    platform: "MacIntel",
    maxTouchPoints: 0
  }), false);
});

test("does not identify Safari on iPhone as Mac Safari", () => {
  assert.equal(isMacSafari({
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) "
      + "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1",
    platform: "iPhone",
    maxTouchPoints: 5
  }), false);
});
