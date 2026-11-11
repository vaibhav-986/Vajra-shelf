using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VajraShelf.Models;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class ItemPipelineTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "VajraShelf-Pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ParseAndRemoveKeepsExternalSource(root);
            ClassifiesBoundedText(root);
            ClipboardImageIsOwnedAndRemoved(root);
            PinExpiryAndCapacity(root);
            ActionsRequireDangerousConfirmation(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void ParseAndRemoveKeepsExternalSource(string root)
    {
        string source = Path.Combine(root, "external-source.txt");
        File.WriteAllText(source, "external", Encoding.UTF8);
        var data = new FakeDataObject();
        data.SetData(DataFormats.FileDrop, new[] { source }, false);

        DateTime now = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DropParseResult result = new DropParser().Parse(data, now, TimeSpan.FromHours(1));
        Assert(result.Candidates.Count == 1, "A valid file drop should produce one candidate.");
        Assert(result.Candidates[0].Kind == ShelfItemKind.File, "The file candidate kind was incorrect.");
        Assert(result.Candidates[0].OwnedCachePath is null, "External drops must never be marked cache-owned.");

        var cache = new CacheService(Path.Combine(root, "cache-file"));
        var shelf = new ShelfService(cache);
        Assert(shelf.AddCandidates(result.Candidates) == 1, "The file candidate was not added.");
        Assert(shelf.AddCandidates(result.Candidates) == 0, "Duplicate file references must be ignored.");
        Assert(shelf.Remove(shelf.Items[0]), "The file candidate was not removed.");
        Assert(File.Exists(source), "Removing a shelf item deleted its external source.");

        IDataObject drag = new DragOutService().CreateDataObject(result.Candidates[0]);
        Assert(drag.GetDataPresent(DataFormats.FileDrop, false), "File drag-out did not provide FileDrop.");
        Assert(drag.GetDataPresent(DataFormats.UnicodeText, false), "File drag-out did not provide text.");
    }

    private static void ClassifiesBoundedText(string root)
    {
        var parser = new DropParser();
        DateTime now = DateTime.UtcNow;

        Assert(ParseText(parser, "https://example.test/a", now).Kind == ShelfItemKind.Link,
            "An HTTPS URL was not classified as a link.");
        ShelfItem color = ParseText(parser, "#a1b2c3", now);
        Assert(color.Kind == ShelfItemKind.Color && color.Payload == "#A1B2C3",
            "A hexadecimal color was not normalized and classified.");
        Assert(ParseText(parser, "plain\ntext", now).Kind == ShelfItemKind.Text,
            "Plain text was not classified as text.");

        var oversized = new FakeDataObject();
        oversized.SetData(DataFormats.UnicodeText, new string('x', DropParser.MaxTextBytes + 1), false);
        DropParseResult rejected = parser.Parse(oversized, now, TimeSpan.Zero);
        Assert(rejected.Candidates.Count == 0 && rejected.RejectedMessages.Count > 0,
            "Text over 1 MiB should be rejected.");

        var files = new List<string>();
        for (int i = 0; i < DropParser.MaxFileReferences + 1; i++)
        {
            string file = Path.Combine(root, "bounded-" + i + ".txt");
            File.WriteAllText(file, string.Empty);
            files.Add(file);
        }

        var many = new FakeDataObject();
        many.SetData(DataFormats.FileDrop, files.ToArray(), false);
        DropParseResult bounded = parser.Parse(many, now, TimeSpan.Zero);
        Assert(bounded.Candidates.Count == DropParser.MaxCandidates,
            "File candidates were not bounded to 100.");
    }

    private static void ClipboardImageIsOwnedAndRemoved(string root)
    {
        string cacheRoot = Path.Combine(root, "cache-image");
        var cache = new CacheService(cacheRoot);
        var data = new FakeDataObject();
        BitmapSource bitmap = MakeBitmap();
        data.SetData(DataFormats.Bitmap, bitmap, false);
        var clipboard = new FakeClipboardAdapter(data);
        var service = new ClipboardService(cache, clipboard, retryDelay: TimeSpan.Zero, delay: _ => { });

        DropParseResult result = service.ReadCandidates(DateTime.UtcNow, TimeSpan.FromMinutes(60));
        Assert(result.Candidates.Count == 1 && result.Candidates[0].Kind == ShelfItemKind.Image,
            "A clipboard bitmap should create one image candidate.");
        ShelfItem image = result.Candidates[0];
        Assert(cache.IsOwnedPath(image.OwnedCachePath), "Clipboard image path was not cache-owned.");
        Assert(File.Exists(image.OwnedCachePath), "Clipboard image PNG was not saved.");

        var shelf = new ShelfService(cache);
        shelf.AddCandidates(result.Candidates);
        string ownedPath = shelf.Items[0].OwnedCachePath!;
        shelf.Remove(shelf.Items[0]);
        Assert(!File.Exists(ownedPath), "Removing an owned clipboard image did not remove its cache PNG.");

        var textItem = new ShelfItem { Kind = ShelfItemKind.Text, Payload = "copy me" };
        Assert(service.Copy(textItem), "Explicit clipboard copy failed.");
        Assert(clipboard.LastSet?.GetData(DataFormats.UnicodeText, false) as string == "copy me",
            "Clipboard copy did not provide Unicode text.");

        var retryData = new FakeDataObject();
        retryData.SetData(DataFormats.UnicodeText, "retried", false);
        var transientClipboard = new FakeClipboardAdapter(retryData, failuresBeforeSuccess: 2);
        var retrying = new ClipboardService(
            cache,
            transientClipboard,
            retryCount: 3,
            retryDelay: TimeSpan.Zero,
            delay: _ => { });
        Assert(retrying.ReadCandidates(DateTime.UtcNow, TimeSpan.Zero).Candidates.Count == 1,
            "Clipboard contention was not retried successfully.");
        Assert(transientClipboard.GetCount == 3, "Clipboard retry count was not bounded as configured.");
    }

    private static void PinExpiryAndCapacity(string root)
    {
        var cache = new CacheService(Path.Combine(root, "cache-shelf"));
        var shelf = new ShelfService(cache);
        int notifications = 0;
        shelf.PinnedItemsChanged += (_, _) => notifications++;
        DateTime now = DateTime.UtcNow;

        var expiring = new ShelfItem
        {
            Kind = ShelfItemKind.Text,
            Payload = "expiring",
            Title = "expiring",
            CreatedUtc = now.AddMinutes(-2),
            ExpiresUtc = now.AddMinutes(-1)
        };
        shelf.AddCandidates(new[] { expiring });
        ShelfItem stored = shelf.Items[0];
        Assert(shelf.TogglePin(stored), "Pinning failed.");
        Assert(shelf.Expire(now) == 0, "Pinned items must not expire.");
        Assert(shelf.TogglePin(stored), "Unpinning failed.");
        Assert(notifications == 2, "Pin state changes did not request persistence.");

        shelf.Remove(stored);
        for (int i = 0; i < ShelfService.MaxItems + 5; i++)
        {
            shelf.AddCandidates(new[]
            {
                new ShelfItem
                {
                    Kind = ShelfItemKind.Text,
                    Payload = "item-" + i,
                    Title = "item-" + i,
                    CreatedUtc = now.AddSeconds(i)
                }
            });
        }
        Assert(shelf.Items.Count == ShelfService.MaxItems, "Shelf capacity exceeded 100 items.");
    }

    private static void ActionsRequireDangerousConfirmation(string root)
    {
        string script = Path.Combine(root, "do-not-run.cmd");
        File.WriteAllText(script, "exit /b 0", Encoding.ASCII);
        var launcher = new FakeProcessLauncher();
        var actions = new FileActionService(launcher);
        var item = new ShelfItem { Kind = ShelfItemKind.File, Payload = script, Title = "script" };

        Assert(!actions.Open(item), "Dangerous files must be denied without confirmation.");
        Assert(launcher.StartCount == 0, "A denied dangerous file reached the process launcher.");
        Assert(actions.Open(item, _ => true), "A confirmed dangerous file should reach the launcher.");
        Assert(launcher.StartCount == 1, "Confirmed open did not invoke exactly one launch.");
        Assert(File.Exists(script), "Opening logic modified the external file.");
    }

    private static ShelfItem ParseText(DropParser parser, string value, DateTime now)
    {
        var data = new FakeDataObject();
        data.SetData(DataFormats.UnicodeText, value, false);
        DropParseResult result = parser.Parse(data, now, TimeSpan.Zero);
        Assert(result.Candidates.Count == 1, "Expected one parsed text candidate.");
        return result.Candidates[0];
    }

    private static BitmapSource MakeBitmap()
    {
        byte[] pixels = { 0x20, 0x40, 0x80, 0xff, 0x10, 0x30, 0x70, 0xff,
                          0x20, 0x40, 0x80, 0xff, 0x10, 0x30, 0x70, 0xff };
        BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeClipboardAdapter : IClipboardAdapter
    {
        private readonly IDataObject? _data;
        private int _failuresRemaining;

        public FakeClipboardAdapter(IDataObject? data, int failuresBeforeSuccess = 0)
        {
            _data = data;
            _failuresRemaining = failuresBeforeSuccess;
        }

        public IDataObject? LastSet { get; private set; }
        public int GetCount { get; private set; }

        public IDataObject? GetDataObject()
        {
            GetCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new ExternalException("Clipboard is busy.");
            }

            return _data;
        }

        public void SetDataObject(IDataObject data, bool copy) => LastSet = data;
    }

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public int StartCount { get; private set; }
        public ProcessStartInfo? LastStartInfo { get; private set; }

        public bool Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            LastStartInfo = startInfo;
            return true;
        }
    }

    private sealed class FakeDataObject : IDataObject
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        public object GetData(string format, bool autoConvert) =>
            _values.TryGetValue(format, out object? value) ? value : null!;

        public object GetData(string format) => GetData(format, true);

        public object GetData(Type format) => GetData(format.FullName ?? format.Name, true);

        public bool GetDataPresent(string format, bool autoConvert) => _values.ContainsKey(format);

        public bool GetDataPresent(string format) => GetDataPresent(format, true);

        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, true);

        public string[] GetFormats(bool autoConvert) => _values.Keys.ToArray();

        public string[] GetFormats() => GetFormats(true);

        public void SetData(object data) => SetData(data.GetType(), data);

        public void SetData(string format, object data) => SetData(format, data, true);

        public void SetData(string format, object data, bool autoConvert) => _values[format] = data;

        public void SetData(Type format, object data) => _values[format.FullName ?? format.Name] = data;
    }
}
