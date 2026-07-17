export async function searchPodcasts(term, options = {}) {
  const url = new URL("/api/podcasts/search", window.location.origin);
  url.searchParams.set("term", term);
  url.searchParams.set("limit", "20");
  const payload = await fetchJSON(url, options.signal);
  return Array.isArray(payload.results) ? payload.results : [];
}

export async function resolvePodcast(feedURL, options = {}) {
  const url = new URL("/api/podcasts/resolve", window.location.origin);
  url.searchParams.set("url", feedURL);
  if (Number.isSafeInteger(options.itunesID)) {
    url.searchParams.set("itunesID", String(options.itunesID));
  }
  return fetchJSON(url, options.signal);
}

async function fetchJSON(url, signal) {
  let response;
  try {
    response = await fetch(url, {
      signal,
      headers: { Accept: "application/json" }
    });
  } catch (error) {
    if (error?.name === "AbortError") throw error;
    throw new Error(navigator.onLine === false
      ? "You’re offline. Reconnect, then try again."
      : "BondCasts could not reach the podcast service.");
  }

  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error || `The podcast service returned HTTP ${response.status}.`);
  return payload;
}
