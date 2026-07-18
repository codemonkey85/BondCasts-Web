export const BONDCASTS_INSTALL_URL = "https://testflight.apple.com/join/fytFVhx2";

export function createShowWebsiteLink(document, websiteURL) {
  const normalizedWebsiteURL = normalizeAbsoluteHTTPURL(websiteURL);
  if (!normalizedWebsiteURL) return null;

  const link = document.createElement("a");
  link.className = "show-website-link";
  link.href = normalizedWebsiteURL;
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  link.textContent = "Visit show website";
  return link;
}

export function createLibrarySetupPanel(document, options = {}) {
  const panel = document.createElement("section");
  panel.className = `library-setup${options.compact ? " is-compact" : ""}`;
  panel.setAttribute("aria-labelledby", options.headingID ?? "library-setup-heading");

  if (!options.compact) {
    panel.append(textElement(document, "p", "First run", "library-setup-label"));
  }

  const heading = textElement(document, options.compact ? "h3" : "h2", "Set up BondCasts first");
  heading.id = options.headingID ?? "library-setup-heading";
  panel.append(heading);
  panel.append(textElement(
    document,
    "p",
    "Use the same iCloud account in the app and on this page. Opening the app once creates the private library this companion connects to.",
    "library-setup-copy"
  ));

  if (!options.compact) {
    const steps = document.createElement("ol");
    steps.className = "library-setup-steps";
    for (const [number, label] of [
      ["1", "Install BondCasts"],
      ["2", "Sign in with this iCloud account"],
      ["3", "Open the app once"]
    ]) {
      const step = document.createElement("li");
      step.append(textElement(document, "b", number), textElement(document, "span", label));
      steps.append(step);
    }
    panel.append(steps);
  }

  const actions = document.createElement("div");
  actions.className = "library-setup-actions";

  const install = document.createElement("a");
  install.className = "library-setup-action is-install";
  install.href = BONDCASTS_INSTALL_URL;
  install.target = "_blank";
  install.rel = "noopener noreferrer";
  install.textContent = "Install with TestFlight";

  const retry = document.createElement("button");
  retry.type = "button";
  retry.className = "library-setup-action is-retry";
  retry.disabled = Boolean(options.retryPending);
  retry.textContent = options.retryPending ? "Checking iCloud…" : "I opened BondCasts — try again";
  retry.addEventListener("click", () => options.onRetry?.());

  actions.append(install, retry);
  panel.append(actions);
  return panel;
}

export function isAbsoluteHTTPURL(value) {
  return normalizeAbsoluteHTTPURL(value) !== null;
}

function normalizeAbsoluteHTTPURL(value) {
  if (typeof value !== "string") return null;
  const normalizedValue = value.trim();
  if (!/^https?:\/\//i.test(normalizedValue)) return null;
  try {
    const url = new URL(normalizedValue);
    return Boolean(url.hostname) && (url.protocol === "http:" || url.protocol === "https:")
      ? normalizedValue
      : null;
  } catch {
    return null;
  }
}

function textElement(document, tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}
