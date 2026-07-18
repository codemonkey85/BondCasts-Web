import { CompanionError } from "./errors.js";

const webAuthTokenParameter = "ckWebAuthToken";
const cloudKitAPIOrigin = "https://api.apple-cloudkit.com";
const trustedSignInHosts = new Set([
  "idmsa.apple.com",
  "idmsa.apple.com.cn",
  "signin.apple.com",
  "signin.apple.com.cn",
  "cdn.apple-cloudkit.com"
]);

export function consumeCloudKitWebAuthToken(browserWindow = window) {
  const url = new URL(browserWindow.location.href);
  const token = url.searchParams.get(webAuthTokenParameter)?.trim() || null;
  if (!token) return null;

  url.searchParams.delete(webAuthTokenParameter);
  browserWindow.history.replaceState(
    browserWindow.history.state,
    "",
    `${url.pathname}${url.search}${url.hash}`
  );
  return token;
}

export async function requestCloudKitSignInURL(configuration, options = {}) {
  if (!configuration?.containerIdentifier || !configuration.apiToken) {
    throw new CompanionError("unavailable", "The iCloud web configuration is incomplete.");
  }

  const environment = configuration.environment === "development" ? "development" : "production";
  const endpoint = new URL(
    `/database/1/${encodeURIComponent(configuration.containerIdentifier)}/${environment}/public/users/current`,
    cloudKitAPIOrigin
  );
  endpoint.searchParams.set("ckAPIToken", configuration.apiToken);

  let response;
  try {
    response = await (options.fetch ?? fetch)(endpoint, {
      headers: { Accept: "application/json" },
      cache: "no-store"
    });
  } catch (error) {
    throw new CompanionError("unavailable", "Apple sign-in could not be reached. Try again.", { cause: error });
  }

  const payload = await response.json().catch(() => ({}));
  if (payload.serverErrorCode !== "AUTHENTICATION_REQUIRED"
    || !isTrustedAppleSignInURL(payload.redirectURL)) {
    throw new CompanionError(
      "unavailable",
      payload.reason || "Apple sign-in did not provide a valid authentication destination."
    );
  }
  return payload.redirectURL;
}

export function isTrustedAppleSignInURL(value) {
  if (typeof value !== "string") return false;
  try {
    const url = new URL(value);
    return url.protocol === "https:" && trustedSignInHosts.has(url.hostname.toLowerCase());
  } catch {
    return false;
  }
}
