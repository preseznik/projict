using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProJict.App.Services;

/// <summary>Files or an image (e.g. a screenshot) on the clipboard, to attach to an item.</summary>
public static class ClipboardAttachments
{
    /// <summary>True when the clipboard holds files or an image but no text; text still pastes into the item as usual.</summary>
    public static bool HasAttachment()
    {
        try
        {
            return !Clipboard.ContainsText() &&
                   (Clipboard.ContainsFileDropList() || Clipboard.ContainsData("PNG") || Clipboard.ContainsImage());
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public static (List<string> Files, byte[]? Png) Read()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) return ([.. Clipboard.GetFileDropList().Cast<string>()], null);
            // The PNG copy keeps transparency; WPF reads the plain bitmap copy with a broken alpha channel, so that one is saved opaque.
            if (Clipboard.GetData("PNG") is MemoryStream png) return ([], png.ToArray());
            if (Clipboard.GetImage() is { } image) return ([], EncodePng(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0)));
        }
        catch (ExternalException) { }
        return ([], null);
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
