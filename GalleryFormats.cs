namespace Backtrack;

/// <summary>Shared between MainWindow's local Gallery and PairingService's remote
/// gallery listing/download, so both sides agree on what counts as a clip.</summary>
public static class GalleryFormats
{
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".flv", ".mov" };
}
