import assert from "node:assert/strict";
import test from "node:test";
import { classifyCloudKitError, isExpiredChangeToken } from "../../scripts/companion/errors.js";

test("classifies expired authentication", () => {
  const error = classifyCloudKitError({ ckErrorCode: "AUTHENTICATION_REQUIRED" });
  assert.equal(error.kind, "auth-expired");
  assert.equal(error.outcomeUnknown, false);
});

test("classifies throttling and retains retry guidance", () => {
  const error = classifyCloudKitError({ statusCode: 429, retryAfter: 12 }, { mutation: true });
  assert.equal(error.kind, "throttled");
  assert.equal(error.retryAfterSeconds, 12);
  assert.equal(error.outcomeUnknown, true);
});

test("classifies server-record conflicts", () => {
  const error = classifyCloudKitError({ ckErrorCode: "SERVER_RECORD_CHANGED" }, { mutation: true });
  assert.equal(error.kind, "conflict");
  assert.equal(error.outcomeUnknown, true);
});

test("unknown mutation errors never imply failure or invite a blind create", () => {
  const error = classifyCloudKitError({ code: "NETWORK_FAILURE" }, { mutation: true });
  assert.equal(error.kind, "unknown-outcome");
  assert.equal(error.outcomeUnknown, true);
});

test("recognizes expired record-zone change tokens", () => {
  assert.equal(isExpiredChangeToken({ ckErrorCode: "CHANGE_TOKEN_EXPIRED" }), true);
  assert.equal(isExpiredChangeToken({ ckErrorCode: "NETWORK_FAILURE" }), false);
});
