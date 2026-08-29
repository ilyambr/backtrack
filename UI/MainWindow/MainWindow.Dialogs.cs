using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{

    private void ShowConfirmDialog(string message, string confirmButtonText, Action<bool> callback)
    {
        _activeConfirmDialog?.Close();
        _activeConfirmDialog = ConfirmDialog.ShowNonModal(this, message, confirmButtonText, confirmed =>
        {
            _activeConfirmDialog = null;
            callback(confirmed);
        });
    }


        
    
    
    
            private static void FadeWindowIn(Window window, double durationMs = 180)
    {
        window.BeginAnimation(OpacityProperty, null);
        window.Opacity = 0;
        window.Show();

        EventHandler? renderHandler = null;
        renderHandler = (s, e) =>
        {
            CompositionTarget.Rendering -= renderHandler;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) =>
            {
                window.BeginAnimation(OpacityProperty, null);
                window.Opacity = 1;
            };
            window.BeginAnimation(OpacityProperty, fade);
        };
        CompositionTarget.Rendering += renderHandler;
    }

    private static void FadeWindowOut(Window window, double durationMs = 150, Action? onCompleted = null, bool useCache = false)
    {
        var fade = new DoubleAnimation(window.Opacity, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            window.Hide();
            window.BeginAnimation(OpacityProperty, null);
            window.Opacity = 0;
            onCompleted?.Invoke();
        };
        window.BeginAnimation(OpacityProperty, fade);
    }
}
