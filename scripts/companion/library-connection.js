export async function discoverFollowedShows(cloudKitClient) {
  const store = await cloudKitClient.openFollowedShowStore();
  const followedShows = await store.readAll();
  return { store, followedShows };
}

export function isLibrarySetupRequired(error) {
  return error?.kind === "zone-unavailable";
}
