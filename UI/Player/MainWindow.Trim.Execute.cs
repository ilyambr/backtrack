using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Backtrack.Core;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    internal async void TrimReplace_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: true);

    internal async void TrimSaveNew_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: false);

    private async Task RunTrimAsync(bool replaceOriginal)
    {
        if (_trimStart is null || _trimEnd is null || _trimEnd <= _trimStart)
        {
            MessageBox.Show(this, "Set both a start and end point first (end must be after start).", "Backtrack");
            return;
        }

        if (_currentPlayerFile is null)
        {
            if (_currentPlayerRemoteOrigin is not null)
            {
                await RunRemoteTrimAsync(replaceOriginal);
                return;
            }

            AppLog.Write("[trim_clip] RunTrimAsync: both _currentPlayerFile and _currentPlayerRemoteOrigin are null -- nothing to trim, this is the actual failure");
            MessageBox.Show(this, "Nothing to trim -- this clip isn't tracked as either a local file or a remote clip right now. Try reopening it.", "Backtrack");
            return;
        }

        if (_libVlc is null)
            return;

        FileInfo sourceFile = _currentPlayerFile;
        TimeSpan start = _trimStart.Value;
        TimeSpan end = _trimEnd.Value;

        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;

        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{sourceFile.Extension}");

        _isTrimming = true;
        TrimReplaceButton.IsEnabled = false;
        TrimSaveNewButton.IsEnabled = false;
        TrimStatusText.Text = "Trimming...";

        await CaptureAndShowPlayerFreezeFrameAsync(sourceFile);
        StopPlayerPlayback(keepFreezeFrame: true);

        try
        {
            await Task.Run(() => ExportTrim(sourceFile.FullName, tempOut, start, end));

            if (replaceOriginal)
            {
                bool? userConfirmed = null;
                ShowConfirmDialog("Replace the original clip with this trimmed version? This can't be undone.", "Replace", c => userConfirmed = c);
                while (!userConfirmed.HasValue && IsVisible)
                {
                    await Task.Delay(50);
                }
                if (userConfirmed != true)
                {
                    File.Delete(tempOut);
                    OpenInPlayer(sourceFile, keepCurrentFreezeFrame: true);
                    return;
                }
                File.Copy(tempOut, sourceFile.FullName, overwrite: true);
                File.Delete(tempOut);
                _currentPlayerFile = new FileInfo(sourceFile.FullName);
                OpenInPlayer(_currentPlayerFile, keepCurrentFreezeFrame: true);
                _toastOverlay.ShowTrimSaved(sourceFile.FullName);

                if (remoteOrigin is (string relPath, _))
                {
                    _currentPlayerRemoteOrigin = remoteOrigin;
                    TrimStatusText.Text = $"Sending to {_settings.PairedPeerName}'s PC...";
                    (bool upSuccess, string? upError, _) = await _pairing.UploadRemoteClipAsync(relPath, _currentPlayerFile.FullName, overwrite: true);
                    if (!upSuccess)
                        MessageBox.Show(this, $"Trimmed locally, but couldn't send it back to {_settings.PairedPeerName}'s PC: {upError}", "Backtrack");
                }
            }
            else
            {
                string destPath = GetTrimmedDestinationPath(sourceFile.DirectoryName!, sourceFile.Name);
                File.Copy(tempOut, destPath, overwrite: false);
                File.Delete(tempOut);
                _ = RefreshGalleryCountAsync();
                var newFileInfo = new FileInfo(destPath);
                _currentPlayerFile = newFileInfo;
                OpenInPlayer(newFileInfo, keepCurrentFreezeFrame: true);
                _toastOverlay.ShowTrimSaved(destPath);

                if (remoteOrigin is (string relPath, _))
                {
                    int lastSlash = relPath.LastIndexOf('/');
                    string folderPrefix = lastSlash < 0 ? "" : relPath[..lastSlash];
                    string remoteDestRelPath = folderPrefix.Length == 0 ? Path.GetFileName(destPath) : $"{folderPrefix}/{Path.GetFileName(destPath)}";
                    _currentPlayerRemoteOrigin = (remoteDestRelPath, _settings.PairedPeerDeviceId ?? "");
                    TrimStatusText.Text = $"Sending to {_settings.PairedPeerName}'s PC...";
                    (bool upSuccess, string? upError, string? actualRemotePath) = await _pairing.UploadRemoteClipAsync(remoteDestRelPath, destPath, overwrite: false);
                    if (actualRemotePath is not null)
                        _currentPlayerRemoteOrigin = (actualRemotePath, _settings.PairedPeerDeviceId ?? "");
                    if (!upSuccess)
                        MessageBox.Show(this, $"Trimmed locally, but couldn't send the new clip to {_settings.PairedPeerName}'s PC: {upError}", "Backtrack");
                }
            }

            TrimPanel.Visibility = Visibility.Collapsed;
            PlayerTransportRow.Visibility = Visibility.Visible;
            MoveTransportControlsForTrim(intoTrimRow: false);
        }
        catch (Exception ex)
        {
            TrimStatusText.Text = "";
            MessageBox.Show(this, $"Trim failed: {ex.Message}", "Backtrack");
            OpenInPlayer(sourceFile);
        }
        finally
        {
            _isTrimming = false;
            TrimReplaceButton.IsEnabled = true;
            TrimSaveNewButton.IsEnabled = true;
        }
    }

    private async Task<(bool Success, string? Error, string? NewFileName, long Size)> TrimClipForRemoteAsync(string fullPath, double startSeconds, double endSeconds, bool replaceOriginal)
    {
        var file = new FileInfo(fullPath);
        var start = TimeSpan.FromSeconds(startSeconds);
        var end = TimeSpan.FromSeconds(endSeconds);
        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{file.Extension}");
        AppLog.Write($"[trim_clip] TrimClipForRemoteAsync: '{fullPath}' {start}-{end} replace={replaceOriginal}, exporting to '{tempOut}'");

        try
        {
            await Task.Run(() => ExportTrim(fullPath, tempOut, start, end));

            long tempOutSize = File.Exists(tempOut) ? new FileInfo(tempOut).Length : -1;
            AppLog.Write($"[trim_clip] ExportTrim finished -- tempOut {(tempOutSize < 0 ? "does not exist" : $"is {tempOutSize} bytes")}");
            if (tempOutSize <= 0)
                return (false, "The trim produced no output file (libvlc export failed silently) -- check this PC's own log around ExportTrim for details.", null, 0);

            if (replaceOriginal)
            {
                await CopyWithRetryAsync(tempOut, fullPath, overwrite: true);
                File.Delete(tempOut);
                long replacedSize = new FileInfo(fullPath).Length;
                AppLog.Write($"[trim_clip] replaced '{fullPath}' in place (size {replacedSize} bytes)");
                return (true, null, file.Name, replacedSize);
            }

            string newName = GetTrimmedFileName(file.Name, name => File.Exists(Path.Combine(file.DirectoryName!, name)));
            string destPath = Path.Combine(file.DirectoryName!, newName);
            await CopyWithRetryAsync(tempOut, destPath, overwrite: false);
            File.Delete(tempOut);
            long newSize = new FileInfo(destPath).Length;
            AppLog.Write($"[trim_clip] saved as new file '{destPath}' (size {newSize} bytes)");
            return (true, null, newName, newSize);
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[trim_clip] TrimClipForRemoteAsync threw", ex);
            try { File.Delete(tempOut); } catch { }
            return (false, ex.Message, null, 0);
        }
    }

    private static string GetTrimmedFileName(string originalFileName, Func<string, bool> fileExists)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        string ext = Path.GetExtension(originalFileName);

        string baseName = Regex.Replace(nameWithoutExt, @"(\s*\(trimmed(?:\s+\d+)?\)\s*(\(\d+\))?)+$", "", RegexOptions.IgnoreCase).TrimEnd();
        if (string.IsNullOrEmpty(baseName))
            baseName = nameWithoutExt;

        string candidateName = $"{baseName} (trimmed){ext}";
        if (!fileExists(candidateName))
            return candidateName;

        int i = 1;
        while (true)
        {
            candidateName = $"{baseName} (trimmed) ({i}){ext}";
            if (!fileExists(candidateName))
                return candidateName;
            i++;
        }
    }

    private static string GetTrimmedDestinationPath(string directory, string originalFileName) =>
        Path.Combine(directory, GetTrimmedFileName(originalFileName, name => File.Exists(Path.Combine(directory, name))));

    private void ExportTrim(string sourcePath, string destPath, TimeSpan start, TimeSpan end)
    {
        if (_libVlc is null)
            return;

        using var media = new LibVlc.Media(_libVlc, new Uri(sourcePath));
        media.AddOption($":start-time={start.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        media.AddOption($":stop-time={end.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst={destPath.Replace("\\", "/")}}}");
        media.AddOption(":sout-keep");

        using var exportPlayer = new LibVlc.MediaPlayer(media);
        using var done = new System.Threading.ManualResetEventSlim(false);
        bool encounteredError = false;

        exportPlayer.EndReached += (_, _) => done.Set();

        exportPlayer.EncounteredError += (_, _) =>
        {
            encounteredError = true;
            done.Set();
        };

        exportPlayer.Play();
        if (!done.Wait(TimeSpan.FromMinutes(10)))
            throw new TimeoutException("Trim export took too long.");
        exportPlayer.Stop();

        if (encounteredError)
            throw new InvalidOperationException("LibVLC reported an error during trim export.");

        if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
            throw new InvalidOperationException("Trim export produced no output file.");
    }

    private async Task RunRemoteTrimAsync(bool replaceOriginal)
    {
        (string relPath, string _) = _currentPlayerRemoteOrigin!.Value;
        TimeSpan start = _trimStart!.Value;
        TimeSpan end = _trimEnd!.Value;
        AppLog.Write($"[trim_clip] RunRemoteTrimAsync entered: path='{relPath}' {start}-{end} replace={replaceOriginal}");

        if (replaceOriginal)
        {
            bool? userConfirmed = null;
            ShowConfirmDialog("Replace the original clip with this trimmed version? This can't be undone.", "Replace", c => userConfirmed = c);
            while (!userConfirmed.HasValue && IsVisible)
                await Task.Delay(50);
            if (userConfirmed != true)
            {
                AppLog.Write("[trim_clip] replace not confirmed -- aborted");
                return;
            }
        }

        await CaptureAndShowPlayerFreezeFrameAsync();
        DetachPlayerVideo(keepFreezeFrame: true);
        DisposeVlcPlayerAsync();

        _isTrimming = true;
        TrimReplaceButton.IsEnabled = false;
        TrimSaveNewButton.IsEnabled = false;
        TrimStatusText.Text = $"Trimming on {_settings.PairedPeerName}'s PC...";

        try
        {
            (bool success, string? error, string? newPath, long trimmedSize) = await _pairing.TrimRemoteClipAsync(relPath, start, end, replaceOriginal);
            AppLog.Write(success ? $"[trim_clip] RunRemoteTrimAsync: succeeded (size {trimmedSize} bytes)" : $"[trim_clip] RunRemoteTrimAsync: failed -- {error}");
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't trim on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                return;
            }

            TrimPanel.Visibility = Visibility.Collapsed;
            PlayerTransportRow.Visibility = Visibility.Visible;
            MoveTransportControlsForTrim(intoTrimRow: false);

            string openedRelPath = newPath ?? relPath;
            _toastOverlay.ShowTrimSaved(openedRelPath);

            _ = RefreshGalleryCountAsync();
            RefreshRecentClipsOverlay();

            var remoteFile = new RemoteGalleryFile(
                Name: Path.GetFileName(openedRelPath),
                Size: trimmedSize,
                Modified: DateTime.UtcNow
            );
            OpenRemoteClipStreaming(openedRelPath, remoteFile);
        }
        finally
        {
            _isTrimming = false;
            TrimReplaceButton.IsEnabled = true;
            TrimSaveNewButton.IsEnabled = true;
        }
    }
}
