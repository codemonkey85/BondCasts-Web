export class CompanionError extends Error {
  constructor(kind, message, options = {}) {
    super(message, { cause: options.cause });
    this.name = "CompanionError";
    this.kind = kind;
    this.retryAfterSeconds = options.retryAfterSeconds ?? null;
    this.outcomeUnknown = Boolean(options.outcomeUnknown);
  }
}

export function classifyCloudKitError(error, options = {}) {
  if (error instanceof CompanionError) return error;

  const code = String(error?.ckErrorCode ?? error?.code ?? error?.name ?? "").toUpperCase();
  const status = Number(error?.statusCode ?? error?.status);
  const retryAfterSeconds = numericRetryAfter(error);

  if (typeof navigator !== "undefined" && navigator.onLine === false) {
    return new CompanionError("offline", "You’re offline. Reconnect, then try again.", { cause: error, outcomeUnknown: options.mutation });
  }
  if (status === 401 || status === 403
    || matches(code, ["AUTHENTICATION", "NOT_AUTHENTICATED", "AUTH_EXPIRED", "PERMISSION_FAILURE"])) {
    return new CompanionError("auth-expired", "Your iCloud session expired. Sign in again to continue.", { cause: error });
  }
  if (status === 429 || status === 503
    || matches(code, ["THROTTL", "RATE_LIMIT", "ZONE_BUSY", "SERVICE_UNAVAILABLE", "TRY_AGAIN_LATER"])) {
    return new CompanionError("throttled", "iCloud is busy. Your library has not been changed yet.", {
      cause: error,
      retryAfterSeconds,
      outcomeUnknown: options.mutation
    });
  }
  if (matches(code, ["CONFLICT", "SERVER_RECORD_CHANGED", "BATCH_REQUEST_FAILED"])) {
    return new CompanionError("conflict", "Your library changed on another device. Checking the latest version now.", {
      cause: error,
      outcomeUnknown: options.mutation
    });
  }
  return new CompanionError(options.mutation ? "unknown-outcome" : "cloudkit", options.mutation
    ? "iCloud did not confirm the change. BondCasts will check your library before offering a retry."
    : "BondCasts could not load your iCloud library.", {
    cause: error,
    outcomeUnknown: options.mutation
  });
}

export function isExpiredChangeToken(error) {
  const code = String(error?.ckErrorCode ?? error?.code ?? "").toUpperCase();
  return matches(code, ["CHANGE_TOKEN_EXPIRED", "CHANGE_TOKEN_INVALID", "SYNC_TOKEN_EXPIRED"]);
}

function matches(code, fragments) {
  return fragments.some((fragment) => code.includes(fragment));
}

function numericRetryAfter(error) {
  const value = Number(error?.retryAfter ?? error?.retryAfterSeconds);
  return Number.isFinite(value) && value >= 0 ? value : null;
}
