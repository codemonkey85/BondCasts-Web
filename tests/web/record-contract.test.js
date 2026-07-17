import assert from "node:assert/strict";
import test from "node:test";
import {
  decodeFollowedShowRecord,
  encodeFollowedShowRecord,
  FOLLOWED_SHOW_RECORD_TYPE
} from "../../scripts/companion/record-contract.js";

test("encodes the Core Data FollowedShow contract and safe defaults", () => {
  const record = encodeFollowedShowRecord({
    feedURL: "https://example.com/feed.xml",
    title: "Example Show",
    author: "Example Network",
    artworkURLString: null,
    itunesID: 1234
  }, { recordName: "3C7EFD53-ED28-442F-B188-C62CE65B9982", followedAt: 1_721_234_567_000 });

  assert.equal(record.recordType, FOLLOWED_SHOW_RECORD_TYPE);
  assert.equal(record.fields.CD_entityName.value, "FollowedShow");
  assert.deepEqual(record.fields.CD_notifyNewEpisodes, { value: 1, type: "INT64" });
  assert.deepEqual(record.fields.CD_autoDownloadRaw, { value: 0, type: "INT64" });
  assert.deepEqual(record.fields.CD_autoQueueRaw, { value: 0, type: "INT64" });
  assert.deepEqual(record.fields.CD_isPinned, { value: 0, type: "INT64" });
  assert.deepEqual(record.fields.CD_pinnedSortOrder, { value: 0, type: "DOUBLE" });
  assert.equal("CD_artworkURLString" in record.fields, false);
  assert.equal("CD_feedURL_ckAsset" in record.fields, false);
});

test("decodes native records and ignores additive fields", () => {
  const decoded = decodeFollowedShowRecord({
    recordName: "native-id",
    recordType: FOLLOWED_SHOW_RECORD_TYPE,
    recordChangeTag: "tag",
    fields: {
      CD_feedURL: { value: "HTTP://Example.com/feed/", type: "STRING" },
      CD_title: { value: "Example", type: "STRING" },
      CD_notifyNewEpisodes: { value: 0, type: "INT64" },
      CD_futureField: { value: "ignored", type: "STRING" }
    }
  });

  assert.equal(decoded.normalizedFeedURL, "https://example.com/feed");
  assert.equal(decoded.notifyNewEpisodes, false);
});

test("malformed or unsafe records fail closed", () => {
  assert.equal(decodeFollowedShowRecord({ recordName: "x", recordType: FOLLOWED_SHOW_RECORD_TYPE, fields: {} }), null);
  assert.equal(decodeFollowedShowRecord({
    recordName: "x",
    recordType: FOLLOWED_SHOW_RECORD_TYPE,
    fields: { CD_feedURL: { value: "file:///private/feed.xml" } }
  }), null);
});
