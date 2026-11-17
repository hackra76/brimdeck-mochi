using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

// The music page's gestures (MusicGestures), on a page of their own under music: dragging with the left button in four directions,
// and a two-finger touchpad swipe to either side. A two-finger swipe up or down is the mouse wheel's (DeckSettings.MouseWheel).
public sealed partial class SettingsWindow
{
    internal const int MusicGesturesPage = 15;

    internal static string GestureName(MusicGestureAction action) => action switch
    {
        MusicGestureAction.NextTrack => "Next song",
        MusicGestureAction.PreviousTrack => "Previous song",
        MusicGestureAction.NextPlayer => "Next player",
        MusicGestureAction.PreviousPlayer => "Previous player",
        MusicGestureAction.PlayPause => "Play/pause",
        MusicGestureAction.VolumeUp => "Volume up",
        MusicGestureAction.VolumeDown => "Volume down",
        _ => "No action"
    };

    private void MusicGesturesSettings()
    {
        SectionTitle("Mouse drag");
        Group(GestureRow("Drag left", S.MusicDragLeft, v => Change(s => s.MusicDragLeft = v)),
            GestureRow("Drag right", S.MusicDragRight, v => Change(s => s.MusicDragRight = v)),
            GestureRow("Drag up", S.MusicDragUp, v => Change(s => s.MusicDragUp = v)),
            GestureRow("Drag down", S.MusicDragDown, v => Change(s => s.MusicDragDown = v)));
        SectionTitle("Touchpad", help: () => "Swipe sideways with two fingers over the music page. Tilting the mouse wheel sideways does the same. A two-finger swipe up or down follows the mouse wheel setting.");
        Group(GestureRow("Two-finger swipe left", S.MusicSwipeLeft, v => Change(s => s.MusicSwipeLeft = v)),
            GestureRow("Two-finger swipe right", S.MusicSwipeRight, v => Change(s => s.MusicSwipeRight = v)));
    }

    private FrameworkElement GestureRow(string title, MusicGestureAction selected, Action<MusicGestureAction> change)
    {
        var actions = Enum.GetValues<MusicGestureAction>();
        var choice = new ComboBox { Width = 150, Style = (Style)FindResource("SettingsCombo"), ItemsSource = actions.Select(GestureName).ToList(),
            SelectedIndex = Array.IndexOf(actions, selected), IsEnabled = S.MusicPage };
        AutomationProperties.SetName(choice, title); PopupMotion.Attach(choice);
        choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) change(actions[choice.SelectedIndex]); };
        return Row(title, choice);
    }
}
