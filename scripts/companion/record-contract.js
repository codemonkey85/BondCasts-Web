import { isHTTPFeedURL, normalizeFeedURL } from "./feed-url.js";

export const FOLLOWED_SHOW_CONTRACT_VERSION = 1;
export const FOLLOWED_SHOW_ZONE_NAME = "com.apple.coredata.cloudkit.zone";
export const FOLLOWED_SHOW_RECORD_TYPE = "CD_FollowedShow";

const fields = Object.freeze({
  feedURL: "CD_feedURL",
  title: "CD_title",
  author: "CD_author",
  artworkURLString: "CD_artworkURLString",
  itunesID: "CD_itunesID",
  followedAt: "CD_followedAt",
  notifyNewEpisodes: "CD_notifyNewEpisodes",
  autoDownloadRaw: "CD_autoDownloadRaw",
  autoQueueRaw: "CD_autoQueueRaw",
  isPinned: "CD_isPinned",
  pinnedSortOrder: "CD_pinnedSortOrder"
});

export function decodeFollowedShowRecord(record) {
  if (!record || record.recordType !== FOLLOWED_SHOW_RECORD_TYPE || !record.recordName) return null;

  const feedURL = fieldValue(record, fields.feedURL);
  if (typeof feedURL !== "string" || !isHTTPFeedURL(feedURL)) return null;

  return Object.freeze({
    recordName: record.recordName,
    changeTag: record.recordChangeTag ?? null,
    feedURL,
    normalizedFeedURL: normalizeFeedURL(feedURL),
    title: stringValue(fieldValue(record, fields.title)) || "Untitled podcast",
    author: nullableString(fieldValue(record, fields.author)),
    artworkURLString: nullableString(fieldValue(record, fields.artworkURLString)),
    itunesID: safeIntegerOrNull(fieldValue(record, fields.itunesID)),
    followedAt: numberOrNull(fieldValue(record, fields.followedAt)),
    notifyNewEpisodes: booleanFromInt(fieldValue(record, fields.notifyNewEpisodes), true),
    autoDownloadRaw: numberOrDefault(fieldValue(record, fields.autoDownloadRaw), 0),
    autoQueueRaw: numberOrDefault(fieldValue(record, fields.autoQueueRaw), 0),
    isPinned: booleanFromInt(fieldValue(record, fields.isPinned), false),
    pinnedSortOrder: numberOrDefault(fieldValue(record, fields.pinnedSortOrder), 0)
  });
}

export function encodeFollowedShowRecord(show, options = {}) {
  if (!isHTTPFeedURL(show?.feedURL)) throw new TypeError("Feed URL must be an absolute public http(s) URL.");
  const title = String(show?.title ?? "").trim();
  if (!title) throw new TypeError("A resolved feed title is required.");

  const recordName = options.recordName ?? crypto.randomUUID();
  const followedAt = options.followedAt ?? Date.now();
  const encodedFields = {
    CD_entityName: ckField("FollowedShow", "STRING"),
    [fields.feedURL]: ckField(show.feedURL, "STRING"),
    [fields.title]: ckField(title, "STRING"),
    [fields.followedAt]: ckField(followedAt, "TIMESTAMP"),
    [fields.notifyNewEpisodes]: ckField(1, "INT64"),
    [fields.autoDownloadRaw]: ckField(0, "INT64"),
    [fields.autoQueueRaw]: ckField(0, "INT64"),
    [fields.isPinned]: ckField(0, "INT64"),
    [fields.pinnedSortOrder]: ckField(0, "DOUBLE")
  };

  addOptionalString(encodedFields, fields.author, show.author);
  addOptionalString(encodedFields, fields.artworkURLString, show.artworkURLString);
  if (Number.isSafeInteger(show.itunesID)) {
    encodedFields[fields.itunesID] = ckField(show.itunesID, "INT64");
  }

  return {
    recordName,
    recordType: FOLLOWED_SHOW_RECORD_TYPE,
    fields: encodedFields
  };
}

function fieldValue(record, name) {
  const field = record.fields?.[name];
  return field && Object.prototype.hasOwnProperty.call(field, "value") ? field.value : field;
}

function ckField(value, type) {
  return { value, type };
}

function addOptionalString(target, name, value) {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (normalized) target[name] = ckField(normalized, "STRING");
}

function stringValue(value) {
  return typeof value === "string" ? value.trim() : "";
}

function nullableString(value) {
  return stringValue(value) || null;
}

function numberOrNull(value) {
  return Number.isFinite(Number(value)) ? Number(value) : null;
}

function numberOrDefault(value, fallback) {
  return numberOrNull(value) ?? fallback;
}

function safeIntegerOrNull(value) {
  const number = Number(value);
  return Number.isSafeInteger(number) ? number : null;
}

function booleanFromInt(value, fallback) {
  if (value === true || value === 1) return true;
  if (value === false || value === 0) return false;
  return fallback;
}
