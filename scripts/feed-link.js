// Surface the source feed on static fallbacks without accepting executable URLs.
const value = new URLSearchParams(location.search).get("feed");
const container = document.getElementById("feed-line");

if (value && container) {
  try {
    const url = new URL(value);
    if (url.protocol === "http:" || url.protocol === "https:") {
      const link = document.createElement("a");
      link.href = url.href;
      link.textContent = value;
      link.rel = "noopener";
      container.append("Podcast feed: ", link);
    }
  } catch {
    // Invalid feed URLs stay hidden on the fallback page.
  }
}
