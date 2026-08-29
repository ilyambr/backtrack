using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void ShowAppStartedToast()
    {
        string hotkey = FormatHotkeyText();
        _toastOverlay.ShowAppStarted(hotkey);
    }


    private string FormatHotkeyText()
    {
        var parts = new List<string>();
        if ((_settings.HotkeyModifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((_settings.HotkeyModifiers & 0x1) != 0) parts.Add("Alt");
        if ((_settings.HotkeyModifiers & 0x4) != 0) parts.Add("Shift");
        if ((_settings.HotkeyModifiers & 0x8) != 0) parts.Add("Win");

        string keyStr;
        if (_settings.HotkeyVirtualKey >= 'A' && _settings.HotkeyVirtualKey <= 'Z')
            keyStr = ((char)_settings.HotkeyVirtualKey).ToString();
        else if (_settings.HotkeyVirtualKey >= 0x30 && _settings.HotkeyVirtualKey <= 0x39)
            keyStr = ((char)_settings.HotkeyVirtualKey).ToString();
        else
            keyStr = System.Windows.Input.KeyInterop.KeyFromVirtualKey(_settings.HotkeyVirtualKey).ToString();

        parts.Add(keyStr);
        return string.Join("+", parts);
    }


    
    private void RegisterHotkeyFromSettings()
    {
        try
        {
            _hotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey, id: OpenOverlayHotkeyId);
            _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Hotkey registration failed: {ex.Message}");
        }

        if (_settings.CancelRecordHotkeyVirtualKey != 0)
        {
            try
            {
                _cancelRecordHotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey, id: CancelRecordHotkeyId);
                _cancelRecordHotkey.Pressed += () => Dispatcher.Invoke(async () =>
                {
                    await CancelActiveRecordingsAsync();
                    await RefreshStatusAsync();
                });
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Cancel record hotkey registration failed: {ex.Message}");
            }
        }

        if (_settings.BookmarkHotkeyVirtualKey != 0)
        {
            try
            {
                _bookmarkHotkey = new GlobalHotkey(this, (GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey, id: BookmarkHotkeyId);
                _bookmarkHotkey.Pressed += () => Dispatcher.Invoke(OnBookmarkHotkeyPressed);
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"Bookmark hotkey registration failed: {ex.Message}");
            }
        }
    }


    private static string FormatHotkey(GlobalHotkey.Modifiers modifiers, uint virtualKey)
    {
        if (virtualKey == 0)
            return "(unbound)";
        var parts = new List<string>();
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(GlobalHotkey.Modifiers.Win)) parts.Add("Win");

        string keyStr = virtualKey switch
        {
            186 => ";",
            187 => "=",
            188 => ",",
            189 => "-",
            190 => ".",
            191 => "/",
            192 => "`",
            219 => "[",
            220 => "\\",
            221 => "]",
            222 => "'",
            _ => (virtualKey >= 'A' && virtualKey <= 'Z') || (virtualKey >= '0' && virtualKey <= '9')
                ? ((char)virtualKey).ToString()
                : KeyInterop.KeyFromVirtualKey((int)virtualKey).ToString()
        };

        parts.Add(keyStr);
        return string.Join("+", parts);
    }


    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey || _capturingCancelRecordHotkey)
            return;

        _capturingHotkey = true;
        HotkeyCaptureButton.Content = "Press a key combo...";
        PreviewKeyDown += HotkeyCapture_PreviewKeyDown;
    }


    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            EndHotkeyCapture(cancelled: true);
            return;
        }

        e.Handled = true;

        GlobalHotkey.Modifiers modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkey.Modifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkey.Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkey.Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkey.Modifiers.Win;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        try
        {
            if (_hotkey is null)
            {
                _hotkey = new GlobalHotkey(this, modifiers, virtualKey, id: OpenOverlayHotkeyId);
                _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
            }
            else
            {
                _hotkey.Rebind(modifiers, virtualKey);
            }

            _settings.HotkeyModifiers = (int)modifiers;
            _settings.HotkeyVirtualKey = (int)virtualKey;
            _settings.Save();
            HotkeyCaptureButton.Content = FormatHotkey(modifiers, virtualKey);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Backtrack");
            HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
        }

        EndHotkeyCapture(cancelled: false);
    }


    private void EndHotkeyCapture(bool cancelled)
    {
        PreviewKeyDown -= HotkeyCapture_PreviewKeyDown;
        _capturingHotkey = false;
        if (cancelled)
            HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
    }


    private void CancelRecordHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingCancelRecordHotkey || _capturingHotkey)
            return;

        _capturingCancelRecordHotkey = true;
        CancelRecordHotkeyCaptureButton.Content = "Press a key combo (Esc to clear)...";
        PreviewKeyDown += CancelRecordHotkeyCapture_PreviewKeyDown;
    }


    private void CancelRecordHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            _cancelRecordHotkey?.Dispose();
            _cancelRecordHotkey = null;
            _settings.CancelRecordHotkeyModifiers = 0;
            _settings.CancelRecordHotkeyVirtualKey = 0;
            _settings.Save();
            CancelRecordHotkeyCaptureButton.Content = "(unbound)";
            EndCancelRecordHotkeyCapture(cancelled: false);
            return;
        }

        e.Handled = true;

        GlobalHotkey.Modifiers modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkey.Modifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkey.Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkey.Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkey.Modifiers.Win;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        try
        {
            if (_cancelRecordHotkey is null)
            {
                _cancelRecordHotkey = new GlobalHotkey(this, modifiers, virtualKey, id: CancelRecordHotkeyId);
                _cancelRecordHotkey.Pressed += () => Dispatcher.Invoke(async () =>
                {
                    await CancelActiveRecordingsAsync();
                    await RefreshStatusAsync();
                });
            }
            else
            {
                _cancelRecordHotkey.Rebind(modifiers, virtualKey);
            }

            _settings.CancelRecordHotkeyModifiers = (int)modifiers;
            _settings.CancelRecordHotkeyVirtualKey = (int)virtualKey;
            _settings.Save();
            CancelRecordHotkeyCaptureButton.Content = FormatHotkey(modifiers, virtualKey);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Backtrack");
            CancelRecordHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey);
        }

        EndCancelRecordHotkeyCapture(cancelled: false);
    }


    private void EndCancelRecordHotkeyCapture(bool cancelled)
    {
        PreviewKeyDown -= CancelRecordHotkeyCapture_PreviewKeyDown;
        _capturingCancelRecordHotkey = false;
        if (cancelled)
            CancelRecordHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey);
    }

    private void BookmarkHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingBookmarkHotkey || _capturingHotkey || _capturingCancelRecordHotkey)
            return;

        _capturingBookmarkHotkey = true;
        BookmarkHotkeyCaptureButton.Content = "Press a key combo (Esc to clear)...";
        PreviewKeyDown += BookmarkHotkeyCapture_PreviewKeyDown;
    }

    private void BookmarkHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            _bookmarkHotkey?.Dispose();
            _bookmarkHotkey = null;
            _settings.BookmarkHotkeyModifiers = 0;
            _settings.BookmarkHotkeyVirtualKey = 0;
            _settings.Save();
            BookmarkHotkeyCaptureButton.Content = "(unbound)";
            EndBookmarkHotkeyCapture(cancelled: false);
            return;
        }

        e.Handled = true;

        GlobalHotkey.Modifiers modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkey.Modifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkey.Modifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkey.Modifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkey.Modifiers.Win;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        try
        {
            if (_bookmarkHotkey is null)
            {
                _bookmarkHotkey = new GlobalHotkey(this, modifiers, virtualKey, id: BookmarkHotkeyId);
                _bookmarkHotkey.Pressed += () => Dispatcher.Invoke(OnBookmarkHotkeyPressed);
            }
            else
            {
                _bookmarkHotkey.Rebind(modifiers, virtualKey);
            }

            _settings.BookmarkHotkeyModifiers = (int)modifiers;
            _settings.BookmarkHotkeyVirtualKey = (int)virtualKey;
            _settings.Save();
            BookmarkHotkeyCaptureButton.Content = FormatHotkey(modifiers, virtualKey);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Backtrack");
            BookmarkHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey);
        }

        EndBookmarkHotkeyCapture(cancelled: false);
    }

    private void EndBookmarkHotkeyCapture(bool cancelled)
    {
        PreviewKeyDown -= BookmarkHotkeyCapture_PreviewKeyDown;
        _capturingBookmarkHotkey = false;
        if (cancelled)
            BookmarkHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey);
    }

    private static bool TryParseHotkeyString(string hotkeyStr, out GlobalHotkey.Modifiers modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeyStr) || hotkeyStr.Equals("(unbound)", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = hotkeyStr.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        string mainKeyPart = parts[^1];

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string p = parts[i].ToLowerInvariant();
            if (p is "ctrl" or "control")
                modifiers |= GlobalHotkey.Modifiers.Control;
            else if (p is "alt" or "menu")
                modifiers |= GlobalHotkey.Modifiers.Alt;
            else if (p is "shift")
                modifiers |= GlobalHotkey.Modifiers.Shift;
            else if (p is "win" or "windows" or "super" or "cmd")
                modifiers |= GlobalHotkey.Modifiers.Win;
        }

        string keyClean = mainKeyPart.Trim();
        string keyLower = keyClean.ToLowerInvariant();

        if (keyLower.StartsWith("f") && int.TryParse(keyLower[1..], out int fNum) && fNum >= 1 && fNum <= 24)
        {
            virtualKey = (uint)(0x70 + (fNum - 1));
            return true;
        }

        if (keyClean.Length == 1)
        {
            char c = char.ToUpperInvariant(keyClean[0]);
            if (c >= 'A' && c <= 'Z')
            {
                virtualKey = c;
                return true;
            }
            if (c >= '0' && c <= '9')
            {
                virtualKey = c;
                return true;
            }
            virtualKey = c switch
            {
                ';' => 186,
                '=' or '+' => 187,
                ',' => 188,
                '-' => 189,
                '.' => 190,
                '/' => 191,
                '`' or '~' => 192,
                '[' => 219,
                '\\' => 220,
                ']' => 221,
                '\'' => 222,
                _ => 0
            };
            if (virtualKey != 0)
                return true;
        }

        virtualKey = keyLower switch
        {
            "space" or "spacebar" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" or "back" => 0x08,
            "del" or "delete" => 0x2E,
            "ins" or "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "page up" or "pageup" or "pgup" => 0x21,
            "page down" or "pagedown" or "pgdn" => 0x22,
            "up" or "arrow up" or "arrowup" => 0x26,
            "down" or "arrow down" or "arrowdown" => 0x28,
            "left" or "arrow left" or "arrowleft" => 0x25,
            "right" or "arrow right" or "arrowright" => 0x27,
            "caps lock" or "capslock" or "caps" => 0x14,
            "scroll lock" or "scrolllock" => 0x91,
            "num lock" or "numlock" => 0x90,
            "print screen" or "printscreen" or "prtscn" or "snapshot" => 0x2C,
            "pause" or "break" => 0x13,
            "num 0" or "numpad 0" or "numpad0" => 0x60,
            "num 1" or "numpad 1" or "numpad1" => 0x61,
            "num 2" or "numpad 2" or "numpad2" => 0x62,
            "num 3" or "numpad 3" or "numpad3" => 0x63,
            "num 4" or "numpad 4" or "numpad4" => 0x64,
            "num 5" or "numpad 5" or "numpad5" => 0x65,
            "num 6" or "numpad 6" or "numpad6" => 0x66,
            "num 7" or "numpad 7" or "numpad7" => 0x67,
            "num 8" or "numpad 8" or "numpad8" => 0x68,
            "num 9" or "numpad 9" or "numpad9" => 0x69,
            "period" => 190,
            "comma" => 188,
            "minus" => 189,
            "plus" or "equals" => 187,
            "slash" => 191,
            "backslash" => 220,
            "semicolon" => 186,
            "quote" or "apostrophe" => 222,
            _ => 0
        };

        if (virtualKey != 0)
            return true;

        if (Enum.TryParse<Key>(keyClean, true, out Key parsedKey) && parsedKey != Key.None)
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(parsedKey);
            return virtualKey != 0;
        }

        return false;
    }
}
