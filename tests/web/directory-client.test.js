import assert from "node:assert/strict";
import test from "node:test";
import { resolvePodcast, searchPodcasts } from "../../scripts/companion/directory-client.js";

test("directory searches use a no-store POST body instead of a query string", async () => {
  const call = await captureFetch({ results: [] }, () => searchPodcasts("privacy test"));

  assert.equal(call.url, "https://bondcasts.example/api/podcasts/search");
  assert.equal(call.options.method, "POST");
  assert.equal(call.options.cache, "no-store");
  assert.deepEqual(JSON.parse(call.options.body), { term: "privacy test", limit: 20 });
});

test("feed resolution keeps tokenized addresses in a no-store POST body", async () => {
  const call = await captureFetch({}, () => resolvePodcast(
    "https://example.com/private.xml?token=secret",
    { itunesID: 1234 }
  ));

  assert.equal(call.url, "https://bondcasts.example/api/podcasts/resolve");
  assert.equal(call.options.method, "POST");
  assert.equal(call.options.cache, "no-store");
  assert.deepEqual(JSON.parse(call.options.body), {
    url: "https://example.com/private.xml?token=secret",
    itunesID: 1234
  });
});

async function captureFetch(payload, action) {
  const previousWindow = globalThis.window;
  const previousNavigator = globalThis.navigator;
  const previousFetch = globalThis.fetch;
  let call;

  globalThis.window = { location: { origin: "https://bondcasts.example" } };
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: { onLine: true }
  });
  globalThis.fetch = async (url, options) => {
    call = { url: String(url), options };
    return { ok: true, json: async () => payload };
  };

  try {
    await action();
    return call;
  } finally {
    globalThis.window = previousWindow;
    Object.defineProperty(globalThis, "navigator", {
      configurable: true,
      value: previousNavigator
    });
    globalThis.fetch = previousFetch;
  }
}
