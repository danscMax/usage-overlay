using System.Collections.Concurrent;
using System.Text.Json;
using UsageOverlay.Infrastructure;
using UsageOverlay.Services;
using CodexUsage.Core.Models;
using CodexUsage.Core.Protocol;
using CodexUsage.Core.Providers;
using UsageOverlay;
using CodexUsage.Core.Settings;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Localization = UsageOverlay.Infrastructure.Localization;

if (args.Contains("--fake"))
{
    var auth = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")!, "auth.json");
    var account = File.Exists(auth) ? File.ReadAllText(auth) : null;
    while (Console.ReadLine() is { } line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var requestId)) continue;
        var id = requestId.GetInt32();
        var method = root.GetProperty("method").GetString();
        if (method == "initialize")
            Reply(new { id, result = new { } });
        else if (method == "account/read")
        {
            if (root.GetProperty("params").GetProperty("refreshToken").GetBoolean())
                throw new InvalidOperationException("Routine polling forced a token refresh.");
            Reply(new { id, result = new { account = account is null ? null : new { type = "chatgpt", email = account } } });
        }
        else if (method == "account/rateLimits/read")
        {
            var percent = account == "A" ? 70 : account == "B" ? 20 : 40;
            Reply(new { id, result = new { rateLimits = new { limitId = "codex", primary = new { usedPercent = percent, windowDurationMins = 300, resetsAt = 1900000000 } } } });
            // A delayed reply from an invalidated/unknown request must not repaint usage.
            Reply(new { id = -1, result = new { rateLimits = new { limitId = "codex", primary = new { usedPercent = 99, windowDurationMins = 300, resetsAt = 1900000000 } } } });
        }
    }
    return;
}

if (args.Length == 1 && args[0] == "--probe-route")
{
    using var monitor = new CodexStatusbar.CodexIpcActiveThreadMonitor();
    monitor.Start();
    await Task.Delay(3000);
    Console.WriteLine(JsonSerializer.Serialize(monitor.GetStatus()));
    return;
}

if (args.Length == 1 && args[0] == "--probe-session")
{
    using var service = new SessionTelemetry();
    service.Start();
    await Task.Delay(3000);
    var snapshot = service.Read(DateTimeOffset.UtcNow);
    Console.WriteLine(JsonSerializer.Serialize(snapshot with { Title = null }));
    Console.WriteLine(JsonSerializer.Serialize(new { Processes = ProcessAncestry.CountCodexDescendants() }));
    return;
}

if (args.Length == 1 && args[0] == "--check-metrics")
{
    await StatusbarSpecs.RunAsync();
    TelemetrySpecs.Run();
    BarPlacementSpecs.Run();
    return;
}

if (args.Length == 1 && args[0] == "--probe-statusbar")
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", ".codex-global-state.json");
    var selected = ProjectGitStatus.ReadSelectedProject(path);
    var snapshot = selected is null ? null : await ProjectGitStatus.ReadAsync(selected);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Git = snapshot, Environment = EnvironmentDiagnostics.Read(selected, DateTimeOffset.Now) }));
    return;
}

if (args.Length == 2 && args[0] == "--render-presentation")
{
    RenderPresentation(args[1]);
    return;
}

SettingsPersistenceSpecs.Run();
LanguageSpecs.Run();
await ReleaseUpdateSpecs.RunAsync();
await StatusbarSpecs.RunAsync();
TelemetrySpecs.Run();
BarPlacementSpecs.Run();
Localization.Language = AppLanguage.Russian;
var themeFile = Path.GetTempFileName();
try
{
    File.WriteAllText(themeFile, "[desktop.appearanceDarkChromeTheme]\nsurface = \"#0d1117\"\nink = \"#e6edf3\"\n[desktop.appearanceLightChromeTheme]\nsurface = \"#ffffff\"\nink = \"#111111\"\n");
    var darkPalette = ThemeManager.ReadCodexPalette(themeFile, true);
    var lightPalette = ThemeManager.ReadCodexPalette(themeFile, false);
    Assert(darkPalette?.Surface == Color.FromRgb(13, 17, 23) && darkPalette?.Ink == Color.FromRgb(230, 237, 243),
        "Reads the configured dark Codex palette");
    Assert(lightPalette?.Surface == Colors.White && lightPalette?.Ink == Color.FromRgb(17, 17, 17),
        "Reads the configured light Codex palette");
    File.WriteAllText(themeFile, "[desktop.appearanceDarkChromeTheme]\nsurface = \"#000000\"\nink = \"#111111\"\n");
    Assert(ThemeManager.ReadCodexPalette(themeFile, true) is null, "Rejects unreadable low-contrast theme colors");
}
finally { File.Delete(themeFile); }
var projectStateFile = Path.GetTempFileName();
try
{
    var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    File.WriteAllText(projectStateFile, JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["selected-project"] = new { type = "local", projectId = "project-1" },
        ["local-projects"] = new Dictionary<string, object> { ["project-1"] = new { name = "Overlay", rootPaths = new[] { repository } } }
    }));
    var selectedProject = ProjectGitStatus.ReadSelectedProject(projectStateFile);
    Assert(selectedProject?.Root == repository, "Resolves the selected project's single root");
    var gitStatus = await ProjectGitStatus.ReadAsync(selectedProject!);
    Assert(gitStatus.IsRepository && gitStatus.Branches is >= 0 && gitStatus.LastCommit is not null,
        "Reads repository metadata in attached and detached checkouts");
    var futureCommit = gitStatus with { LastCommit = DateTimeOffset.Now.AddHours(1) };
    Assert(ProjectGitStatus.Format(futureCommit, DateTimeOffset.Now).Contains("в будущем", StringComparison.Ordinal),
        "Does not describe a future commit as recent");
    var notRepository = await ProjectGitStatus.ReadAsync(new SelectedProject("non-git", "Scripts", @"E:\Scripts"));
    Assert(!notRepository.IsRepository && notRepository.Branches is null && notRepository.LastCommit is null,
        "Does not invent Git values when no repository can be selected");
    var nestedFixture = Path.Combine(Path.GetTempPath(), "usage-overlay-project-spec-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(nestedFixture);
    try
    {
        Directory.CreateDirectory(Path.Combine(nestedFixture, "other", ".git"));
        Directory.CreateDirectory(Path.Combine(nestedFixture, "app", ".git"));
        var nested = await ProjectGitStatus.ReadAsync(new SelectedProject("nested", "Example", nestedFixture));
        Assert(nested.RepositoryRoot == Path.Combine(nestedFixture, "app") && nested.MultipleRepositories &&
               ProjectGitStatus.Format(nested, DateTimeOffset.Now).Contains("Репозиторий: app (из нескольких)", StringComparison.Ordinal),
            "Selects and labels the app repository among multiple nested repositories");
    }
    finally { Directory.Delete(nestedFixture, true); }
    File.WriteAllText(projectStateFile, "{invalid");
    Assert(ProjectGitStatus.ReadSelectedProject(projectStateFile) is null, "Ignores an incomplete global-state file");
    File.WriteAllText(projectStateFile, """{"selected-project":{"type":"local","projectId":"p"},"local-projects":{"p":{"name":"multi","rootPaths":["C:/one","C:/two"]}}}""");
    Assert(ProjectGitStatus.ReadSelectedProject(projectStateFile) is null, "Does not select an arbitrary root in a multi-root project");
}
finally { File.Delete(projectStateFile); }
var presentationNow = DateTimeOffset.UtcNow;
Assert(Localization.Limit("Weekly limit") == "Недельный лимит", "Translates the weekly limit label");
Assert(Localization.Status("Updated 5m ago") == "5 мин назад", "Translates relative update time");
Assert(Localization.Reset(null, presentationNow) == "Время сброса неизвестно", "Shows an unknown reset time without inventing a countdown");
Assert(Localization.Reset(presentationNow.AddDays(2).AddHours(3), presentationNow) == "Сброс через 2 д 3 ч", "Formats the reset countdown in Russian");
var claudeSnapshot = ClaudeUsageSnapshot.Empty.Apply(
    new ClaudeRateLimits(
        new QuotaWindow(2, 300, presentationNow.AddHours(4)),
        new QuotaWindow(56, 10_080, presentationNow.AddDays(4))),
    "presentation-spec", [], presentationNow);
var claudeDisplay = MainWindow.ForDisplay(claudeSnapshot.ToState(presentationNow));
Assert(claudeDisplay.Lead!.Key == "claude:seven_day" && claudeDisplay.Lead.Window.UsedPercent == 56,
    "Claude leads with the weekly limit");
Assert(claudeDisplay.Windows[1].Key == "claude:five_hour", "Claude keeps the five-hour limit in details");
Assert(MainWindow.ForDisplay((claudeSnapshot with { SevenDay = null }).ToState(presentationNow)).Lead!.Key == "claude:five_hour",
    "Claude falls back to the five-hour window when the weekly limit is unavailable");
Assert(!MainWindow.ForDisplay(ClaudeUsageSnapshot.Empty.ToState(presentationNow)).HasUsage,
    "Missing Claude usage remains unavailable");
var codexDisplay = claudeDisplay with { Provider = UsageProviderKind.Codex };
Assert(ReferenceEquals(codexDisplay, MainWindow.ForDisplay(codexDisplay)), "Codex keeps its original window selection");
Assert(MainWindow.IsOverlayHost(UsageProviderKind.Codex) && !MainWindow.IsOverlayHost(UsageProviderKind.Claude)
    && !MainWindow.IsOverlayHost(UsageProviderKind.Cursor), "The overlay attaches only to Codex");
Assert(HostWindowLocator.SelectHostWindow(new IntPtr(1), (uint)Environment.ProcessId, new IntPtr(2)) == new IntPtr(2),
    "Focusing the overlay retains the previous Codex host instead of hiding and reopening");
Assert(HostWindowLocator.SelectHostWindow(new IntPtr(1), (uint)Environment.ProcessId + 1, new IntPtr(2)) == new IntPtr(1),
    "Focusing another application does not retain a stale Codex host");
Assert(HostWindowLocator.SelectHostWindow(new IntPtr(1), (uint)Environment.ProcessId, IntPtr.Zero) == IntPtr.Zero,
    "An overlay without a previous host does not invent a Codex window");
Assert(HostWindowLocator.SelectHostWindow(IntPtr.Zero, 0, new IntPtr(2)) == new IntPtr(2),
    "A transient null foreground during activation retains the host for revalidation");
ClaudeIntegrationSpecs.Run();
await ClaudeSignInSpecs.RunAsync();
await ClaudeRenewalSpecs.RunAsync();

var directory = Path.Combine(Path.GetTempPath(), "usage-overlay-switch-spec-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
Environment.SetEnvironmentVariable("CODEX_HOME", directory);
var authPath = Path.Combine(directory, "auth.json");
var launcher = Path.Combine(directory, "fake-codex.cmd");
File.WriteAllText(launcher, $"@echo off\r\n\"{Environment.ProcessPath}\" --fake\r\n");
File.WriteAllText(authPath, "A");
var snapshots = new ConcurrentQueue<double>();
var statuses = new ConcurrentQueue<string>();
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(75));
await using var client = new AppServerClient(new AppLogger(), launcher, TimeSpan.FromSeconds(2));
client.SnapshotChanged += (_, snapshot) => snapshots.Enqueue(snapshot.Primary.Primary.UsedPercent);
client.StatusChanged += (_, status) => statuses.Enqueue(status);
var run = client.RunAsync(cancellation.Token);
try
{
    await Until(() => snapshots.Contains(70), "Initial account A loads");
    File.Delete(authPath);
    await Until(() => statuses.Contains("Signed out"), "External logout detected without restart");
    snapshots.Clear();
    File.WriteAllText(authPath, "B");
    await Until(() => snapshots.Contains(20), "External login B loads automatically");
    Assert(!snapshots.Contains(70), "Previous account usage is not reused");
    snapshots.Clear();
    File.WriteAllText(authPath, "C");
    await Until(() => snapshots.Contains(40), "Direct B to C switch detected");
    Assert(!snapshots.Contains(99), "Uncorrelated late responses are discarded");
    cancellation.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(!client.IsProcessRunning, "Overlay-owned child stops on cancellation");
    Console.WriteLine("All account-switch integration checks passed.");
}
finally
{
    cancellation.Cancel();
    await run;
    Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
    Directory.Delete(directory, recursive: true);
}

static void Reply(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
static void Assert(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
}
static async Task Until(Func<bool> predicate, string label)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (!predicate()) await Task.Delay(100, timeout.Token);
    Console.WriteLine("PASS " + label);
}

static void RenderPresentation(string outputDirectory)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        App? application = null;
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            application = new App { IsRenderingPreview = true };
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow(new OverlayOptions(25, true, false, false), new AppLogger());
            window.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Localization.Language = AppLanguage.Russian;
            Localization.Apply(window);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            typeof(MainWindow).GetField("_settings", flags)!.SetValue(window, new OverlaySettings
            {
                CodexEnabled = true, ClaudeEnabled = true, ShowOtherApps = true,
                AnimationsEnabled = false, PrimaryDisplay = PrimaryUsageDisplay.Used, Language = AppLanguage.Russian
            });
            var states = (Dictionary<UsageProviderKind, ProviderUsageState>)typeof(MainWindow)
                .GetField("_states", flags)!.GetValue(window)!;
            var now = DateTimeOffset.UtcNow;
            typeof(MainWindow).GetField("_projectGit", flags)!.SetValue(window,
                new ProjectGitSnapshot(new SelectedProject("demo", "Overlay", @"E:\Scripts\External\usage-overlay"), 35, now.AddMinutes(-2), true,
                    Branch: "feature/statusbar", Ahead: 3, Behind: 2, ChangedFiles: 4, UntrackedFiles: 1, HasUpstream: true,
                    Stashes: 3, OldestStash: now.AddDays(-2), Worktrees: 4, ActiveWorktree: "statusbar"));
            typeof(MainWindow).GetField("_environmentNotices", flags)!.SetValue(window,
                new EnvironmentNotice[] { new("MCP/statusline (test): нет соединения: example-server", true), new("Инфраструктура: нет свежих данных") });
            typeof(MainWindow).GetField("_session", flags)!.SetValue(window,
                new SessionSnapshot("demo", @"E:\Scripts\External\usage-overlay", ContextUsed: 116000, ContextWindow: 258000,
                    Input: 840000, Cached: 772800, Output: 18400, TokensPerSecond: 42.7, Duration: TimeSpan.FromMinutes(8.5),
                    Working: true, Action: "Editing", Agents: 2, Queued: 1, MeasuredAt: now));
            typeof(MainWindow).GetField("_resources", flags)!.SetValue(window, new ResourceSnapshot(120_000_000_000, 4_200_000_000, ChildProcesses: 12));
            typeof(MainWindow).GetField("_pullRequest", flags)!.SetValue(window, new PullRequestSnapshot(42, "OPEN", 2));
            states[UsageProviderKind.Claude] = MainWindow.ForDisplay(ClaudeUsageSnapshot.Empty.Apply(
                new ClaudeRateLimits(new QuotaWindow(2, 300, now.AddHours(4)), new QuotaWindow(56, 10_080, now.AddDays(4))),
                "render", [], now).ToState(now));
            states[UsageProviderKind.Codex] = states[UsageProviderKind.Claude] with
            {
                Provider = UsageProviderKind.Codex,
                Windows = [new UsageWindowDisplay("codex:weekly", "Weekly limit", new QuotaWindow(6, 10_080, now.AddDays(6)))]
            };
            var root = (FrameworkElement)window.Content;
            var panel = (FrameworkElement)window.FindName("DetailsPanel");
            panel.Visibility = Visibility.Visible;
            panel.Opacity = 1;
            ((TranslateTransform)window.FindName("DetailsTranslate")).X = 0;
            QuickLayoutSpecs.Run(window);
            HoverCollapseSpecs.Run(window);
            PopupStabilitySpecs.Run(window, outputDirectory);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Instance.ApplyTheme(theme);
                foreach (var layout in StatusbarSpecs.PresentationLayouts)
                foreach (var provider in new[] { UsageProviderKind.Codex, UsageProviderKind.Claude })
                {
                    var orientation = layout == "Vertical" ? RailOrientation.Vertical : RailOrientation.Horizontal;
                    typeof(MainWindow).GetField("_settings", flags)!.SetValue(window, new OverlaySettings
                    {
                        CodexEnabled = true, ClaudeEnabled = true, ShowOtherApps = true, AnimationsEnabled = false,
                        PrimaryDisplay = PrimaryUsageDisplay.Used, Language = AppLanguage.Russian,
                        Orientation = orientation, DetailsPosition = layout == "HorizontalBelow" ? DetailsPlacement.Below : DetailsPlacement.Above
                    });
                    typeof(MainWindow).GetField("_isExpanded", flags)!.SetValue(window, true);
                    typeof(MainWindow).GetField("_lastBounds", flags)!.SetValue(window,
                        layout == "HorizontalNarrow" ? new WindowBounds(0, 0, 440, 700, false)
                        : layout == "HorizontalTiny" ? new WindowBounds(0, 0, 316, 700, false) : null);
                    typeof(MainWindow).GetMethod("ApplyRailLayout", flags)!.Invoke(window, null);
                    typeof(MainWindow).GetField("_displayedProvider", flags)!.SetValue(window, provider);
                    typeof(MainWindow).GetMethod("Render", flags)!.Invoke(window, null);
                    var label = ((TextBlock)window.FindName("BucketNameText")).Text;
                    var value = ((TextBlock)window.FindName("UsagePercentText")).Text;
                    var gitText = (TextBlock)window.FindName("ProjectGitText");
                    Assert(gitText.Visibility == (provider == UsageProviderKind.Codex ? Visibility.Visible : Visibility.Collapsed),
                        $"{theme} Git details appear only in the Codex card");
                    if (provider == UsageProviderKind.Codex)
                        Assert(gitText.Text.Contains("Ветки: 35", StringComparison.Ordinal), "Git branch count is rendered");
                    Assert(label == "Недельный лимит" && value == (provider == UsageProviderKind.Claude ? "56% использовано" : "6% использовано"),
                        $"{theme} {provider} renders its weekly percentage");
                    var width = (int)Math.Ceiling(window.Width);
                    var height = (int)Math.Ceiling(window.Height);
                    for (var layoutPass = 0; layoutPass < 3; layoutPass++)
                    {
                        root.InvalidateMeasure();
                        root.Measure(new Size(width, height));
                        root.Arrange(new Rect(0, 0, width, height));
                        root.UpdateLayout();
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                    Console.WriteLine($"LAYOUT {provider}-{theme}-{layout}: window={width}x{height}; panel={panel.ActualHeight}/{panel.DesiredSize.Height}; scroll={((ScrollViewer)window.FindName("DetailsScroll")).ActualHeight}/{((ScrollViewer)window.FindName("DetailsScroll")).ExtentHeight}; bottom={panel.TranslatePoint(new Point(0, panel.ActualHeight), root).Y}");
                    Assert(panel.TranslatePoint(new Point(0, panel.ActualHeight), root).Y <= height + 1, "The details card stays inside the overlay window");
                    var horizontal = (FrameworkElement)window.FindName("HorizontalHitTarget");
                    Assert(horizontal.Visibility == (orientation == RailOrientation.Horizontal ? Visibility.Visible : Visibility.Collapsed), "Orientation selects the correct meter");
                    var expandButton = (Button)window.FindName(orientation == RailOrientation.Horizontal ? "ExpandMicroButton" : "VerticalExpandButton");
                    Assert(expandButton.ToolTip is null && !ToolTipService.GetIsEnabled(expandButton), "Expansion help tooltip cannot cover the data");
                    expandButton.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                    var hoverTimer = (System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_hoverTimer", flags)!.GetValue(window)!;
                    Assert(hoverTimer.IsEnabled, "Hovering the expansion button starts its deliberate reveal delay");
                    expandButton.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                    Assert(!hoverTimer.IsEnabled, "Leaving the expansion button cancels a pending reveal");
                    if (orientation == RailOrientation.Horizontal)
                    {
                        Assert(horizontal.ActualWidth <= 561 && horizontal.ActualWidth >= 239 && horizontal.ActualHeight is >= 53 and <= 80, "Micro meter follows its measured content size");
                        if (layout == "HorizontalNarrow") Assert(width <= 424, "Horizontal meter fits a narrow host window");
                        if (layout == "HorizontalTiny") Assert(width <= 301, "Micro meter fits a 300-pixel host area");
                        var info = (Grid)window.FindName("HorizontalInfo");
                        foreach (var child in info.Children.OfType<FrameworkElement>().Where(child => child.Visibility == Visibility.Visible))
                            Assert(child.TranslatePoint(new Point(child.ActualWidth, 0), info).X <= info.ActualWidth + 1, "Micro content stays inside the available width");
                        var diagnosticLabel = (FrameworkElement)window.FindName("HorizontalDiagnosticsText");
                        Assert(diagnosticLabel.TranslatePoint(new Point(0, diagnosticLabel.ActualHeight), horizontal).Y <= horizontal.ActualHeight + 1, "Summary diagnostics stay inside the meter");
                        var placement = (BarLayout)typeof(MainWindow).GetField("_barLayout", flags)!.GetValue(window)!;
                        Assert(placement.Above ? Canvas.GetTop(panel) < Canvas.GetTop(horizontal) : Canvas.GetTop(panel) > Canvas.GetTop(horizontal), "Details follow the available side of the meter");
                        Assert(((TextBlock)window.FindName("HorizontalPercentText")).Text == (provider == UsageProviderKind.Claude ? "56%" : "6%"), "Horizontal summary shows the provider's current percentage");
                        if (provider == UsageProviderKind.Codex)
                        {
                            Assert(((TextBlock)window.FindName("HorizontalContextText")).Text == MainWindow.CompactTokens(116000) + " / " + MainWindow.CompactTokens(258000), "Micro meter shows current context rather than cumulative tokens");
                            Assert(((StackPanel)window.FindName("SessionMetricsPanel")).Children.Count == 7 && ((StackPanel)window.FindName("ProjectMetricsPanel")).Children.Count == 7, "Popup contains all selected metric rows");
                            var row = (Grid)((StackPanel)window.FindName("SessionMetricsPanel")).Children[0];
                            var icon = (TextBlock)row.Children[0];
                            Assert(new Typeface(icon.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var glyphs)
                                && glyphs.CharacterToGlyphMap.ContainsKey(0xf0f6), "Bundled statusline font resolves real glyphs without an installed font");
                        }
                    }
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(outputDirectory, $"{provider}-{theme}-{layout}.png"));
                    encoder.Save(output);
                    if (orientation == RailOrientation.Horizontal)
                    {
                        var barCenter = new Point(window.Left + Canvas.GetLeft(horizontal) + horizontal.Width / 2, window.Top + Canvas.GetTop(horizontal) + horizontal.Height / 2);
                        typeof(MainWindow).GetMethod("CloseUsageDetails", flags)!.Invoke(window, null);
                        Assert(Math.Abs(window.Width - horizontal.Width) <= 1 && Math.Abs(window.Height - horizontal.Height) < 1, "Closing details leaves only the measured micro bar");
                        Assert(Math.Abs(window.Left + horizontal.Width / 2 - barCenter.X) <= 1 && Math.Abs(window.Top + horizontal.Height / 2 - barCenter.Y) <= 1, "Opening and closing details preserve the bar's screen anchor");
                        ((Button)window.FindName("ExpandMicroButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert((bool)typeof(MainWindow).GetField("_isExpanded", flags)!.GetValue(window)!
                            && (bool)typeof(MainWindow).GetField("_isPinned", flags)!.GetValue(window)!, "Micro chevron opens and pins its details");
                        panel.Opacity = 1;
                    }
                }
            }
            SettingsPresentation.Render(outputDirectory);
            window.Close();
            application.Shutdown();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (window?.IsLoaded == true) window.Close();
            if (application is not null && !application.Dispatcher.HasShutdownStarted) application.Shutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Presentation rendering failed.", failure);
}
