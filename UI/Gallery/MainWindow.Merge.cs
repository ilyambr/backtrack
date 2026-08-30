using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Backtrack.Core;

namespace Backtrack;

public partial class MainWindow : Window
{
    private static readonly Regex ConcatTimeRegex = new(@"time=(\d+):(\d+):(\d+\.?\d*)", RegexOptions.Compiled);
    private readonly Dictionary<string, double> _activeMergingClips = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string, double>? ClipMergeProgressChanged;
    public event Action<string>? ClipMergeCompleted;

    private void SetClipMerging(string clipPath, double progress)
    {
        _activeMergingClips[clipPath] = progress;
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeMergingClips[fileName] = progress;

        Dispatcher.BeginInvoke(() => ClipMergeProgressChanged?.Invoke(clipPath, progress));
    }

    private void ClearClipMerging(string clipPath)
    {
        _activeMergingClips.Remove(clipPath);
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeMergingClips.Remove(fileName);

        Dispatcher.BeginInvoke(() => ClipMergeCompleted?.Invoke(clipPath));
    }

    public async Task MergeDeduplicatedClipsAsync(FileInfo targetFile, string draggedClipPath)
    {
        if (!File.Exists(draggedClipPath) || !File.Exists(targetFile.FullName))
        {
            _toastOverlay.ShowMergeFailed("One of the clips no longer exists.");
            return;
        }

        string originPath = targetFile.FullName;
        string dedupPath = draggedClipPath;

        SetClipMerging(originPath, 0.0);

        long originMs = TryGetCachedDurationMs(new FileInfo(originPath)) ?? 30000;
        long dedupMs = TryGetCachedDurationMs(new FileInfo(dedupPath)) ?? 10000;
        double totalDurationSec = (originMs + dedupMs) / 1000.0;

        string dir = targetFile.DirectoryName ?? _settings.ClipsFolder;
        string ext = targetFile.Extension;
        string tempMergedPath = Path.Combine(Path.GetTempPath(), $"backtrack_merged_{Guid.NewGuid():N}{ext}");

        bool success = await Task.Run(() => RunFfmpegConcatClips(originPath, dedupPath, tempMergedPath, totalDurationSec, prog =>
        {
            SetClipMerging(originPath, prog);
        }));

        if (success && File.Exists(tempMergedPath))
        {
            try
            {
                string backupPath = originPath + ".bak";
                File.Move(originPath, backupPath, true);
                File.Move(tempMergedPath, originPath, true);
                try { File.Delete(backupPath); } catch { }

                try { File.Delete(dedupPath); } catch { }

                try
                {
                    string durCache = GetDurationCachePath(new FileInfo(originPath));
                    if (File.Exists(durCache)) File.Delete(durCache);
                }
                catch { }

                DeduplicationService.Instance.RemoveRecord(dedupPath);
                DeduplicationService.Instance.UpdateOriginAfterMerge(dedupPath, originPath);

                string dedupKey = Path.GetFileName(dedupPath);
                string originKey = Path.GetFileName(originPath);
                if (_settings.ClipMarkers.TryGetValue(dedupKey, out var dedupMarkers) && dedupMarkers.Count > 0)
                {
                    double offsetSec = originMs / 1000.0;
                    if (!_settings.ClipMarkers.TryGetValue(originKey, out var originMarkers))
                    {
                        originMarkers = new List<double>();
                        _settings.ClipMarkers[originKey] = originMarkers;
                    }
                    foreach (double m in dedupMarkers)
                    {
                        originMarkers.Add(m + offsetSec);
                    }
                    _settings.ClipMarkers.Remove(dedupKey);
                    _settings.SaveBookmarks();
                }

                _toastOverlay.ShowMergeSaved(originKey);
                AppLog.Write($"Successfully merged {dedupKey} into {originKey}");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Merge swap error: {ex.Message}");
                _toastOverlay.ShowMergeFailed(ex.Message);
            }
        }
        else
        {
            _toastOverlay.ShowMergeFailed("FFmpeg merge process failed.");
        }

        ClearClipMerging(originPath);

        Dispatcher.Invoke(() =>
        {
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            RefreshRecentClipsOverlay();
        });
    }

    private static int ProbeAudioTrackCount(string ffmpegPath, string videoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 1;
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            var matches = Regex.Matches(err, @"Stream #0:\d+.*?: Audio:", RegexOptions.IgnoreCase);
            return matches.Count > 0 ? matches.Count : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static double ProbeDurationSec(string ffmpegPath, string videoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            var match = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+\.?\d*)");
            if (match.Success)
            {
                if (double.TryParse(match.Groups[1].Value, out double h) &&
                    double.TryParse(match.Groups[2].Value, out double m) &&
                    double.TryParse(match.Groups[3].Value, out double s))
                {
                    return h * 3600.0 + m * 60.0 + s;
                }
            }
        }
        catch { }
        return 0;
    }

    private static float[]? ExtractAudioPcm(string ffmpegPath, string videoPath, double startSec, double durationSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-ss {startSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} -t {durationSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} -i \"{videoPath}\" -vn -ac 1 -ar 8000 -f f32le pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = new Process { StartInfo = psi };
            using var ms = new MemoryStream();
            proc.Start();

            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit(5000);

            byte[] bytes = ms.ToArray();
            if (bytes.Length < 3200) return null;

            float[] samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }
        catch
        {
            return null;
        }
    }

    private static double DetectOverlapOffsetSeconds(string ffmpegPath, string firstClipPath, string secondClipPath, double firstClipDur, double secondClipDur)
    {
        try
        {
            double windowSec = Math.Min(15.0, Math.Min(firstClipDur, secondClipDur));
            if (windowSec < 2.0) return 0.0;

            double start1 = Math.Max(0.0, firstClipDur - windowSec);
            float[]? a1 = ExtractAudioPcm(ffmpegPath, firstClipPath, start1, windowSec);
            float[]? a2 = ExtractAudioPcm(ffmpegPath, secondClipPath, 0.0, windowSec);

            if (a1 == null || a2 == null || a1.Length < 16000 || a2.Length < 16000)
                return 0.0;

            int sampleRate = 8000;
            int refLen = Math.Min(sampleRate * 5 / 2, a1.Length / 2);
            int refStart = a1.Length - refLen;

            double refEnergy = 0;
            for (int i = 0; i < refLen; i++)
            {
                float v = a1[refStart + i];
                refEnergy += v * v;
            }
            double refNorm = Math.Sqrt(refEnergy);
            if (refNorm < 1e-4) return 0.0;

            double bestCorr = -1.0;
            int bestShift = 0;
            int maxShift = a2.Length - refLen;

            for (int shift = 0; shift <= maxShift; shift += 4)
            {
                double dot = 0, candEnergy = 0;
                for (int i = 0; i < refLen; i++)
                {
                    float r = a1[refStart + i];
                    float c = a2[shift + i];
                    dot += r * c;
                    candEnergy += c * c;
                }

                if (candEnergy > 1e-6)
                {
                    double corr = dot / (refNorm * Math.Sqrt(candEnergy));
                    if (corr > bestCorr)
                    {
                        bestCorr = corr;
                        bestShift = shift;
                    }
                }
            }

            int fineMin = Math.Max(0, bestShift - 8);
            int fineMax = Math.Min(maxShift, bestShift + 8);
            for (int shift = fineMin; shift <= fineMax; shift++)
            {
                double dot = 0, candEnergy = 0;
                for (int i = 0; i < refLen; i++)
                {
                    float r = a1[refStart + i];
                    float c = a2[shift + i];
                    dot += r * c;
                    candEnergy += c * c;
                }

                if (candEnergy > 1e-6)
                {
                    double corr = dot / (refNorm * Math.Sqrt(candEnergy));
                    if (corr > bestCorr)
                    {
                        bestCorr = corr;
                        bestShift = shift;
                    }
                }
            }

            AppLog.Write($"[Merge] Audio correlation match: {bestCorr:P1} at sample offset {bestShift}");
            if (bestCorr > 0.6)
            {
                double spliceOffsetSec = (double)(bestShift + refLen) / sampleRate;
                return spliceOffsetSec;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Merge] Audio cross-correlation error: {ex.Message}");
        }

        return 0.0;
    }

    private bool RunFfmpegConcatClips(string firstClipPath, string secondClipPath, string destPath, double totalDurationSec, Action<double>? onProgress)
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            AppLog.Write("FFmpeg not found for merge.");
            return false;
        }

        try
        {
            int audioTracks = ProbeAudioTrackCount(ffmpegPath, firstClipPath);
            string videoArgs = GetFastestVideoEncoderArgs(ffmpegPath, 16000);

            double firstClipDur = ProbeDurationSec(ffmpegPath, firstClipPath);
            double secondClipDur = ProbeDurationSec(ffmpegPath, secondClipPath);

            double secondClipStartOffsetSec = DetectOverlapOffsetSeconds(ffmpegPath, firstClipPath, secondClipPath, firstClipDur, secondClipDur);

            if (secondClipStartOffsetSec <= 0.05 && DeduplicationService.Instance.IsDeduplicated(secondClipPath, out var dedupRecord) && dedupRecord != null)
            {
                double targetSec = dedupRecord.ExactDurationSeconds > 0 ? dedupRecord.ExactDurationSeconds : dedupRecord.DurationSeconds;
                if (targetSec > 0 && secondClipDur > (targetSec + 0.05))
                {
                    secondClipStartOffsetSec = secondClipDur - targetSec;
                    AppLog.Write($"[Merge] High-precision timestamp trim: {secondClipStartOffsetSec:F3}s from deduplicated clip ({secondClipPath})");
                }
            }
            else if (secondClipStartOffsetSec > 0.05)
            {
                AppLog.Write($"[Merge] Sample-accurate audio correlation trim: {secondClipStartOffsetSec:F3}s from deduplicated clip ({secondClipPath})");
            }

            string secondInputArg = secondClipStartOffsetSec > 0.05
                ? $"-ss {secondClipStartOffsetSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} -i \"{secondClipPath}\""
                : $"-i \"{secondClipPath}\"";

            var sbFilter = new System.Text.StringBuilder();
            sbFilter.Append("[0:v:0]setpts=PTS-STARTPTS[v0];");
            for (int i = 0; i < audioTracks; i++)
            {
                sbFilter.Append($"[0:a:{i}]asetpts=PTS-STARTPTS,aresample=async=1000[a0_{i}];");
            }

            sbFilter.Append("[1:v:0]setpts=PTS-STARTPTS[v1];");
            for (int i = 0; i < audioTracks; i++)
            {
                sbFilter.Append($"[1:a:{i}]asetpts=PTS-STARTPTS,aresample=async=1000[a1_{i}];");
            }

            sbFilter.Append("[v0]");
            for (int i = 0; i < audioTracks; i++) sbFilter.Append($"[a0_{i}]");
            sbFilter.Append("[v1]");
            for (int i = 0; i < audioTracks; i++) sbFilter.Append($"[a1_{i}]");

            sbFilter.Append($"concat=n=2:v=1:a={audioTracks}[v]");
            for (int i = 0; i < audioTracks; i++) sbFilter.Append($"[a{i}]");

            var mapArgs = new System.Text.StringBuilder();
            mapArgs.Append("-map \"[v]\" ");
            for (int i = 0; i < audioTracks; i++)
            {
                mapArgs.Append($"-map \"[a{i}]\" ");
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{firstClipPath}\" {secondInputArg} -filter_complex \"{sbFilter}\" {mapArgs}{videoArgs} -c:a aac -b:a 192k -g 60 -keyint_min 30 -movflags +faststart \"{destPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = new Process { StartInfo = psi };
            var stderrOutput = new System.Text.StringBuilder();

            proc.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    stderrOutput.AppendLine(args.Data);
                    if (onProgress is not null && totalDurationSec > 0)
                    {
                        var match = ConcatTimeRegex.Match(args.Data);
                        if (match.Success)
                        {
                            if (double.TryParse(match.Groups[1].Value, out double h) &&
                                double.TryParse(match.Groups[2].Value, out double m) &&
                                double.TryParse(match.Groups[3].Value, out double s))
                            {
                                double currentSec = h * 3600.0 + m * 60.0 + s;
                                double progress = Math.Clamp(currentSec / totalDurationSec, 0.0, 0.99);
                                onProgress(progress);
                            }
                        }
                    }
                }
            };

            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            bool exited = proc.WaitForExit(300000);
            if (exited && proc.ExitCode == 0 && File.Exists(destPath) && new FileInfo(destPath).Length > 10000)
            {
                return true;
            }

            AppLog.Write($"Seamless concat failed. Stderr: {stderrOutput}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Write($"FFmpeg concat exception: {ex.Message}");
            return false;
        }
    }
}
