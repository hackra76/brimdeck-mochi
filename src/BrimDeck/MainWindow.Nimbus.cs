using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private readonly NimbusCompanion _compactNimbus = new() { IsMini = true };
    private readonly DispatcherTimer _nimbusPoll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<string, NimbusAgentTask> _sessionTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Border Pill, NimbusCompanion Bot, TextBlock Label)> _miniBotPills = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _conversationWatchers = [];
    private readonly NimbusQuotaTracker _quotaTracker = new();
    private long _latestConversationWriteTicks;

    private NimbusAgentPipeListener? _nimbusPipe;
    private DesktopNimbusWindow? _desktopNimbus;
    private NimbusState? _hookAgentState;
    private string? _hookDetail;
    private DateTimeOffset _hookExpiresUtc;
    private NimbusHookEvent? _pendingPrompt;
    private DateTimeOffset _pendingPromptExpiresUtc;
    private NimbusState _lastResolvedNimbusState = NimbusState.Idle;
    private DateTimeOffset _lastAgentActivityUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastMouseNearUtc = DateTimeOffset.UtcNow;
    private bool _hoverPeek;
    private bool _roamingOnDesktop;
    private bool _wasMailbox;
    private bool _yawnedBeforeSleep;
    private bool _notchHiddenAtIdle;

    private bool IsNimbusOnlyNotch =>
        Settings.NimbusEnabled && Settings.NimbusCollapseToCharacter && !_roamingOnDesktop && !_expanded && !_hoverPeek && !_preview && !CompactColumnMode
        && EffectiveStyle != CompactStyle.Line && _alert is null && _pendingPrompt is null;

    internal bool IsNimbusActionPill =>
        Settings.NimbusEnabled && _pendingPrompt is not null && !_expanded && !_preview && !CompactColumnMode
        && EffectiveStyle != CompactStyle.Line && _alert is null;

    internal void SetHoverPeek(bool peek)
    {
        if (_hoverPeek == peek) return;
        bool wasNimbusOnly = IsNimbusOnlyNotch;
        _hoverPeek = peek;
        if (peek)
        {
            _lastMouseNearUtc = DateTimeOffset.UtcNow;
            _compactNimbus.TriggerEmote(NimbusEmote.Happy, 0.9);
        }
        if (wasNimbusOnly != IsNimbusOnlyNotch)
        {
            RenderCompact();
            FitCompactToContent();
        }
    }

    internal void UpdateNimbusGlobalLook(Point cursorScreen)
    {
        if (_desktopNimbus is { IsVisible: true } desktop)
        {
            var dpi = VisualTreeHelper.GetDpi(desktop);
            var dCenter = new Point((desktop.Left + desktop.Width / 2.0) * dpi.DpiScaleX, (desktop.Top + desktop.Height / 2.0) * dpi.DpiScaleY);
            double ddx = cursorScreen.X - dCenter.X;
            double ddy = cursorScreen.Y - dCenter.Y;
            double nearPx = 150.0 * dpi.DpiScaleX;
            if (ddx * ddx + ddy * ddy < nearPx * nearPx)
            {
                _lastMouseNearUtc = DateTimeOffset.UtcNow;
                if (_lastResolvedNimbusState == NimbusState.Sleeping)
                    UpdateNimbusStatus();
            }
            double dnx = Math.Clamp(ddx / 480.0, -1, 1);
            double dny = Math.Clamp(-ddy / 360.0, -1, 1);
            desktop.Nimbus.SetGlobalLook(dnx, dny);
        }

        if (_expanded || PresentationSource.FromVisual(Island) is null || !(Island.ActualWidth > 0)) return;
        var center = Island.PointToScreen(new Point(Island.ActualWidth / 2.0, Island.ActualHeight / 2.0));
        double dx = cursorScreen.X - center.X;
        double dy = cursorScreen.Y - center.Y;
        if (dx * dx + dy * dy < 280.0 * 280.0)
        {
            _lastMouseNearUtc = DateTimeOffset.UtcNow;
            if (_lastResolvedNimbusState == NimbusState.Sleeping)
                UpdateNimbusStatus();
        }

        double nx = Math.Clamp(dx / 480.0, -1, 1);
        double ny = Math.Clamp(-dy / 360.0, -1, 1);
        _compactNimbus.SetGlobalLook(nx, ny);
    }

    private void InitializeNimbus()
    {
        _nimbusPoll.Tick += (_, _) => UpdateNimbusStatus();
        _nimbusPoll.Start();

        _compactNimbus.Clicked += () => { if (!_expanded && !_preview && !_moveArmed && !IsSuppressed) OpenAtPointer(); };
        ExpandedNimbus.DragOutRequested += OnNimbusDragOutRequested;
        AttachNimbusContextMenu(_compactNimbus, isDesktop: false);
        AttachNimbusContextMenu(ExpandedNimbus, isDesktop: false);

        // Only the lead panel owns the named pipe listener to avoid duplicate pipe servers on multi-monitor setups.
        if (_lead is null)
        {
            _nimbusPipe = new NimbusAgentPipeListener(ev =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var now = DateTimeOffset.UtcNow;
                    _hookAgentState = ev.State;
                    _hookDetail = ev.Detail;
                    _hookExpiresUtc = now.AddSeconds(ev.State is NimbusState.Approval or NimbusState.Question ? 120 : 15);
                    if (ev.State is not NimbusState.Idle and not NimbusState.Sleeping)
                        _lastAgentActivityUtc = now;

                    bool promptChanged = false;
                    if (ev.State is NimbusState.Approval or NimbusState.Question)
                    {
                        _pendingPrompt = ev;
                        _pendingPromptExpiresUtc = now.AddSeconds(120);
                        promptChanged = true;
                    }
                    else if (_pendingPrompt is not null)
                    {
                        _pendingPrompt = null;
                        promptChanged = true;
                    }

                    string key = string.IsNullOrWhiteSpace(ev.SessionId) ? "default" : ev.SessionId;
                    _sessionTasks[key] = new NimbusAgentTask(key, ev.State, ev.Detail, now);

                    UpdateNimbusStatus();
                    if (promptChanged && !_expanded && IsLoaded)
                    {
                        RenderCompact();
                        FitCompactToContent();
                    }
                });
            });

            _app.Updates.Changed += OnUpdatesChangedForNimbus;
        }
        _app.Weather.Changed += OnWeatherChangedForNimbus;

        StartConversationWatchers();

        Closed += (_, _) =>
        {
            _quickAskCts?.Cancel();
            _nimbusPoll.Stop();
            _nimbusPipe?.Dispose();
            foreach (var watcher in _conversationWatchers)
                watcher.Dispose();
            _conversationWatchers.Clear();
            if (_lead is null) _app.Updates.Changed -= OnUpdatesChangedForNimbus;
            _app.Weather.Changed -= OnWeatherChangedForNimbus;
            _desktopNimbus?.Close();
            _desktopNimbus = null;
        };

        ApplyNimbusSettings();
        UpdateNimbusStatus();

        if (!CanAnimateLifecycle)
            TriggerNimbusLaunchGreetingIfEnabled();

        if (_lead is null && Settings.NimbusEnabled && Settings.NimbusDesktopRoamingEnabled
            && Settings.NimbusDesktopX is { } sx && Settings.NimbusDesktopY is { } sy)
        {
            Loaded += (_, _) =>
            {
                if (_roamingOnDesktop || _desktopNimbus is { IsVisible: true }) return;
                if (Settings.NimbusEnabled && Settings.NimbusDesktopRoamingEnabled
                    && Settings.NimbusDesktopX is { } lx && Settings.NimbusDesktopY is { } ly)
                {
                    ShowDesktopNimbusAt(lx, ly);
                }
            };
        }
    }

    private void StartConversationWatchers()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = AntigravityUsage.Folders(home);

        _ = Task.Run(() =>
        {
            if (NimbusCompanionLogic.LatestConversationWriteUtc(folders) is { } initial)
                RecordConversationWrite(initial);
        });

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder, "*.db*")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                void OnChange(object _, FileSystemEventArgs __) => RecordConversationWrite(DateTimeOffset.UtcNow);
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Renamed += (_, _) => RecordConversationWrite(DateTimeOffset.UtcNow);
                _conversationWatchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore inaccessible conversation folders.
            }
        }
    }

    private void RecordConversationWrite(DateTimeOffset utc)
    {
        long next = utc.UtcTicks;
        while (true)
        {
            long current = Volatile.Read(ref _latestConversationWriteTicks);
            if (next <= current) break;
            if (Interlocked.CompareExchange(ref _latestConversationWriteTicks, next, current) == current)
                break;
        }
    }

    private bool _launchGreeted;
    internal void TriggerNimbusLaunchGreetingIfEnabled()
    {
        if (_launchGreeted || !Settings.NimbusEnabled) return;
        _launchGreeted = true;
        if (Settings.NimbusLaunchGreetingEnabled)
        {
            _compactNimbus.StartLaunchGreeting();
            ExpandedNimbus.StartLaunchGreeting();
            _desktopNimbus?.Nimbus.StartLaunchGreeting();
        }
        else
        {
            ExpandedNimbus.Greet();
        }
    }

    private void OnUpdatesChangedForNimbus() => Dispatcher.BeginInvoke(ApplyNimbusSettings);

    private void OnWeatherChangedForNimbus() => Dispatcher.BeginInvoke(() =>
    {
        ApplyNimbusWeather();
        UpdateNimbusStatus();
        if (!_expanded && IsLoaded) RenderCompact();
        if (_expanded && IsWeatherPage) RenderWeather();
    });

    internal void ApplyNimbusWeather()
    {
        var kind = Settings.NimbusEnabled && Settings.NimbusWeatherEnabled
            ? (_app.Weather.Current?.Kind ?? WeatherKind.None)
            : WeatherKind.None;
        _compactNimbus.SetWeather(kind);
        ExpandedNimbus.SetWeather(kind);
        _desktopNimbus?.Nimbus.SetWeather(kind);
    }

    internal void ApplyNimbusSettings()
    {
        NimbusSound.Enabled = Settings.NimbusSoundsEnabled && !Settings.NimbusDoNotDisturb;

        _compactNimbus.SetOutfit(Settings.NimbusOutfit);
        ExpandedNimbus.SetOutfit(Settings.NimbusOutfit);
        _desktopNimbus?.Nimbus.SetOutfit(Settings.NimbusOutfit);
        ApplyNimbusWeather();

        bool allowDrag = _lead is null && Settings.NimbusEnabled && Settings.NimbusDesktopRoamingEnabled;
        _compactNimbus.AllowDragOut = false;
        ExpandedNimbus.AllowDragOut = allowDrag;

        if (!Settings.NimbusEnabled || !Settings.NimbusDesktopRoamingEnabled)
            ReturnNimbusToIsland(clearSavedPosition: false);

        Color? hint = Settings.NimbusEnabled && Settings.NimbusUpdateHintEnabled && _app.Updates.HasUpdate
            ? Color.FromRgb(56, 189, 248)
            : null;
        _compactNimbus.SetUpdateHint(hint);
        ExpandedNimbus.SetUpdateHint(hint);
        _desktopNimbus?.Nimbus.SetUpdateHint(hint);

        bool mailbox = hint is not null && _app.Updates.Stage == AppUpdateStage.Ready;
        _compactNimbus.SetMailbox(mailbox);
        ExpandedNimbus.SetMailbox(mailbox);
        _desktopNimbus?.Nimbus.SetMailbox(mailbox);
        if (mailbox && !_wasMailbox)
        {
            ExpandedNimbus.Gulp();
            _desktopNimbus?.Nimbus.Gulp();
        }
        _wasMailbox = mailbox;

        ExpandedNimbus.Visibility = Settings.NimbusEnabled && !_roamingOnDesktop ? Visibility.Visible : Visibility.Collapsed;
        UpdateNimbusDance();
        RenderMiniNimbus();
    }

    private void OnNimbusDragOutRequested(Point cursorScreen)
    {
        if (_lead is not null || !Settings.NimbusEnabled || !Settings.NimbusDesktopRoamingEnabled) return;
        _islandPressed = false;
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double left = cursorScreen.X / scaleX - 100;
        double top = cursorScreen.Y / scaleY - 116;
        ShowDesktopNimbusAt(left, top);
        _desktopNimbus?.BeginExternalDrag(cursorScreen);
        SaveDesktopNimbusPosition(left, top);
    }

    private void ShowDesktopNimbusAt(double left, double top)
    {
        if (_desktopNimbus is null)
        {
            _desktopNimbus = new DesktopNimbusWindow();
            _desktopNimbus.PositionCommitted += SaveDesktopNimbusPosition;
            _desktopNimbus.ReturnToIslandRequested += () => ReturnNimbusToIsland(clearSavedPosition: true);
            _desktopNimbus.OpenSettingsRequested += () => _app.OpenSettings();
            _desktopNimbus.Clicked += () => { _lastMouseNearUtc = DateTimeOffset.UtcNow; UpdateNimbusStatus(); };
            AttachNimbusContextMenu(_desktopNimbus, isDesktop: true);
        }
        _desktopNimbus.Left = left;
        _desktopNimbus.Top = top;
        _desktopNimbus.Nimbus.SetOutfit(Settings.NimbusOutfit);
        _desktopNimbus.Nimbus.SetWeather(Settings.NimbusEnabled && Settings.NimbusWeatherEnabled ? (_app.Weather.Current?.Kind ?? WeatherKind.None) : WeatherKind.None);
        _desktopNimbus.Nimbus.SetDancing(Settings.NimbusEnabled && Settings.NimbusDanceEnabled && MusicAudioActive);
        if (!_desktopNimbus.IsVisible) _desktopNimbus.Show();
        _roamingOnDesktop = true;
        ExpandedNimbus.Visibility = Visibility.Collapsed;
        if (IsLoaded)
        {
            RenderCompact();
            FitCompactToContent();
        }
    }

    internal void ReturnNimbusToIsland(bool clearSavedPosition = true)
    {
        if (_desktopNimbus is not null)
        {
            _desktopNimbus.Hide();
        }
        bool wasRoaming = _roamingOnDesktop;
        _roamingOnDesktop = false;
        if (clearSavedPosition && (Settings.NimbusDesktopX is not null || Settings.NimbusDesktopY is not null))
        {
            var next = Settings.Copy();
            next.NimbusDesktopX = null;
            next.NimbusDesktopY = null;
            _app.UpdateSettings(next);
        }
        if (wasRoaming)
        {
            ExpandedNimbus.Visibility = Settings.NimbusEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (IsLoaded)
            {
                RenderCompact();
                FitCompactToContent();
            }
        }
    }

    private void SaveDesktopNimbusPosition(double left, double top)
    {
        var next = Settings.Copy();
        next.NimbusDesktopX = left;
        next.NimbusDesktopY = top;
        _app.UpdateSettings(next);
    }

    internal void UpdateNimbusStatus()
    {
        var now = DateTimeOffset.UtcNow;
        if (_hookAgentState is not null && now >= _hookExpiresUtc)
        {
            _hookAgentState = null;
            _hookDetail = null;
        }
        if (_pendingPrompt is not null && now >= _pendingPromptExpiresUtc)
        {
            _pendingPrompt = null;
            if (!_expanded && IsLoaded)
            {
                RenderCompact();
                FitCompactToContent();
            }
        }

        // Prune stale multi-agent session tasks (older than 30 s)
        foreach (var staleKey in _sessionTasks.Where(kv => (now - kv.Value.UpdatedUtc).TotalSeconds > 30).Select(kv => kv.Key).ToList())
            _sessionTasks.Remove(staleKey);

        long ticks = Volatile.Read(ref _latestConversationWriteTicks);
        DateTimeOffset? lastWrite = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        if (lastWrite is { } lw && lw > _lastAgentActivityUtc && (now - lw).TotalSeconds < 30)
            _lastAgentActivityUtc = lw;

        double? remaining = NimbusCompanionLogic.ExtractAntigravityRemainingPercent(Snapshots);
        var (justReset, eta) = _quotaTracker.Update(remaining, now);

        var status = NimbusCompanionLogic.Resolve(
            _hookAgentState,
            lastWrite,
            now,
            remaining,
            _hookDetail,
            NimbusCompanionLogic.ExtractExhaustedAntigravityPools(Snapshots));

        if (eta is { } timeLeft && status.DetailText.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            string etaText = timeLeft.TotalHours >= 1
                ? $"~{(int)timeLeft.TotalHours}h {timeLeft.Minutes}m left at current pace"
                : $"~{Math.Max(1, (int)Math.Round(timeLeft.TotalMinutes))}m left at current pace";
            status = status with { DetailText = $"{status.DetailText} ({etaText})" };
        }

        // Auto-sleep when idle and both agent & mouse have been inactive for NimbusAutoSleepMinutes
        bool dancing = Settings.NimbusEnabled && Settings.NimbusDanceEnabled && MusicAudioActive;
        if (Settings.NimbusAutoSleepEnabled && status.State == NimbusState.Idle && !dancing && !_hoverPeek && !_expanded)
        {
            var lastActive = _lastAgentActivityUtc > _lastMouseNearUtc ? _lastAgentActivityUtc : _lastMouseNearUtc;
            var idleFor = now - lastActive;
            var sleepAfter = TimeSpan.FromMinutes(Math.Max(1, Settings.NimbusAutoSleepMinutes));
            if (idleFor >= sleepAfter)
            {
                status = status with
                {
                    State = NimbusState.Sleeping,
                    StatusTitle = "Sleeping",
                    DetailText = "Zzz... idle for a while"
                };
            }
            else if (!_yawnedBeforeSleep && idleFor >= sleepAfter - TimeSpan.FromSeconds(15))
            {
                _yawnedBeforeSleep = true;
                _compactNimbus.TriggerEmote(NimbusEmote.Yawn, 1.8);
                ExpandedNimbus.TriggerEmote(NimbusEmote.Yawn, 1.8);
                _desktopNimbus?.Nimbus.TriggerEmote(NimbusEmote.Yawn, 1.8);
            }
        }
        if (status.State != NimbusState.Idle) _yawnedBeforeSleep = false;

        // Auto-retract roaming desktop Nimbus back to the island when an urgent notification/alert arrives
        if (_roamingOnDesktop && status.State is NimbusState.Approval or NimbusState.Question or NimbusState.Error)
        {
            ReturnNimbusToIsland(clearSavedPosition: false);
            ExpandedNimbus.TriggerEmote(NimbusEmote.Surprised, 0.9);
        }

        // Wake up surprised when transitioning out of Sleeping into Idle
        if (_lastResolvedNimbusState == NimbusState.Sleeping && status.State == NimbusState.Idle)
        {
            _compactNimbus.TriggerEmote(NimbusEmote.Surprised, 0.9);
            ExpandedNimbus.TriggerEmote(NimbusEmote.Surprised, 0.9);
            _desktopNimbus?.Nimbus.TriggerEmote(NimbusEmote.Surprised, 0.9);
        }

        // Trigger a brief celebration emote when transitioning into Finished while quota is healthy.
        if (status.State == NimbusState.Finished && _lastResolvedNimbusState != NimbusState.Finished
            && status.QuotaMood == NimbusQuotaMood.Energetic)
        {
            ExpandedNimbus.TriggerEmote(NimbusEmote.Proud, 1.6);
            _desktopNimbus?.Nimbus.TriggerEmote(NimbusEmote.Proud, 1.6);
        }
        if (justReset)
        {
            _compactNimbus.TriggerEmote(NimbusEmote.Proud, 2.0);
            ExpandedNimbus.TriggerEmote(NimbusEmote.Proud, 2.0);
            _desktopNimbus?.Nimbus.TriggerEmote(NimbusEmote.Proud, 2.0);
            if (_roamingOnDesktop && _desktopNimbus is { IsVisible: true } && !Settings.NimbusDoNotDisturb)
                _desktopNimbus.ShowBubble("Quota reset!");
        }
        else if (_roamingOnDesktop && _desktopNimbus is { IsVisible: true } && status.State != _lastResolvedNimbusState
            && (!Settings.NimbusDoNotDisturb || status.State == NimbusState.Error)
            && status.State is NimbusState.Finished or NimbusState.Error or NimbusState.RateLimit or NimbusState.Working)
        {
            _desktopNimbus.ShowBubble(status.StatusTitle);
        }

        _lastResolvedNimbusState = status.State;

        if (status.State == NimbusState.Idle && Settings.NimbusEnabled && Settings.NimbusWeatherEnabled && _app.Weather.Current is { } wx)
        {
            string temp = WeatherLogic.FormatTemp(wx.TempC, Settings.WeatherUnit);
            status = status with
            {
                DetailText = $"{wx.LocationName} · {temp} · {wx.ConditionText}\n“{wx.Quip}”"
            };
        }

        bool hideNotch = Settings.NimbusEnabled && Settings.NimbusHideWhenIdle
            && status.State == NimbusState.Sleeping
            && !_expanded && !_hoverPeek && !_preview && _pendingPrompt is null && _alert is null;
        if (hideNotch != _notchHiddenAtIdle)
        {
            _notchHiddenAtIdle = hideNotch;
            Island.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(hideNotch ? 0 : 1, TimeSpan.FromMilliseconds(220)));
            ShadowHost.Visibility = hideNotch ? Visibility.Collapsed : Visibility.Visible;
        }

        ExpandedNimbus.Visibility = Settings.NimbusEnabled && !_roamingOnDesktop ? Visibility.Visible : Visibility.Collapsed;
        _compactNimbus.ApplyStatus(status);
        ExpandedNimbus.ApplyStatus(status);
        _desktopNimbus?.Nimbus.ApplyStatus(status);
        UpdateNimbusStatusChip(status);
        UpdateNimbusDance();
        RenderMiniNimbus();
    }

    private void UpdateNimbusStatusChip(NimbusStatusSnapshot status)
    {
        if (!Settings.NimbusEnabled)
        {
            NimbusStatusChip.Visibility = Visibility.Collapsed;
            return;
        }
        var accent = SessionStateColor(status.State);
        NimbusStatusChip.BorderBrush = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        NimbusStatusChip.Background = new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B));
        NimbusStatusText.Foreground = status.State == NimbusState.Idle
            ? UI.Brush(UI.Secondary)
            : new SolidColorBrush(Color.FromRgb(
                (byte)Math.Min(255, accent.R + 60),
                (byte)Math.Min(255, accent.G + 60),
                (byte)Math.Min(255, accent.B + 60)));
        NimbusStatusText.Text = status.StatusTitle;
        NimbusStatusChip.ToolTip = status.DetailText;
        NimbusStatusChip.Visibility = Visibility.Visible;
    }

    internal void UpdateNimbusDance()
    {
        bool dancing = Settings.NimbusEnabled && Settings.NimbusDanceEnabled && MusicAudioActive;
        _compactNimbus.SetDancing(dancing);
        ExpandedNimbus.SetDancing(dancing);
        _desktopNimbus?.Nimbus.SetDancing(dancing);
        foreach (var mini in _miniBotPills.Values)
            mini.Bot.SetDancing(dancing);
    }

    private void RenderMiniNimbus()
    {
        if (!Settings.NimbusEnabled || !Settings.NimbusMiniBotsEnabled || _sessionTasks.Count <= 1)
        {
            MiniNimbusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var active = _sessionTasks.Values.OrderByDescending(t => t.UpdatedUtc).Take(4).ToList();
        var activeKeys = active.Select(t => t.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var dead in _miniBotPills.Keys.Where(k => !activeKeys.Contains(k)).ToList())
        {
            if (_miniBotPills.TryGetValue(dead, out var entry))
                MiniNimbusPanel.Children.Remove(entry.Pill);
            _miniBotPills.Remove(dead);
        }

        bool dancing = Settings.NimbusDanceEnabled && MusicAudioActive;
        foreach (var task in active)
        {
            if (!_miniBotPills.TryGetValue(task.SessionId, out var entry))
            {
                var bot = new NimbusCompanion { IsMini = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock
                {
                    FontFamily = UI.TextFont,
                    FontSize = 10.5,
                    Foreground = UI.Brush(UI.Secondary),
                    Margin = new Thickness(4, 0, 2, 0),
                    MaxWidth = 68,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(bot);
                row.Children.Add(label);
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(4, 1, 5, 1),
                    Margin = new Thickness(2, 0, 2, 0),
                    BorderThickness = new Thickness(1),
                    Child = row
                };
                entry = (pill, bot, label);
                _miniBotPills[task.SessionId] = entry;
                MiniNimbusPanel.Children.Add(pill);
            }

            var accent = SessionStateColor(task.State);
            entry.Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(95, accent.R, accent.G, accent.B));
            entry.Pill.Background = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B));
            entry.Label.Text = FormatSessionLabel(task.SessionId, task.Detail);
            entry.Pill.ToolTip = $"{task.SessionId} · {task.State}" + (string.IsNullOrWhiteSpace(task.Detail) ? "" : $"\n{task.Detail}");

            entry.Bot.SetDancing(dancing);
            entry.Bot.SetPermanentEmote(task.State switch
            {
                NimbusState.Finished => NimbusEmote.Happy,
                NimbusState.Error => NimbusEmote.Annoyed,
                NimbusState.Approval => NimbusEmote.Wink,
                _ => null
            });
            entry.Bot.ApplyStatus(new NimbusStatusSnapshot(task.State, NimbusQuotaMood.Energetic, 100, task.State.ToString(), task.Detail ?? task.SessionId));
        }

        MiniNimbusPanel.Visibility = Visibility.Visible;
    }

    private static string FormatSessionLabel(string sessionId, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail)) return detail;
        if (sessionId.Length <= 8) return sessionId;
        return sessionId[..6] + "…";
    }

    private static Color SessionStateColor(NimbusState state) => state switch
    {
        NimbusState.Working => Color.FromRgb(59, 158, 255),
        NimbusState.Thinking => Color.FromRgb(139, 92, 246),
        NimbusState.Searching => Color.FromRgb(99, 102, 241),
        NimbusState.Approval => Color.FromRgb(245, 165, 36),
        NimbusState.Question => Color.FromRgb(34, 211, 238),
        NimbusState.Error => Color.FromRgb(244, 80, 94),
        NimbusState.Finished => Color.FromRgb(52, 211, 153),
        NimbusState.RateLimit => Color.FromRgb(251, 146, 60),
        _ => Color.FromRgb(148, 163, 184)
    };

    private static readonly (NimbusOutfit Outfit, string En)[] NimbusWardrobeItems =
    [
        (NimbusOutfit.Auto, "Auto (Seasonal)"),
        (NimbusOutfit.None, "None"),
        (NimbusOutfit.SantaHat, "Santa Hat"),
        (NimbusOutfit.PartyHat, "Party Hat"),
        (NimbusOutfit.WitchHat, "Witch Hat"),
        (NimbusOutfit.Sunglasses, "Sunglasses"),
        (NimbusOutfit.RoundGlasses, "Round Glasses"),
        (NimbusOutfit.Bow, "Ribbon Bow"),
        (NimbusOutfit.Scarf, "Winter Scarf"),
        (NimbusOutfit.Pumpkin, "Pumpkin")
    ];

    private bool _nimbusMenuOpen;
    internal bool NimbusHoldsMenu => _nimbusMenuOpen || _quickAskBackend?.IsDropDownOpen == true;
    internal bool QuickAskHolds => QuickAskOverlay.Visibility == Visibility.Visible && !_pressedOutside
        && (IsActive || _pointerInside || _quickAskCts is not null || _quickAskBackend?.IsDropDownOpen == true);

    internal bool QuickAskEscape()
    {
        if (!_expanded || QuickAskOverlay.Visibility != Visibility.Visible) return false;
        CloseQuickAsk();
        return true;
    }

    private void AttachNimbusContextMenu(FrameworkElement target, bool isDesktop)
    {
        var menu = new ContextMenu();
        target.ContextMenu = menu;
        target.ContextMenuOpening += (_, _) =>
        {
            _nimbusMenuOpen = true;
            _hover.Stop();
            _leave.Stop();
            PopulateNimbusContextMenu(menu, isDesktop);
        };
        menu.Opened += (_, _) =>
        {
            _nimbusMenuOpen = true;
            _hover.Stop();
            _leave.Stop();
        };
        menu.Closed += (_, _) =>
        {
            _nimbusMenuOpen = false;
            Dispatcher.BeginInvoke(() =>
            {
                if (_expanded && !_pointerInside && !ShelfHolds && !NimbusHoldsMenu && !QuickAskHolds) PointerChanged(false);
                else if (!_expanded && _hoverPeek && !_preview && !_pointerInside) SetHoverPeek(false);
            });
        };
    }

    private void PopulateNimbusContextMenu(ContextMenu menu, bool isDesktop)
    {
        menu.Items.Clear();

        var wardrobe = new MenuItem { Header = "Wardrobe" };
        foreach (var (outfit, en) in NimbusWardrobeItems)
        {
            var item = new MenuItem
            {
                Header = en,
                IsCheckable = true,
                IsChecked = Settings.NimbusOutfit == outfit
            };
            item.Click += (_, _) =>
            {
                var next = Settings.Copy();
                next.NimbusOutfit = outfit;
                _app.UpdateSettings(next);
            };
            wardrobe.Items.Add(item);
        }
        menu.Items.Add(wardrobe);

        var sounds = new MenuItem
        {
            Header = "Sounds",
            IsCheckable = true,
            IsChecked = Settings.NimbusSoundsEnabled
        };
        sounds.Click += (_, _) =>
        {
            var next = Settings.Copy();
            next.NimbusSoundsEnabled = !Settings.NimbusSoundsEnabled;
            _app.UpdateSettings(next);
        };
        menu.Items.Add(sounds);

        var dnd = new MenuItem
        {
            Header = "Do Not Disturb",
            IsCheckable = true,
            IsChecked = Settings.NimbusDoNotDisturb
        };
        dnd.Click += (_, _) =>
        {
            var next = Settings.Copy();
            next.NimbusDoNotDisturb = !Settings.NimbusDoNotDisturb;
            _app.UpdateSettings(next);
        };
        menu.Items.Add(dnd);

        if (_lead is null && Settings.NimbusDesktopRoamingEnabled)
        {
            menu.Items.Add(new Separator());
            if (isDesktop || _roamingOnDesktop)
            {
                var dock = new MenuItem { Header = "Return to Island" };
                dock.Click += (_, _) => ReturnNimbusToIsland(clearSavedPosition: true);
                menu.Items.Add(dock);
            }
            else
            {
                var roam = new MenuItem { Header = "Send to Desktop" };
                roam.Click += (_, _) =>
                {
                    var center = PointToScreen(new Point(ActualWidth / 2.0, ActualHeight + 40));
                    OnNimbusDragOutRequested(center);
                };
                menu.Items.Add(roam);
            }
        }

        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Nimbus Settings" };
        settings.Click += (_, _) => _app.OpenSettings();
        menu.Items.Add(settings);
    }

    internal (UIElement Content, double WantedWidth) BuildNimbusActionPill(double scale, double rowHeight)
    {
        var prompt = _pendingPrompt!;
        double miniSize = CompactMusicLayout.Whole(Math.Max(22 * scale, rowHeight - 2));
        var nimbus = AcquireCompactNimbus(miniSize);
        nimbus.Width = miniSize;
        nimbus.Height = miniSize;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new Border
        {
            Width = miniSize,
            Height = miniSize,
            Margin = new Thickness(0, 0, 6 * scale, 0),
            Child = nimbus
        });

        string prefix = prompt.State == NimbusState.Approval
            ? "Approve"
            : "Question";
        string labelText = string.IsNullOrWhiteSpace(prompt.Detail) ? prefix : $"{prefix}: {prompt.Detail}";
        var label = UI.Fixed(labelText, Math.Max(9, 11 * scale), Math.Max(14, 16 * scale), UI.Primary, FontWeights.SemiBold);
        label.MaxWidth = 170 * scale;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Margin = new Thickness(0, 0, 8 * scale, 0);
        label.ToolTip = labelText;
        panel.Children.Add(label);

        Button MakePillButton(string text, Color bg, Color fg, string decision, NimbusState nextState)
        {
            var btn = new Button
            {
                Content = text,
                Height = Math.Max(18, 20 * scale),
                Padding = new Thickness(8 * scale, 0, 8 * scale, 0),
                Margin = new Thickness(4 * scale, 0, 0, 0),
                FontFamily = UI.TextFont,
                FontSize = Math.Max(9, 10.5 * scale),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg),
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Resources[typeof(Border)] = new Style(typeof(Border))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(9 * scale)) }
            };
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                ResolvePendingPrompt(prompt, decision, nextState);
            };
            return btn;
        }

        if (prompt.State == NimbusState.Approval)
        {
            panel.Children.Add(MakePillButton("Allow", Color.FromRgb(52, 211, 153), Color.FromRgb(10, 20, 16), "allow", NimbusState.Working));
            panel.Children.Add(MakePillButton("Deny", Color.FromArgb(64, 244, 80, 94), Color.FromRgb(255, 220, 224), "deny", NimbusState.Idle));
        }
        else if (prompt.Options.Count > 0)
        {
            foreach (var opt in prompt.Options.Take(3))
            {
                string shortOpt = opt.Length > 14 ? opt[..13] + "…" : opt;
                panel.Children.Add(MakePillButton(shortOpt, Color.FromArgb(56, 34, 211, 238), Color.FromRgb(224, 249, 255), opt, NimbusState.Working));
            }
        }
        else
        {
            panel.Children.Add(MakePillButton("OK", Color.FromArgb(56, 34, 211, 238), Color.FromRgb(224, 249, 255), "ok", NimbusState.Working));
        }

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double wanted = Math.Ceiling(panel.DesiredSize.Width + 24 * scale);
        return (panel, wanted);
    }

    private void ResolvePendingPrompt(NimbusHookEvent prompt, string decision, NimbusState nextState)
    {
        if (!string.IsNullOrWhiteSpace(prompt.RequestId))
            _nimbusPipe?.TryRespond(prompt.RequestId, decision);

        _pendingPrompt = null;
        _hookAgentState = nextState;
        _hookDetail = nextState == NimbusState.Working ? prompt.Detail : null;
        _hookExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(nextState == NimbusState.Working ? 15 : 1);
        UpdateNimbusStatus();
        if (!_expanded && IsLoaded)
        {
            RenderCompact();
            FitCompactToContent();
        }
    }

    private CancellationTokenSource? _quickAskCts;
    private TextBox? _quickAskInput;
    private TextBox? _quickAskOutput;
    private ComboBox? _quickAskBackend;
    private Button? _quickAskSend;

    private void NimbusStatusChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (QuickAskOverlay.Visibility == Visibility.Visible)
            CloseQuickAsk();
        else
            OpenQuickAsk();
    }

    internal void CloseQuickAsk()
    {
        if (QuickAskOverlay.Visibility != Visibility.Visible) return;
        QuickAskOverlay.Visibility = Visibility.Collapsed;
        if (IsActive)
        {
            EndTyping();
            System.Windows.Input.Keyboard.ClearFocus();
        }
    }

    private void OpenQuickAsk()
    {
        EnsureQuickAskBuilt();
        RefreshQuickAskBackends();
        QuickAskOverlay.Visibility = Visibility.Visible;
        BeginTyping(_quickAskInput);
    }

    private void RefreshQuickAskBackends()
    {
        if (_quickAskBackend is null) return;
        var backends = NimbusQuickAsk.DetectBackends();
        if (backends.Count == 0)
            backends = NimbusQuickAsk.DetectBackends(refresh: true);
        var items = backends.Count > 0 ? backends.Select(b => b.Backend.Name).ToList() : new List<string> { "No CLI found" };
        if (_quickAskBackend.ItemsSource is IEnumerable<string> current && current.SequenceEqual(items))
        {
            _quickAskBackend.IsEnabled = backends.Count > 0;
            return;
        }
        string? prev = _quickAskBackend.SelectedItem as string;
        _quickAskBackend.ItemsSource = items;
        _quickAskBackend.IsEnabled = backends.Count > 0;
        int nextIdx = prev is not null ? items.IndexOf(prev) : -1;
        _quickAskBackend.SelectedIndex = nextIdx >= 0 ? nextIdx : 0;
        if (_quickAskOutput is not null && backends.Count > 0 && _quickAskOutput.Text == "No claude / gemini / codex / agy CLI detected on PATH.")
            _quickAskOutput.Text = "Ask anything using your local signed-in AI CLI (no extra API keys needed).";
    }

    private void EnsureQuickAskBuilt()
    {
        if (QuickAskOverlay.Children.Count > 0) return;

        var backends = NimbusQuickAsk.DetectBackends();
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 20, 22, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock
        {
            Text = "Nimbus Quick Ask",
            FontFamily = UI.TextFont,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(title);

        var rightHeader = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _quickAskBackend = new ComboBox
        {
            Width = 140,
            FontFamily = UI.TextFont,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
            ItemsSource = backends.Count > 0 ? backends.Select(b => b.Backend.Name).ToList() : ["No CLI found"],
            SelectedIndex = 0,
            IsEnabled = backends.Count > 0
        };
        _quickAskBackend.DropDownClosed += (_, _) =>
        {
            if (QuickAskOverlay.Visibility == Visibility.Visible)
                BeginTyping(_quickAskInput);
        };
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            FontSize = 11,
            Foreground = UI.Brush(UI.Secondary),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        closeBtn.Click += (_, _) => CloseQuickAsk();
        rightHeader.Children.Add(_quickAskBackend);
        rightHeader.Children.Add(closeBtn);
        header.Children.Add(rightHeader);
        grid.Children.Add(header);

        _quickAskOutput = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Foreground = UI.Brush(UI.Primary),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            FontFamily = UI.TextFont,
            FontSize = 11.5,
            Text = backends.Count > 0
                ? "Ask anything using your local signed-in AI CLI (no extra API keys needed)."
                : "No claude / gemini / codex / agy CLI detected on PATH."
        };
        TakeKeyboardOnClick(_quickAskOutput);
        Grid.SetRow(_quickAskOutput, 1);
        grid.Children.Add(_quickAskOutput);

        var inputRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputHost = new Grid();
        _quickAskInput = new TextBox
        {
            Style = (Style)FindResource("ShelfTextBox"),
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = UI.TextFont,
            FontSize = 11.5
        };
        var placeholder = new TextBlock
        {
            Text = "Ask Nimbus anything…",
            FontFamily = UI.TextFont,
            FontSize = 11.5,
            Foreground = UI.Brush(UI.Tertiary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            IsHitTestVisible = false
        };
        _quickAskInput.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _quickAskInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        TakeKeyboardOnClick(_quickAskInput);
        _quickAskInput.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                _ = SendQuickAskAsync();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                CloseQuickAsk();
            }
        };
        inputHost.Children.Add(_quickAskInput);
        inputHost.Children.Add(placeholder);
        inputRow.Children.Add(inputHost);

        _quickAskSend = new Button
        {
            Content = "Ask",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = UI.TextFont,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(12, 18, 28)),
            Background = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _quickAskSend.Click += (_, _) => _ = SendQuickAskAsync();
        Grid.SetColumn(_quickAskSend, 1);
        inputRow.Children.Add(_quickAskSend);

        Grid.SetRow(inputRow, 2);
        grid.Children.Add(inputRow);

        root.Child = grid;
        QuickAskOverlay.Children.Add(root);
    }

    private async Task SendQuickAskAsync()
    {
        if (_quickAskInput is null || _quickAskOutput is null || _quickAskSend is null || _quickAskBackend is null) return;
        string prompt = _quickAskInput.Text.Trim();
        if (prompt.Length == 0) return;

        var backends = NimbusQuickAsk.DetectBackends();
        if (backends.Count == 0)
        {
            backends = NimbusQuickAsk.DetectBackends(refresh: true);
            RefreshQuickAskBackends();
        }
        if (backends.Count == 0)
        {
            _quickAskOutput.Text = "No claude / gemini / codex / agy CLI detected on PATH.";
            return;
        }
        int idx = _quickAskBackend.SelectedIndex;
        if (idx < 0 || idx >= backends.Count) idx = 0;
        var (backend, resolved) = backends[idx];

        _quickAskCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _quickAskCts = cts;
        var ct = cts.Token;

        _quickAskInput.Text = "";
        _quickAskSend.IsEnabled = false;
        _quickAskOutput.Text = $"Asking {backend.Name}…";

        _hookAgentState = NimbusState.Thinking;
        _hookDetail = $"Quick Ask ({backend.Name})";
        _hookExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(65);
        UpdateNimbusStatus();

        try
        {
            var (ok, output) = await NimbusQuickAsk.AskAsync(backend, resolved, prompt, ct);
            if (ct.IsCancellationRequested) return;
            _quickAskOutput.Text = output;
            _hookAgentState = ok ? NimbusState.Finished : NimbusState.Error;
            _hookDetail = ok ? backend.Name : "Quick Ask failed";
            _hookExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(8);
            UpdateNimbusStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _quickAskOutput.Text = ex.Message;
            _hookAgentState = NimbusState.Error;
            _hookDetail = ex.Message;
            _hookExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(8);
            UpdateNimbusStatus();
        }
        finally
        {
            if (ReferenceEquals(_quickAskCts, cts))
                _quickAskCts = null;
            cts.Dispose();
            _quickAskSend.IsEnabled = true;
            if (QuickAskOverlay.Visibility == Visibility.Visible && IsActive)
            {
                _quickAskInput.Focus();
                System.Windows.Input.Keyboard.Focus(_quickAskInput);
            }
        }
    }

    private NimbusCompanion AcquireCompactNimbus(double size)
    {
        if (_compactNimbus.Parent is Panel prev)
            prev.Children.Remove(_compactNimbus);
        _compactNimbus.IsMini = size < 26;
        _compactNimbus.Width = size;
        _compactNimbus.Height = size;
        return _compactNimbus;
    }
}
