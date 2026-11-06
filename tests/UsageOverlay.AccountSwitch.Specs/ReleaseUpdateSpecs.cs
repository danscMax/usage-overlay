using System.Net;
using System.Net.Http;
using System.Text.Json;
using UsageOverlay.Infrastructure;
using UsageOverlay.Services;

internal static class ReleaseUpdateSpecs
{
    internal static async Task RunAsync()
    {
        using (var service = Service(HttpStatusCode.NotFound))
        {
            var result = await service.CheckAsync();
            Check(!result.HasPublishedRelease && !result.IsUpdateAvailable
                && result.ReleaseUri == ReleaseUpdateService.ReleasesPage, "Unpublished fork is a normal empty release state");
        }
        foreach (var version in new[] { "99.0.0", AppVersion.Current, "0.0.1" })
        {
            using var service = Service(HttpStatusCode.OK, version);
            var result = await service.CheckAsync();
            Check(result.HasPublishedRelease && result.IsUpdateAvailable == (version == "99.0.0")
                && result.LatestVersion == version, "Compares fork version " + version);
        }
        foreach (var url in new[] {
            "https://github.com/haroonsulahri/usage-overlay/releases/tag/v99.0.0",
            "https://github.com/danscMax/other/releases/tag/v99.0.0",
            "https://github.com.evil.invalid/danscMax/usage-overlay/releases/tag/v99.0.0",
            "http://github.com/danscMax/usage-overlay/releases/tag/v99.0.0",
            "https://user@github.com/danscMax/usage-overlay/releases/tag/v99.0.0",
            "https://github.com:444/danscMax/usage-overlay/releases/tag/v99.0.0", "invalid" })
        {
            using var service = Service(HttpStatusCode.OK, "99.0.0", url);
            await Reject<InvalidOperationException>(service, "Rejects an update URL outside this fork: " + url);
        }
        using (var service = Service(HttpStatusCode.OK, "not-a-version"))
            await Reject<InvalidOperationException>(service, "Rejects an invalid release version");
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError })
        {
            using var service = Service(status);
            await Reject<HttpRequestException>(service, "Preserves GitHub errors: " + status);
        }
    }

    private static ReleaseUpdateService Service(HttpStatusCode status, string version = "99.0.0", string? url = null) =>
        new(new HttpClient(new Handler(status, JsonSerializer.Serialize(new {
            tag_name = "v" + version,
            html_url = url ?? "https://github.com/danscMax/usage-overlay/releases/tag/v" + version
        }))));

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri == ReleaseUpdateService.LatestReleaseApi
                && request.RequestUri.AbsolutePath == "/repos/danscMax/usage-overlay/releases/latest", "Queries only the fork's latest release API");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static async Task Reject<T>(ReleaseUpdateService service, string message) where T : Exception
    {
        try { await service.CheckAsync(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
