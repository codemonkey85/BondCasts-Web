import { CompanionError, classifyCloudKitError } from "./errors.js";
import { FollowedShowStoreV1 } from "./followed-show-store.js";
import { FOLLOWED_SHOW_ZONE_NAME } from "./record-contract.js";

const cloudKitScriptURL = "https://cdn.apple-cloudkit.com/ck/2/cloudkit.js";
let cloudKitLoadPromise;

export async function loadCompanionConfiguration() {
  const response = await fetch("/api/companion/config", {
    headers: { Accept: "application/json" },
    cache: "no-store"
  });
  if (!response.ok) throw new Error(`Configuration request failed with HTTP ${response.status}.`);
  return response.json();
}

export async function createCloudKitClient(configuration, options = {}) {
  if (!configuration?.enabled) {
    throw new CompanionError("unavailable", "iCloud follow controls are not enabled on this site yet.");
  }
  if (!configuration.containerIdentifier || !configuration.apiToken) {
    throw new CompanionError("unavailable", "The iCloud web configuration is incomplete.");
  }

  const CloudKit = await loadCloudKitJS();
  const apiTokenAuth = {
    apiToken: configuration.apiToken,
    persist: true
  };
  if (options.webAuthToken) apiTokenAuth.ckWebAuthToken = options.webAuthToken;

  CloudKit.configure({
    containers: [{
      containerIdentifier: configuration.containerIdentifier,
      environment: configuration.environment,
      apiTokenAuth
    }]
  });

  const container = CloudKit.getDefaultContainer();
  const database = container.privateCloudDatabase;
  const listeners = new Set();
  const emit = (identity) => listeners.forEach((listener) => listener(identity));

  if (typeof container.whenUserSignsIn === "function") {
    void container.whenUserSignsIn().then(emit, () => {});
  }
  if (typeof container.whenUserSignsOut === "function") {
    void container.whenUserSignsOut().then(() => emit(null), () => {});
  }

  return {
    configuration,
    container,
    database,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    async setUpAuth() {
      try {
        const identity = await container.setUpAuth();
        emit(identity ?? null);
        return identity ?? null;
      } catch (error) {
        throw classifyCloudKitError(error);
      }
    },
    signOut() {
      container.signOut();
      emit(null);
    },
    async openFollowedShowStore() {
      let response;
      try {
        response = await database.fetchAllRecordZones();
      } catch (error) {
        throw classifyCloudKitError(error);
      }
      const zone = (response.zones ?? []).find((candidate) =>
        candidate.zoneID?.zoneName === FOLLOWED_SHOW_ZONE_NAME);
      if (!zone?.zoneID) {
        throw new CompanionError(
          "zone-unavailable",
          "Open BondCasts on one of your devices once, then retry. Your iCloud library has not been created yet."
        );
      }
      return new FollowedShowStoreV1(database, zone.zoneID);
    }
  };
}

function loadCloudKitJS() {
  if (window.CloudKit) return Promise.resolve(window.CloudKit);
  if (cloudKitLoadPromise) return cloudKitLoadPromise;

  cloudKitLoadPromise = new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = cloudKitScriptURL;
    script.async = true;
    script.addEventListener("load", () => resolve(window.CloudKit), { once: true });
    script.addEventListener("error", () => reject(new CompanionError(
      "unavailable",
      "The iCloud sign-in service could not be loaded. Public podcast search is still available."
    )), { once: true });
    document.head.append(script);
  });
  return cloudKitLoadPromise;
}
