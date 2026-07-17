import { CompanionError, classifyCloudKitError, isExpiredChangeToken } from "./errors.js";
import { normalizeFeedURL } from "./feed-url.js";
import {
  decodeFollowedShowRecord,
  encodeFollowedShowRecord,
  FOLLOWED_SHOW_RECORD_TYPE
} from "./record-contract.js";

/**
 * A versioned adapter around the Core Data-generated CD_FollowedShow records.
 * The rest of the web companion never reads or writes CD_* fields directly.
 */
export class FollowedShowStoreV1 {
  constructor(database, zoneID) {
    this.database = database;
    this.zoneID = Object.freeze({ ...zoneID });
    this.records = new Map();
    this.syncToken = null;
    this.initialized = false;
  }

  async readAll() {
    if (!this.initialized) await this.reloadAll();
    else await this.refresh();
    return this.snapshot();
  }

  async reloadAll() {
    const result = await this.#drainChanges(new Map(), null);
    this.records = result.records;
    this.syncToken = result.syncToken;
    this.initialized = true;
    return this.snapshot();
  }

  async refresh() {
    if (!this.initialized) return this.reloadAll();
    try {
      const result = await this.#drainChanges(new Map(this.records), this.syncToken);
      this.records = result.records;
      this.syncToken = result.syncToken;
      return this.snapshot();
    } catch (error) {
      if (isExpiredChangeToken(error)) return this.reloadAll();
      throw classifyCloudKitError(error);
    }
  }

  matching(feedURL) {
    const identity = normalizeFeedURL(feedURL);
    return [...this.records.values()].filter((record) => record.normalizedFeedURL === identity);
  }

  async follow(resolvedShow) {
    const identity = normalizeFeedURL(resolvedShow.feedURL);
    await this.refresh();
    const existing = this.matching(identity);
    if (existing.length) return { status: "already-following", records: existing };

    const record = encodeFollowedShowRecord(resolvedShow);
    let mutationError = null;
    try {
      await this.database.saveRecords(record, { zoneID: this.zoneID });
    } catch (error) {
      mutationError = error;
    }

    const confirmed = await this.#confirmIdentity(identity, mutationError);
    if (confirmed.length) {
      return {
        status: mutationError ? "reconciled" : "followed",
        records: confirmed
      };
    }

    if (mutationError) throw classifyCloudKitError(mutationError, { mutation: true });
    throw new CompanionError(
      "unknown-outcome",
      "iCloud accepted the request but the followed show is not visible yet. Check again before retrying.",
      { outcomeUnknown: true }
    );
  }

  async unfollow(feedURL) {
    const identity = normalizeFeedURL(feedURL);
    await this.refresh();
    const matches = this.matching(identity);
    if (!matches.length) return { status: "already-unfollowed", deletedCount: 0 };

    const errors = [];
    for (const record of matches) {
      try {
        await this.database.deleteRecords(record.recordName, { zoneID: this.zoneID });
      } catch (error) {
        errors.push(error);
      }
    }

    const remaining = await this.#confirmIdentity(identity, errors[0]);
    if (!remaining.length) {
      return { status: errors.length ? "reconciled" : "unfollowed", deletedCount: matches.length };
    }

    if (errors.length) throw classifyCloudKitError(errors[0], { mutation: true });
    throw new CompanionError(
      "unknown-outcome",
      "iCloud has not confirmed every removal yet. Check again before retrying.",
      { outcomeUnknown: true }
    );
  }

  snapshot() {
    return [...this.records.values()].sort((a, b) => {
      const dateDifference = (b.followedAt ?? 0) - (a.followedAt ?? 0);
      return dateDifference || a.title.localeCompare(b.title);
    });
  }

  async #confirmIdentity(identity, originalError) {
    try {
      await this.refresh();
      let matches = this.matching(identity);
      if (matches.length || !originalError) return matches;
      await this.reloadAll();
      matches = this.matching(identity);
      return matches;
    } catch (confirmationError) {
      throw classifyCloudKitError(originalError ?? confirmationError, { mutation: true });
    }
  }

  async #drainChanges(baseRecords, startingToken) {
    const workingRecords = new Map(baseRecords);
    let syncToken = startingToken;

    while (true) {
      const options = {
        zoneID: this.zoneID,
        resultsLimit: 200,
        desiredRecordTypes: [FOLLOWED_SHOW_RECORD_TYPE]
      };
      if (syncToken) options.syncToken = syncToken;

      const response = await this.database.fetchRecordZoneChanges(options);
      const zone = (response.zones ?? []).find((candidate) =>
        candidate.zoneID?.zoneName === this.zoneID.zoneName) ?? response.zones?.[0];

      if (!zone) return { records: workingRecords, syncToken };

      for (const record of zone.records ?? []) {
        const decoded = decodeFollowedShowRecord(record);
        if (decoded) workingRecords.set(decoded.recordName, decoded);
      }
      for (const deleted of zone.deletedRecords ?? []) {
        const recordName = typeof deleted === "string" ? deleted : deleted.recordName;
        if (recordName) workingRecords.delete(recordName);
      }

      syncToken = zone.syncToken ?? syncToken;
      if (!zone.moreComing) return { records: workingRecords, syncToken };
      if (!syncToken) {
        throw new CompanionError("cloudkit", "iCloud returned an incomplete library page without a continuation token.");
      }
    }
  }
}
