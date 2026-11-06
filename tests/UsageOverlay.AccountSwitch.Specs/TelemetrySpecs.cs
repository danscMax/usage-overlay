using System.Diagnostics;
using System.Text.Json;
using CodexStatusbar;
using Microsoft.Data.Sqlite;
using UsageOverlay.Services;

internal static class TelemetrySpecs
{
    internal static void Run()
    {
        var state = new RolloutStreamState();
        state.ObserveLine("""{"timestamp":"2026-11-06T08:00:00Z","type":"event_msg","payload":{"type":"task_started","turn_id":"a"}}""");
        state.ObserveLine(Usage(900000, 110000));
        Check(state.ContextUsedTokens == 110000 && state.TotalTokens == 900000, "Context uses the last request, not cumulative thread tokens");
        Check(state.HasContextUsage && state.Streaming && state.TurnStartedAt is not null, "Task start and context availability are explicit");
        state.ObserveLine(Usage(900000, 110000));
        Check(state.TotalTokens == 900000 && state.DuplicateUsageReadingsIgnored > 0, "Repeated cumulative samples cannot double-count tokens");
        state.ObserveLine("""{"type":"compacted","payload":{}}""");
        Check(!state.HasContextUsage, "Compaction invalidates context until the next measured request");
        state.ObserveLine(Usage(950000, 30000));
        Check(state.HasContextUsage && state.ContextUsedTokens == 30000, "Context can shrink after compaction");
        state.ObserveLine("""{"type":"token_usage_record","payload":{"usage":{"input_tokens":39000,"output_tokens":1000,"total_tokens":40000},"thread_token_usage":{"input_tokens":990000,"output_tokens":10000,"total_tokens":1000000}}}""");
        Check(state.ContextUsedTokens == 40000 && state.TotalTokens == 1000000, "New token_usage_record carrier separates current request and thread totals");
        state.ObserveLine("""{"type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{\"cmd\":\"dotnet test\"}"}}""");
        Check(state.Action == "Testing", "Current action is classified without exposing command text");
        state.ObserveLine("""{"type":"event_msg","payload":{"type":"item_started","item":[]}}""");
        state.ObserveLine("""{"type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"[]"}}""");
        state.ObserveLine("""{"type":"event_msg","payload":{"type":"turn_aborted"}}""");
        Check(!state.Streaming && state.Action == "Interrupted", "Aborted tasks stop the live duration state");
        Check(new SessionSnapshot().ContextFree is null && new SessionSnapshot(Input: 0, Cached: 0).CachePercent is null,
            "Unknown context and empty cache denominator stay unknown");
        Check(new SessionSnapshot(ContextUsed: 300, ContextWindow: 200).ContextFree == 0 && new SessionSnapshot(Input: 10, Cached: 20).CachePercent is null,
            "Overflowing context is bounded and invalid cache ratios stay unknown");
        var tps = new TpsTracker();
        tps.BeginTurn("t", true, 0, true);
        tps.ObserveModelOutputItem(1000, 2000);
        tps.ObserveModelOutputItem(1500, 2500);
        tps.ObserveResponseOutput("r", 150);
        tps.ObserveResponseOutput("r", 150);
        tps.RefreshMeasurement();
        Check(tps.MergedModelElapsedMs == 1500 && tps.DisplayTps == 100, "Generation speed unions overlapping model windows and deduplicates output");
        var processes = new Dictionary<int, (int Parent, string Name)>
        { [1] = (0, "root"), [2] = (1, "child"), [3] = (2, "tool"), [4] = (99, "other") };
        Check(ProcessAncestry.CountDescendants(processes, 1) == 2, "Process count includes descendants and excludes unrelated processes");
        Check(SessionTelemetry.NormalizePath(@"\\?\E:\Scripts\project") == @"E:\Scripts\project" && SessionTelemetry.NormalizePath(@"\\?\UNC\host\share") == @"\\host\share", "Windows extended paths normalize for Git and drive metrics");

        var directory = Path.Combine(Path.GetTempPath(), "usage-overlay-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "a.jsonl");
            var second = Path.Combine(directory, "b.jsonl");
            var child = Path.Combine(directory, "child.jsonl");
            File.WriteAllText(first, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"a\"}}\n" + Usage(900000, 110000) + "\n");
            File.WriteAllText(second, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"b\"}}\n");
            File.WriteAllText(child, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "state_5.sqlite"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE threads (id TEXT, rollout_path TEXT, cwd TEXT, title TEXT); CREATE TABLE thread_spawn_edges (parent_thread_id TEXT, child_thread_id TEXT, status TEXT); INSERT INTO threads VALUES ('a', $a, $cwd, 'A'), ('b', $b, $cwd, 'B'), ('child', $child, $cwd, 'Child'); INSERT INTO thread_spawn_edges VALUES ('a','child','open')";
                command.Parameters.AddWithValue("$a", first);
                command.Parameters.AddWithValue("$b", second);
                command.Parameters.AddWithValue("$child", child);
                command.Parameters.AddWithValue("$cwd", directory);
                command.ExecuteNonQuery();
            }
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "queue_1.sqlite"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE queued_items(thread_id TEXT); INSERT INTO queued_items VALUES ('a'),('a'),('b')";
                command.ExecuteNonQuery();
            }
            using var service = new SessionTelemetry(directory);
            var a = service.ReadThread("a", DateTimeOffset.UtcNow);
            Check(a.ContextUsed == 110000 && a.Input == 880000 && a.Agents == 1 && a.Queued == 2, "Read-only session snapshot joins the correct log, direct agents and chat queue");
            var b = service.ReadThread("b", DateTimeOffset.UtcNow);
            Check(b.ContextUsed is null && b.Input is null && b.Agents == 0 && b.Queued == 1, "Changing chats clears previous context, tokens and agents");
            Check(SessionTelemetry.ReadQueued(directory, "a' OR 1=1 --") == 0, "Queue lookup is parameterized");
            Check(!SessionTelemetry.IsProfileRollout(directory, Path.Combine(directory, "..", "outside.jsonl")), "Log path cannot escape its profile");
            File.AppendAllText(child, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n");
            Check(SessionTelemetry.ReadAgentWorking(child) == false && SessionTelemetry.ReadAgentWorking(second) is null,
                "Open agent channels do not imply active tasks; missing lifecycle stays unknown");
            var target = Path.Combine(directory, "target");
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, "cache"), new byte[25]);
            var resource = ProjectResources.ReadDisk(directory);
            Check(resource.BuildBytes == 25 && resource.DiskFree > 0 && !resource.BuildPartial, "Disk and bounded Cargo cache use the project drive");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("All telemetry checks passed.");
    }

    private static string Usage(long total, long last) => JsonSerializer.Serialize(new
    {
        type = "event_msg", payload = new
        {
            type = "token_count", info = new
            {
                model_context_window = 258000,
                total_token_usage = new { total_tokens = total, input_tokens = total - 20000, cached_input_tokens = 800000, output_tokens = 20000 },
                last_token_usage = new { total_tokens = last, input_tokens = last - 1000, output_tokens = 1000 }
            }
        }
    });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
