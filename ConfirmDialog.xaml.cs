using System;
using System.Windows;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class ConfirmDialog : Window
{
    private Action<bool>? _callback;

    public ConfirmDialog(string message, string confirmText, Action<bool> callback)
    {
        InitializeComponent();
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        _callback = callback;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var cb = _callback;
        _callback = null;
        Close();
        if (cb != null)
        {
            Dispatcher.BeginInvoke(() => cb(true));
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var cb = _callback;
        _callback = null;
        Close();
        if (cb != null)
        {
            Dispatcher.BeginInvoke(() => cb(false));
        }
    }

    public static ConfirmDialog ShowNonModal(Window owner, string message, string confirmText, Action<bool> callback)
    {
        var dialog = new ConfirmDialog(message, confirmText, callback)
        {
            Owner = owner
        };

        DependencyPropertyChangedEventHandler? ownerVisibleHandler = null;
        ownerVisibleHandler = (_, _) =>
        {
            if (!owner.IsVisible)
            {
                dialog.Hide();
            }
            else
            {
                dialog.Show();
                IntPtr hwnd = new WindowInteropHelper(dialog).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    WindowZOrder.BringToFrontWithoutActivating(hwnd);
                }
            }
        };

        owner.IsVisibleChanged += ownerVisibleHandler;

        dialog.Closed += (_, _) =>
        {
            owner.IsVisibleChanged -= ownerVisibleHandler;
        };

        dialog.Show();
        IntPtr dialogHwnd = new WindowInteropHelper(dialog).Handle;
        if (dialogHwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(dialogHwnd);
        }
        return dialog;
    }
}
