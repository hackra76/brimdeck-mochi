using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private readonly MochiCompanion _compactMochi = new() { IsMini = true };
    private readonly DispatcherTimer _mochiPoll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private MochiAgentPipeListener? _mochiPipe;
    private MochiState? _hookAgentState;
    private string? _hookDetail;
    private DateTimeOffset _hookExpiresUtc;
    private MochiState _lastResolvedMochiState = MochiState.Idle;
    private bool _hoverPeek;

    private bool IsMochiOnlyNotch =>
        !_expanded && !_hoverPeek && !_preview && !CompactColumnMode
        && EffectiveStyle != CompactStyle.Line && _alert is null;

    internal void SetHoverPeek(bool peek)
    {
        if (_hoverPeek == peek) return;
        _hoverPeek = peek;
        if (peek) _compactMochi.TriggerEmote(MochiEmote.Happy, 0.9);
        RenderCompact();
        FitCompactToContent();
    }

    internal void UpdateMochiGlobalLook(Point cursorScreen)
    {
        if (_expanded || PresentationSource.FromVisual(Island) is null || !(Island.ActualWidth > 0)) return;
        var center = Island.PointToScreen(new Point(Island.ActualWidth / 2.0, Island.ActualHeight / 2.0));
        double nx = Math.Clamp((cursorScreen.X - center.X) / 480.0, -1, 1);
        double ny = Math.Clamp(-(cursorScreen.Y - center.Y) / 360.0, -1, 1);
        _compactMochi.SetGlobalLook(nx, ny);
    }

    private void InitializeMochi()
    {
        _mochiPoll.Tick += (_, _) => UpdateMochiStatus();
        _mochiPoll.Start();

        // Only the lead panel owns the named pipe listener to avoid duplicate pipe servers on multi-monitor setups.
        if (_lead is null)
        {
            _mochiPipe = new MochiAgentPipeListener((state, detail) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _hookAgentState = state;
                    _hookDetail = detail;
                    _hookExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(state == MochiState.Approval ? 120 : 15);
                    UpdateMochiStatus();
                });
            });
        }

        Closed += (_, _) =>
        {
            _mochiPoll.Stop();
            _mochiPipe?.Dispose();
        };

        UpdateMochiStatus();
        ExpandedMochi.Greet();
    }

    internal void UpdateMochiStatus()
    {
        var now = DateTimeOffset.UtcNow;
        if (_hookAgentState is not null && now >= _hookExpiresUtc)
        {
            _hookAgentState = null;
            _hookDetail = null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = AntigravityUsage.Folders(home);
        var lastWrite = MochiCompanionLogic.LatestConversationWriteUtc(folders);
        double? remaining = MochiCompanionLogic.ExtractAntigravityRemainingPercent(Snapshots);

        var status = MochiCompanionLogic.Resolve(
            _hookAgentState,
            lastWrite,
            now,
            remaining,
            _hookDetail);

        // Trigger a brief celebration emote when transitioning into Finished while quota is healthy.
        if (status.State == MochiState.Finished && _lastResolvedMochiState != MochiState.Finished
            && status.QuotaMood == MochiQuotaMood.Energetic)
        {
            ExpandedMochi.TriggerEmote(MochiEmote.Proud, 1.6);
        }
        _lastResolvedMochiState = status.State;

        _compactMochi.ApplyStatus(status);
        ExpandedMochi.ApplyStatus(status);
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
