import assert from "node:assert/strict";
import test from "node:test";
import { createCloudKitClient } from "../../scripts/companion/cloudkit-client.js";

test("CloudKit client seeds and persists the redirect callback session", async () => {
  let configured = null;
  let signOutCount = 0;
  const identity = { userRecordName: "test-user" };
  const container = {
    privateCloudDatabase: {},
    whenUserSignsIn() { return new Promise(() => {}); },
    whenUserSignsOut() { return new Promise(() => {}); },
    async setUpAuth() { return identity; },
    signOut() { signOutCount += 1; }
  };
  globalThis.window = {
    CloudKit: {
      configure(value) { configured = value; },
      getDefaultContainer() { return container; }
    }
  };

  try {
    const client = await createCloudKitClient({
      enabled: true,
      containerIdentifier: "iCloud.com.bondcodes.PodcastApp",
      environment: "production",
      apiToken: "browser-token"
    }, { webAuthToken: "callback-session" });

    assert.deepEqual(configured.containers[0].apiTokenAuth, {
      apiToken: "browser-token",
      persist: true,
      ckWebAuthToken: "callback-session"
    });
    assert.equal("signInButton" in configured.containers[0].apiTokenAuth, false);

    const identities = [];
    client.subscribe((value) => identities.push(value));
    assert.equal(await client.setUpAuth(), identity);
    client.signOut();

    assert.deepEqual(identities, [identity, null]);
    assert.equal(signOutCount, 1);
  } finally {
    delete globalThis.window;
  }
});
