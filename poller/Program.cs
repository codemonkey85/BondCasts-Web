using Azure.Data.Tables;
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
