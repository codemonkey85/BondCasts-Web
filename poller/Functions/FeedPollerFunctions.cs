using BondCasts.Poller.Services.Polling;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BondCasts.Poller.Functions;

/// Server-side new-episode discovery for the BondCasts app (PodcastApp#135).
/// Devices register followed feeds as PolledFeed records in the app's public
/// CloudKit database; this timer polls the union and writes NewEpisode
/// records, which fan out to devices via their CKQuerySubscriptions.
public sealed class FeedPollerFunctions(FeedPoller poller, ILogger<FeedPollerFunctions> logger)
{
    /// Fires every 10 minutes; each feed carries its own next-poll time
    /// (base interval + jitter, exponential backoff on failures), so most
    /// runs touch only the feeds that are due. No-ops without CloudKit
    /// credentials so local/link-preview-only deployments are unaffected.
    [Function("PollFeeds")]
    public async Task PollFeeds([TimerTrigger("0 */10 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        try
        {
            await poller.RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log-and-swallow: a thrown exception would mark the invocation
            // failed and retry immediately; the next tick is soon enough.
            logger.LogError(ex, "Feed poll cycle failed.");
        }
    }
}
