using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BondCasts.Poller.Services.Cache;
using BondCasts.Poller.Services.Polling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // One HTTP client both fetches feeds (conditional GETs, so a generous
        // timeout for slow podcast hosts) and signs CloudKit Web Services
        // requests.
        services.AddHttpClient(FeedPoller.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BondCastsPoller/1.0 (+https://bondcasts.com)");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // Feed URLs are user-registered (CloudKit public DB), so every
            // connection — including each redirect hop, the classic
            // public-host-302s-to-IMDS bypass — goes through the SSRF guard
            // (#20). CloudKit's own requests ride the same handler; its host
            // is public, so the guard is a no-op for them.
            ConnectCallback = FeedUrlGuard.GuardedConnectAsync,
            MaxAutomaticRedirections = 5,
        });
        services.AddSingleton<PollerOptions>();
        services.AddSingleton(_ =>
        {
            var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                ?? "UseDevelopmentStorage=true";
            return new FeedPollStateStore(new TableClient(storage, FeedPollStateStore.TableName));
        });
        services.AddSingleton(_ =>
        {
            var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                ?? "UseDevelopmentStorage=true";
            var container = new BlobServiceClient(storage).GetBlobContainerClient(FeedCacheStore.ContainerName);
            return new FeedCacheStore(container);
        });
        services.AddSingleton<FeedPoller>();
    })
    .Build();

host.Run();
