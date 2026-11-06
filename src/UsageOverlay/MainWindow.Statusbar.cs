using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using CodexUsage.Core.Models;
using CodexUsage.Core.Providers;
using CodexUsage.Core.Settings;
using UsageOverlay.Infrastructure;
using UsageOverlay.Services;
using Localization = UsageOverlay.Infrastructure.Localization;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using System.Globalization;
using Panel = System.Windows.Controls.Panel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace UsageOverlay;

public partial class MainWindow
{
    private IReadOnlyList<EnvironmentNotice> _environmentNotices = [];
    private string? _settingsError;
    private FrameworkElement? _dragTarget;
    private readonly SessionTelemetry _telemetry = new();
    private readonly ProjectResources _projectResources = new();
    private SessionSnapshot _session = new();
    private ResourceSnapshot _resources = new();
    private PullRequestSnapshot _pullRequest = new();
    private bool _telemetryRefreshRunning;
    private DateTimeOffset _nextTelemetryRefresh;
    private bool IsHorizontal => _settings.Orientation == RailOrientation.Horizontal;
    private Size _barSize = new(360, 54);
    private BarLayout? _barLayout;
    private bool _applyingLayout;
    private bool _rendering;
    private bool _detailsClosing;
    private bool _hoverDismissed;
    private readonly Dictionary<string, TextBlock> _metricValues = [];
    private string? _microLimitsSignature;
    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void ApplyRailLayout() => ApplyRailLayoutWithin(GetCurrentPlacementBounds());

    private void ApplyRailLayoutWithin(WindowBounds bounds, System.Windows.Point? anchorOverride = null)
    {
        if (_applyingLayout || _rendering || _isDragging && anchorOverride is null) return;
        _applyingLayout = true;
        try
        {
            var availableWidth = Math.Max(1, bounds.Right - bounds.Left - WindowPadding * 2);
            HorizontalProviderText.Visibility = availableWidth < 350 ? Visibility.Collapsed : Visibility.Visible;
            var verticalWidth = Math.Clamp(Math.Ceiling(16 + Math.Max(
                39 + Math.Max(TextWidth(VerticalContextText), TextWidth(VerticalCapacityText)),
                Math.Max(18 + Math.Max(TextWidth(RailRemainingText), TextWidth(VerticalResetText)),
                    Math.Max(TextWidth(VerticalFreeText), TextWidth(VerticalGitText))))), 92, 116);
            VerticalInfo.InvalidateMeasure();
            VerticalInfo.Measure(new Size(verticalWidth - 16, double.PositiveInfinity));
            var textWidth = Math.Max(TextWidth(HorizontalContextText), TextWidth(HorizontalFreeText));
            var quotaWidth = Math.Max(TextWidth(HorizontalProviderText) + (HorizontalProviderText.Visibility == Visibility.Visible ? 5 : 0)
                + TextWidth(HorizontalPercentText), TextWidth(HorizontalResetText));
            // Measure text independently: a trimmed Grid can retain its previous constrained width.
            var horizontalWidth = 36 + 15 + textWidth + quotaWidth + TextWidth(HorizontalDiagnosticsText) + 9 + 52 + 20;
            _barSize = IsHorizontal ? new Size(Math.Min(availableWidth, Math.Clamp(Math.Ceiling(horizontalWidth), 240, 560)), 54)
                : new Size(Math.Min(verticalWidth, availableWidth), Math.Ceiling(VerticalInfo.DesiredSize.Height + 16));
            var anchor = anchorOverride ?? BarAnchor(bounds, _barSize);
            var requestedWidth = _displayedProvider == UsageProviderKind.Codex ? 460 : 380;
            var preliminary = BarPlacement.Calculate(bounds, anchor, _barSize, new Size(requestedWidth, 500), IsHorizontal,
                _settings.DetailsPosition == DetailsPlacement.Above, _isExpanded);
            var popupWidth = preliminary.Details.Width;
            QuickHorizontalLayout.IsChecked = IsHorizontal;
            QuickVerticalLayout.IsChecked = !IsHorizontal;
            var columns = popupWidth >= 360;
            TelemetryGrid.ColumnDefinitions[1].Width = columns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (TelemetryGrid.RowDefinitions.Count == 0)
            {
                TelemetryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TelemetryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Grid.SetColumn(ProjectMetricsPanel, columns ? 1 : 0);
            Grid.SetRow(ProjectMetricsPanel, columns ? 0 : 1);
            GitDetailsGroup.Visibility = Visibility.Collapsed;
            DetailsPanel.Margin = new Thickness(0);
            DetailsPanel.Padding = new Thickness(12);
            DetailsTranslate.X = DetailsTranslate.Y = 0;
            DetailsScroll.Margin = new Thickness(0, 10, 0, 0);
            DetailsGroups.Orientation = System.Windows.Controls.Orientation.Vertical;
            TelemetryGrid.InvalidateMeasure();
            DetailsGroups.InvalidateMeasure();
            DetailsGroups.Measure(new Size(Math.Max(1, popupWidth - 26), double.PositiveInfinity));
            const double chromeHeight = 60;
            var naturalHeight = Math.Min(560, Math.Ceiling(DetailsGroups.DesiredSize.Height + chromeHeight));
            _barLayout = BarPlacement.Calculate(bounds, anchor, _barSize, new Size(popupWidth, naturalHeight), IsHorizontal,
                _settings.DetailsPosition == DetailsPlacement.Above, _isExpanded);
            var dpi = VisualTreeHelper.GetDpi(this);
            Rect Snap(Rect rectangle) => new(new System.Windows.Point(Math.Round(rectangle.Left * dpi.DpiScaleX) / dpi.DpiScaleX,
                    Math.Round(rectangle.Top * dpi.DpiScaleY) / dpi.DpiScaleY),
                new System.Windows.Point(Math.Round(rectangle.Right * dpi.DpiScaleX) / dpi.DpiScaleX,
                    Math.Round(rectangle.Bottom * dpi.DpiScaleY) / dpi.DpiScaleY));
            var snappedBar = Snap(_barLayout.Bar);
            var snappedDetails = Snap(_barLayout.Details);
            _barLayout = _barLayout with { Bar = snappedBar, Details = snappedDetails,
                Window = _isExpanded ? Rect.Union(snappedBar, snappedDetails) : snappedBar };
            DetailsScroll.Height = DetailsScroll.MaxHeight = Math.Max(1, _barLayout.Details.Height - chromeHeight);
            DetailsPanel.Width = popupWidth;
            DetailsPanel.Height = _barLayout.Details.Height;
            RailHitTarget.Visibility = IsHorizontal ? Visibility.Collapsed : Visibility.Visible;
            HorizontalHitTarget.Visibility = IsHorizontal ? Visibility.Visible : Visibility.Collapsed;
            HorizontalHitTarget.ContextMenu = RailHitTarget.ContextMenu;
            var target = IsHorizontal ? HorizontalHitTarget : RailHitTarget;
            target.Width = _barLayout.Bar.Width;
            target.Height = _barLayout.Bar.Height;
            Place(target, _barLayout.Bar, _barLayout.Window);
            Place(DetailsPanel, _barLayout.Details, _barLayout.Window);
            var closingAngle = _isExpanded ? 180 : 0;
            HorizontalChevronRotation.Angle = (_barLayout.Above ? 0 : 180) + closingAngle;
            VerticalChevronRotation.Angle = (_barLayout.Left ? -90 : 90) + closingAngle;
            foreach (var button in new[] { ExpandMicroButton, VerticalExpandButton })
            {
                AutomationProperties.SetName(button, Localization.Text(_isExpanded ? "Close usage details" : "Show usage details"));
                button.ToolTip = null;
            }
            OverlayRoot.Width = _barLayout.Window.Width;
            OverlayRoot.Height = _barLayout.Window.Height;
            if (_windowHandle == IntPtr.Zero || !NativeWindowStyle.SetBounds(_windowHandle, _barLayout.Window, dpi))
            {
                Width = _barLayout.Window.Width;
                Height = _barLayout.Window.Height;
                Left = _barLayout.Window.Left;
                Top = _barLayout.Window.Top;
            }
        }
        finally { _applyingLayout = false; }
    }

    private System.Windows.Point BarAnchor(WindowBounds bounds, Size bar) => new(
        (_settings.Placement == HorizontalPlacement.Custom ? bounds.Left + (bounds.Right - bounds.Left) * _settings.CustomXRatio
            : _settings.Placement == HorizontalPlacement.LeftEdge ? bounds.Left + RightInset + bar.Width / 2 : bounds.Right - RightInset - bar.Width / 2) + _settings.HorizontalOffset,
        (_settings.Placement == HorizontalPlacement.Custom ? bounds.Top + (bounds.Bottom - bounds.Top) * _settings.CustomYRatio
            : bounds.Bottom - BottomInset - bar.Height / 2) - _settings.VerticalOffset);

    private static void Place(FrameworkElement target, Rect rectangle, Rect window)
    {
        Canvas.SetLeft(target, rectangle.Left - window.Left);
        Canvas.SetTop(target, rectangle.Top - window.Top);
    }

    private double TextWidth(TextBlock text)
    {
        if (text.Visibility != Visibility.Visible || string.IsNullOrEmpty(text.Text)) return 0;
        var format = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Min(text.MaxWidth, format.WidthIncludingTrailingWhitespace);
    }

    private void ExpandButton_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        if (!_hoverDismissed) _hoverTimer.Start();
    }

    private void ExpandButton_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (!_applyingLayout && !IsPointerOverOverlay()) _hoverDismissed = false;
    }

    private void HoverTimer_OnTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (!_hoverDismissed && !_isDragging && (ExpandMicroButton.IsMouseOver || VerticalExpandButton.IsMouseOver)) ExpandDetails();
    }

    private bool IsPointerOverOverlay()
    {
        if (_barLayout is null || !IsVisible) return false;
        var cursor = System.Windows.Forms.Cursor.Position;
        return BarPlacement.ContainsPhysicalPoint(_barLayout, new System.Windows.Point(cursor.X, cursor.Y), VisualTreeHelper.GetDpi(this), _isExpanded);
    }

    private SolidColorBrush BudgetBrush(QuotaWindow window) => new((Color)FindResource(UsageBudget.ForReset(window, DateTimeOffset.Now) switch
    {
        UsageBudgetLevel.Normal => "OverlayNormalColor",
        UsageBudgetLevel.Warning => "OverlayWarningColor",
        UsageBudgetLevel.Tight => "OverlayTightColor",
        UsageBudgetLevel.Critical => "OverlayCriticalColor",
        _ => "OverlayMutedColor"
    }));

    private void RenderDiagnostics()
    {
        var notices = _environmentNotices.ToList();
        if (_settingsError is { } settingsError) notices.Add(new(Localization.Text(settingsError), true));
        foreach (var state in _states.Values.Where(state => IsProviderEnabled(state.Provider)))
        {
            if (state.Connection is ProviderConnection.Stale or ProviderConnection.SignedOut or ProviderConnection.Unavailable or ProviderConnection.Disconnected)
                notices.Add(new($"{state.DisplayName}: {Localization.Status(state.StatusText)}. {Localization.Status(state.Detail)}", true));
        }
        if (_session.Error is { } sessionError) notices.Add(new(Localization.Text(sessionError)));
        if (_pullRequest.Error is { } prError) notices.Add(new(Localization.Text(prError)));
        if (_resources.Error is { } resourceError) notices.Add(new(Localization.Text(resourceError)));
        if (_projectGit?.Error is { } error) notices.Add(new(Localization.Text(error), true));
        if (_projectGit?.Conflicts is > 0 and var conflicts) notices.Add(new(Localization.Format("Merge conflicts: {0}", conflicts), true));
        if (notices.Count == 0) notices.Add(new(Localization.Text("Diagnostics: no readings yet")));
        var problems = notices.Where(notice => notice.IsProblem).ToArray();
        DiagnosticsText.Text = string.Join("\n", notices.OrderByDescending(notice => notice.IsProblem).Select(notice => notice.Text));
        DiagnosticsText.Foreground = problems.Length > 0 ? new SolidColorBrush((Color)FindResource("OverlayWarningColor")) : (System.Windows.Media.Brush)FindResource("OverlayMutedBrush");
        HorizontalDiagnosticsText.Text = problems.Length > 0 ? $"⚠{problems.Length}" : "";
        HorizontalDiagnosticsText.ToolTip = DiagnosticsText.Text;
        HorizontalDiagnosticsText.Foreground = DiagnosticsText.Foreground;
    }

    private void UpdateHorizontalSummary()
    {
        var state = CurrentState;
        HorizontalProviderText.Text = state.DisplayName + (state.HasUsage ? "  " + Localization.Limit(state.Lead!.Label) : "");
        HorizontalProviderText.MaxWidth = 125;
        HorizontalProviderText.TextTrimming = TextTrimming.CharacterEllipsis;
        HorizontalPercentText.Text = state.HasUsage ? $"{(_settings.PrimaryDisplay == PrimaryUsageDisplay.Used ? state.Lead!.Window.UsedPercent : 100 - state.Lead!.Window.UsedPercent):0}%" : "—";
        HorizontalPercentText.Foreground = UsagePercentText.Foreground;
        HorizontalResetText.Text = state.HasUsage ? "⏳ " + ShortReset(state.Lead!.Window.ResetsAt) : Localization.Status(state.StatusText);
        HorizontalResetText.MaxWidth = 140;
        HorizontalResetText.TextTrimming = TextTrimming.CharacterEllipsis;
        HorizontalPercentText.ToolTip = UsagePercentText.Text;
        HorizontalResetText.ToolTip = ResetText.Text;
        HorizontalResetText.Foreground = state.HasUsage ? BudgetBrush(state.Lead!.Window) : (System.Windows.Media.Brush)FindResource("OverlayMutedBrush");
        HorizontalGitText.Visibility = Visibility.Collapsed;
        HorizontalGitText.Text = _projectGit is null ? Localization.Text("Select a local project to show Git")
            : _projectGit.Project.Name + ": " + ProjectGitStatus.FormatCompact(_projectGit);
        HorizontalGitText.ToolTip = ProjectGitText.Text;
        HorizontalFillScale.ScaleX = UsageFillScale.ScaleY;
        RenderTelemetry();
        RenderMicroLimits();
        AutomationProperties.SetName(HorizontalHitTarget, $"{HorizontalContextText.Text}. {HorizontalFreeText.Text}. {HorizontalProviderText.Text}. {HorizontalPercentText.Text}. {HorizontalResetText.Text}. {HorizontalGitText.Text}. {HorizontalDiagnosticsText.Text}");
    }

    private void RenderMicroLimits()
    {
        MicroLimitsPanel.Visibility = Visibility.Visible;
        UsageLinkHost.Visibility = Visibility.Collapsed;
        BucketNameText.Visibility = UsagePercentText.Visibility = ResetText.Visibility = SummaryText.Visibility = AdditionalLimitsPanel.Visibility = OtherAppsPanel.Visibility = Visibility.Collapsed;
        var signature = $"{_displayedProvider}|{_settings.ShowOtherApps}|{_settings.PrimaryDisplay}|{Localization.Language}|{_settings.WarningThreshold}|{_settings.CriticalThreshold}|{FindResource("OverlayNormalColor")}|{FindResource("OverlayWarningColor")}|{FindResource("OverlayCriticalColor")}|"
            + string.Join("|", _states.Values.Where(state => IsProviderEnabled(state.Provider)).SelectMany(state => state.Windows.Select(limit =>
                $"{state.Provider}:{limit.Key}:{limit.Window.WindowDurationMinutes}:{limit.Window.UsedPercent}:{Localization.Reset(limit.Window.ResetsAt, DateTimeOffset.Now)}")));
        if (_microLimitsSignature == signature) return;
        _microLimitsSignature = signature;
        MicroLimitsPanel.Children.Clear();
        foreach (var state in _states.Values.Where(state => IsProviderEnabled(state.Provider) && (state.Provider == _displayedProvider || _settings.ShowOtherApps)))
        {
            foreach (var limit in state.Windows)
            {
                var period = limit.Window.WindowDurationMinutes is >= 10080 ? "7d" : limit.Window.WindowDurationMinutes is { } minutes ? $"{minutes / 60d:0.#}h" : "";
                var displayed = _settings.PrimaryDisplay == PrimaryUsageDisplay.Used ? limit.Window.UsedPercent : 100 - limit.Window.UsedPercent;
                var text = new TextBlock { Text = $"{state.DisplayName} {Localization.Text(period)} {displayed:0}%", FontSize = 10,
                    Margin = new Thickness(0, 0, 12, 5), Foreground = new SolidColorBrush(LevelColor(limit.Window.UsedPercent)),
                    ToolTip = Localization.Limit(limit.Label) + "  " + Localization.Text(_settings.PrimaryDisplay == PrimaryUsageDisplay.Used ? "#used" : "#left") + "\n" + Localization.Reset(limit.Window.ResetsAt, DateTimeOffset.Now) };
                MicroLimitsPanel.Children.Add(text);
            }
        }
        var link = new System.Windows.Controls.Button { Content = Localization.Text("View usage"), FontSize = 10, Margin = new Thickness(0, 0, 0, 5), Style = (Style)FindResource("UsageActionLinkStyle") };
        link.Click += ViewUsageLink_OnClick;
        MicroLimitsPanel.Children.Add(link);
    }

    private void MicroExpand_OnClick(object sender, RoutedEventArgs e)
    {
        _hoverTimer.Stop();
        if (_isExpanded)
        {
            _hoverDismissed = true;
            CloseUsageDetails();
        }
        else
        {
            _hoverDismissed = false;
            ExpandDetails();
            _isPinned = true;
        }
        UpdateEscapeHotKey();
        ApplyRailLayout();
        e.Handled = true;
    }

    private void QuickLayout_OnClick(object sender, RoutedEventArgs e)
    {
        var orientation = ReferenceEquals(sender, QuickHorizontalLayout) ? RailOrientation.Horizontal : RailOrientation.Vertical;
        if (_settings.Orientation == orientation) return;
        var previous = _settings;
        _settings = _settings with { Orientation = orientation };
        if (_options.DemoPercent is null && !PersistSettings())
        {
            _settings = previous;
            Render();
            return;
        }
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _isPinned = true;
        _detailsClosing = false;
        DetailsPanel.BeginAnimation(OpacityProperty, null);
        DetailsPanel.Opacity = 1;
        _settingsWindow?.UpdateOrientation(_settings);
        Render();
        UpdateEscapeHotKey();
        e.Handled = true;
    }

    private static string ShortReset(DateTimeOffset? time)
    {
        if (time is null) return "—";
        var left = time.Value - DateTimeOffset.Now;
        return left <= TimeSpan.Zero ? Localization.Text("now") : ShortDuration(left);
    }

    private static string ShortDuration(TimeSpan duration) => duration.TotalDays >= 1 ? Localization.Format("{0}d {1}h", (int)duration.TotalDays, duration.Hours)
        : duration.TotalHours >= 1 ? Localization.Format("{0}h {1}m", (int)duration.TotalHours, duration.Minutes)
        : Localization.Format("{0}m {1}s", (int)duration.TotalMinutes, duration.Seconds);

    internal static string CompactTokens(long value) => value >= 1000000 ? $"{value / 1000000d:0.#}M" : value >= 1000 ? $"{value / 1000d:0.#}K" : value.ToString(CultureInfo.CurrentCulture);
    private static string Tokens(long? number) => number is { } value ? CompactTokens(value) : "—";
    private static string Number(int? number) => number?.ToString(CultureInfo.CurrentCulture) ?? "—";
    private static string Bytes(long? number) => number is { } value ? $"{value / 1073741824d:0.0} GB" : "—";
    private static string Percent(double? number) => number is { } value ? $"{value:0}%" : "—";

    private void RenderTelemetry()
    {
        var codex = _displayedProvider == UsageProviderKind.Codex;
        var percent = codex ? _session.ContextPercent : null;
        ContextRingText.Text = percent is { } used ? $"{used:0}" : "—";
        var color = (Color)FindResource(percent switch { >= 80 => "OverlayCriticalColor", >= 50 => "OverlayWarningColor", not null => "OverlayNormalColor", _ => "OverlayMutedColor" });
        ContextRingFill.Stroke = new SolidColorBrush(color);
        ContextRingFill.Data = RingGeometry(percent);
        VerticalContextRingFill.Stroke = ContextRingFill.Stroke;
        VerticalContextRingFill.Data = ContextRingFill.Data;
        VerticalContextRingText.Text = ContextRingText.Text;
        VerticalContextText.Text = codex ? Tokens(_session.ContextUsed) : "—";
        VerticalCapacityText.Text = codex ? "/ " + Tokens(_session.ContextWindow) : "—";
        VerticalFreeText.Text = codex ? Localization.Format("free {0}", Tokens(_session.ContextFree)) : "—";
        VerticalQuotaText.Text = CurrentState.DisplayName + (CurrentState.Lead?.Window.WindowDurationMinutes is >= 10080 ? "  " + Localization.Text("7d") : "");
        VerticalQuotaText.ToolTip = HorizontalProviderText.Text + "\n" + HorizontalResetText.Text;
        VerticalResetText.Text = HorizontalResetText.Text;
        VerticalResetText.Foreground = HorizontalResetText.Foreground;
        VerticalResetText.ToolTip = HorizontalResetText.ToolTip;
        VerticalGitText.Visibility = _projectGit is { IsRepository: true } ? Visibility.Visible : Visibility.Collapsed;
        VerticalGitText.Text = _projectGit is { } verticalGit ? $"↑{Number(verticalGit.Ahead ?? verticalGit.LocalOnly)} ↓{Number(verticalGit.Behind)} ✎{Number(verticalGit.ChangedFiles)}" : "";
        VerticalGitText.ToolTip = ProjectGitText.Text;
        VerticalDiagnosticsText.Text = HorizontalDiagnosticsText.Text;
        VerticalDiagnosticsText.Visibility = VerticalDiagnosticsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VerticalDiagnosticsText.Foreground = HorizontalDiagnosticsText.Foreground;
        VerticalDiagnosticsText.ToolTip = DiagnosticsText.Text;
        HorizontalPercentText.Visibility = _settings.ShowCompactPercentage ? Visibility.Visible : Visibility.Collapsed;
        HorizontalContextText.Text = codex ? $"{Tokens(_session.ContextUsed)} / {Tokens(_session.ContextWindow)}" : CurrentState.DisplayName;
        HorizontalFreeText.Text = codex ? Localization.Format("free {0}", Tokens(_session.ContextFree)) : Localization.Text("Chat context unavailable");
        var contextTip = Localization.Format("Context: {0:N0} / {1:N0} tokens. Latest request, not cumulative usage.", _session.ContextUsed?.ToString("N0", CultureInfo.CurrentCulture) ?? "—", _session.ContextWindow?.ToString("N0", CultureInfo.CurrentCulture) ?? "—")
            + (_session.MeasuredAt is { } measured ? "\n" + Localization.Format("Measured at {0}", measured.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)) : "")
            + (_session.Error is { } error ? "\n" + Localization.Text(error) : "");
        ContextRing.ToolTip = HorizontalContextText.ToolTip = HorizontalFreeText.ToolTip = contextTip;
        VerticalContextRing.ToolTip = VerticalContextText.ToolTip = VerticalCapacityText.ToolTip = VerticalFreeText.ToolTip = contextTip;
        TelemetryGrid.Visibility = TelemetryProjectText.Visibility = codex ? Visibility.Visible : Visibility.Collapsed;
        GitDetailsGroup.Visibility = codex && !IsHorizontal && _projectGit is not null ? Visibility.Visible : Visibility.Collapsed;
        TelemetryProjectText.Text = (_session.Cwd is { } cwd ? System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/')) : "—") + "  "
            + (_projectGit is { } git ? ProjectGitStatus.FormatCompact(git) : Localization.Text("Waiting for the active Codex chat"));
        TelemetryProjectText.ToolTip = (_session.Cwd ?? "—") + "\n" + ProjectGitText.Text + "\n" + (_session.Title ?? "");
        AddMetric(SessionMetricsPanel, '\uf0f6', "Tokens", $"↓{Tokens(_session.Input)} ↑{Tokens(_session.Output)}", Localization.Text("Cumulative input / output for this chat; cached input is included in input."));
        AddMetric(SessionMetricsPanel, '\uf1c0', "Cache", Percent(_session.CachePercent), Localization.Format("Cached input: {0:N0}; input: {1:N0}", _session.Cached?.ToString("N0", CultureInfo.CurrentCulture) ?? "—", _session.Input?.ToString("N0", CultureInfo.CurrentCulture) ?? "—"));
        AddMetric(SessionMetricsPanel, '\uf0e7', "Speed", (_session.SpeedHeld ? "~" : "") + (_session.TokensPerSecond is { } speed ? $"{speed:0.0} tok/s" : "—"), Localization.Text("Output tokens / measured model activity time; tool time excluded. ~ means held during tool work."));
        AddMetric(SessionMetricsPanel, '\uf017', "Task", _session.Duration is { } duration ? ShortDuration(duration) : "—", Localization.Text(_session.Working ? "Current turn duration" : "Last completed turn duration"));
        AddMetric(SessionMetricsPanel, '\uf0ad', "Action", _session.Action is { } action ? Localization.Text(action) : "—", Localization.Text("Last action reported in the active chat log; observed, not a prediction."));
        AddMetric(SessionMetricsPanel, '\uf0c0', "Agents", Number(_session.Agents), Localization.Text("Direct child agents with an unfinished task in their local logs. Missing lifecycle data stays unknown."));
        AddMetric(SessionMetricsPanel, '\uf0ae', "Queue", Number(_session.Queued), Localization.Text("Queued user messages in the active chat."));
        var pr = _pullRequest.Number is { } number ? $"#{number} {_pullRequest.State}  {Number(_pullRequest.Reviews)} " + Localization.Text("reviews")
            : _pullRequest.State == "None" ? Localization.Text("no PR") : "—";
        AddMetric(ProjectMetricsPanel, '\uf407', "PR", pr, _pullRequest.Error is { } prError ? Localization.Text(prError) : Localization.Text("Pull request for this branch; number of requested reviewers. Updated once a minute."));
        AddMetric(ProjectMetricsPanel, '\uf01c', "Stash", Number(_projectGit?.Stashes) + (_projectGit?.OldestStash is { } oldest ? "  " + ShortDuration(DateTimeOffset.Now - oldest) : ""), Localization.Text("Stash count and age of the oldest stash."));
        AddMetric(ProjectMetricsPanel, '\uf126', "Worktrees", Number(_projectGit?.Worktrees) + "  " + (_projectGit?.ActiveWorktree ?? "—"), _projectGit?.RepositoryRoot ?? Localization.Text("Active worktree is unavailable"));
        AddMetric(ProjectMetricsPanel, '\uf126', "Branches", Number(_projectGit?.Branches), Localization.Text("Local branches in this repository."));
        AddMetric(ProjectMetricsPanel, '\uf0a0', "Disk", Bytes(_resources.DiskFree), Localization.Text("Free space on the project drive."));
        AddMetric(ProjectMetricsPanel, '\uf085', "Build cache", (_resources.BuildPartial ? "≥" : "") + Bytes(_resources.BuildBytes), _resources.BuildPath is { } buildPath ? buildPath + "\n" + Localization.Text("Cargo target size; ≥ means a bounded or incomplete scan. Updated once a minute.") : Localization.Text("No Cargo target directory found."));
        AddMetric(ProjectMetricsPanel, '\uf0e8', "Processes", Number(_resources.ChildProcesses), Localization.Text("All Windows descendants of the Codex desktop app. Separate from chat agents."));
    }

    private void AddMetric(Panel panel, char glyph, string label, string value, string tooltip)
    {
        var content = Localization.Text(label) + " " + value;
        var description = Localization.Text(label) + ": " + value + "\n" + tooltip;
        if (_metricValues.TryGetValue(label, out var existing))
        {
            existing.Text = content;
            existing.Foreground = (System.Windows.Media.Brush)FindResource("OverlayTextBrush");
            var existingRow = (Grid)existing.Parent;
            ((TextBlock)existingRow.Children[0]).Foreground = (System.Windows.Media.Brush)FindResource("OverlayMutedBrush");
            existingRow.ToolTip = description;
            AutomationProperties.SetName(existingRow, Localization.Text(label) + ": " + value);
            return;
        }
        var row = new Grid { Height = 23, ToolTip = description };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(19) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = glyph.ToString(), FontFamily = new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/UsageOverlay;component/"), "./Assets/Statusline/#CaskaydiaCove NF"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("OverlayMutedBrush") });
        var text = new TextBlock { Text = Localization.Text(label) + " " + value, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (System.Windows.Media.Brush)FindResource("OverlayTextBrush") };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        AutomationProperties.SetName(row, Localization.Text(label) + ": " + value);
        panel.Children.Add(row);
        _metricValues[label] = text;
    }

    private static Geometry? RingGeometry(double? percent)
    {
        if (percent is null or <= 0) return null;
        if (percent >= 100) return new EllipseGeometry(new System.Windows.Point(16, 16), 13, 13);
        var angle = percent.Value / 100 * Math.PI * 2;
        var path = new PathFigure { StartPoint = new System.Windows.Point(16, 3), IsClosed = false };
        path.Segments.Add(new ArcSegment(new System.Windows.Point(16 + 13 * Math.Sin(angle), 16 - 13 * Math.Cos(angle)), new Size(13, 13), 0, percent > 50, SweepDirection.Clockwise, true));
        return new PathGeometry([path]);
    }

    private async Task RefreshTelemetryAsync()
    {
        _telemetryRefreshRunning = true;
        try
        {
            var snapshot = await Task.Run(() => _telemetry.Read(DateTimeOffset.UtcNow));
            if (_isClosing) return;
            if (_session.ThreadId != snapshot.ThreadId || _session.Cwd != snapshot.Cwd)
            {
                _projectGit = null;
                _resources = new();
                _pullRequest = new();
                _environmentNotices = [];
                _nextGitRefresh = default;
            }
            _session = snapshot;
            Render();
        }
        finally { _telemetryRefreshRunning = false; }
    }
}
