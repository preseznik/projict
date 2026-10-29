using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using ProJict.Core.Model;

namespace ProJict.App.ViewModels;

/// <summary>A file attached to an item, shown under its text: a thumbnail for images, otherwise a chip with the file name.</summary>
public sealed partial class AttachmentViewModel
{
    // Rows are rebuilt on every edit; stored attachments never change, so each thumbnail is decoded once.
    private static readonly Dictionary<Guid, ImageSource?> Thumbnails = [];
    private const int ThumbnailPixels = 480;

    public AttachmentViewModel(ItemRowViewModel row, Attachment model)
    {
        Row = row;
        Model = model;
        Path = row.Owner.AttachmentPath(model);
    }

    public ItemRowViewModel Row { get; }
    public Attachment Model { get; }
    public string Path { get; }
    public string FileName => Model.FileName;
    public ImageSource? Thumbnail => Model.IsImage ? LoadThumbnail() : null;
    public bool ShowsThumbnail => Thumbnail != null;
    public string SizeText => FormatSize(Model.Size);
    public string ToolTip => $"{FileName} · {SizeText}\nClick to open · right-click for more";

    [RelayCommand] private void Open() => Row.Owner.OpenAttachment(this);
    [RelayCommand] private void ShowInFolder() => Row.Owner.ShowAttachmentInFolder(this);
    [RelayCommand] private void Remove() => Row.Owner.RemoveAttachment(Row, Model);

    [RelayCommand]
    private void CopyPath()
    {
        try { Clipboard.SetText(Path); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private ImageSource? LoadThumbnail()
    {
        if (Thumbnails.TryGetValue(Model.Id, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var stream = File.OpenRead(Path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            var scale = Math.Min(1.0, Math.Min((double)ThumbnailPixels / frame.PixelWidth, ThumbnailPixels / 3.0 / frame.PixelHeight));
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (scale < 1) bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception)
        {
            // Missing file or a format Windows can't decode: the file chip is shown instead.
        }
        Thumbnails[Model.Id] = image;
        return image;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", bytes / 1024.0),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
    };
}
