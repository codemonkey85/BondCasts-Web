export function isMacSafari({
  userAgent = globalThis.navigator?.userAgent ?? "",
  platform = globalThis.navigator?.platform ?? "",
  maxTouchPoints = globalThis.navigator?.maxTouchPoints ?? 0
} = {}) {
  const isSafari = /\bSafari\//.test(userAgent)
    && /\bVersion\//.test(userAgent)
    && !/\b(?:Chrome|Chromium|CriOS|FxiOS|Edg|EdgiOS|OPR|OPiOS)\//.test(userAgent);
  const isAppleDesktop = /\bMac/.test(platform) || /\bMacintosh\b/.test(userAgent);
  const isIPadUsingDesktopIdentity = platform === "MacIntel" && Number(maxTouchPoints) > 1;

  return isSafari && isAppleDesktop && !isIPadUsingDesktopIdentity;
}
