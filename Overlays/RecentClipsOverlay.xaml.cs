using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class RecentClipsOverlay : Window
{
    /// <summary>Fired once a drag actually completes (DragMove returns), not on every intermediate mouse-move -- MainWindow persists Left/Top from this into settings.</summary>
    public event Action<double, double>? PositionChanged;

    public RecentClipsOverlay()
    {
        InitializeComponent();
        ShellDragHelper.EnableDropPreview(this, this);
        // No ClickThrough.Enable here -- see the XAML's own comment on why
        // this one, unlike StreamingStatusOverlay, needs to stay interactive.
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// DragMove() blocks until the mouse button is released (it runs its own
    /// message loop), so firing PositionChanged right after it returns is
    /// exactly "the drag just finished" -- no separate LocationChanged
    /// debounce needed the way a continuously-updating position would.
    /// </summary>
    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        DragMove();
        PositionChanged?.Invoke(Left, Top);
    }

    public void SetTiles(IEnumerable<UIElement> tiles)
    {
        TilesPanel.Children.Clear();
        foreach (UIElement tile in tiles)
            TilesPanel.Children.Add(tile);
    }
}
