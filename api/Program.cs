using Azure.Data.Tables;
using BondCasts.Api.Rendering;
using BondCasts.Api.Services;
using BondCasts.Api.Services.Polling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddMemoryCache();

        // A single named client for feed fetches. Server-to-server, so no CORS
        // constraints. A short timeout keeps us well inside the ~a-few-seconds
        // budget link-preview crawlers allow before they give up.
        services.AddHttpClient(FeedService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BondCastsLinkPreview/1.0 (+https://bondcasts.com)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml, text/xml;q=0.9, */*;q=0.8");
        });

        services.AddSingleton<FeedService>();
        services.AddSingleton<PageRenderer>();

        // Feed poller for BondCasts new-episode push (PodcastApp#135). Its
        // HTTP client both fetches feeds (conditional GETs, so a generous
        // timeout for slow hosts) and signs CloudKit Web Services requests.
        services.AddHttpClient(FeedPoller.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BondCastsPoller/1.0 (+https://bondcasts.com)");
        });
        services.AddSingleton<PollerOptions>();
        services.AddSingleton(_ =>
        {
            var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                ?? "UseDevelopmentStorage=true";
            return new FeedPollStateStore(new TableClient(storage, FeedPollStateStore.TableName));
        });
        services.AddSingleton<FeedPoller>();
    })
    .Build();

host.Run();
