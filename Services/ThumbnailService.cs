using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class ThumbnailService
{
    private const long MaxDecodedFileBytes = 32L * 1024 * 1024;
    private const int DecodeWidth = 128;
    private readonly CacheService _cache;

    public ThumbnailService(CacheService cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<ImageSource> GetThumbnailAsync(
        ShelfItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        // Only app-owned clipboard images are decoded. File and folder thumbnails use local
        // vector fallbacks and never invoke blocking shell thumbnail/icon handlers.
        if (item.Kind == ShelfItemKind.Image &&
            _cache.IsOwnedPath(item.OwnedCachePath) &&
            File.Exists(item.OwnedCachePath))
        {
            try
            {
                ImageSource? image = await Task.Run(
                    () => DecodeOwnedImage(item.OwnedCachePath!, cancellationToken),
                    cancellationToken).ConfigureAwait(true);
                if (image is not null)
                {
                    return image;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                // Corrupt, oversized or unavailable cache images receive a safe vector fallback.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateFallback(item.Kind);
    }

    public ImageSource CreateFallback(ShelfItemKind kind)
    {
        Color accent = kind switch
        {
            ShelfItemKind.Folder => Color.FromRgb(232, 184, 85),
            ShelfItemKind.Link => Color.FromRgb(85, 170, 232),
            ShelfItemKind.Color => Color.FromRgb(208, 112, 202),
            ShelfItemKind.Image => Color.FromRgb(105, 194, 157),
            ShelfItemKind.Text => Color.FromRgb(150, 158, 173),
            _ => Color.FromRgb(130, 143, 168)
        };

        var background = new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B));
        var foreground = new SolidColorBrush(accent);
        background.Freeze();
        foreground.Freeze();

        var group = new DrawingGroup();
        using (DrawingContext context = group.Open())
        {
            context.DrawRoundedRectangle(background, new Pen(foreground, 1.5), new Rect(1, 1, 46, 46), 8, 8);
            DrawSymbol(context, kind, foreground);
        }

        group.Freeze();
        var drawing = new DrawingImage(group);
        drawing.Freeze();
        return drawing;
    }

    private static ImageSource? DecodeOwnedImage(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaxDecodedFileBytes)
        {
            return null;
        }

        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        BitmapSource source = decoder.Frames[0];
        if (source.PixelWidth > DecodeWidth)
        {
            double scale = DecodeWidth / (double)source.PixelWidth;
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            source = transformed;
        }
        else if (source.CanFreeze && !source.IsFrozen)
        {
            source.Freeze();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return source;
    }

    private static void DrawSymbol(DrawingContext context, ShelfItemKind kind, Brush brush)
    {
        var pen = new Pen(brush, 2.5);
        pen.Freeze();

        switch (kind)
        {
            case ShelfItemKind.Folder:
                context.DrawGeometry(null, pen, Geometry.Parse("M10,18 L20,18 L23,22 L38,22 L38,36 L10,36 Z"));
                break;
            case ShelfItemKind.Link:
                context.DrawEllipse(null, pen, new Point(19, 24), 8, 6);
                context.DrawEllipse(null, pen, new Point(29, 24), 8, 6);
                break;
            case ShelfItemKind.Image:
                context.DrawRectangle(null, pen, new Rect(11, 12, 26, 24));
                context.DrawEllipse(brush, null, new Point(29, 19), 2.5, 2.5);
                context.DrawGeometry(null, pen, Geometry.Parse("M13,33 L21,25 L26,30 L31,25 L36,32"));
                break;
            case ShelfItemKind.Color:
                context.DrawEllipse(brush, null, new Point(24, 24), 11, 11);
                break;
            case ShelfItemKind.Text:
                context.DrawLine(pen, new Point(13, 17), new Point(35, 17));
                context.DrawLine(pen, new Point(13, 24), new Point(35, 24));
                context.DrawLine(pen, new Point(13, 31), new Point(29, 31));
                break;
            default:
                context.DrawGeometry(null, pen, Geometry.Parse("M14,10 L29,10 L36,17 L36,38 L14,38 Z M29,10 L29,17 L36,17"));
                break;
        }
    }
}
