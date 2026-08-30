using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backtrack.Core;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void UpdateGalleryFooterStats(int count, long totalBytes, string? folderName)
    {
        double mb = totalBytes / (1024.0 * 1024.0);
        string sizeStr = mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb:0.#} MB";
        string folderPrefix = folderName is not null ? $"{folderName}: " : "";
        GalleryTotalStatsText.Text = $"{folderPrefix}{count} clip{(count == 1 ? "" : "s")} · {sizeStr}";

        UpdateGalleryStorageBar();
    }

    private void UpdateGalleryStorageBar()
    {
        try
        {
            if (GalleryStorageProgressBar is null || GalleryStorageText is null)
                return;

            if (_galleryIsRemote)
            {
                if (_lastRemoteStorageInfo is not null)
                {
                    if (_lastRemoteStorageInfo.StorageLimitEnabled && _lastRemoteStorageInfo.StorageLimitGb > 0)
                    {
                        double usedGb = _lastRemoteStorageInfo.ClipsFolderBytes / (double)BytesPerGb;
                        double limitGb = _lastRemoteStorageInfo.StorageLimitGb;
                        double ratio = Math.Clamp(usedGb / limitGb, 0.0, 1.0);

                        const double maxBarWidth = 110.0;
                        GalleryStorageProgressBar.Width = ratio * maxBarWidth;

                        bool isOver85 = ratio >= 0.85;
                        if (isOver85)
                        {
                            GalleryStorageProgressBar.Background = (Brush)FindResource("Rec");
                            GalleryStorageText.Foreground = (Brush)FindResource("Rec");
                        }
                        else
                        {
                            GalleryStorageProgressBar.SetResourceReference(Border.BackgroundProperty, "Accent");
                            GalleryStorageText.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
                        }

                        GalleryStorageText.Text = $"{usedGb:0.0} out of {limitGb:0.#} GB used";
                        return;
                    }
                    else if (_lastRemoteStorageInfo.DriveTotalBytes > 0)
                    {
                        long totalBytes = _lastRemoteStorageInfo.DriveTotalBytes;
                        long freeBytes = _lastRemoteStorageInfo.DriveFreeBytes;
                        long usedBytes = Math.Max(0, totalBytes - freeBytes);

                        double usedRatio = Math.Clamp((double)usedBytes / totalBytes, 0.0, 1.0);
                        double freeGb = freeBytes / (double)BytesPerGb;

                        const double maxBarWidth = 110.0;
                        GalleryStorageProgressBar.Width = usedRatio * maxBarWidth;

                        bool isOver85 = usedRatio >= 0.85;
                        if (isOver85)
                        {
                            GalleryStorageProgressBar.Background = (Brush)FindResource("Rec");
                            GalleryStorageText.Foreground = (Brush)FindResource("Rec");
                        }
                        else
                        {
                            GalleryStorageProgressBar.SetResourceReference(Border.BackgroundProperty, "Accent");
                            GalleryStorageText.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
                        }

                        string freeGbText = freeGb >= 100 ? $"{freeGb:0} GBs Free" : $"{freeGb:0.0} GBs Free";
                        GalleryStorageText.Text = freeGbText;
                        return;
                    }
                }

                GalleryStorageProgressBar.Width = 0;
                GalleryStorageText.Text = $"{_settings.PairedPeerName ?? "Remote"} PC";
                GalleryStorageText.Foreground = (Brush)FindResource("Text2");
                return;
            }

            if (_settings.StorageLimitEnabled && _settings.StorageLimitGb > 0)
            {
                double usedGb = GetClipsFolderUsageBytes() / (double)BytesPerGb;
                double limitGb = _settings.StorageLimitGb;
                double ratio = Math.Clamp(usedGb / limitGb, 0.0, 1.0);

                const double maxBarWidth = 110.0;
                GalleryStorageProgressBar.Width = ratio * maxBarWidth;

                bool isOver85 = ratio >= 0.85;
                if (isOver85)
                {
                    GalleryStorageProgressBar.Background = (Brush)FindResource("Rec");
                    GalleryStorageText.Foreground = (Brush)FindResource("Rec");
                }
                else
                {
                    GalleryStorageProgressBar.SetResourceReference(Border.BackgroundProperty, "Accent");
                    GalleryStorageText.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
                }

                GalleryStorageText.Text = $"{usedGb:0.0} out of {limitGb:0.#} GB used";
            }
            else
            {
                string root = Path.GetPathRoot(_settings.ClipsFolder) ?? "C:\\";
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    long totalBytes = drive.TotalSize;
                    long freeBytes = drive.AvailableFreeSpace;
                    long usedBytes = totalBytes - freeBytes;

                    double usedRatio = Math.Clamp((double)usedBytes / totalBytes, 0.0, 1.0);
                    double freeGb = freeBytes / (double)BytesPerGb;

                    const double maxBarWidth = 110.0;
                    GalleryStorageProgressBar.Width = usedRatio * maxBarWidth;

                    bool isOver85 = usedRatio >= 0.85;
                    if (isOver85)
                    {
                        GalleryStorageProgressBar.Background = (Brush)FindResource("Rec");
                        GalleryStorageText.Foreground = (Brush)FindResource("Rec");
                    }
                    else
                    {
                        GalleryStorageProgressBar.SetResourceReference(Border.BackgroundProperty, "Accent");
                        GalleryStorageText.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
                    }

                    string freeGbText = freeGb >= 100 ? $"{freeGb:0} GBs Free" : $"{freeGb:0.0} GBs Free";
                    GalleryStorageText.Text = freeGbText;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Gallery storage bar update error: {ex.Message}");
        }
    }
}
