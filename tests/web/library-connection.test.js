import assert from "node:assert/strict";
import test from "node:test";
import { CompanionError } from "../../scripts/companion/errors.js";
import {
  discoverFollowedShows,
  isLibrarySetupRequired
} from "../../scripts/companion/library-connection.js";

test("a missing private zone is recognized as first-run setup", () => {
  const error = new CompanionError("zone-unavailable", "Library does not exist yet.");
  assert.equal(isLibrarySetupRequired(error), true);
  assert.equal(isLibrarySetupRequired(new CompanionError("cloudkit", "Unavailable")), false);
});

test("retry connects after the native app creates the private zone", async () => {
  let attempts = 0;
  const followedShows = [{ title: "Example" }];
  const store = { async readAll() { return followedShows; } };
  const client = {
    async openFollowedShowStore() {
      attempts += 1;
      if (attempts === 1) throw new CompanionError("zone-unavailable", "Not created yet.");
      return store;
    }
  };

  await assert.rejects(() => discoverFollowedShows(client), { kind: "zone-unavailable" });
  const result = await discoverFollowedShows(client);

  assert.equal(attempts, 2);
  assert.equal(result.store, store);
  assert.deepEqual(result.followedShows, followedShows);
});

test("repeat retry preserves the missing-zone failure until setup is complete", async () => {
  let attempts = 0;
  const client = {
    async openFollowedShowStore() {
      attempts += 1;
      throw new CompanionError("zone-unavailable", "Not created yet.");
    }
  };

  await assert.rejects(() => discoverFollowedShows(client), { kind: "zone-unavailable" });
  await assert.rejects(() => discoverFollowedShows(client), { kind: "zone-unavailable" });
  assert.equal(attempts, 2);
});
