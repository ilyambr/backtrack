using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Backtrack;

internal static class ShellDragHelper
{

    [ComImport]
    [Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelperCoClass { }

    [ComImport]
    [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        [PreserveSig]
        int InitializeFromBitmap(
            [In] ref SHDRAGIMAGE pshdi,
            [In] ComIDataObject pDataObject);

        [PreserveSig]
        int InitializeFromWindow(
            IntPtr hwnd,
            [In] ref NativeMethods.POINT pt,
            [In] ComIDataObject pDataObject);
    }

    [ComImport]
    [Guid("4657278B-411B-11D2-839A-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        [PreserveSig]
        int DragEnter(IntPtr hwndTarget, ComIDataObject dataObject, ref NativeMethods.POINT pt, int effect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int DragOver(ref NativeMethods.POINT pt, int effect);

        [PreserveSig]
        int Drop(ComIDataObject dataObject, ref NativeMethods.POINT pt, int effect);

        [PreserveSig]
        int Show(bool show);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHDRAGIMAGE
    {
        public SIZEL sizeDragImage;
        public NativeMethods.POINT ptOffset;
        public IntPtr hbmpDragImage;
        public uint crColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZEL { public int cx; public int cy; }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IntPtr ppv);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IntPtr ppvOut);
    }

    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromIDLists(
        uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgpidl,
        out IShellItemArray ppsiItemArray);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int GhostWidth = 180;
    private const int ThumbHeight = 101;
    private const int LabelHeight = 30;
    private const int TotalHeight = ThumbHeight + LabelHeight;

    public static void DoFileDragDrop(
        DependencyObject dragSource,
        string[] filePaths,
        ImageSource? thumbnail,
        string label)
    {
        if (filePaths == null || filePaths.Length == 0) return;

        object dataObjectToUse;
        ComIDataObject? shellDataObj = null;

        try
        {
            shellDataObj = CreateShellDataObject(filePaths);
        }
        catch (Exception ex)
        {
            AppLog.Write($"ShellDataObj creation fallback: {ex.Message}");
        }

        if (shellDataObj != null)
        {
            AttachDragImage(shellDataObj, thumbnail, label);
            dataObjectToUse = new System.Windows.DataObject(shellDataObj);
        }
        else
        {
            dataObjectToUse = new System.Windows.DataObject(DataFormats.FileDrop, filePaths);
        }

        try
        {
            DragDrop.DoDragDrop(dragSource, dataObjectToUse, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            AppLog.Write($"ShellDragHelper DoDragDrop exception: {ex.Message}");
        }
    }

    private static IDropTargetHelper? _dropTargetHelper;
    private static IDropTargetHelper DropTargetHelper => _dropTargetHelper ??= (IDropTargetHelper)new DragDropHelperCoClass();
    private static Action? _activeLeaveAction;

    public static void ResetDropHelper()
    {
        try
        {
            _activeLeaveAction?.Invoke();
            _activeLeaveAction = null;
            DropTargetHelper.DragLeave();
        }
        catch { }
    }

    public static void EnableDropPreview(UIElement element, Window window)
    {
        element.AllowDrop = true;
        bool isEntered = false;

        _activeLeaveAction = () =>
        {
            if (isEntered)
            {
                isEntered = false;
                try { DropTargetHelper.DragLeave(); } catch { }
            }
        };

        element.PreviewDragEnter += (s, e) =>
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                ComIDataObject? comObj = GetComDataObject(e.Data);
                if (comObj != null)
                {
                    if (!isEntered)
                    {
                        try { DropTargetHelper.DragEnter(hwnd, comObj, ref pt, (int)e.Effects); } catch { }
                        isEntered = true;
                    }
                    else
                    {
                        try { DropTargetHelper.DragOver(ref pt, (int)e.Effects); } catch { }
                    }
                }
            }
        };

        element.PreviewDragOver += (s, e) =>
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                if (!isEntered)
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    ComIDataObject? comObj = GetComDataObject(e.Data);
                    if (comObj != null)
                    {
                        try { DropTargetHelper.DragEnter(hwnd, comObj, ref pt, (int)e.Effects); } catch { }
                        isEntered = true;
                    }
                }
                else
                {
                    try { DropTargetHelper.DragOver(ref pt, (int)e.Effects); } catch { }
                }
            }
        };

        element.PreviewDragLeave += (s, e) =>
        {
            System.Windows.Point pos = e.GetPosition(element);
            System.Windows.Size size = element.RenderSize;
            if (pos.X < 0 || pos.Y < 0 || pos.X >= size.Width || pos.Y >= size.Height)
            {
                if (isEntered)
                {
                    try { DropTargetHelper.DragLeave(); } catch { }
                    isEntered = false;
                }
            }
        };

        element.PreviewDrop += (s, e) =>
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                ComIDataObject? comObj = GetComDataObject(e.Data);
                if (comObj != null && isEntered)
                {
                    try { DropTargetHelper.Drop(comObj, ref pt, (int)e.Effects); } catch { }
                }
            }
            isEntered = false;
        };
    }

    private static ComIDataObject? GetComDataObject(System.Windows.IDataObject data)
    {
        if (data is ComIDataObject comObj)
            return comObj;
        if (data is System.Windows.DataObject wpfObj && wpfObj is ComIDataObject wpfComObj)
            return wpfComObj;
        return null;
    }

    private static ComIDataObject? CreateShellDataObject(string[] filePaths)
    {
        if (filePaths.Length == 1)
        {
            SHCreateItemFromParsingName(filePaths[0], IntPtr.Zero, IID_IShellItem, out IntPtr pItem);
            if (pItem == IntPtr.Zero) return null;
            try
            {
                var item = (IShellItem)Marshal.GetObjectForIUnknown(pItem);
                item.BindToHandler(IntPtr.Zero, BHID_DataObject, IID_IDataObject, out IntPtr pDataObj);
                if (pDataObj == IntPtr.Zero) return null;
                return (ComIDataObject)Marshal.GetObjectForIUnknown(pDataObj);
            }
            finally
            {
                Marshal.Release(pItem);
            }
        }
        else
        {
            var pidls = new IntPtr[filePaths.Length];
            for (int i = 0; i < filePaths.Length; i++)
            {
                SHParseDisplayName(filePaths[i], IntPtr.Zero, out pidls[i], 0, out _);
            }

            try
            {
                SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out IShellItemArray itemArray);
                int hr = itemArray.BindToHandler(IntPtr.Zero, BHID_DataObject, IID_IDataObject, out IntPtr pDataObj);
                if (hr != 0 || pDataObj == IntPtr.Zero) return null;
                return (ComIDataObject)Marshal.GetObjectForIUnknown(pDataObj);
            }
            finally
            {
                for (int i = 0; i < pidls.Length; i++)
                {
                    if (pidls[i] != IntPtr.Zero)
                        CoTaskMemFree(pidls[i]);
                }
            }
        }
    }

    private static void AttachDragImage(ComIDataObject dataObject, ImageSource? thumbnail, string label)
    {
        try
        {
            using var bmp = RenderGhostBitmap(thumbnail, label);
            if (bmp is null) return;

            IntPtr hBitmap = bmp.GetHbitmap(System.Drawing.Color.Black);

            var helperObj = new DragDropHelperCoClass();
            if (helperObj is not IDragSourceHelper helper)
            {
                DeleteObject(hBitmap);
                return;
            }

            var shdi = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZEL { cx = GhostWidth, cy = TotalHeight },
                ptOffset = new NativeMethods.POINT { X = 12, Y = 12 },
                hbmpDragImage = hBitmap,
                crColorKey = 0xFFFF_FFFFu,
            };

            int hr = helper.InitializeFromBitmap(ref shdi, dataObject);
            if (hr != 0)
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"AttachDragImage exception: {ex.Message}");
        }
    }

    private static Bitmap? RenderGhostBitmap(ImageSource? thumbnail, string label)
    {
        var bmp = new Bitmap(
            GhostWidth, TotalHeight,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        bool drewThumb = false;
        if (thumbnail is BitmapSource bs)
        {
            using var srcBmp = BitmapSourceToGdi(bs);
            if (srcBmp != null)
            {
                g.DrawImage(srcBmp, 0, 0, GhostWidth, ThumbHeight);
                drewThumb = true;
            }
        }
        if (!drewThumb)
        {
            using var placeholderBrush = new SolidBrush(
                System.Drawing.Color.FromArgb(0xFF, 0x1E, 0x20, 0x28));
            g.FillRectangle(placeholderBrush, 0, 0, GhostWidth, ThumbHeight);

            DrawFolderGlyph(g, GhostWidth / 2.0f, ThumbHeight / 2.0f, 46.0f, 38.0f, System.Drawing.Color.FromArgb(0xFF, 0xAE, 0xB4, 0xBD));
        }

        using var labelBg = new SolidBrush(
            System.Drawing.Color.FromArgb(0xFF, 0x12, 0x14, 0x1A));
        g.FillRectangle(labelBg, 0, ThumbHeight, GhostWidth, LabelHeight);

        using var font = new Font(
            "Segoe UI", 9.5f, System.Drawing.FontStyle.Bold,
            GraphicsUnit.Point);
        using var textBrush = new SolidBrush(
            System.Drawing.Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
        using var sf = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center,
        };
        var textRect = new RectangleF(
            8, ThumbHeight, GhostWidth - 16, LabelHeight);
        g.DrawString(label, font, textBrush, textRect, sf);

        using var borderPen = new System.Drawing.Pen(
            System.Drawing.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        g.DrawRectangle(borderPen, 0, 0, GhostWidth - 1, TotalHeight - 1);

        return bmp;
    }

    private static void DrawFolderGlyph(Graphics g, float cx, float cy, float width, float height, System.Drawing.Color color)
    {
        float sx = width / 20.0f;
        float sy = height / 16.0f;
        float ox = cx - width / 2.0f - 2.0f * sx;
        float oy = cy - height / 2.0f - 4.0f * sy;

        PointF Pt(float x, float y) => new(ox + x * sx, oy + y * sy);

        using var brush = new SolidBrush(color);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();

        path.AddLine(Pt(10, 4), Pt(4, 4));
        path.AddBezier(Pt(4, 4), Pt(2.89f, 4), Pt(2, 4.89f), Pt(2, 6));
        path.AddLine(Pt(2, 6), Pt(2, 18));
        path.AddBezier(Pt(2, 18), Pt(2, 19.11f), Pt(2.89f, 20), Pt(4, 20));
        path.AddLine(Pt(4, 20), Pt(20, 20));
        path.AddBezier(Pt(20, 20), Pt(21.11f, 20), Pt(22, 19.11f), Pt(22, 18));
        path.AddLine(Pt(22, 18), Pt(22, 8));
        path.AddBezier(Pt(22, 8), Pt(22, 6.89f), Pt(21.1f, 6), Pt(20, 6));
        path.AddLine(Pt(20, 6), Pt(12, 6));
        path.AddLine(Pt(12, 6), Pt(10, 4));
        path.CloseFigure();

        g.FillPath(brush, path);
    }

    private static Bitmap? BitmapSourceToGdi(BitmapSource src)
    {
        try
        {
            BitmapSource converted = src.Format == PixelFormats.Pbgra32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);

            var bmp = new Bitmap(
                converted.PixelWidth, converted.PixelHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            var data = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            converted.CopyPixels(
                Int32Rect.Empty, data.Scan0,
                data.Stride * bmp.Height, data.Stride);

            bmp.UnlockBits(data);
            return bmp;
        }
        catch { return null; }
    }
}
