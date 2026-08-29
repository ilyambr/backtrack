using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class OverlayLogWindow : Window
{
    private bool _isFadingOut;

    public OverlayLogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ToolWindow.Enable(new WindowInteropHelper(this).Handle);
            Reposition();
        };
        SizeChanged += (_, _) => Reposition();
    }

    public void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X + bounds.Width - Width - 12;
        Top = bounds.Y + bounds.Height - ActualHeight - 12;
    }

    public void SetMode(bool obsMode)
    {
        ObsModeText.Visibility = obsMode ? Visibility.Visible : Visibility.Collapsed;
        BacktrackModeScroll.Visibility = obsMode ? Visibility.Collapsed : Visibility.Visible;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            if (obsMode)
                ClickThrough.Enable(hwnd);
            else
                ClickThrough.Disable(hwnd);
        }

        Reposition();
    }

    public void FadeIn()
    {
        _isFadingOut = false;
        if (Visibility != Visibility.Visible)
        {
            Opacity = 0;
            Visibility = Visibility.Visible;
        }

        var anim = new DoubleAnimation
        {
            From = Opacity,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void FadeOut()
    {
        if (Visibility != Visibility.Visible || _isFadingOut || Opacity <= 0)
            return;

        _isFadingOut = true;
        var anim = new DoubleAnimation
        {
            From = Opacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (_isFadingOut)
            {
                Visibility = Visibility.Hidden;
                ObsModeText.Text = "";
                _isFadingOut = false;
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void SetObsLine(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            ObsModeText.Text = text;
            FadeIn();
        }
        else
        {
            FadeOut();
        }
        Reposition();
    }

    public void SetBacktrackLines(IReadOnlyList<string> lines)
    {
        BacktrackModeList.ItemsSource = lines;
        if (lines != null && lines.Count > 0)
        {
            FadeIn();
        }
        else
        {
            FadeOut();
        }
        BacktrackModeScroll.ScrollToBottom();
        Reposition();
    }
}
