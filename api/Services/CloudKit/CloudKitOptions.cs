using System.Security.Cryptography;

namespace BondCasts.Api.Services.CloudKit;

/// Server-to-server CloudKit Web Services configuration. Keys are
/// ENVIRONMENT-SCOPED in the CloudKit console (one key per env, same keypair
/// is fine), so KeyId must match the configured Environment.
///
/// Settings (Azure app settings / local.settings.json Values):
///   CloudKit__Container         e.g. iCloud.com.bondcodes.PodcastApp
///   CloudKit__Environment       development | production
///   CloudKit__KeyId             key ID from CloudKit Dashboard for that env
///   CloudKit__PrivateKeyPemBase64   base64 of the EC P-256 private key PEM
///                                   (base64 because multi-line values are
///                                   awkward in Azure app settings)
public sealed class CloudKitOptions
{
    public required string Container { get; init; }
    public required string Environment { get; init; }
    public required string KeyId { get; init; }
    public required string PrivateKeyPem { get; init; }

    /// Null when any setting is missing — the poller then no-ops, so the
    /// link-preview functions keep working in environments without CloudKit
    /// credentials (local dev, PR preview slots).
    public static CloudKitOptions? FromEnvironment()
    {
        var container = Get("CloudKit__Container");
        var environment = Get("CloudKit__Environment");
        var keyId = Get("CloudKit__KeyId");
        var pemBase64 = Get("CloudKit__PrivateKeyPemBase64");
        if (container is null || environment is null || keyId is null || pemBase64 is null)
            return null;

        return new CloudKitOptions
        {
            Container = container,
            Environment = environment,
            KeyId = keyId,
            PrivateKeyPem = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pemBase64)),
        };
    }

    public ECDsa LoadPrivateKey()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(PrivateKeyPem);
        return ecdsa;
    }

    private static string? Get(string name) =>
        System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
