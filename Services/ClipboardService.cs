using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using VajraShelf.Models;

namespace VajraShelf.Services;

public interface IClipboardAdapter
{
    IDataObject? GetDataObject();
    void SetDataObject(IDataObject data, bool copy);
}

public sealed class WpfClipboardAdapter : IClipboardAdapter
{
    public IDataObject? GetDataObject()
    {
        EnsureSta();
        return Clipboard.GetDataObject();
    }

    public void SetDataObject(IDataObject data, bool copy)
    {
        ArgumentNullException.ThrowIfNull(data);
        EnsureSta();
        Clipboard.SetDataObject(data, copy);
    }

    private static void EnsureSta()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("WPF clipboard access requires an STA thread.");
        }
    }
}

public sealed class ClipboardService
{
    private readonly IClipboardAdapter _clipboard;
    private readonly CacheService _cache;
    private readonly DropParser _parser;
    private readonly int _retryCount;
    private readonly TimeSpan _retryDelay;
    private readonly Action<TimeSpan> _delay;

    public ClipboardService(
        CacheService cache,
        IClipboardAdapter? clipboard = null,
        DropParser? parser = null,
        int retryCount = 3,
        TimeSpan? retryDelay = null,
        Action<TimeSpan>? delay = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _clipboard = clipboard ?? new WpfClipboardAdapter();
        _parser = parser ?? new DropParser();
        _retryCount = Math.Clamp(retryCount, 1, 10);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(25);
        _delay = delay ?? Thread.Sleep;
    }

    /// <summary>Reads the clipboard once when explicitly called; this service never monitors clipboard changes.</summary>
    public DropParseResult ReadCandidates(DateTime now, TimeSpan lifetime)
    {
        IDataObject? data = Retry(() => _clipboard.GetDataObject());
        return ParseClipboardData(data, now, lifetime);
    }

    /// <summary>Reads the clipboard once when explicitly called and retries only transient clipboard contention.</summary>
    public async Task<DropParseResult> ReadCandidatesAsync(
        DateTime now,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        IDataObject? data = null;
        ExternalException? lastError = null;
        for (int attempt = 0; attempt < _retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                data = _clipboard.GetDataObject();
                lastError = null;
                break;
            }
            catch (ExternalException ex)
            {
                lastError = ex;
                if (attempt + 1 < _retryCount)
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(true);
                }
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        return ParseClipboardData(data, now, lifetime);
    }

    public bool Copy(ShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        IDataObject data = CreateCopyData(item);
        Retry(() =>
        {
            _clipboard.SetDataObject(data, true);
            return true;
        });
        return true;
    }

    private DropParseResult ParseClipboardData(IDataObject? data, DateTime now, TimeSpan lifetime)
    {
        if (data is null)
        {
            return new DropParseResult(rejectedMessages: new[] { "The clipboard is empty." });
        }

        var candidates = new List<ShelfItem>(DropParser.MaxCandidates);
        var rejected = new List<string>();

        BitmapSource? image = TryGetBitmap(data, rejected);
        if (image is not null)
        {
            try
            {
                string path = _cache.SaveClipboardImage(image);
                candidates.Add(new ShelfItem
                {
                    Kind = ShelfItemKind.Image,
                    Title = "Clipboard image",
                    Preview = $"{image.PixelWidth} × {image.PixelHeight} PNG",
                    Payload = path,
                    OwnedCachePath = path,
                    CreatedUtc = now,
                    ExpiresUtc = GetExpiration(now, lifetime)
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                rejected.Add("The clipboard image could not be saved to the private cache.");
            }
        }

        DropParseResult parsed = _parser.Parse(data, now, lifetime);
        int remaining = DropParser.MaxCandidates - candidates.Count;
        candidates.AddRange(parsed.Candidates.Take(remaining));
        rejected.AddRange(parsed.RejectedMessages);
        if (parsed.Candidates.Count > remaining)
        {
            rejected.Add($"Only the first {DropParser.MaxCandidates} clipboard items were accepted.");
        }

        return new DropParseResult(candidates, rejected);
    }

    private static BitmapSource? TryGetBitmap(IDataObject data, List<string> rejected)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Bitmap, false))
            {
                return null;
            }

            if (data.GetData(DataFormats.Bitmap, false) is not BitmapSource source)
            {
                rejected.Add("The clipboard bitmap was not in a supported WPF format.");
                return null;
            }

            if (source.CanFreeze && !source.IsFrozen)
            {
                source = source.Clone();
                source.Freeze();
            }

            return source;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            rejected.Add("The clipboard bitmap could not be read.");
            return null;
        }
    }

    private IDataObject CreateCopyData(ShelfItem item)
    {
        var data = (DataObject)new DragOutService().CreateDataObject(item);
        if (item.Kind == ShelfItemKind.Image &&
            _cache.IsOwnedPath(item.OwnedCachePath) &&
            File.Exists(item.OwnedCachePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(item.OwnedCachePath!, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                data.SetData(DataFormats.Bitmap, bitmap, false);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
            {
                // File-drop and text formats remain available when bitmap decoding fails.
            }
        }

        return data;
    }

    private T Retry<T>(Func<T> operation)
    {
        ExternalException? lastError = null;
        for (int attempt = 0; attempt < _retryCount; attempt++)
        {
            try
            {
                return operation();
            }
            catch (ExternalException ex)
            {
                lastError = ex;
                if (attempt + 1 < _retryCount)
                {
                    _delay(_retryDelay);
                }
            }
        }

        throw lastError!;
    }

    private static DateTime? GetExpiration(DateTime now, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            return now + lifetime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MaxValue;
        }
    }
}
