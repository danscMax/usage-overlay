using System.Diagnostics;
using System.Text.Json;
using CodexUsage.Core.Models;
using CodexUsage.Core.Settings;
using UsageOverlay.Services;

internal static class StatusbarSpecs
{
    internal static readonly string[] PresentationLayouts = ["Vertical", "HorizontalAbove", "HorizontalBelow", "HorizontalNarrow", "HorizontalTiny"];
    private static readonly string[] FixtureIdentity = ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid"];
    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        Check(UsageBudget.ForReset(new(40, 10_080, now.AddDays(5)), now) == UsageBudgetLevel.Normal, "12 points/day is green");
        Check(UsageBudget.ForReset(new(60, 10_080, now.AddDays(8)), now) == UsageBudgetLevel.Warning, "5 points/day is yellow");
        Check(UsageBudget.ForReset(new(80, 10_080, now.AddDays(10)), now) == UsageBudgetLevel.Tight, "2 points/day is orange");
        Check(UsageBudget.ForReset(new(90, 10_080, now.AddDays(10)), now) == UsageBudgetLevel.Critical, "A smaller budget is red");
        Check(UsageBudget.ForReset(new(29, 10_080, now.AddDays(100)), now) == UsageBudgetLevel.Normal, "Under 30 percent stays green like statusline");
        Check(UsageBudget.ForReset(new(90, 10_080, null), now) is null && UsageBudget.ForReset(new(90, 10_080, now), now) is null, "Missing or elapsed reset has no invented budget");
        Check(UsageBudget.ForReset(new(double.NaN, 10_080, now.AddDays(1)), now) is null, "Invalid percentage is unavailable");
        Check(UsageBudget.ForReset(new(50, 300, now.AddHours(1)), now) == UsageBudgetLevel.Warning &&
              UsageBudget.ForReset(new(80, 300, now.AddHours(1)), now) == UsageBudgetLevel.Critical, "Short window colors match 50/80 boundaries");
        var settings = OverlaySettings.FromJson(JsonSerializer.Serialize(new OverlaySettings { Orientation = RailOrientation.Horizontal, DetailsPosition = DetailsPlacement.Below }));
        Check(settings.Orientation == RailOrientation.Horizontal && settings.DetailsPosition == DetailsPlacement.Below, "Layout settings round-trip");
        Check(OverlaySettings.FromJson("{}").Orientation == RailOrientation.Vertical &&
              new OverlaySettings { Orientation = (RailOrientation)999, DetailsPosition = (DetailsPlacement)999 }.Normalize().DetailsPosition == DetailsPlacement.Above, "Legacy and invalid layout settings are normalized");

        var directory = Path.Combine(Path.GetTempPath(), "usage-overlay-statusbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new SelectedProject("p", "sample", Path.Combine(directory, "sample"));
            var mcp = Path.Combine(directory, "claude-statusline-mcp-test.json");
            File.WriteAllText(mcp, """{"total":4,"failed":1,"warn":1,"auth":1,"names":["offline"],"warn_names":["tools"],"auth_names":["login"]}""");
            var infra = Path.Combine(directory, "claude-statusline-infra-sample.json");
            File.WriteAllText(infra, """{"local_name":"Main","local":{"dockerUnhealthy":2,"sync":false,"mcpOrphan":7,"nodeTotal":140},"peer":null}""");
            var notices = EnvironmentDiagnostics.Read(project, now, [directory]);
            Check(notices.Count(notice => notice.IsProblem) == 6, "Reads MCP failures, auth, containers, sync and orphan processes from fresh caches");
            File.SetLastWriteTimeUtc(mcp, now.AddMinutes(-6).UtcDateTime);
            File.SetLastWriteTimeUtc(infra, now.AddMinutes(-3).UtcDateTime);
            Check(EnvironmentDiagnostics.Read(project, now, [directory]).All(notice => !notice.IsProblem), "Stale cache failures are never presented as current");
            File.WriteAllText(mcp, """{"total":"invalid","failed":1}""");
            Check(EnvironmentDiagnostics.Read(project, now, [directory]).All(notice => !notice.IsProblem), "Malformed cache values remain unavailable");
            File.WriteAllText(infra, """{"local":{"dockerUnhealthy":2}}""");
            Check(EnvironmentDiagnostics.Read(project with { Name = "other", Root = Path.Combine(directory, "other") }, now, [directory]).All(notice => !notice.IsProblem), "Does not attribute another project's infrastructure errors");

            var empty = Path.Combine(directory, "empty");
            await Git(directory, "init", "--initial-branch=main", empty);
            var emptyStatus = await ProjectGitStatus.ReadAsync(project with { Root = empty });
            Check(emptyStatus.LocalOnly == 0 && emptyStatus.ChangedFiles == 0, "An unborn branch has zero local commits");

            var local = Path.Combine(directory, "local");
            await Git(directory, "init", "--initial-branch=main", local);
            File.WriteAllText(Path.Combine(local, "seed.txt"), "seed");
            await Git(local, "add", ".");
            await Git(local, "commit", "-m", "Seed");
            var remote = Path.Combine(directory, "remote.git");
            await Git(directory, "clone", "--bare", local, remote);
            await Git(local, "remote", "add", "origin", remote);
            await Git(local, "fetch", "origin");
            await Git(local, "branch", "--set-upstream-to=origin/main");
            File.WriteAllText(Path.Combine(local, "local.txt"), "local");
            await Git(local, "add", ".");
            await Git(local, "commit", "-m", "Local change");
            var peer = Path.Combine(directory, "peer");
            await Git(directory, "clone", remote, peer);
            File.WriteAllText(Path.Combine(peer, "peer.txt"), "peer");
            await Git(peer, "add", ".");
            await Git(peer, "commit", "-m", "Peer change");
            await Git(peer, "push", "origin", "main");
            await Git(local, "fetch", "origin");
            await Git(local, "mv", "seed.txt", "renamed.txt");
            File.AppendAllText(Path.Combine(local, "local.txt"), "modified");
            File.WriteAllText(Path.Combine(local, "untracked one.txt"), "new");
            File.WriteAllText(Path.Combine(local, "untracked two.txt"), "new");
            var snapshot = await ProjectGitStatus.ReadAsync(project with { Root = local });
            Check(snapshot.HasUpstream && snapshot.Ahead == 1 && snapshot.Behind == 1, "Reads divergence against local remote refs");
            Check(snapshot.ChangedFiles == 2 && snapshot.UntrackedFiles == 2, "Counts a rename once, a modification once, and individual new files");
            await Git(local, "stash", "push", "--include-untracked", "-m", "Fixture stash");
            var stashed = await ProjectGitStatus.ReadAsync(project with { Root = local });
            Check(stashed.Stashes == 1 && stashed.OldestStash is not null, "Stash count and oldest age come from the repository");
            await Git(local, "stash", "pop");
            var tree = Path.Combine(directory, "worktree");
            await Git(local, "worktree", "add", "-b", "fixture-worktree", tree);
            var nested = Path.Combine(local, "nested");
            Directory.CreateDirectory(nested);
            var worktree = await ProjectGitStatus.ReadAsync(project with { Root = nested });
            Check(worktree.Worktrees == 2 && worktree.RepositoryRoot == local && worktree.ActiveWorktree == "local", "Nested chat folders resolve their actual repository and worktree count");
            await Git(local, "branch", "--unset-upstream");
            snapshot = await ProjectGitStatus.ReadAsync(project with { Root = local });
            Check(!snapshot.HasUpstream && snapshot.Ahead is null && snapshot.Behind is null && snapshot.LocalOnly == 1, "No upstream keeps comparison unknown and counts unpublished commits");
            var parsed = ProjectGitStatus.ParseStatus(new(project, 1, now, true), "2 R. ignored new-name\0? old-name\0u UU conflict\0? actual-new\0");
            Check(parsed.ChangedFiles == 2 && parsed.Conflicts == 1 && parsed.UntrackedFiles == 1, "NUL parsing handles conflict and rename source that looks like a record");
            var conflict = Path.Combine(directory, "conflict");
            await Git(directory, "init", "--initial-branch=main", conflict);
            File.WriteAllText(Path.Combine(conflict, "same.txt"), "base\n");
            await Git(conflict, "add", ".");
            await Git(conflict, "commit", "-m", "Base");
            await Git(conflict, "checkout", "-b", "other");
            File.WriteAllText(Path.Combine(conflict, "same.txt"), "other\n");
            await Git(conflict, "commit", "-am", "Other edit");
            await Git(conflict, "checkout", "main");
            File.WriteAllText(Path.Combine(conflict, "same.txt"), "main\n");
            await Git(conflict, "commit", "-am", "Main edit");
            await Git(conflict, "merge", "other");
            Check((await ProjectGitStatus.ReadAsync(project with { Root = conflict })).Conflicts == 1, "Reads an actual unresolved merge conflict");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("All statusbar checks passed.");
    }

    private static async Task Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in FixtureIdentity.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git fixture");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        await output;
        var message = await error;
        if (process.ExitCode != 0 && !(arguments[0] == "merge" && process.ExitCode == 1))
            throw new InvalidOperationException("Git fixture failed: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
