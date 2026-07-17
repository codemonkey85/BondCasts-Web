import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { isHTTPFeedURL, isSameFeed, normalizeFeedURL } from "../../scripts/companion/feed-url.js";

const fixtureURL = new URL("./fixtures/feed-url-contract.json", import.meta.url);
const fixtures = JSON.parse(await readFile(fixtureURL, "utf8"));

for (const fixture of fixtures) {
  test(`normalization contract: ${fixture.name}`, () => {
    assert.equal(normalizeFeedURL(fixture.input), fixture.normalized);
  });
}

test("logical comparison uses normalized identity", () => {
  assert.equal(isSameFeed("http://Example.com/feed/", "https://example.com/feed#section"), true);
  assert.equal(isSameFeed("https://example.com/a", "https://example.com/b"), false);
});

test("fetchable URLs reject credentials and non-http schemes", () => {
  assert.equal(isHTTPFeedURL("https://example.com/feed"), true);
  assert.equal(isHTTPFeedURL("https://user:pass@example.com/feed"), false);
  assert.equal(isHTTPFeedURL("file:///tmp/feed.xml"), false);
});
