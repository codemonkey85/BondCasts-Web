import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const hostConfigURL = new URL("../../api/host.json", import.meta.url);
const hostConfig = JSON.parse(await readFile(hostConfigURL, "utf8"));
const pollerHostConfigURL = new URL("../../poller/host.json", import.meta.url);
const pollerHostConfig = JSON.parse(await readFile(pollerHostConfigURL, "utf8"));

test("managed Functions suppress request telemetry at the source", () => {
  assert.equal(hostConfig.logging.logLevel["Host.Results"], "None");
  assert.equal(
    hostConfig.logging.applicationInsights.samplingSettings.excludedTypes,
    undefined,
    "sampling exclusions do not disable collection"
  );
});

test("standalone feed service omits request and dependency telemetry", () => {
  assert.equal(pollerHostConfig.logging.logLevel["Host.Results"], "None");
  assert.equal(
    pollerHostConfig.logging.applicationInsights.enableDependencyTracking,
    false
  );
  assert.equal(
    pollerHostConfig.logging.applicationInsights.samplingSettings.excludedTypes,
    undefined,
    "sampling exclusions do not disable collection"
  );
});
