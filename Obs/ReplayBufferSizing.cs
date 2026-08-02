using System;
using System.Collections.Generic;
using System.IO;

namespace Backtrack.Obs;

/// <summary>
/// Estimates a RAM disk size to safely hold OBS's replay buffer for a given
/// target duration, using OBS's own config on disk rather than a hardcoded
/// guess. Advisory only -- Backtrack never writes to any of these files, and
/// the result is just a suggested value for the user to review and apply by
/// hand in Settings, never auto-applied.
/// </summary>
public static class ReplayBufferSizing
{
    public readonly record struct Estimate(int SuggestedSizeMb, int AssumedBitrateKbps, string Source);

    public static Estimate? TryEstimate(int targetMinutes)
    {
        try
        {
            string? profileDir = TryGetActiveProfileDir();
            if (profileDir is null)
                return null;

            string basicIniPath = Path.Combine(profileDir, "basic.ini");
            if (!File.Exists(basicIniPath))
                return null;

            Dictionary<(string Section, string Key), string> ini = ParseIni(basicIniPath);
            bool isSimple = ini.TryGetValue(("Output", "Mode"), out string? mode) && mode == "Simple";

            int? bitrateKbps = null;
            string source = "a generous resolution-based default (no fixed bitrate could be read from OBS's own config)";

            // Advanced mode, and Simple mode with multitrack video's auto bitrate,
            // both leave no single fixed bitrate number sitting in basic.ini --
            // only plain Simple output has one, so that's the only case read
            // directly rather than falling back to the resolution guess below.
            if (isSimple &&
                ini.TryGetValue(("SimpleOutput", "VBitrate"), out string? vb) && int.TryParse(vb, out int vBitrate))
            {
                int aBitrate = ini.TryGetValue(("SimpleOutput", "ABitrate"), out string? ab) && int.TryParse(ab, out int a) ? a : 160;
                bitrateKbps = vBitrate + aBitrate;
                source = "Simple output's configured video + audio bitrate";
            }

            if (bitrateKbps is null)
            {
                int width = ini.TryGetValue(("Video", "BaseCX"), out string? cx) && int.TryParse(cx, out int w) ? w : 1920;
                bitrateKbps = width switch
                {
                    >= 3840 => 35000,
                    >= 2560 => 16000,
                    >= 1920 => 10000,
                    _ => 6000,
                };
            }

            double bytesPerSecond = bitrateKbps.Value * 1000 / 8.0;
            double totalBytes = bytesPerSecond * targetMinutes * 60;
            // +30% headroom for NTFS overhead and the fact this is an estimate,
            // not a measurement; rounded up to a clean 256MB step.
            int suggestedMb = (int)Math.Ceiling(totalBytes * 1.3 / 1024 / 1024 / 256) * 256;

            return new Estimate(Math.Max(suggestedMb, 256), bitrateKbps.Value, source);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetActiveProfileDir()
    {
        string userIniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "user.ini");
        if (!File.Exists(userIniPath))
            return null;

        Dictionary<(string Section, string Key), string> ini = ParseIni(userIniPath);
        if (!ini.TryGetValue(("Basic", "ProfileDir"), out string? profileDirName) || string.IsNullOrEmpty(profileDirName))
            return null;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "basic", "profiles", profileDirName);
    }

    private static Dictionary<(string Section, string Key), string> ParseIni(string path)
    {
        var result = new Dictionary<(string, string), string>();
        string section = "";
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            result[(section, line[..eq])] = line[(eq + 1)..];
        }
        return result;
    }
}
