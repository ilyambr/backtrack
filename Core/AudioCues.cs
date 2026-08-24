using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Backtrack.Core;

/// <summary>
/// Instant, zero-latency, zero-codec-dependency audio cue playback using Win32 PlaySound from memory.
/// Preloads PCM WAV bytes into memory at startup for true zero-latency sound effects across all Windows versions.
/// </summary>
public static class AudioCues
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PlaySound(byte[]? pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private static string AssetsAudioDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");

    private static byte[]? _clipSavedWav;
    private static byte[]? _recSavedWav;
    private static byte[]? _recStartedWav;
    private static bool _initialized;

    public static void Initialize()
    {
        try
        {
            _clipSavedWav = LoadWavBytes("ps3-trophy-sound-effect.wav");
            _recSavedWav = LoadWavBytes("ps3-trophy-sound-effect.wav");
            _recStartedWav = LoadWavBytes("ps3-game-startup-chime.wav");

            _initialized = true;
            AppLog.Write("[AudioCues] Initialized successfully with in-memory Win32 PlaySound");
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[AudioCues] Initialize failed", ex);
        }
    }

    private static byte[]? LoadWavBytes(string fileName)
    {
        try
        {
            string diskPath = Path.Combine(AssetsAudioDir, fileName);
            if (File.Exists(diskPath))
            {
                return File.ReadAllBytes(diskPath);
            }

            // Fallback: try embedded resource from assembly
            Assembly asm = typeof(AudioCues).Assembly;
            string resourceName = $"Backtrack.Assets.Audio.{fileName}";
            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            AppLog.WriteError($"[AudioCues] Failed to load {fileName}", ex);
        }
        return null;
    }

    public static void PlayRecordingStarted()
    {
        PlayCue(_recStartedWav, "ps3-game-startup-chime.wav", "ps3-game-startup-chime.mp3");
    }

    public static void PlayRecordingSaved()
    {
        PlayCue(_recSavedWav, "ps3-trophy-sound-effect.wav", "ps3-trophy-sound-effect.mp3");
    }

    public static void PlayClipSaved()
    {
        PlayCue(_clipSavedWav, "ps3-trophy-sound-effect.wav", "ps3-trophy-sound-effect.mp3");
    }

    private static void PlayCue(byte[]? memoryBuffer, string wavFileName, string mp3FallbackFileName)
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

            // 1. Play directly from memory buffer (0ms latency, zero disk I/O)
            if (memoryBuffer != null && memoryBuffer.Length > 0)
            {
                PlaySound(memoryBuffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                AppLog.Write($"[AudioCues] Played in-memory cue ({wavFileName})");
                return;
            }

            // 2. Fallback to WAV file on disk
            string wavPath = Path.Combine(AssetsAudioDir, wavFileName);
            if (File.Exists(wavPath))
            {
                PlaySound(wavPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                AppLog.Write($"[AudioCues] Played disk WAV cue ({wavFileName})");
                return;
            }

            // 3. Fallback to MP3 file on disk using MediaPlayer
            string mp3Path = Path.Combine(AssetsAudioDir, mp3FallbackFileName);
            if (File.Exists(mp3Path))
            {
                var player = new System.Windows.Media.MediaPlayer();
                player.Open(new Uri(mp3Path));
                player.Play();
                AppLog.Write($"[AudioCues] Played disk MP3 fallback cue ({mp3FallbackFileName})");
            }
        }
        catch (Exception ex)
        {
            AppLog.WriteError($"[AudioCues] Failed to play cue {wavFileName}", ex);
        }
    }
}
