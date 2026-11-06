using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexStatusbar;
using Microsoft.Data.Sqlite;

namespace UsageOverlay.Services;

internal sealed record SessionSnapshot(string? ThreadId = null, string? Cwd = null, string? Title = null,
    long? ContextUsed = null, long? ContextWindow = null, long? Input = null, long? Cached = null, long? Output = null,
    double? TokensPerSecond = null, bool SpeedHeld = false, TimeSpan? Duration = null, bool Working = false,
    string? Action = null, int? Agents = null, int? Queued = null, DateTimeOffset? MeasuredAt = null, string? Error = null)
{
    public long? ContextFree => ContextUsed is { } used && ContextWindow is { } window ? Math.Max(0, window - used) : null;
    public double? ContextPercent => ContextUsed is { } used && ContextWindow is > 0 and var window ? Math.Clamp(100d * used / window, 0, 100) : null;
    public double? CachePercent => Input is > 0 and var input && Cached is >= 0 and var cached && cached <= input ? MetricFormat.CacheHitPercent(input, cached) : null;
}

internal sealed class SessionTelemetry : IDisposable
{
    private readonly CodexIpcActiveThreadMonitor _monitor = new();
    private readonly string _home;
    private RolloutReader? _reader;
    private string? _threadId;
    private int? _agents;
    private DateTimeOffset _nextAgents;

    internal SessionTelemetry(string? home = null) => _home = home ?? Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public void Start() => _monitor.Start();
    public void Dispose() => _monitor.Dispose();

    internal SessionSnapshot Read(DateTimeOffset now)
    {
        var route = _monitor.GetStatus();
        if (!route.IsConnected || string.IsNullOrEmpty(route.ThreadId) || route.ActiveWindowCount != 1)
        {
            _reader = null;
            _threadId = null;
            return new(Error: route.ActiveWindowCount > 1 ? "Several Codex windows: active chat is ambiguous" : "Waiting for the active Codex chat");
        }
        var snapshot = ReadThread(route.ThreadId, now);
        var current = _monitor.GetStatus();
        return current.IsConnected && current.ActiveWindowCount == 1 && current.ThreadId == route.ThreadId
            ? snapshot : new(Error: "Waiting for the active Codex chat");
    }

    internal SessionSnapshot ReadThread(string threadId, DateTimeOffset now)
    {
        if (_threadId != threadId)
        {
            _reader = null;
            _threadId = threadId;
            _agents = null;
            _nextAgents = default;
        }
        try
        {
            using var db = OpenDatabase(_home, "state");
            if (db is null) return new(threadId, Error: "Codex session database is unavailable");
            var thread = ReadThreadRow(db, threadId);
            if (thread is null) return new(threadId, Error: "Active chat has no local session yet");
            if (!IsProfileRollout(_home, thread.Value.Path)) return new(threadId, thread.Value.Cwd, Error: "Session log is outside the Codex profile");
            if (_reader?.Path != thread.Value.Path)
            {
                _reader = new RolloutReader(thread.Value.Path);
                _reader.Initialize();
            }
            else if (_reader.TryUpdate() == RolloutUpdateResult.NeedsReinitialize) _reader.Initialize();
            var state = _reader.State;
            if (_reader.HeadIdentity is { } identity && identity != threadId)
                return new(threadId, thread.Value.Cwd, Error: "Session log identity does not match the active chat");
            if (now >= _nextAgents)
            {
                _agents = ReadActiveAgents(db, threadId, _home);
                _nextAgents = now.AddSeconds(5);
            }
            var duration = state.Streaming && state.TurnStartedAt is { } started ? now - started
                : state.Tps.LastTurnDurationMs is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds)
                : state.TurnFinishedAt is { } finished && state.TurnStartedAt is { } began ? finished - began : (TimeSpan?)null;
            return new(threadId, thread.Value.Cwd, thread.Value.Title,
                state.HasContextUsage ? state.ContextUsedTokens : null, state.ContextWindowTokens > 0 ? state.ContextWindowTokens : null,
                state.HasCumulativeUsage ? state.InputTokens : null, state.HasCumulativeUsage ? state.CachedInputTokens : null,
                state.HasCumulativeUsage ? state.OutputTokens : null, state.Tps.DisplayTps, state.Tps.IsHeld,
                duration is { } elapsed && elapsed >= TimeSpan.Zero ? elapsed : null, state.Streaming, state.Action,
                _agents, ReadQueued(_home, threadId), state.LastRecordAt,
                File.Exists(thread.Value.Path) ? null : "Session log is unavailable");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new(threadId, Error: "Local session metrics are unavailable");
        }
    }

    internal static SqliteConnection? OpenDatabase(string home, string name)
    {
        var path = Directory.EnumerateFiles(home, name + "_*.sqlite").Select(path => (Path: path,
                Version: int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(name.Length + 1), CultureInfo.InvariantCulture, out var version) ? version : -1))
            .Where(entry => entry.Version >= 0).OrderByDescending(entry => entry.Version).Select(entry => entry.Path).FirstOrDefault();
        if (path is null) return null;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 1 }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static (string Path, string Cwd, string Title)? ReadThreadRow(SqliteConnection db, string id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT rollout_path, cwd, title FROM threads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var row = command.ExecuteReader();
        return row.Read() && !row.IsDBNull(0) && !row.IsDBNull(1) ? (NormalizePath(row.GetString(0)), NormalizePath(row.GetString(1)), row.IsDBNull(2) ? "" : row.GetString(2)) : null;
    }

    internal static string NormalizePath(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    internal static bool IsProfileRollout(string home, string path)
    {
        var root = Path.GetFullPath(NormalizePath(home)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(NormalizePath(path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(full), ".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    internal static int? ReadQueued(string home, string threadId)
    {
        try
        {
            using var db = OpenDatabase(home, "queue");
            if (db is null) return null;
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM queued_items WHERE thread_id = $id";
            command.Parameters.AddWithValue("$id", threadId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static int? ReadActiveAgents(SqliteConnection db, string threadId, string home)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT t.rollout_path FROM thread_spawn_edges e LEFT JOIN threads t ON t.id = e.child_thread_id WHERE e.parent_thread_id = $id AND e.status = 'open' LIMIT 33";
        command.Parameters.AddWithValue("$id", threadId);
        using var rows = command.ExecuteReader();
        var count = 0;
        var scanned = 0;
        while (rows.Read())
        {
            if (++scanned > 32 || rows.IsDBNull(0)) return null;
            var path = rows.GetString(0);
            if (!IsProfileRollout(home, path) || ReadAgentWorking(path) is not { } working) return null;
            if (working) count++;
        }
        return count;
    }

    internal static bool? ReadAgentWorking(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 256 * 1024);
            stream.Position = start;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (start > 0) _ = reader.ReadLine();
            bool? working = null;
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "event_msg"
                        || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                        || !payload.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
                    if (kind.GetString() == "task_started") working = true;
                    else if (kind.GetString() is "task_complete" or "turn_aborted") working = false;
                }
                catch (JsonException) { }
            }
            return working;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
