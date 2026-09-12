import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const privacyPolicyURL = new URL("../../privacy.html", import.meta.url);
const privacyPolicy = await readFile(privacyPolicyURL, "utf8");

test("personalized suggestions disclosure matches the native privacy boundary", () => {
  assert.match(privacyPolicy, /Personalized show suggestions are off by default/i);
  assert.match(privacyPolicy, /on-device Foundation Models framework/i);
  assert.match(privacyPolicy, /fixed list of broad podcast\s+topics/i);
  assert.match(privacyPolicy, /directly to Apple's public podcast directory/i);
  assert.match(privacyPolicy, /do\s+not\s+receive, log, or store the topics or results/i);
  assert.match(privacyPolicy, /Settings &gt;\s*Discovery/i);
});
