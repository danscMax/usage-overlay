using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageOverlay.Services;

internal sealed record PullRequestSnapshot(int? Number = null, string? State = null, int? Reviews = null, string? Error = null);
internal sealed record ResourceSnapshot(long? DiskFree = null, long? BuildBytes = null, bool BuildPartial = false,
    string? BuildPath = null, int? ChildProcesses = null, string? Error = null);

internal sealed class ProjectResources
{
    private string? _key;
    private DateTimeOffset _next;
    private ResourceSnapshot _resources = new();
    private PullRequestSnapshot _pullRequest = new();

    internal async Task<(ResourceSnapshot Resources, PullRequestSnapshot PullRequest)> ReadAsync(ProjectGitSnapshot? git, string? cwd, DateTimeOffset now)
    {
        var key = cwd + "|" + git?.RepositoryRoot + "|" + git?.Branch;
        if (key != _key || now >= _next)
        {
            _key = key;
            _next = now.AddSeconds(60);
            _resources = await Task.Run(() => ReadDisk(git?.RepositoryRoot ?? cwd));
            _pullRequest = git is { IsRepository: true, RepositoryRoot: { } root, Branch: { } branch }
                ? await ReadPullRequestAsync(root, branch) : new();
        }
        var processes = await Task.Run(ProcessAncestry.CountCodexDescendants);
        return (_resources with { ChildProcesses = processes }, _pullRequest);
    }

    internal static ResourceSnapshot ReadDisk(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return new(Error: "Project disk is unavailable");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
            var candidates = new[] { Path.Combine(directory, "target"), Path.Combine(directory, "src-tauri", "target") };
            // Existing shared Cargo targets are preferred to counting a worktree's empty target.
            var config = Path.Combine(directory, ".cargo", "config.toml");
            if (File.Exists(config))
            {
                foreach (var line in File.ReadLines(config).Take(256))
                {
                    var text = line.Trim();
                    if (!text.StartsWith("target-dir", StringComparison.Ordinal) || text.IndexOf('=') <= 0) continue;
                    var value = text[(text.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
                    if (value.Length > 0 && !value.Contains('#')) candidates = [Path.GetFullPath(value, directory)];
                }
            }
            var roots = candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            long size = 0;
            var partial = false;
            var timer = Stopwatch.StartNew();
            foreach (var root in roots)
            {
                var measurement = MeasureDirectory(root, timer);
                size += measurement.Bytes;
                partial |= measurement.Partial;
            }
            return new(drive.IsReady ? drive.AvailableFreeSpace : null, roots.Length > 0 ? size : null, partial,
                roots.Length > 0 ? string.Join("; ", roots) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(Error: "Project disk metrics are unavailable"); }
    }

    internal static (long Bytes, bool Partial) MeasureDirectory(string root, Stopwatch timer)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        long bytes = 0;
        var files = 0;
        var partial = false;
        while (pending.TryPop(out var path))
        {
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) { partial = true; continue; }
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    if (++files > 100_000 || timer.ElapsedMilliseconds > 750) return (bytes, true);
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { partial = true; continue; }
                    if (entry is DirectoryInfo child) pending.Push(child.FullName);
                    else if (entry is FileInfo file) bytes += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
        }
        return (bytes, partial);
    }

    internal static async Task<PullRequestSnapshot> ReadPullRequestAsync(string directory, string branch)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("gh")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var arg in new[] { "pr", "view", branch, "--json", "number,state,reviewRequests" }) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        try
        {
            if (!process.Start()) return new(Error: "PR state is unavailable");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); return new(Error: "PR request timed out"); }
            var message = await error;
            if (process.ExitCode != 0) return message.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
                ? new(State: "None", Reviews: 0) : new(Error: "PR state unavailable: check GitHub CLI sign-in and remote");
            using var doc = JsonDocument.Parse(await output);
            var root = doc.RootElement;
            return new(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString(),
                root.GetProperty("reviewRequests").GetArrayLength());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException)
        { return new(Error: "PR state is unavailable"); }
    }
}
