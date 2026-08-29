using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class RecentClipsOverlay : Window
{
    public event Action<double, double>? PositionChanged;

    public RecentClipsOverlay()
    {
        InitializeComponent();
        ShellDragHelper.EnableDropPreview(this, this);
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
    }

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
