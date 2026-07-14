using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace BondCasts.Poller.Services.Polling;

/// Per-feed polling state, keyed by the app-derived feedHash. KnownIdentities
/// holds short hashes of recently seen episode identities (newest first,
/// capped) — an item is "new" when its identity hash isn't in this window.
public sealed class FeedPollState
{
    public required string FeedHash { get; init; }
    public required string FeedUrl { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    /// Content ETag (hash of the served JSON) of the feed blob we last wrote, so
    /// a poll whose parsed content is unchanged skips a redundant blob upload
    /// (which would otherwise churn the ETag and defeat client-side 304s).
    public string? CacheETag { get; set; }
    public List<string> KnownIdentities { get; set; } = [];
    public DateTimeOffset? LastPolledAt { get; set; }
    public int FailureCount { get; set; }
    public DateTimeOffset NextPollAt { get; set; } = DateTimeOffset.MinValue;

    /// True for a feed we've never successfully parsed: seed the identity
    /// window without announcing, so registering an existing show doesn't
    /// blast a "300 new episodes" banner.
    public bool IsFirstObservation => KnownIdentities.Count == 0;
}

/// Table Storage persistence for FeedPollState plus the poller's own
/// metadata (last NewEpisode prune time). Uses the Functions app's existing
/// AzureWebJobsStorage account.
public sealed class FeedPollStateStore(TableClient table)
{
    private const string FeedPartition = "feed";
    private const string MetaPartition = "meta";
    private const string PruneRowKey = "newEpisodePrune";
    private const int MaxKnownIdentities = 500;

    public const string TableName = "bondcastsfeedpollstate";

    /// Short stable hash of an episode identity; stored instead of raw GUIDs
    /// (which can be arbitrarily long URLs) to bound entity size.
    public static string IdentityHash(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];

    /// Called once per poll cycle (only when CloudKit is configured), so an
    /// unconfigured deployment never touches Table Storage.
    public Task EnsureTableAsync(CancellationToken ct) =>
        table.CreateIfNotExistsAsync(cancellationToken: ct);

    public async Task<Dictionary<string, FeedPollState>> LoadAllAsync(CancellationToken ct)
    {
        var states = new Dictionary<string, FeedPollState>();
        await foreach (var entity in table.QueryAsync<TableEntity>(
            e => e.PartitionKey == FeedPartition, cancellationToken: ct))
        {
            states[entity.RowKey] = new FeedPollState
            {
                FeedHash = entity.RowKey,
                FeedUrl = entity.GetString("FeedUrl") ?? string.Empty,
                ETag = entity.GetString("ETag"),
                LastModified = entity.GetString("LastModified"),
                CacheETag = entity.GetString("CacheETag"),
                KnownIdentities = JsonSerializer.Deserialize<List<string>>(
                    entity.GetString("KnownIdentities") ?? "[]") ?? [],
                LastPolledAt = entity.GetDateTimeOffset("LastPolledAt"),
                FailureCount = entity.GetInt32("FailureCount") ?? 0,
                NextPollAt = entity.GetDateTimeOffset("NextPollAt") ?? DateTimeOffset.MinValue,
            };
        }
        return states;
    }

    public async Task SaveAsync(FeedPollState state, CancellationToken ct)
    {
        if (state.KnownIdentities.Count > MaxKnownIdentities)
            state.KnownIdentities = state.KnownIdentities[..MaxKnownIdentities];

        var entity = new TableEntity(FeedPartition, state.FeedHash)
        {
            ["FeedUrl"] = state.FeedUrl,
            ["ETag"] = state.ETag,
            ["LastModified"] = state.LastModified,
            ["CacheETag"] = state.CacheETag,
            ["KnownIdentities"] = JsonSerializer.Serialize(state.KnownIdentities),
            ["LastPolledAt"] = state.LastPolledAt,
            ["FailureCount"] = state.FailureCount,
            ["NextPollAt"] = state.NextPollAt,
        };
        await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public Task DeleteAsync(string feedHash, CancellationToken ct) =>
        table.DeleteEntityAsync(FeedPartition, feedHash, ETag.All, ct);

    public async Task<DateTimeOffset?> GetLastPruneAsync(CancellationToken ct)
    {
        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(MetaPartition, PruneRowKey, cancellationToken: ct);
            return entity.Value.GetDateTimeOffset("LastPrunedAt");
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public Task SetLastPruneAsync(DateTimeOffset when, CancellationToken ct) =>
        table.UpsertEntityAsync(
            new TableEntity(MetaPartition, PruneRowKey) { ["LastPrunedAt"] = when },
            TableUpdateMode.Replace, ct);
}
