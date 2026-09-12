import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";

const appStoreURL = "https://apps.apple.com/us/app/bondcasts/id6787571328";
const repositoryRoot = new URL("../../", import.meta.url);
const indexHTML = await readFile(new URL("index.html", repositoryRoot), "utf8");

test("launch page links visitors to the App Store", () => {
  assert.equal(indexHTML.split(appStoreURL).length - 1, 3);
  assert.match(indexHTML, /<meta name="apple-itunes-app" content="app-id=6787571328">/);
});

test("launch page no longer advertises the beta or an upcoming launch", () => {
  assert.doesNotMatch(indexHTML, /coming (?:fall|soon)|before launch|ahead of .* launch/i);
});

test("published website has no remaining TestFlight path", async () => {
  const publishedSources = [
    "index.html",
    "privacy.html",
    "support.html",
    "episode.html",
    "show.html",
    ...await sourceFiles("discover"),
    ...await sourceFiles("scripts"),
    ...await sourceFiles("api/Rendering")
  ];

  for (const relativePath of publishedSources) {
    const source = await readFile(new URL(relativePath, repositoryRoot), "utf8");
    assert.doesNotMatch(
      source,
      /testflight|fytFVhx2/i,
      `TestFlight reference remains in ${relativePath}`
    );
  }
});

async function sourceFiles(relativeDirectory) {
  const directoryURL = new URL(`${relativeDirectory}/`, repositoryRoot);
  const entries = await readdir(directoryURL, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const relativePath = `${relativeDirectory}/${entry.name}`;
    if (entry.isDirectory()) {
      files.push(...await sourceFiles(relativePath));
    } else if (/\.(?:cs|css|html|js)$/i.test(entry.name)) {
      files.push(relativePath);
    }
  }

  return files;
}
