import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const appStoreURL = "https://apps.apple.com/us/app/bondcasts/id6787571328";
const indexHTML = await readFile(new URL("../../index.html", import.meta.url), "utf8");

test("launch page links visitors to the App Store", () => {
  assert.equal(indexHTML.split(appStoreURL).length - 1, 3);
  assert.match(indexHTML, /<meta name="apple-itunes-app" content="app-id=6787571328">/);
});

test("launch page no longer advertises the beta or an upcoming launch", () => {
  assert.doesNotMatch(indexHTML, /testflight\.apple\.com/i);
  assert.doesNotMatch(indexHTML, /coming (?:fall|soon)|before launch|ahead of .* launch/i);
});
