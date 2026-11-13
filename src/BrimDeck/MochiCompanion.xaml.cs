using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

/// <summary>
/// Modular WPF UserControl rendering the animated Mochi AI companion.
/// Uses CompositionTarget.Rendering only while visible, keeping idle CPU near zero.
/// </summary>
public partial class MochiCompanion
{
    private sealed class MochiSurface(MochiEngine engine) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth > 1 && ActualHeight > 1)
                engine.Draw(dc, ActualWidth, ActualHeight);
        }
    }

    private readonly MochiEngine _engine = new();
    private readonly MochiSurface _surface;
    private bool _renderingHooked;
    private Point? _dragStartScreen;
    private bool _dragTriggered;

    public bool AllowDragOut { get; set; }
    public event Action<Point>? DragOutRequested;
    public event Action? Clicked;

    public static readonly DependencyProperty IsMiniProperty = DependencyProperty.Register(
        nameof(IsMini), typeof(bool), typeof(MochiCompanion),
        new PropertyMetadata(false, (d, e) => ((MochiCompanion)d)._engine.IsMini = (bool)e.NewValue));

    public bool IsMini
    {
        get => (bool)GetValue(IsMiniProperty);
        set => SetValue(IsMiniProperty, value);
    }

    public MochiState State => _engine.State;

    public MochiCompanion()
    {
        InitializeComponent();
        _surface = new MochiSurface(_engine);
        Root.Children.Add(_surface);

        _engine.DizzyTriggered += () =>
        {
            _ = Task.Delay(2400).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (_engine.State == MochiState.Dizzy)
                    _engine.SetState(MochiState.Idle);
            }));
        };

        Loaded += (_, _) => UpdateRenderSubscription();
        Unloaded += (_, _) => UnhookRendering();
        IsVisibleChanged += (_, _) => UpdateRenderSubscription();

        MouseLeftButtonDown += OnMochiClicked;
        MouseLeftButtonUp += OnMochiMouseUp;
        MouseMove += OnMochiMouseMove;
        MouseLeave += (_, _) => _engine.SetLook(0, 0);
    }

    public void ApplyStatus(MochiStatusSnapshot status)
    {
        _engine.QuotaMood = status.QuotaMood;
        if (_engine.State != MochiState.Dizzy)
            _engine.SetState(status.State);

        ToolTip = $"{status.StatusTitle}\n{status.DetailText}";
    }

    public void Greet() => _engine.Greet();
    public void StartLaunchGreeting() => _engine.StartLaunchGreeting();
    public void Poke() => _engine.Poke();
    public void TriggerEmote(MochiEmote emote, double duration = 1.8) => _engine.TriggerEmote(emote, duration);
    public void SetGlobalLook(double nx, double ny) => _engine.SetLook(nx, ny);
    public void SetDancing(bool dancing) => _engine.SetDancing(dancing);
    public void SetOutfit(MochiOutfit outfit) => _engine.SetOutfit(outfit);
    public void SetMailbox(bool mailbox) => _engine.SetMailbox(mailbox);
    public void SetUpdateHint(Color? color) => _engine.SetUpdateHint(color);

    private void OnMochiClicked(object sender, MouseButtonEventArgs e)
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
    }

    private void OnMochiMouseUp(object sender, MouseButtonEventArgs e)
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

    private void TriggerTapReaction(int clickCount)
    {
        if (IsMini)
        {
            _engine.Squash();
            return;
        }
        _engine.Slap();
        if (_engine.State != MochiState.Dizzy && clickCount == 1)
            _engine.Greet();
    }

    private void OnMochiMouseMove(object sender, MouseEventArgs e)
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
        _engine.Tick();
        _surface.InvalidateVisual();
    }
}
