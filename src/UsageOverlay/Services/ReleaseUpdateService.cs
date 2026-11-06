using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using UsageOverlay.Infrastructure;

namespace UsageOverlay.Services;

public sealed record ReleaseCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    Uri ReleaseUri,
    bool HasPublishedRelease = true);

public sealed class ReleaseUpdateService : IDisposable
{
    internal static readonly Uri LatestReleaseApi =
        new("https://api.github.com/repos/danscMax/usage-overlay/releases/latest");
    internal static readonly Uri ReleasesPage = new("https://github.com/danscMax/usage-overlay/releases");

    private readonly HttpClient _httpClient;

    public ReleaseUpdateService() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }) { }

    internal ReleaseUpdateService(HttpClient client)
    {
        _httpClient = client;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("UsageOverlay", AppVersion.Current));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApi, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(false, AppVersion.Current, AppVersion.Current, ReleasesPage, HasPublishedRelease: false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ??
                  throw new InvalidOperationException("The latest release has no version tag.");
        var releaseUrl = root.GetProperty("html_url").GetString() ??
                         throw new InvalidOperationException("The latest release has no web address.");
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri) || releaseUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(releaseUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !releaseUri.IsDefaultPort
            || releaseUri.UserInfo.Length > 0 || !releaseUri.AbsolutePath.StartsWith("/danscMax/usage-overlay/releases/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update link does not belong to this fork's GitHub releases.");
        var latestVersion = tag.Trim().TrimStart('v', 'V');
        var currentVersion = AppVersion.Current.Trim().TrimStart('v', 'V');

        if (!Version.TryParse(currentVersion, out var current) ||
            !Version.TryParse(latestVersion, out var latest))
        {
            throw new InvalidOperationException("The release version could not be compared.");
        }

        return new ReleaseCheckResult(
            latest > current,
            currentVersion,
            latestVersion,
            releaseUri);
    }

    public void Dispose() => _httpClient.Dispose();
}
