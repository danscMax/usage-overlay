using System.Globalization;
using System.IO;
using System.Text.Json;
using UsageOverlay.Infrastructure;

namespace UsageOverlay.Services;

internal sealed record EnvironmentNotice(string Text, bool IsProblem = false);

internal static class EnvironmentDiagnostics
{
    internal static IReadOnlyList<EnvironmentNotice> Read(SelectedProject? project, DateTimeOffset now, IEnumerable<string>? cacheDirectories = null)
    {
        var notices = new List<EnvironmentNotice>();
        var directories = (cacheDirectories ?? new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp") })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var directory in directories)
        {
            try { files.AddRange(Directory.EnumerateFiles(directory, "claude-statusline-*.json")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var mcpFresh = false;
        var infraFresh = false;
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var mcp = name.StartsWith("claude-statusline-mcp-", StringComparison.Ordinal);
            var infra = name.StartsWith("claude-statusline-infra-", StringComparison.Ordinal);
            if (!mcp && !infra) continue;
            if (infra && (project is null || !new[] { project.Name, Path.GetFileName(project.Root.TrimEnd('\\', '/')), "none" }
                .Contains(name[24..], StringComparer.OrdinalIgnoreCase))) continue;
            try
            {
                var info = new FileInfo(path);
                var age = now - new DateTimeOffset(info.LastWriteTimeUtc);
                if (info.Length > 262_144 || age < TimeSpan.FromMinutes(-1) || age > TimeSpan.FromMinutes(mcp ? 5 : 2)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var time = info.LastWriteTime.ToString("HH:mm", CultureInfo.CurrentCulture);
                if (mcp)
                {
                    if (Count(root, "total") is not > 0 || Count(root, "failed") is not { } failed ||
                        Count(root, "warn") is not { } warn || Count(root, "auth") is not { } auth) continue;
                    mcpFresh = true;
                    var source = $"MCP/statusline ({Clean(name[22..])}, {time})";
                    if (failed > 0) notices.Add(new(source + ": " + Localization.Format("connection failures: {0}", Names(root, "names", failed)), true));
                    if (warn > 0) notices.Add(new(source + ": " + Localization.Format("tool failures: {0}", Names(root, "warn_names", warn)), true));
                    if (auth > 0) notices.Add(new(source + ": " + Localization.Format("sign-in required: {0}", Names(root, "auth_names", auth)), true));
                    if (failed + warn + auth == 0) notices.Add(new(source + ": " + Localization.Text("no reported errors")));
                }
                else
                {
                    var hasHost = false;
                    foreach (var (key, label) in new[] { ("local", "local_name"), ("peer", "peer_name") })
                    {
                        if (!root.TryGetProperty(key, out var host) || host.ValueKind != JsonValueKind.Object) continue;
                        hasHost = true;
                        var source = (root.TryGetProperty(label, out var value) && value.ValueKind == JsonValueKind.String ? Clean(value.GetString()!) : key) + $" ({time})";
                        if (Count(host, "dockerUnhealthy") is > 0 and var unhealthy)
                            notices.Add(new(source + ": " + Localization.Format("unhealthy containers: {0}", unhealthy), true));
                        if (host.TryGetProperty("sync", out var sync) && sync.ValueKind == JsonValueKind.False)
                            notices.Add(new(source + ": " + Localization.Text("Syncthing is unavailable"), true));
                        if (Count(host, "mcpOrphan") is > 6 || Count(host, "nodeTotal") is > 130)
                            notices.Add(new(source + ": " + Localization.Format("MCP orphan processes / Node: {0}/{1}", Count(host, "mcpOrphan")?.ToString(CultureInfo.InvariantCulture) ?? "—", Count(host, "nodeTotal")?.ToString(CultureInfo.InvariantCulture) ?? "—"), true));
                    }
                    infraFresh |= hasHost;
                    if (hasHost) notices.Add(new(Localization.Format("Infrastructure cache updated at {0}", time)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (!mcpFresh) notices.Add(new(Localization.Text("MCP/statusline: no fresh data")));
        if (!infraFresh) notices.Add(new(Localization.Text("Infrastructure: no fresh data")));
        return notices.OrderByDescending(notice => notice.IsProblem).ToArray();
    }

    private static int? Count(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 0 and <= 1_000_000 ? number : null;

    private static string Names(JsonElement root, string key, int count)
    {
        if (!root.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array) return count.ToString(CultureInfo.InvariantCulture);
        var names = values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Take(2).Select(value => Clean(value.GetString()!)).ToArray();
        return names.Length == 0 ? count.ToString(CultureInfo.InvariantCulture) : string.Join(", ", names) + (count > names.Length ? $" +{count - names.Length}" : "");
    }

    private static string Clean(string text) => new(text.Where(character => !char.IsControl(character)).Take(50).ToArray());
}
