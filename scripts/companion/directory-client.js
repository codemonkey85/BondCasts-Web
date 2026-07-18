export async function searchPodcasts(term, options = {}) {
  const url = new URL("/api/podcasts/search", window.location.origin);
  const payload = await postJSON(url, { term, limit: 20 }, options.signal);
  return Array.isArray(payload.results) ? payload.results : [];
}

export async function resolvePodcast(feedURL, options = {}) {
  const url = new URL("/api/podcasts/resolve", window.location.origin);
  const body = { url: feedURL };
  if (Number.isSafeInteger(options.itunesID)) {
    body.itunesID = options.itunesID;
  }
  return postJSON(url, body, options.signal);
}

async function postJSON(url, body, signal) {
  let response;
  try {
    response = await fetch(url, {
      method: "POST",
      signal,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json"
      },
      cache: "no-store",
      body: JSON.stringify(body)
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
