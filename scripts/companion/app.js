import { createCloudKitClient, loadCompanionConfiguration } from "./cloudkit-client.js";
import { createLibrarySetupPanel, createShowWebsiteLink } from "./companion-ui.js";
import { resolvePodcast, searchPodcasts } from "./directory-client.js";
import { CompanionError } from "./errors.js";
import { isHTTPFeedURL, normalizeFeedURL } from "./feed-url.js";
import { discoverFollowedShows, isLibrarySetupRequired } from "./library-connection.js";

const elements = {
  form: document.getElementById("podcast-search"),
  query: document.getElementById("podcast-query"),
  searchStatus: document.getElementById("search-status"),
  resultsHeading: document.getElementById("results-heading"),
  resultCount: document.getElementById("result-count"),
  emptyResult: document.getElementById("empty-result"),
  results: document.getElementById("podcast-results"),
  inspector: document.getElementById("show-inspector"),
  iCloudCard: document.getElementById("icloud-card"),
  iCloudStatus: document.getElementById("icloud-status"),
  iCloudOnboarding: document.getElementById("icloud-onboarding"),
  connectionDot: document.getElementById("connection-dot"),
  refreshLibrary: document.getElementById("refresh-library")
};

const state = {
  results: [],
  selectedDirectoryShow: null,
  resolvedShow: null,
  searchController: null,
  cloudKitClient: null,
  cloudKitConfiguration: null,
  store: null,
  followedShows: [],
  cloudState: "loading",
  cloudMessage: "Checking availability…",
  operationPending: false
};

elements.form.addEventListener("submit", handleSearch);
elements.refreshLibrary.addEventListener("click", refreshLibrary);
window.addEventListener("online", () => setSearchStatus("Back online. Search and iCloud changes are available."));
window.addEventListener("offline", () => setSearchStatus("You’re offline. Existing results remain visible.", "error"));

void initialize();

async function initialize() {
  const initialQuery = new URLSearchParams(window.location.search).get("q")?.trim();
  if (initialQuery) {
    elements.query.value = initialQuery;
    void runSearch(initialQuery);
  }
  await initializeCloudKit();
}

async function handleSearch(event) {
  event.preventDefault();
  const query = elements.query.value.trim();
  if (query.length < 2) {
    setSearchStatus("Enter at least two characters or a complete feed URL.", "error");
    elements.query.focus();
    return;
  }
  const url = new URL(window.location.href);
  url.searchParams.set("q", query);
  history.replaceState(null, "", url);
  await runSearch(query);
}

async function runSearch(query) {
  state.searchController?.abort();
  state.searchController = new AbortController();
  state.resolvedShow = null;
  setSearchBusy(true);

  try {
    if (isHTTPFeedURL(query)) {
      state.results = [];
      renderResults();
      state.selectedDirectoryShow = null;
      setSearchStatus("Verifying the RSS feed and its final address…");
      await selectFeed({ feedURL: query, title: "RSS feed", itunesID: null }, state.searchController.signal);
      elements.resultsHeading.textContent = "Feed address";
      setSearchStatus("Feed verified. Review the resolved show before following.");
      return;
    }

    setSearchStatus(`Searching the public directory for “${query}”…`);
    state.results = await searchPodcasts(query, { signal: state.searchController.signal });
    elements.resultsHeading.textContent = `Results for “${query}”`;
    renderResults();
    setSearchStatus(state.results.length
      ? "Choose a show to verify its RSS feed."
      : "No podcasts matched that search. Try a creator, title, or RSS feed URL.");
  } catch (error) {
    if (error?.name === "AbortError") return;
    setSearchStatus(error.message, "error");
  } finally {
    setSearchBusy(false);
  }
}

function renderResults() {
  elements.results.replaceChildren();
  elements.emptyResult.hidden = state.results.length > 0;
  elements.resultCount.textContent = state.results.length ? `${state.results.length} shows` : "";

  for (const show of state.results) {
    const item = document.createElement("li");
    item.className = "podcast-result";
    if (state.selectedDirectoryShow?.itunesID === show.itunesID) item.classList.add("is-selected");

    item.append(createArtwork(show.artworkURLString, show.title, "result-artwork"));
    const copy = document.createElement("div");
    copy.className = "result-copy";
    copy.append(textElement("h3", show.title));
    copy.append(textElement("p", show.author || "Independent podcast"));
    if (show.genre) copy.append(textElement("small", show.genre));
    item.append(copy);

    const button = document.createElement("button");
    button.type = "button";
    button.className = "inspect-button";
    button.textContent = show.feedURL ? "Check feed" : "Unavailable";
    button.disabled = !show.feedURL;
    button.addEventListener("click", () => selectDirectoryShow(show));
    item.append(button);
    elements.results.append(item);
  }
}

async function selectDirectoryShow(show) {
  if (!show.feedURL) return;
  state.selectedDirectoryShow = show;
  renderResults();
  setSearchStatus(`Verifying the feed for “${show.title}”…`);
  setInspectorBusy(show);
  revealInspectorOnNarrowScreen();
  state.searchController?.abort();
  state.searchController = new AbortController();

  try {
    await selectFeed(show, state.searchController.signal);
    setSearchStatus("Feed verified. The details shown here come from the resolved RSS feed.");
  } catch (error) {
    if (error?.name === "AbortError") return;
    setInspectorError(error.message);
    setSearchStatus(error.message, "error");
  }
}

async function selectFeed(show, signal) {
  const resolved = await resolvePodcast(show.feedURL, {
    itunesID: Number.isSafeInteger(show.itunesID) ? show.itunesID : null,
    signal
  });
  state.resolvedShow = resolved;
  renderInspector();
}

function renderInspector() {
  const show = state.resolvedShow;
  if (!show) return;
  elements.inspector.replaceChildren();
  appendInspectorBackControl();

  const art = document.createElement("div");
  art.className = "resolved-art";
  art.append(createArtwork(show.artworkURLString, show.title, ""));
  elements.inspector.append(art);

  const body = document.createElement("div");
  body.className = "resolved-body";
  const verified = textElement("span", "✓  Verified RSS feed");
  verified.className = "verified-label";
  body.append(verified);
  body.append(textElement("h2", show.title));
  body.append(textElement("p", show.author || "Author not listed", "resolved-author"));
  const websiteLink = createShowWebsiteLink(document, show.websiteURL);
  if (websiteLink) body.append(websiteLink);
  if (show.feedDescription) body.append(textElement("p", show.feedDescription, "resolved-description"));

  const facts = document.createElement("div");
  facts.className = "feed-facts";
  facts.append(textElement("span", `${Number(show.episodeCount || 0).toLocaleString()} episodes`));
  facts.append(textElement("span", feedHostname(show.feedURL)));
  if (show.itunesID) facts.append(textElement("span", `Apple ID ${show.itunesID}`));
  body.append(facts);

  if (state.cloudState === "setup-required") {
    body.append(createLibrarySetupPanel(document, {
      compact: true,
      headingID: "inspector-library-setup-heading",
      onRetry: retryLibrarySetup
    }));
    elements.inspector.append(body);
    return;
  }

  const following = state.followedShows.some((candidate) => candidate.normalizedFeedURL === normalizeFeedURL(show.feedURL));
  const action = document.createElement("button");
  action.type = "button";
  action.className = "show-action";
  const note = document.createElement("p");
  note.className = "show-action-note";

  if (state.operationPending) {
    action.disabled = true;
    action.textContent = "Checking iCloud…";
  } else if (state.cloudState !== "connected") {
    action.textContent = state.cloudState === "signed-out" ? "Sign in to follow" : "iCloud follow unavailable";
    action.addEventListener("click", focusCloudKitCard);
    note.textContent = "Public search and feed previews do not require sign-in.";
  } else if (!state.cloudKitConfiguration?.writesEnabled) {
    action.disabled = true;
    action.textContent = following ? "Following in BondCasts" : "Follow controls are read-only";
    note.textContent = "Writes remain gated while production import testing is completed.";
  } else if (following) {
    action.textContent = "Unfollow in BondCasts";
    action.classList.add("is-unfollow");
    action.addEventListener("click", () => mutateFollow(false));
    note.textContent = "This removes every matching follow record after iCloud confirms the change.";
  } else {
    action.textContent = "Follow in BondCasts";
    action.addEventListener("click", () => mutateFollow(true));
    note.textContent = "The verified feed will appear on your BondCasts devices through iCloud.";
  }

  body.append(action, note);
  elements.inspector.append(body);
}

async function mutateFollow(shouldFollow) {
  if (!state.store || !state.resolvedShow || state.operationPending) return;
  state.operationPending = true;
  renderInspector();
  setSearchStatus(shouldFollow ? "Following after a final duplicate check…" : "Removing every matching follow…");

  try {
    const result = shouldFollow
      ? await state.store.follow(state.resolvedShow)
      : await state.store.unfollow(state.resolvedShow.feedURL);
    state.followedShows = state.store.snapshot();
    setSearchStatus(followResultMessage(result.status));
    updateCloudKitDisplay();
  } catch (error) {
    handleCloudError(error);
    setSearchStatus(error.message, "error");
  } finally {
    state.operationPending = false;
    renderInspector();
  }
}

async function initializeCloudKit() {
  try {
    state.cloudKitConfiguration = await loadCompanionConfiguration();
    if (!state.cloudKitConfiguration.enabled) {
      setCloudState("unavailable", "iCloud follow controls are not configured on this environment.");
      return;
    }
    state.cloudKitClient = await createCloudKitClient(state.cloudKitConfiguration, {
      signIn: "apple-sign-in-button",
      signOut: "apple-sign-out-button"
    });
    state.cloudKitClient.subscribe((identity) => {
      if (identity) void connectLibrary();
      else disconnectLibrary();
    });
    setCloudState("signed-out", "Sign in with iCloud to match results against your BondCasts library.");
    await state.cloudKitClient.setUpAuth();
    // The spike found identity labels can be stale even while private database
    // access succeeds, so use a real private-zone read as the authority.
    await connectLibrary();
  } catch (error) {
    handleCloudError(error);
  }
}

let libraryConnectionPromise;
async function connectLibrary() {
  if (!state.cloudKitClient) return;
  if (libraryConnectionPromise) return libraryConnectionPromise;
  libraryConnectionPromise = (async () => {
    setCloudState("loading", "Checking your private BondCasts library…");
    try {
      const library = await discoverFollowedShows(state.cloudKitClient);
      state.store = library.store;
      state.followedShows = library.followedShows;
      setCloudState("connected", libraryCountMessage());
      elements.refreshLibrary.hidden = false;
      renderInspector();
    } catch (error) {
      handleCloudError(error);
    } finally {
      libraryConnectionPromise = null;
    }
  })();
  return libraryConnectionPromise;
}

async function retryLibrarySetup() {
  if (!state.cloudKitClient || libraryConnectionPromise) return;
  await connectLibrary();
}

async function refreshLibrary() {
  if (!state.store || state.operationPending) return;
  state.operationPending = true;
  setCloudState("loading", "Checking iCloud for changes from your other devices…");
  try {
    state.followedShows = await state.store.readAll();
    setCloudState("connected", libraryCountMessage());
  } catch (error) {
    handleCloudError(error);
  } finally {
    state.operationPending = false;
    renderInspector();
  }
}

function disconnectLibrary() {
  state.store = null;
  state.followedShows = [];
  elements.refreshLibrary.hidden = true;
  setCloudState("signed-out", "Sign in with iCloud to match results against your BondCasts library.");
  renderInspector();
}

function handleCloudError(error) {
  const companionError = error instanceof CompanionError
    ? error
    : new CompanionError("unavailable", "iCloud follow controls are unavailable in this environment. Public search still works.");
  if (companionError.kind === "auth-expired") {
    disconnectLibrary();
    return;
  }
  if (isLibrarySetupRequired(companionError)) {
    state.store = null;
    state.followedShows = [];
    elements.refreshLibrary.hidden = true;
    setCloudState("setup-required", "BondCasts is not set up for this iCloud account yet.");
    return;
  }
  setCloudState("unavailable", companionError.message);
}

function setCloudState(nextState, message) {
  state.cloudState = nextState;
  state.cloudMessage = message;
  updateCloudKitDisplay();
}

function updateCloudKitDisplay() {
  elements.iCloudStatus.textContent = state.cloudState === "connected" ? libraryCountMessage() : state.cloudMessage;
  const setupRequired = state.cloudState === "setup-required";
  elements.iCloudOnboarding.hidden = !setupRequired;
  elements.iCloudOnboarding.replaceChildren(...(setupRequired
    ? [createLibrarySetupPanel(document, { onRetry: retryLibrarySetup })]
    : []));
  elements.connectionDot.dataset.state = state.cloudState === "connected"
    ? "connected"
    : setupRequired || state.cloudState === "signed-out" ? "attention" : "";
  renderInspector();
}

function libraryCountMessage() {
  const count = state.followedShows.length;
  return `Connected · ${count.toLocaleString()} followed ${count === 1 ? "show" : "shows"}`;
}

function setSearchBusy(busy) {
  const button = elements.form.querySelector("button[type=submit]");
  button.disabled = busy;
  button.textContent = busy ? "Looking…" : "Find podcasts";
}

function setSearchStatus(message, kind = "info") {
  elements.searchStatus.textContent = message;
  elements.searchStatus.dataset.kind = kind;
}

function setInspectorBusy(show) {
  elements.inspector.replaceChildren();
  appendInspectorBackControl();
  const placeholder = document.createElement("div");
  placeholder.className = "inspector-placeholder";
  placeholder.append(textElement("span", "↻", "verify-mark"));
  placeholder.append(textElement("h2", `Checking ${show.title}`));
  placeholder.append(textElement("p", "Following redirects and parsing the live RSS feed…"));
  elements.inspector.append(placeholder);
}

function setInspectorError(message) {
  elements.inspector.replaceChildren();
  appendInspectorBackControl();
  const placeholder = document.createElement("div");
  placeholder.className = "inspector-placeholder";
  placeholder.append(textElement("span", "!", "verify-mark"));
  placeholder.append(textElement("h2", "Feed could not be verified"));
  placeholder.append(textElement("p", message));
  elements.inspector.append(placeholder);
}

function appendInspectorBackControl() {
  if (!state.results.length) return;
  const button = document.createElement("button");
  button.type = "button";
  button.className = "inspector-back";
  button.textContent = "← Back to results";
  button.addEventListener("click", returnToSelectedResult);
  elements.inspector.append(button);
}

function revealInspectorOnNarrowScreen() {
  if (!window.matchMedia("(max-width: 820px)").matches) return;
  elements.inspector.scrollIntoView({ behavior: preferredScrollBehavior(), block: "start" });
}

function returnToSelectedResult() {
  const selectedButton = elements.results.querySelector(".podcast-result.is-selected .inspect-button");
  const target = selectedButton ?? elements.resultsHeading;
  target.scrollIntoView({ behavior: preferredScrollBehavior(), block: "center" });
  selectedButton?.focus({ preventScroll: true });
}

function preferredScrollBehavior() {
  return window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";
}

function focusCloudKitCard() {
  elements.iCloudCard.scrollIntoView({ behavior: preferredScrollBehavior(), block: "center" });
  elements.iCloudCard.setAttribute("tabindex", "-1");
  elements.iCloudCard.focus({ preventScroll: true });
}

function createArtwork(url, title, className) {
  if (url) {
    const image = document.createElement("img");
    image.className = className;
    image.src = url;
    image.alt = "";
    image.loading = "lazy";
    image.referrerPolicy = "no-referrer";
    image.addEventListener("error", () => image.replaceWith(artworkFallback(title, className)), { once: true });
    return image;
  }
  return artworkFallback(title, className);
}

function artworkFallback(title, className) {
  const fallback = textElement("span", String(title || "B").trim().charAt(0).toUpperCase() || "B");
  fallback.className = `artwork-fallback ${className}`.trim();
  fallback.setAttribute("aria-hidden", "true");
  return fallback;
}

function textElement(tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}

function feedHostname(value) {
  try { return new URL(value).hostname; } catch { return "RSS feed"; }
}

function followResultMessage(status) {
  switch (status) {
    case "already-following": return "Already following. No duplicate was created.";
    case "followed": return "Followed. iCloud confirmed the show in your BondCasts library.";
    case "already-unfollowed": return "Already unfollowed. Your library is up to date.";
    case "unfollowed": return "Unfollowed. iCloud confirmed every matching record was removed.";
    case "reconciled": return "Your library changed during the request; the final iCloud state is now confirmed.";
    default: return "Your iCloud library is up to date.";
  }
}
