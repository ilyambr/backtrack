using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Backtrack.Core;

/// <summary>
/// Instant, zero-latency, zero-codec-dependency audio cue playback using Win32 PlaySound from memory.
/// Preloads PCM WAV bytes into memory at startup for true zero-latency sound effects across all Windows versions.
/// Supports dynamic volume scaling and seamless remote execution over PairingService.
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

    public static Func<string, int, Task<bool>>? RemoteCuePlayer { get; set; }
    public static Func<bool>? IsRemoteModeActive { get; set; }

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
        PlayCue("recording_started", _recStartedWav, "ps3-game-startup-chime.wav", "ps3-game-startup-chime.mp3");
    }

    public static void PlayRecordingSaved()
    {
        PlayCue("recording_saved", _recSavedWav, "ps3-trophy-sound-effect.wav", "ps3-trophy-sound-effect.mp3");
    }

    public static void PlayClipSaved()
    {
        PlayCue("clip_saved", _clipSavedWav, "ps3-trophy-sound-effect.wav", "ps3-trophy-sound-effect.mp3");
    }

    public static void PlayPreview(int volume)
    {
        if (!_initialized)
            Initialize();

        PlayLocalCueDirect(_clipSavedWav, "ps3-trophy-sound-effect.wav", "ps3-trophy-sound-effect.mp3", volume);
    }

    public static void PlayCueByName(string cueName, int volume = -1)
    {
        if (!_initialized)
            Initialize();

        if (string.Equals(cueName, "recording_started", StringComparison.OrdinalIgnoreCase))
        {
            PlayLocalCueDirect(_recStartedWav, "ps3-game-startup-chime.wav", "ps3-game-startup-chime.mp3", volume);
        }
        else
        {
            PlayLocalCueDirect(_clipSavedWav, "ps3-trophy-sound-effect.wav", "ps3-trophy-sound-effect.mp3", volume);
        }
    }

    private static void PlayCue(string cueName, byte[]? memoryBuffer, string wavFileName, string mp3FallbackFileName)
    {
        try
        {
            var settings = AppSettings.Load();
            if (settings.DisableAudioCues)
                return;

            int volume = settings.AudioCueVolume;
            if (volume <= 0)
                return;

            // If OBS is remote AND Backtrack is paired with the remote PC, delegate audio cue to remote PC
            if (IsRemoteModeActive?.Invoke() == true && RemoteCuePlayer != null)
            {
                _ = RemoteCuePlayer(cueName, volume);
                AppLog.Write($"[AudioCues] Forwarded cue '{cueName}' to remote paired PC (vol {volume}%)");
                return;
            }

            PlayLocalCueDirect(memoryBuffer, wavFileName, mp3FallbackFileName, volume);
        }
        catch (Exception ex)
        {
            AppLog.WriteError($"[AudioCues] Failed to play cue {wavFileName}", ex);
        }
    }

    private static void PlayLocalCueDirect(byte[]? memoryBuffer, string wavFileName, string mp3FallbackFileName, int volume)
    {
        try
        {
            if (!_initialized)
                Initialize();

            if (volume < 0)
            {
                var settings = AppSettings.Load();
                if (settings.DisableAudioCues)
                    return;
                volume = settings.AudioCueVolume;
            }

            if (volume <= 0)
                return;

            double volumeFraction = Math.Clamp(volume / 100.0, 0.0, 1.0);

            // 1. Play scaled directly from memory buffer (0ms latency, zero disk I/O)
            if (memoryBuffer != null && memoryBuffer.Length > 0)
            {
                byte[]? scaledBuffer = ScalePcmWav(memoryBuffer, volumeFraction);
                if (scaledBuffer != null && scaledBuffer.Length > 0)
                {
                    PlaySound(scaledBuffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                    AppLog.Write($"[AudioCues] Played in-memory cue ({wavFileName}) at {volume}% volume");
                    return;
                }
            }

            // 2. Fallback to WAV file on disk
            string wavPath = Path.Combine(AssetsAudioDir, wavFileName);
            if (File.Exists(wavPath))
            {
                byte[] raw = File.ReadAllBytes(wavPath);
                byte[]? scaled = ScalePcmWav(raw, volumeFraction);
                if (scaled != null && scaled.Length > 0)
                {
                    PlaySound(scaled, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                    AppLog.Write($"[AudioCues] Played disk WAV cue ({wavFileName}) at {volume}% volume");
                    return;
                }
            }

            // 3. Fallback to MP3 file on disk using MediaPlayer
            string mp3Path = Path.Combine(AssetsAudioDir, mp3FallbackFileName);
            if (File.Exists(mp3Path))
            {
                var player = new System.Windows.Media.MediaPlayer();
                player.Volume = volumeFraction;
                player.Open(new Uri(mp3Path));
                player.Play();
                AppLog.Write($"[AudioCues] Played disk MP3 fallback cue ({mp3FallbackFileName}) at {volume}% volume");
            }
        }
        catch (Exception ex)
        {
            AppLog.WriteError($"[AudioCues] Failed to play local cue {wavFileName}", ex);
        }
    }

    private static byte[]? ScalePcmWav(byte[]? wavBytes, double volumeFactor)
    {
        if (wavBytes == null || wavBytes.Length < 44)
            return wavBytes;
        if (volumeFactor >= 0.999)
            return wavBytes;
        if (volumeFactor <= 0.001)
            return null;

        try
        {
            // Scan for "fmt " chunk to verify 16-bit uncompressed PCM
            int fmtIndex = -1;
            for (int i = 12; i < wavBytes.Length - 8; i++)
            {
                if (wavBytes[i] == 'f' && wavBytes[i + 1] == 'm' && wavBytes[i + 2] == 't' && wavBytes[i + 3] == ' ')
                {
                    fmtIndex = i;
                    break;
                }
            }

            int bitsPerSample = 16;
            if (fmtIndex >= 0 && fmtIndex + 24 <= wavBytes.Length)
            {
                short formatTag = BitConverter.ToInt16(wavBytes, fmtIndex + 8);
                if (formatTag != 1) // 1 = WAVE_FORMAT_PCM
                    return wavBytes;
                bitsPerSample = BitConverter.ToInt16(wavBytes, fmtIndex + 22);
            }

            if (bitsPerSample != 16)
                return wavBytes;

            // Scan for "data" chunk
            int dataIndex = -1;
            int dataSize = 0;
            for (int i = 12; i < wavBytes.Length - 8; i++)
            {
                if (wavBytes[i] == 'd' && wavBytes[i + 1] == 'a' && wavBytes[i + 2] == 't' && wavBytes[i + 3] == 'a')
                {
                    dataIndex = i + 8;
                    dataSize = BitConverter.ToInt32(wavBytes, i + 4);
                    break;
                }
            }

            if (dataIndex < 0 || dataIndex >= wavBytes.Length)
                return wavBytes;

            byte[] scaled = new byte[wavBytes.Length];
            Buffer.BlockCopy(wavBytes, 0, scaled, 0, wavBytes.Length);

            int end = Math.Min(scaled.Length, dataIndex + dataSize);
            for (int i = dataIndex; i + 1 < end; i += 2)
            {
                short sample = BitConverter.ToInt16(scaled, i);
                int scaledSample = (int)Math.Round(sample * volumeFactor);
                scaledSample = Math.Clamp(scaledSample, short.MinValue, short.MaxValue);
                scaled[i] = (byte)(scaledSample & 0xFF);
                scaled[i + 1] = (byte)((scaledSample >> 8) & 0xFF);
            }

            return scaled;
        }
        catch
        {
            return wavBytes;
        }
    }
}
