using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

/// <summary>
/// Floating transparent desktop window for Mochi when dragged out of the Dynamic Island.
/// </summary>
public sealed class DesktopMochiWindow : Window
{
    public MochiCompanion Mochi { get; } = new() { Width = 112, Height = 112 };
    public event Action<double, double>? PositionCommitted;
    public event Action? ReturnToIslandRequested;
    public event Action? OpenSettingsRequested;

    private Point? _pressScreen;
    private Point _windowAtPress;
    private bool _dragging;

    public DesktopMochiWindow()
    {
        Width = 124;
        Height = 124;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;

        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(Mochi);
        Content = host;

        var menu = new ContextMenu();
        var returnItem = new MenuItem { Header = Loc.T("回到灵动岛", "Return to Island") };
        returnItem.Click += (_, _) => ReturnToIslandRequested?.Invoke();
        var settingsItem = new MenuItem { Header = Loc.T("Mochi 设置", "Mochi Settings") };
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();
        menu.Items.Add(returnItem);
        menu.Items.Add(settingsItem);
        ContextMenu = menu;

        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseUp;
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
            Mochi.Poke();
        }
        _pressScreen = null;
        _dragging = false;
    }
}
