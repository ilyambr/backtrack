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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
        private async Task<string?> EnsureThumbnailCachedAsync(FileInfo file)
    {
        if (!file.Exists || file.Length == 0)
            return null;

        string cachePath = GetThumbnailCachePath(file);
        
        
        
        
        
        bool durationCached = File.Exists(GetDurationCachePath(file));
        if (File.Exists(cachePath) && durationCached)
            return cachePath;

        if (_libVlc is null)
            return null;

        await ThumbnailGenerationLock.WaitAsync();
        try
        {
            if (!File.Exists(cachePath) || !File.Exists(GetDurationCachePath(file)))
                await GenerateThumbnailAsync(file, cachePath);
        }
        finally
        {
            ThumbnailGenerationLock.Release();
        }

        return File.Exists(cachePath) ? cachePath : null;
    }


        private async Task PrewarmGalleryThumbnailsAsync()
    {
        if (_libVlc is null || !Directory.Exists(_settings.ClipsFolder))
            return;

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(_settings.ClipsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length > 0)
                .OrderByDescending(f => f.LastWriteTime) 
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (FileInfo file in files)
            await EnsureThumbnailCachedAsync(file);
    }


    private async Task GenerateThumbnailAsync(FileInfo file, string cachePath)
    {
        
        
        
        
        
        
        
        await Task.Run(() =>
        {
            try
            {
                using var media = new LibVlc.Media(_libVlc!, file.FullName, LibVlc.FromType.FromPath);
                media.AddOption(":no-audio");
                using var player = new LibVlc.MediaPlayer(media) { Hwnd = _thumbnailSinkHwnd };
                using var playingSignal = new ManualResetEventSlim(false);

                player.Playing += (_, _) => playingSignal.Set();
                player.EncounteredError += (_, _) => playingSignal.Set();

                player.Play();
                if (!playingSignal.Wait(TimeSpan.FromSeconds(5)))
                {
                    player.Stop();
                    return;
                }

                
                
                try { File.WriteAllText(Path.ChangeExtension(cachePath, ".duration"), player.Length.ToString()); }
                catch {  }

                long seekTarget = Math.Min(2000, Math.Max(player.Length / 4, 0));
                if (seekTarget > 0)
                    player.Time = seekTarget;
                Thread.Sleep(200);

                player.TakeSnapshot(0, cachePath, 480, 0);
                for (int i = 0; i < 20 && !File.Exists(cachePath); i++)
                    Thread.Sleep(100);

                player.Stop();
            }
            catch
            {
                
                try
                {
                    string durPath = Path.ChangeExtension(cachePath, ".duration");
                    if (!File.Exists(cachePath) && File.Exists(durPath))
                        File.Delete(durPath);
                }
                catch { }
            }
        });
    }
}
