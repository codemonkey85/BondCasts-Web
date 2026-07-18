/**
 * Feed URL identity contract shared with FeedURL.normalize in the native app.
 * This value is for comparisons only; callers must fetch the original URL.
 */
export function normalizeFeedURL(value) {
  const trimmed = String(value ?? "").trim();

  try {
    const url = new URL(trimmed);
    if (!url.hostname) return trimmed;

    const originalProtocol = url.protocol.toLowerCase();
    const originalPort = url.port;
    const keepOriginalPort = originalPort
      && !((originalProtocol === "https:" && originalPort === "443")
        || (originalProtocol === "http:" && originalPort === "80"));

    url.protocol = "https:";
    url.hostname = url.hostname.toLowerCase();
    url.port = keepOriginalPort ? originalPort : "";

    const shouldRemoveRootSlash = url.pathname === "/";
    if (url.pathname.endsWith("/")) {
      url.pathname = url.pathname.slice(0, -1);
    }

    const sortedQueryItems = [...url.searchParams.entries()].sort(compareQueryItems);
    url.search = "";
    for (const [name, itemValue] of sortedQueryItems) {
      url.searchParams.append(name, itemValue);
    }
    url.hash = "";

    let result = url.toString();
    if (keepOriginalPort && originalPort === "443" && !resultAuthority(result).endsWith(":443")) {
      result = insertPort(result, originalPort);
    }
    if (shouldRemoveRootSlash) {
      result = removeRootPathSlash(result);
    }
    return uppercasePercentEscapes(result);
  } catch {
    return trimmed;
  }
}

export function isSameFeed(a, b) {
  return normalizeFeedURL(a) === normalizeFeedURL(b);
}

export function isHTTPFeedURL(value) {
  try {
    const url = new URL(String(value ?? ""));
    return (url.protocol === "http:" || url.protocol === "https:")
      && Boolean(url.hostname)
      && !url.username
      && !url.password;
  } catch {
    return false;
  }
}

export function isPotentiallyPrivateFeedURL(value) {
  try {
    const url = new URL(String(value ?? ""));
    return Boolean(url.username || url.password || url.search || url.hash);
  } catch {
    return false;
  }
}

function compareQueryItems([aName, aValue], [bName, bValue]) {
  if (aName !== bName) return aName < bName ? -1 : 1;
  if (aValue === bValue) return 0;
  return aValue < bValue ? -1 : 1;
}

function uppercasePercentEscapes(value) {
  return value.replace(/%[0-9a-fA-F]{2}/g, (match) => match.toUpperCase());
}

function resultAuthority(value) {
  return value.match(/^https:\/\/([^/?#]+)/)?.[1] ?? "";
}

function insertPort(value, port) {
  return value.replace(/^(https:\/\/[^/?#:]+)(?=[/?#]|$)/, `$1:${port}`);
}

function removeRootPathSlash(value) {
  return value.replace(/^(https:\/\/[^/?#]+)\/(?=[?#]|$)/, "$1");
}
