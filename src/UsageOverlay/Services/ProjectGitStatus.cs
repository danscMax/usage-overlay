using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Localization = UsageOverlay.Infrastructure.Localization;

namespace UsageOverlay.Services;

internal sealed record SelectedProject(string Id, string Name, string Root);
internal sealed record ProjectGitSnapshot(SelectedProject Project, int? Branches, DateTimeOffset? LastCommit, bool IsRepository, string? RepositoryRoot = null, bool MultipleRepositories = false,
    string? Branch = null, int? Ahead = null, int? Behind = null, int? ChangedFiles = null, int? UntrackedFiles = null,
    int Conflicts = 0, bool HasUpstream = false, int? LocalOnly = null, string? Error = null,
    int? Stashes = null, DateTimeOffset? OldestStash = null, int? Worktrees = null, string? ActiveWorktree = null);

internal static class ProjectGitStatus
{
    internal static SelectedProject? ReadSelectedProject(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var selection = root.GetProperty("selected-project");
            if (selection.GetProperty("type").GetString() != "local") return null;
            var id = selection.GetProperty("projectId").GetString();
            if (string.IsNullOrWhiteSpace(id)) return null;
            var project = root.GetProperty("local-projects").GetProperty(id);
            var roots = project.GetProperty("rootPaths");
            if (roots.ValueKind != JsonValueKind.Array || roots.GetArrayLength() != 1) return null;
            var directory = roots[0].GetString();
            var name = project.GetProperty("name").GetString();
            return string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name)
                ? null : new SelectedProject(id, name, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static async Task<ProjectGitSnapshot> ReadAsync(SelectedProject project)
    {
        var (repository, multiple) = await Task.Run(() => FindRepository(project.Root));
        if (repository is null) return new(project, null, null, false, MultipleRepositories: multiple);
        var branches = await RunGitAsync(repository, "for-each-ref", "--format=.", "refs/heads");
        var commit = await RunGitAsync(repository, "log", "-1", "--format=%ct");
        var status = await RunGitAsync(repository, "status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z");
        int? count = branches is null ? null : branches.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        DateTimeOffset? time = null;
        if (long.TryParse(commit?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            try { time = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        var snapshot = new ProjectGitSnapshot(project, count, time, true, repository, multiple);
        if (status is null) return snapshot with { Error = "Git status is unavailable" };
        snapshot = ParseStatus(snapshot, status);
        var stash = await RunGitAsync(repository, "stash", "list", "--format=%ct");
        var trees = await RunGitAsync(repository, "worktree", "list", "--porcelain", "-z");
        var stamps = stash?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => long.TryParse(line.Trim(), CultureInfo.InvariantCulture, out var stamp) ? stamp : (long?)null).ToArray();
        snapshot = snapshot with
        {
            Stashes = stamps?.Length,
            OldestStash = stamps is { Length: > 0 } && stamps.All(stamp => stamp is >= 0 and < 253402300800)
                ? DateTimeOffset.FromUnixTimeSeconds(stamps.Min()!.Value) : null,
            Worktrees = trees?.Split('\0').Count(record => record.StartsWith("worktree ", StringComparison.Ordinal)),
            ActiveWorktree = Path.GetFileName(repository.TrimEnd('\\', '/'))
        };
        if (!snapshot.HasUpstream && snapshot.Branch is not null and not "(detached)" && time is not null)
        {
            var local = await RunGitAsync(repository, "rev-list", "--count", "HEAD", "--not", "--remotes");
            snapshot = snapshot with { LocalOnly = int.TryParse(local?.Trim(), CultureInfo.InvariantCulture, out var value) ? value : null };
        }
        return snapshot;
    }

    internal static ProjectGitSnapshot ParseStatus(ProjectGitSnapshot snapshot, string status)
    {
        var records = status.Split('\0');
        var changed = 0;
        var untracked = 0;
        var conflicts = 0;
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record == "# branch.oid (initial)") snapshot = snapshot with { LocalOnly = 0 };
            else if (record.StartsWith("# branch.head ", StringComparison.Ordinal)) snapshot = snapshot with { Branch = record[14..] };
            else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal)) snapshot = snapshot with { HasUpstream = true };
            else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var counts = record[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (counts.Length == 2 && int.TryParse(counts[0].TrimStart('+'), CultureInfo.InvariantCulture, out var ahead) &&
                    int.TryParse(counts[1].TrimStart('-'), CultureInfo.InvariantCulture, out var behind))
                    snapshot = snapshot with { Ahead = ahead, Behind = behind };
            }
            else if (record.StartsWith("? ", StringComparison.Ordinal)) untracked++;
            else if (record.StartsWith("1 ", StringComparison.Ordinal)) changed++;
            // A rename's second NUL-delimited path can resemble a status record.
            else if (record.StartsWith("2 ", StringComparison.Ordinal)) { changed++; index++; }
            else if (record.StartsWith("u ", StringComparison.Ordinal)) { changed++; conflicts++; }
        }
        return snapshot with { ChangedFiles = changed, UntrackedFiles = untracked, Conflicts = conflicts };
    }

    private static (string? Repository, bool Multiple) FindRepository(string root)
    {
        static bool HasGit(string path) => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
        try
        {
            if (!Path.IsPathFullyQualified(root)) return (null, false);
            if (HasGit(root)) return (root, false);
            for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
                if (HasGit(parent.FullName)) return (parent.FullName, false);
            var children = new DirectoryInfo(root).EnumerateDirectories()
                .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint) && HasGit(child.FullName))
                .Select(child => child.FullName).ToArray();
            if (children.Length == 1) return (children[0], false);
            var app = children.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "app", StringComparison.OrdinalIgnoreCase));
            return (app, children.Length > 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (null, false); }
    }

    internal static async Task<string?> RunGitAsync(string directory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(directory);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); return null; }
            await error;
            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static string Format(ProjectGitSnapshot snapshot, DateTimeOffset now)
    {
        var prefix = Localization.Format("Project: {0} (root)", snapshot.Project.Name);
        if (!snapshot.IsRepository) return prefix + "\n" + Localization.Text(snapshot.MultipleRepositories ? "Multiple Git repositories found" : "Git repository not found");
        if (snapshot.RepositoryRoot is { } root && !string.Equals(root, snapshot.Project.Root, StringComparison.OrdinalIgnoreCase))
            prefix += "\n" + Localization.Format("Repository: {0}", Path.GetFileName(root)) + (snapshot.MultipleRepositories ? Localization.Text(" (of several)") : "");
        var branches = snapshot.Branches?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var age = "—";
        if (snapshot.LastCommit is { } commit)
        {
            var elapsed = now - commit;
            age = elapsed < TimeSpan.Zero ? Localization.Text("in the future") : elapsed.TotalMinutes < 1 ? Localization.Text("<1 min") : elapsed.TotalHours < 1 ? Localization.Format("{0} min ago", (int)elapsed.TotalMinutes)
                : elapsed.TotalDays < 1 ? Localization.Format("{0} h ago", (int)elapsed.TotalHours) : Localization.Format("{0} d ago", (int)elapsed.TotalDays);
        }
        return prefix + "\n" + Localization.Format("Branches: {0} · last commit: {1}", branches, age)
            + "\n" + FormatCompact(snapshot) + "\n" + Localization.Text("Remote comparison uses local refs; no automatic fetch")
            + (snapshot.Error is null ? string.Empty : "\n" + Localization.Text(snapshot.Error))
            + (snapshot.Conflicts == 0 ? string.Empty : "\n" + Localization.Format("Merge conflicts: {0}", snapshot.Conflicts));
    }

    internal static string FormatCompact(ProjectGitSnapshot snapshot)
    {
        if (!snapshot.IsRepository) return Localization.Text(snapshot.MultipleRepositories ? "Multiple Git repositories found" : "Git repository not found");
        static string Number(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var branch = snapshot.Branch ?? "—";
        var comparison = snapshot.HasUpstream
            ? $"↑{Number(snapshot.Ahead)} ↓{Number(snapshot.Behind)}"
            : Localization.Format("↑{0} local · no upstream", Number(snapshot.LocalOnly));
        return $"{branch}  {comparison}  " + Localization.Format("changed {0} · new {1}", Number(snapshot.ChangedFiles), Number(snapshot.UntrackedFiles));
    }
}
