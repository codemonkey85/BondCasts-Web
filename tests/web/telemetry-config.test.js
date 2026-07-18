import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const hostConfigURL = new URL("../../api/host.json", import.meta.url);
const hostConfig = JSON.parse(await readFile(hostConfigURL, "utf8"));

test("Functions telemetry configuration excludes request URLs", () => {
  assert.equal(
    hostConfig.logging.applicationInsights.samplingSettings.excludedTypes,
    "Request"
  );
  assert.equal(hostConfig.logging.logLevel["Host.Results"], "None");
});
