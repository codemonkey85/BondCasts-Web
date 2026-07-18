import assert from "node:assert/strict";
import test from "node:test";
import {
  BONDCASTS_INSTALL_URL,
  createLibrarySetupPanel,
  createShowWebsiteLink
} from "../../scripts/companion/companion-ui.js";

test("resolved show website renders as a protected, keyboard-accessible link", () => {
  const document = new FakeDocument();
  const link = createShowWebsiteLink(document, "https://publisher.example/show?from=rss");

  assert.ok(link);
  assert.equal(link.tagName, "A");
  assert.equal(link.textContent, "Visit show website");
  assert.equal(link.href, "https://publisher.example/show?from=rss");
  assert.equal(link.target, "_blank");
  assert.equal(link.rel, "noopener noreferrer");
});

test("resolved show website trims surrounding whitespace before rendering", () => {
  const document = new FakeDocument();
  const link = createShowWebsiteLink(document, "  https://publisher.example/show  ");

  assert.ok(link);
  assert.equal(link.href, "https://publisher.example/show");
});

test("resolved show website is omitted for missing or unsafe URLs", () => {
  const document = new FakeDocument();
  assert.equal(createShowWebsiteLink(document, null), null);
  assert.equal(createShowWebsiteLink(document, "/relative/show"), null);
  assert.equal(createShowWebsiteLink(document, "http:/missing-host"), null);
  assert.equal(createShowWebsiteLink(document, "javascript:alert(1)"), null);
});

test("missing-zone onboarding renders install, same-account, open-once, and retry guidance", () => {
  const document = new FakeDocument();
  let retryCount = 0;
  const panel = createLibrarySetupPanel(document, { onRetry: () => retryCount += 1 });

  assert.match(allText(panel), /Set up BondCasts first/);
  assert.match(allText(panel), /same iCloud account/);
  assert.match(allText(panel), /Open the app once/);

  const install = find(panel, (element) => element.tagName === "A");
  assert.equal(install.href, BONDCASTS_INSTALL_URL);
  assert.equal(install.target, "_blank");
  assert.equal(install.rel, "noopener noreferrer");

  const retry = find(panel, (element) => element.tagName === "BUTTON");
  assert.equal(retry.textContent, "I opened BondCasts — try again");
  retry.click();
  assert.equal(retryCount, 1);
});

class FakeDocument {
  createElement(tagName) {
    return new FakeElement(tagName);
  }
}

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.children = [];
    this.attributes = {};
    this.listeners = {};
    this.textContent = "";
  }

  append(...children) {
    this.children.push(...children);
  }

  setAttribute(name, value) {
    this.attributes[name] = String(value);
  }

  addEventListener(type, listener) {
    this.listeners[type] ??= [];
    this.listeners[type].push(listener);
  }

  click() {
    for (const listener of this.listeners.click ?? []) listener();
  }
}

function find(element, predicate) {
  if (predicate(element)) return element;
  for (const child of element.children) {
    const match = find(child, predicate);
    if (match) return match;
  }
  return null;
}

function allText(element) {
  return [element.textContent, ...element.children.map(allText)].filter(Boolean).join(" ");
}
