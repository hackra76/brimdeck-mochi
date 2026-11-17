using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

/// <summary>
/// Floating transparent desktop window for Mochi when dragged out of the Dynamic Island.
/// </summary>
public sealed class DesktopMochiWindow : Window
{
    public MochiCompanion Mochi { get; } = new() { Width = 128, Height = 128 };
    public event Action<double, double>? PositionCommitted;
    public event Action? ReturnToIslandRequested;
    public event Action? OpenSettingsRequested;
    public event Action? Clicked;

    private Point? _pressScreen;
    private Point _windowAtPress;
    private bool _dragging;
    private readonly Border _bubble;
    private readonly TextBlock _bubbleText;
    private readonly DispatcherTimer _bubbleTimer;

    public DesktopMochiWindow()
    {
        Width = 200;
        Height = 184;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;

        _bubbleText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 248)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 176
        };
        _bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(228, 22, 24, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = _bubbleText
        };
        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            _bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        };

        Mochi.HorizontalAlignment = HorizontalAlignment.Center;
        Mochi.VerticalAlignment = VerticalAlignment.Bottom;
        Mochi.Margin = new Thickness(0, 0, 0, 6);

        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(_bubble);
        host.Children.Add(Mochi);
        Content = host;

        var menu = new ContextMenu();
        var returnItem = new MenuItem { Header = "Return to Island" };
        returnItem.Click += (_, _) => ReturnToIslandRequested?.Invoke();
        var settingsItem = new MenuItem { Header = "Mochi Settings" };
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();
        menu.Items.Add(returnItem);
        menu.Items.Add(settingsItem);
        ContextMenu = menu;

        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseUp;
    }

    public void ShowBubble(string text, int durationMs = 3500)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _bubbleText.Text = text;
        _bubbleTimer.Stop();
        _bubbleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, durationMs));
        _bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        _bubbleTimer.Start();
    }

    public void BeginExternalDrag(Point cursorScreen)
    {
        _pressScreen = cursorScreen;
        _windowAtPress = new Point(Left, Top);
        _dragging = true;
        CaptureMouse();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            ReturnToIslandRequested?.Invoke();
            return;
        }
        _pressScreen = PointToScreen(e.GetPosition(this));
        _windowAtPress = new Point(Left, Top);
        _dragging = false;
        CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressScreen is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var cur = PointToScreen(e.GetPosition(this));
        var dx = cur.X - start.X;
        var dy = cur.Y - start.Y;
        if (!_dragging && Math.Sqrt(dx * dx + dy * dy) > 4)
            _dragging = true;
        if (_dragging)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = _windowAtPress.X + dx / dpi.DpiScaleX;
            Top = _windowAtPress.Y + dy / dpi.DpiScaleY;
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_dragging)
        {
            PositionCommitted?.Invoke(Left, Top);
        }
        else if (_pressScreen is not null)
        {
            Clicked?.Invoke();
            Mochi.TriggerTapReaction(1);
        }
        _pressScreen = null;
        _dragging = false;
    }
}
