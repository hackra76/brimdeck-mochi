using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private readonly MochiCompanion _compactMochi = new() { IsMini = true };
    private readonly DispatcherTimer _mochiPoll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<string, MochiAgentTask> _sessionTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MochiCompanion> _miniBotControls = new(StringComparer.Ordinal);

    private MochiAgentPipeListener? _mochiPipe;
    private DesktopMochiWindow? _desktopMochi;
    private MochiState? _hookAgentState;
    private string? _hookDetail;
    private DateTimeOffset _hookExpiresUtc;
    private MochiState _lastResolvedMochiState = MochiState.Idle;
    private DateTimeOffset _lastAgentActivityUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastMouseNearUtc = DateTimeOffset.UtcNow;
    private bool _hoverPeek;
    private bool _roamingOnDesktop;

    private bool IsMochiOnlyNotch =>
        Settings.MochiEnabled && !_roamingOnDesktop && !_expanded && !_hoverPeek && !_preview && !CompactColumnMode
        && EffectiveStyle != CompactStyle.Line && _alert is null;

    internal void SetHoverPeek(bool peek)
    {
        if (_hoverPeek == peek) return;
        _hoverPeek = peek;
        if (peek)
        {
            _lastMouseNearUtc = DateTimeOffset.UtcNow;
            _compactMochi.TriggerEmote(MochiEmote.Happy, 0.9);
        }
        RenderCompact();
        FitCompactToContent();
    }

    internal void UpdateMochiGlobalLook(Point cursorScreen)
    {
        if (_desktopMochi is { IsVisible: true } desktop)
        {
            var dpi = VisualTreeHelper.GetDpi(desktop);
            var dCenter = new Point((desktop.Left + desktop.Width / 2.0) * dpi.DpiScaleX, (desktop.Top + desktop.Height / 2.0) * dpi.DpiScaleY);
            double dnx = Math.Clamp((cursorScreen.X - dCenter.X) / 480.0, -1, 1);
            double dny = Math.Clamp(-(cursorScreen.Y - dCenter.Y) / 360.0, -1, 1);
            desktop.Mochi.SetGlobalLook(dnx, dny);
        }

        if (_expanded || PresentationSource.FromVisual(Island) is null || !(Island.ActualWidth > 0)) return;
        var center = Island.PointToScreen(new Point(Island.ActualWidth / 2.0, Island.ActualHeight / 2.0));
        double dx = cursorScreen.X - center.X;
        double dy = cursorScreen.Y - center.Y;
        if (dx * dx + dy * dy < 280.0 * 280.0)
            _lastMouseNearUtc = DateTimeOffset.UtcNow;

        double nx = Math.Clamp(dx / 480.0, -1, 1);
        double ny = Math.Clamp(-dy / 360.0, -1, 1);
        _compactMochi.SetGlobalLook(nx, ny);
    }

    private void InitializeMochi()
    {
        _mochiPoll.Tick += (_, _) => UpdateMochiStatus();
        _mochiPoll.Start();

        _compactMochi.DragOutRequested += OnMochiDragOutRequested;
        ExpandedMochi.DragOutRequested += OnMochiDragOutRequested;

        // Only the lead panel owns the named pipe listener to avoid duplicate pipe servers on multi-monitor setups.
        if (_lead is null)
        {
            _mochiPipe = new MochiAgentPipeListener((state, detail, sessionId) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var now = DateTimeOffset.UtcNow;
                    _hookAgentState = state;
                    _hookDetail = detail;
                    _hookExpiresUtc = now.AddSeconds(state == MochiState.Approval ? 120 : 15);
                    if (state is not MochiState.Idle and not MochiState.Sleeping)
                        _lastAgentActivityUtc = now;

                    string key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
                    _sessionTasks[key] = new MochiAgentTask(key, state, detail, now);

                    UpdateMochiStatus();
                });
            });

            _app.Updates.Changed += OnUpdatesChangedForMochi;
        }

        Closed += (_, _) =>
        {
            _mochiPoll.Stop();
            _mochiPipe?.Dispose();
            if (_lead is null) _app.Updates.Changed -= OnUpdatesChangedForMochi;
            _desktopMochi?.Close();
            _desktopMochi = null;
        };

        ApplyMochiSettings();
        UpdateMochiStatus();

        if (Settings.MochiEnabled)
        {
            if (Settings.MochiLaunchGreetingEnabled)
            {
                _compactMochi.StartLaunchGreeting();
                ExpandedMochi.StartLaunchGreeting();
                _desktopMochi?.Mochi.StartLaunchGreeting();
            }
            else
            {
                ExpandedMochi.Greet();
            }
        }

        if (_lead is null && Settings.MochiEnabled && Settings.MochiDesktopRoamingEnabled
            && Settings.MochiDesktopX is { } sx && Settings.MochiDesktopY is { } sy)
        {
            ShowDesktopMochiAt(sx, sy);
        }
    }

    private void OnUpdatesChangedForMochi() => Dispatcher.BeginInvoke(ApplyMochiSettings);

    internal void ApplyMochiSettings()
    {
        MochiSound.Enabled = Settings.MochiSoundsEnabled;

        _compactMochi.SetOutfit(Settings.MochiOutfit);
        ExpandedMochi.SetOutfit(Settings.MochiOutfit);
        _desktopMochi?.Mochi.SetOutfit(Settings.MochiOutfit);

        bool allowDrag = _lead is null && Settings.MochiEnabled && Settings.MochiDesktopRoamingEnabled;
        _compactMochi.AllowDragOut = allowDrag;
        ExpandedMochi.AllowDragOut = allowDrag;

        if (!Settings.MochiEnabled || !Settings.MochiDesktopRoamingEnabled)
            ReturnMochiToIsland(clearSavedPosition: false);

        Color? hint = Settings.MochiEnabled && Settings.MochiUpdateHintEnabled && _app.Updates.HasUpdate
            ? Color.FromRgb(56, 189, 248)
            : null;
        _compactMochi.SetUpdateHint(hint);
        ExpandedMochi.SetUpdateHint(hint);
        _desktopMochi?.Mochi.SetUpdateHint(hint);

        bool mailbox = hint is not null && _app.Updates.Stage == AppUpdateStage.Ready;
        _compactMochi.SetMailbox(mailbox);
        ExpandedMochi.SetMailbox(mailbox);
        _desktopMochi?.Mochi.SetMailbox(mailbox);

        ExpandedMochi.Visibility = Settings.MochiEnabled && !_roamingOnDesktop ? Visibility.Visible : Visibility.Collapsed;
        UpdateMochiDance();
        RenderMiniMochis();
    }

    private void OnMochiDragOutRequested(Point cursorScreen)
    {
        if (_lead is not null || !Settings.MochiEnabled || !Settings.MochiDesktopRoamingEnabled) return;
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double left = cursorScreen.X / scaleX - 62;
        double top = cursorScreen.Y / scaleY - 62;
        ShowDesktopMochiAt(left, top);
        SaveDesktopMochiPosition(left, top);
    }

    private void ShowDesktopMochiAt(double left, double top)
    {
        if (_desktopMochi is null)
        {
            _desktopMochi = new DesktopMochiWindow();
            _desktopMochi.PositionCommitted += SaveDesktopMochiPosition;
            _desktopMochi.ReturnToIslandRequested += () => ReturnMochiToIsland(clearSavedPosition: true);
            _desktopMochi.OpenSettingsRequested += () => _app.OpenSettings();
        }
        _desktopMochi.Left = left;
        _desktopMochi.Top = top;
        _desktopMochi.Mochi.SetOutfit(Settings.MochiOutfit);
        _desktopMochi.Mochi.SetDancing(Settings.MochiEnabled && Settings.MochiDanceEnabled && MusicAudioActive);
        if (!_desktopMochi.IsVisible) _desktopMochi.Show();
        _roamingOnDesktop = true;
        ExpandedMochi.Visibility = Visibility.Collapsed;
        RenderCompact();
        FitCompactToContent();
    }

    internal void ReturnMochiToIsland(bool clearSavedPosition = true)
    {
        if (_desktopMochi is not null)
        {
            _desktopMochi.Hide();
        }
        bool wasRoaming = _roamingOnDesktop;
        _roamingOnDesktop = false;
        if (clearSavedPosition && (Settings.MochiDesktopX is not null || Settings.MochiDesktopY is not null))
        {
            var next = Settings.Copy();
            next.MochiDesktopX = null;
            next.MochiDesktopY = null;
            _app.UpdateSettings(next);
        }
        if (wasRoaming)
        {
            ExpandedMochi.Visibility = Settings.MochiEnabled ? Visibility.Visible : Visibility.Collapsed;
            RenderCompact();
            FitCompactToContent();
        }
    }

    private void SaveDesktopMochiPosition(double left, double top)
    {
        var next = Settings.Copy();
        next.MochiDesktopX = left;
        next.MochiDesktopY = top;
        _app.UpdateSettings(next);
    }

    internal void UpdateMochiStatus()
    {
        var now = DateTimeOffset.UtcNow;
        if (_hookAgentState is not null && now >= _hookExpiresUtc)
        {
            _hookAgentState = null;
            _hookDetail = null;
        }

        // Prune stale multi-agent session tasks (older than 30 s)
        foreach (var staleKey in _sessionTasks.Where(kv => (now - kv.Value.UpdatedUtc).TotalSeconds > 30).Select(kv => kv.Key).ToList())
            _sessionTasks.Remove(staleKey);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = AntigravityUsage.Folders(home);
        var lastWrite = MochiCompanionLogic.LatestConversationWriteUtc(folders);
        if (lastWrite is { } lw && lw > _lastAgentActivityUtc && (now - lw).TotalSeconds < 30)
            _lastAgentActivityUtc = lw;

        double? remaining = MochiCompanionLogic.ExtractAntigravityRemainingPercent(Snapshots);

        var status = MochiCompanionLogic.Resolve(
            _hookAgentState,
            lastWrite,
            now,
            remaining,
            _hookDetail);

        // Auto-sleep when idle and both agent & mouse have been inactive for MochiAutoSleepMinutes
        bool dancing = Settings.MochiEnabled && Settings.MochiDanceEnabled && MusicAudioActive;
        if (Settings.MochiAutoSleepEnabled && status.State == MochiState.Idle && !dancing && !_hoverPeek && !_expanded)
        {
            var lastActive = _lastAgentActivityUtc > _lastMouseNearUtc ? _lastAgentActivityUtc : _lastMouseNearUtc;
            if ((now - lastActive) >= TimeSpan.FromMinutes(Math.Max(1, Settings.MochiAutoSleepMinutes)))
            {
                status = status with
                {
                    State = MochiState.Sleeping,
                    StatusTitle = "Sleeping",
                    DetailText = "Zzz... idle for a while"
                };
            }
        }

        // Auto-retract roaming desktop Mochi back to the island when an urgent alert arrives
        if (_roamingOnDesktop && status.State is MochiState.Approval or MochiState.Question or MochiState.Error or MochiState.RateLimit)
            ReturnMochiToIsland(clearSavedPosition: false);

        // Trigger a brief celebration emote when transitioning into Finished while quota is healthy.
        if (status.State == MochiState.Finished && _lastResolvedMochiState != MochiState.Finished
            && status.QuotaMood == MochiQuotaMood.Energetic)
        {
            ExpandedMochi.TriggerEmote(MochiEmote.Proud, 1.6);
            _desktopMochi?.Mochi.TriggerEmote(MochiEmote.Proud, 1.6);
        }
        _lastResolvedMochiState = status.State;

        ExpandedMochi.Visibility = Settings.MochiEnabled && !_roamingOnDesktop ? Visibility.Visible : Visibility.Collapsed;
        _compactMochi.ApplyStatus(status);
        ExpandedMochi.ApplyStatus(status);
        _desktopMochi?.Mochi.ApplyStatus(status);
        UpdateMochiDance();
        RenderMiniMochis();
    }

    internal void UpdateMochiDance()
    {
        bool dancing = Settings.MochiEnabled && Settings.MochiDanceEnabled && MusicAudioActive;
        _compactMochi.SetDancing(dancing);
        ExpandedMochi.SetDancing(dancing);
        _desktopMochi?.Mochi.SetDancing(dancing);
        foreach (var mini in _miniBotControls.Values)
            mini.SetDancing(dancing);
    }

    private void RenderMiniMochis()
    {
        if (!Settings.MochiEnabled || !Settings.MochiMiniBotsEnabled || _sessionTasks.Count <= 1)
        {
            MiniMochisPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var active = _sessionTasks.Values.OrderByDescending(t => t.UpdatedUtc).Take(4).ToList();
        var activeKeys = active.Select(t => t.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var dead in _miniBotControls.Keys.Where(k => !activeKeys.Contains(k)).ToList())
        {
            if (_miniBotControls.TryGetValue(dead, out var ctrl))
                MiniMochisPanel.Children.Remove(ctrl);
            _miniBotControls.Remove(dead);
        }

        bool dancing = Settings.MochiDanceEnabled && MusicAudioActive;
        foreach (var task in active)
        {
            if (!_miniBotControls.TryGetValue(task.SessionId, out var ctrl))
            {
                ctrl = new MochiCompanion { IsMini = true, Width = 18, Height = 18, Margin = new Thickness(2, 0, 2, 0) };
                _miniBotControls[task.SessionId] = ctrl;
                MiniMochisPanel.Children.Add(ctrl);
            }
            ctrl.SetDancing(dancing);
            ctrl.ApplyStatus(new MochiStatusSnapshot(task.State, MochiQuotaMood.Energetic, 100, task.State.ToString(), task.Detail ?? task.SessionId));
        }

        MiniMochisPanel.Visibility = Visibility.Visible;
    }

    private MochiCompanion AcquireCompactMochi(double size)
    {
        if (_compactMochi.Parent is Panel prev)
            prev.Children.Remove(_compactMochi);
        _compactMochi.Width = size;
        _compactMochi.Height = size;
        return _compactMochi;
    }
}
