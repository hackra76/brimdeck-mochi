using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

/// <summary>
/// Modular WPF UserControl rendering the animated Nimbus AI companion.
/// Uses CompositionTarget.Rendering only while visible, keeping idle CPU near zero.
/// </summary>
public partial class NimbusCompanion
{
    private sealed class NimbusSurface(NimbusEngine engine) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth > 1 && ActualHeight > 1)
                engine.Draw(dc, ActualWidth, ActualHeight);
        }
    }

    private readonly NimbusEngine _engine = new();
    private readonly NimbusSurface _surface;
    private bool _renderingHooked;
    private long _lastRenderTicks;
    private Point? _dragStartScreen;
    private bool _dragTriggered;

    public bool AllowDragOut { get; set; }
    public event Action<Point>? DragOutRequested;
    public event Action? Clicked;

    public static readonly DependencyProperty IsMiniProperty = DependencyProperty.Register(
        nameof(IsMini), typeof(bool), typeof(NimbusCompanion),
        new PropertyMetadata(false, (d, e) => ((NimbusCompanion)d)._engine.IsMini = (bool)e.NewValue));

    public bool IsMini
    {
        get => (bool)GetValue(IsMiniProperty);
        set => SetValue(IsMiniProperty, value);
    }

    public NimbusState State => _engine.State;

    public NimbusCompanion()
    {
        InitializeComponent();
        _surface = new NimbusSurface(_engine);
        Root.Children.Add(_surface);

        _engine.DizzyTriggered += () =>
        {
            _ = Task.Delay(2400).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (_engine.State == NimbusState.Dizzy)
                    _engine.SetState(NimbusState.Idle);
            }));
        };

        Loaded += (_, _) => UpdateRenderSubscription();
        Unloaded += (_, _) => UnhookRendering();
        IsVisibleChanged += (_, _) => UpdateRenderSubscription();

        MouseLeftButtonDown += OnNimbusClicked;
        MouseLeftButtonUp += OnNimbusMouseUp;
        MouseMove += OnNimbusMouseMove;
        MouseLeave += (_, _) => _engine.SetLook(0, 0);
    }

    public void ApplyStatus(NimbusStatusSnapshot status)
    {
        _engine.QuotaMood = status.QuotaMood;
        if (_engine.State != NimbusState.Dizzy)
            _engine.SetState(status.State);

        ToolTip = $"{status.StatusTitle}\n{status.DetailText}";
    }

    public void Greet() => _engine.Greet();
    public void StartLaunchGreeting() => _engine.StartLaunchGreeting();
    public void Poke() => _engine.Poke();
    public void Gulp() => _engine.Gulp();
    public void TriggerEmote(NimbusEmote emote, double duration = 1.8) => _engine.TriggerEmote(emote, duration);
    public void SetPermanentEmote(NimbusEmote? emote) => _engine.SetPermanentEmote(emote);
    public void SetGlobalLook(double nx, double ny) => _engine.SetLook(nx, ny);
    public void SetDancing(bool dancing) => _engine.SetDancing(dancing);
    public void SetOutfit(NimbusOutfit outfit) => _engine.SetOutfit(outfit);
    public void SetMailbox(bool mailbox) => _engine.SetMailbox(mailbox);
    public void SetUpdateHint(Color? color) => _engine.SetUpdateHint(color);

    private void OnNimbusClicked(object sender, MouseButtonEventArgs e)
    {
        if (AllowDragOut)
        {
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _dragTriggered = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        TriggerTapReaction(e.ClickCount);
        Clicked?.Invoke();
        e.Handled = true;
    }

    private void OnNimbusMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        bool wasTap = _dragStartScreen is not null && !_dragTriggered;
        _dragStartScreen = null;
        _dragTriggered = false;
        if (wasTap)
        {
            TriggerTapReaction(e.ClickCount);
            Clicked?.Invoke();
            e.Handled = true;
        }
    }

    public void TriggerTapReaction(int clickCount = 1)
    {
        if (IsMini)
        {
            _engine.Squash();
            return;
        }
        _engine.Slap();
        if (_engine.State != NimbusState.Dizzy && clickCount == 1)
            _engine.Greet();
    }

    private void OnNimbusMouseMove(object sender, MouseEventArgs e)
    {
        if (AllowDragOut && !_dragTriggered && _dragStartScreen is { } startScreen && e.LeftButton == MouseButtonState.Pressed)
        {
            var curScreen = PointToScreen(e.GetPosition(this));
            if ((curScreen - startScreen).Length > 16)
            {
                _dragTriggered = true;
                _dragStartScreen = null;
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
                DragOutRequested?.Invoke(curScreen);
                return;
            }
        }
        if (ActualWidth <= 1 || ActualHeight <= 1) return;
        var pos = e.GetPosition(this);
        double nx = (pos.X / ActualWidth - 0.5) * 2.0;
        double ny = -(pos.Y / ActualHeight - 0.5) * 2.0;
        _engine.SetLook(nx, ny);
    }

    private void UpdateRenderSubscription()
    {
        if (IsLoaded && IsVisible) HookRendering();
        else UnhookRendering();
    }

    private void HookRendering()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        _renderingHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_engine.IsLowRateIdle)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            // Throttle resting Idle/Sleeping frames to ~30 fps instead of 60-240 Hz vsync.
            if (_lastRenderTicks != 0 && (now - _lastRenderTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 33.0)
                return;
            _lastRenderTicks = now;
        }
        else
        {
            _lastRenderTicks = 0;
        }

        _engine.Tick();
        _surface.InvalidateVisual();
    }
}
