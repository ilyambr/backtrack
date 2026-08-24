using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Backtrack.Core;

/// <summary>
/// Instant, zero-latency audio cue playback using Windows native winmm.dll MCI.
/// Opens aliases at startup and plays from 0 on demand.
/// </summary>
public static class AudioCues
{
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern long mciSendString(string command, string? returnString, int returnLength, IntPtr callback);

    private static string AssetsAudioDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");
    private static bool _initialized;

    public static void Initialize()
    {
        try
        {
            string recPath = Path.Combine(AssetsAudioDir, "ps3-game-startup-chime.mp3");
            string clipPath = Path.Combine(AssetsAudioDir, "ps3-trophy-sound-effect.mp3");

            if (File.Exists(recPath))
            {
                mciSendString("close rec_chime", null, 0, IntPtr.Zero);
                mciSendString($"open \"{recPath}\" type mpegvideo alias rec_chime", null, 0, IntPtr.Zero);
            }

            if (File.Exists(clipPath))
            {
                mciSendString("close clip_chime", null, 0, IntPtr.Zero);
                mciSendString($"open \"{clipPath}\" type mpegvideo alias clip_chime", null, 0, IntPtr.Zero);
            }

            _initialized = true;
            AppLog.Write("[AudioCues] Initialized successfully with native winmm MCI");
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[AudioCues] Initialize failed", ex);
        }
    }

    public static void PlayRecordingSaved()
    {
        PlaySound("rec_chime", "ps3-game-startup-chime.mp3");
    }

    public static void PlayClipSaved()
    {
        PlaySound("clip_chime", "ps3-trophy-sound-effect.mp3");
    }

    private static void PlaySound(string alias, string fileName)
    {
        try
        {
            var settings = AppSettings.Load();
            if (settings.DisableAudioCues)
                return;

            if (!_initialized)
            {
                Initialize();
            }

            // 'play <alias> from 0' seeks to start and plays instantly
            long result = mciSendString($"play {alias} from 0", null, 0, IntPtr.Zero);
            if (result != 0)
            {
                // Fallback: re-open and play if device was closed or reset
                string soundPath = Path.Combine(AssetsAudioDir, fileName);
                if (File.Exists(soundPath))
                {
                    mciSendString($"close {alias}", null, 0, IntPtr.Zero);
                    mciSendString($"open \"{soundPath}\" type mpegvideo alias {alias}", null, 0, IntPtr.Zero);
                    mciSendString($"play {alias} from 0", null, 0, IntPtr.Zero);
                }
            }

            AppLog.Write($"[AudioCues] Played {alias} ({fileName})");
        }
        catch (Exception ex)
        {
            AppLog.WriteError($"[AudioCues] Failed to play {fileName}", ex);
        }
    }
}
