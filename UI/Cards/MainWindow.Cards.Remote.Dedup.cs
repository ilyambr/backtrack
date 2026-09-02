using System;
using System.IO;
using System.Linq;
using System.Windows;
using Backtrack.Core;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
    private static bool IsDeduplicatedFileNamePair(string childName, string parentName)
    {
        if (string.IsNullOrWhiteSpace(childName) || string.IsNullOrWhiteSpace(parentName))
            return false;

        string childBase = Path.GetFileNameWithoutExtension(childName);
        string parentBase = Path.GetFileNameWithoutExtension(parentName);
        if (childBase.EndsWith(" (1)", StringComparison.OrdinalIgnoreCase) ||
            childBase.EndsWith(" (2)", StringComparison.OrdinalIgnoreCase) ||
            childBase.EndsWith(" (3)", StringComparison.OrdinalIgnoreCase) ||
            childBase.EndsWith(" (4)", StringComparison.OrdinalIgnoreCase))
        {
            int idx = childBase.LastIndexOf(" (", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                string trimmed = childBase[..idx].Trim();
                if (string.Equals(trimmed, parentBase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsCandidateDeduplicationPair(RemoteGalleryFile newer, RemoteGalleryFile older)
    {
        if (string.Equals(newer.Name, older.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        // 1. Host RPC says newer is deduplicated AND its origin is older
        if (newer.IsDeduplicated && !string.IsNullOrEmpty(newer.OriginFileName) &&
            string.Equals(newer.OriginFileName, older.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Host RPC says older has deduplicated children AND newer's origin points to older
        if (older.HasDeduplicatedChildren && !string.IsNullOrEmpty(newer.OriginFileName) &&
            string.Equals(newer.OriginFileName, older.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        // 3. Synchronized DeduplicationService (imported from host at gallery fetch time)
        if (DeduplicationService.Instance.IsDeduplicated(newer.Name, out var dEntry) &&
            (string.Equals(dEntry?.OriginClipFileName, older.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetFileName(dEntry?.OriginClipPath ?? ""), older.Name, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }
}
