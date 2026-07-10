using BondCasts.Api.Rendering;
using BondCasts.Api.Services;
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
    })
    .Build();

host.Run();
