import assert from "node:assert/strict";
import test from "node:test";
import { FollowedShowStoreV1 } from "../../scripts/companion/followed-show-store.js";

const zoneID = { zoneName: "com.apple.coredata.cloudkit.zone", ownerRecordName: "test-owner" };

test("initial read drains pages and applies normalized records", async () => {
  const database = new FakeDatabase([
    nativeRecord("one", "http://Example.com/feed/", "Example"),
    nativeRecord("two", "https://elsewhere.example/rss", "Elsewhere")
  ], 1);
  const store = new FollowedShowStoreV1(database, zoneID);

  const records = await store.readAll();
  assert.equal(records.length, 2);
  assert.equal(store.matching("https://example.com/feed").length, 1);
});

test("follow re-reads and does not create a logical duplicate", async () => {
  const database = new FakeDatabase([
    nativeRecord("one", "http://Example.com/feed/", "Example")
  ]);
  const store = new FollowedShowStoreV1(database, zoneID);

  const result = await store.follow({ feedURL: "https://example.com/feed", title: "Example" });
  assert.equal(result.status, "already-following");
  assert.equal(database.saveCount, 0);
});

test("unfollow removes every normalized duplicate", async () => {
  const database = new FakeDatabase([
    nativeRecord("one", "http://Example.com/feed/", "Example"),
    nativeRecord("two", "https://example.com/feed#fragment", "Example copy"),
    nativeRecord("three", "https://example.com/other", "Other")
  ]);
  const store = new FollowedShowStoreV1(database, zoneID);

  const result = await store.unfollow("https://example.com/feed");
  assert.equal(result.status, "unfollowed");
  assert.equal(result.deletedCount, 2);
  assert.deepEqual(database.deletedNames.sort(), ["one", "two"]);
  assert.equal(store.matching("https://example.com/feed").length, 0);
});

test("a successful create is confirmed from server state", async () => {
  const database = new FakeDatabase([]);
  const store = new FollowedShowStoreV1(database, zoneID);
  const result = await store.follow({ feedURL: "https://example.com/new", title: "New Show" });

  assert.equal(result.status, "followed");
  assert.equal(result.records.length, 1);
  assert.equal(database.saveCount, 1);
});

test("a lost save response is reconciled before offering a retry", async () => {
  const database = new FakeDatabase([]);
  database.throwAfterSave = true;
  const store = new FollowedShowStoreV1(database, zoneID);

  const result = await store.follow({ feedURL: "https://example.com/reconciled", title: "Reconciled" });
  assert.equal(result.status, "reconciled");
  assert.equal(store.matching("https://example.com/reconciled").length, 1);
});

class FakeDatabase {
  constructor(records, pageSize = 50) {
    this.records = new Map(records.map((record) => [record.recordName, record]));
    this.pageSize = pageSize;
    this.version = 1;
    this.saveCount = 0;
    this.deletedNames = [];
    this.changes = records.map((record) => ({ version: 1, record }));
  }

  async fetchRecordZoneChanges(options) {
    const token = Number(options.syncToken ?? 0);
    const offset = Number(options.__testOffset ?? 0);
    const candidates = this.changes.filter((change) => change.version > token);
    const page = candidates.slice(offset, offset + this.pageSize);
    const moreComing = offset + this.pageSize < candidates.length;
    const responseToken = moreComing ? token + 0.5 : this.version;

    if (moreComing) {
      // The production adapter passes only CloudKit tokens between pages. Model
      // that by consuming the page from the fake's visible change list.
      this.changes = [...page.map((entry) => ({ ...entry, version: token })), ...candidates.slice(this.pageSize)];
    }

    return {
      zones: [{
        zoneID,
        records: page.flatMap((entry) => entry.record ? [entry.record] : []),
        deletedRecords: page.flatMap((entry) => entry.deleted ? [{ recordName: entry.deleted }] : []),
        moreComing,
        syncToken: responseToken
      }]
    };
  }

  async saveRecords(record) {
    this.saveCount += 1;
    this.version += 1;
    this.records.set(record.recordName, record);
    this.changes.push({ version: this.version, record });
    if (this.throwAfterSave) {
      const error = new Error("Connection closed after request upload");
      error.code = "NETWORK_FAILURE";
      throw error;
    }
    return { records: [record] };
  }

  async deleteRecords(recordName) {
    this.deletedNames.push(recordName);
    this.records.delete(recordName);
    this.version += 1;
    this.changes.push({ version: this.version, deleted: recordName });
    return { deletedRecordNames: [recordName] };
  }
}

function nativeRecord(recordName, feedURL, title) {
  return {
    recordName,
    recordType: "CD_FollowedShow",
    fields: {
      CD_feedURL: { value: feedURL, type: "STRING" },
      CD_title: { value: title, type: "STRING" },
      CD_followedAt: { value: 1, type: "TIMESTAMP" }
    }
  };
}
