using System.Runtime.InteropServices;

namespace Backtrack.Interop;

/// <summary>Sends a file to the Recycle Bin instead of permanently deleting it -- free, real "undo" via Windows' own trash, no custom toast/undo system needed.</summary>
public static class RecycleBin
{
    private const int FO_DELETE = 3;
    private const int FOF_ALLOWUNDO = 0x40;
    private const int FOF_NOCONFIRMATION = 0x10;
    private const int FOF_SILENT = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    /// <returns>true if the file was moved to the Recycle Bin successfully.</returns>
    public static bool Delete(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0' + '\0', // double-null terminated, as SHFileOperation requires
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        int result = SHFileOperation(ref op);
        return result == 0 && op.fAnyOperationsAborted == 0;
    }
}
