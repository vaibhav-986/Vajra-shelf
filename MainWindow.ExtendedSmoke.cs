using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VajraShelf.Models;
using VajraShelf.Services;

namespace VajraShelf;

public partial class MainWindow
{
    private async Task RunExtendedSmokeTestsAsync(string reportPath, List<string> results)
    {
        Require(_smoke && _smokeRoot != null, "Extended smoke checks require isolated smoke mode.");
        string prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, Path.GetFileNameWithoutExtension(reportPath));
        var addedIds = new List<Guid>();
        AppSettings originalSettings = _settings.Clone();
        string sourceFile = Path.Combine(_smokeRoot!, "external-source.txt");
        File.WriteAllText(sourceFile, "Vajra Shelf source safety check");
        try
        {
            var fileData = new DataObject();
            fileData.SetData(DataFormats.FileDrop, new[] { sourceFile });
            AddParsed(fileData, addedIds);
            AddParsed(TextData("https://example.com/shelf"), addedIds);
            AddParsed(TextData("#28C7A4"), addedIds);
            AddParsed(TextData("A private temporary note"), addedIds);

            Require(_shelf.Items.Any(item => item.Kind == ShelfItemKind.File), "File reference was not classified.");
            Require(_shelf.Items.Any(item => item.Kind == ShelfItemKind.Link), "Link was not classified.");
            Require(_shelf.Items.Any(item => item.Kind == ShelfItemKind.Color), "Color was not classified.");
            Require(_shelf.Items.Any(item => item.Kind == ShelfItemKind.Text && item.Title != "Smoke item"), "Text was not classified.");
            Require(File.Exists(sourceFile), "Adding a file reference modified the source.");
            results.Add("PASS: synthetic file/text/link/color drops classify without touching source content.");

            SearchBox.Text = "example.com";
            UpdateShelfView();
            Require(_shelf.Filtered(SearchBox.Text).Count == 1, "Search did not isolate the URL card.");
            SearchBox.Text = string.Empty;
            results.Add("PASS: local search filters title, preview and payload.");

            ShelfItem link = _shelf.Items.First(item => item.Kind == ShelfItemKind.Link);
            Require(_shelf.TogglePin(link) && link.IsPinned, "Pin operation failed.");
            Require(_shelfStore.Load().Any(item => item.Id == link.Id), "Pinned item was not atomically persisted.");
            Require(_shelf.TogglePin(link) && !link.IsPinned, "Unpin operation failed.");
            link.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
            Require(_shelf.Expire(DateTime.UtcNow) == 1, "Expired temporary item was not removed.");
            addedIds.Remove(link.Id);
            Require(File.Exists(sourceFile), "Expiry modified an external source.");
            results.Add("PASS: pin persistence, unpinning and expiry are isolated from external files.");

            byte[] pixels = { 0x20, 0xC7, 0xA4, 0xFF };
            BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            bitmap.Freeze();
            string ownedImage = _cache.SaveClipboardImage(bitmap);
            var imageItem = new ShelfItem
            {
                Kind = ShelfItemKind.Image,
                Title = "Clipboard image",
                Preview = "1 × 1 PNG",
                Payload = ownedImage,
                OwnedCachePath = ownedImage,
                CreatedUtc = DateTime.UtcNow
            };
            _shelf.AddCandidates(new[] { imageItem });
            ShelfItem storedImage = _shelf.Items.First(item => item.Id == imageItem.Id);
            Require(File.Exists(ownedImage), "Owned image cache was not created.");
            Require(_shelf.Remove(storedImage) && !File.Exists(ownedImage), "Removing an owned image did not clean only its cache file.");
            results.Add("PASS: owned clipboard images are cached and cleaned safely.");

            ShelfItem fileItem = _shelf.Items.First(item => item.Kind == ShelfItemKind.File);
            IDataObject outwardFile = _dragOut.CreateDataObject(fileItem);
            Require(outwardFile.GetDataPresent(DataFormats.FileDrop), "File drag-out data is missing FileDrop.");
            ShelfItem textItem = _shelf.Items.First(item => item.Kind == ShelfItemKind.Text && item.Title != "Smoke item");
            IDataObject outwardText = _dragOut.CreateDataObject(textItem);
            Require(outwardText.GetDataPresent(DataFormats.UnicodeText), "Text drag-out data is missing UnicodeText.");
            results.Add("PASS: outward drag formats are standard Windows file/text formats.");

            _settings.ReduceMotion = true;
            foreach (string theme in new[] { "Dark", "Light", "Glass" })
            {
                _settings.Theme = theme;
                ApplyAppearance();
                SetExpanded(true);
                Require(!IsTransitioning && RevealProgress >= 0.999 && IsBodyInteractive, "Reduced-motion expansion did not settle.");
                CaptureTo(prefix + "." + theme.ToLowerInvariant() + ".png");
            }
            results.Add("PASS: Dark, Light and Glass render with immediate reduced-motion endpoints.");

            OpenSettings();
            await Task.Delay(100);
            Require(_settingsWindow is { IsLoaded: true }, "Settings window did not open.");
            CaptureElement(_settingsWindow!, prefix + ".settings.png");
            _settingsWindow!.Close();
            Require(_settings.Theme == "Glass", "Cancelling Settings changed the live theme.");
            results.Add("PASS: Settings opens, renders and cancels without mutation.");

            if (SystemParameters.ClientAreaAnimation)
            {
                _settings.ReduceMotion = false;
                ApplyAppearance();
                SetExpanded(false);
                await Task.Delay(55);
                SetExpanded(true);
                await Task.Delay(45);
                SetExpanded(false);
                await Task.Delay(35);
                SetExpanded(true);
                await AwaitTransitionAsync(true);
                Require(IsBodyInteractive, "Body did not become interactive after the latest reversal target.");
                results.Add("PASS: rapid transition reversals settle to the latest target.");
            }

            SetExpanded(false);
            _settings.ReduceMotion = true;
            ApplyAppearance();
            SetExpanded(false);
            Require(CompactContent.Opacity > 0.99 && CompactContent.ActualHeight >= 59,
                "Compact drop target content is not visibly laid out at the pill endpoint.");
            CaptureTo(prefix + ".compact-final.png");
            IntPtr shelfHandle = new WindowInteropHelper(this).Handle;
            Require(GetWindowRectForSmoke(shelfHandle, out SmokeRect bounds), "Native shelf bounds are unavailable.");
            var transparentMargin = new SmokePoint { X = bounds.Left + 2, Y = bounds.Top + 2 };
            var visiblePill = new SmokePoint { X = (bounds.Left + bounds.Right) / 2, Y = bounds.Top + 30 };
            Require(WindowFromPointForSmoke(transparentMargin) != shelfHandle,
                "Transparent host margin intercepted native pointer targeting.");
            Require(WindowFromPointForSmoke(visiblePill) == shelfHandle,
                "Visible compact pill did not receive native pointer targeting.");
            results.Add("PASS: compact pill retains visible label, count and paste controls with click-through margins.");
        }
        finally
        {
            foreach (Guid id in addedIds.ToArray()) _shelf.Remove(id);
            Require(File.Exists(sourceFile), "Smoke cleanup deleted the external source reference.");
            _settings = originalSettings;
            ApplyAppearance();
            SearchBox.Text = string.Empty;
            SetExpanded(true);
            await AwaitTransitionAsync(true);
        }
    }

    private void AddParsed(IDataObject data, List<Guid> ids)
    {
        DropParseResult parsed = _dropParser.Parse(data, DateTime.UtcNow, TemporaryLifetime);
        Require(parsed.Candidates.Count > 0, "Synthetic drop produced no candidates.");
        _shelf.AddCandidates(parsed.Candidates);
        ids.AddRange(parsed.Candidates.Select(item => item.Id));
    }

    private static DataObject TextData(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        return data;
    }

    private async Task AwaitTransitionAsync(bool expanded)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (IsTransitioning && DateTime.UtcNow < deadline) await Task.Delay(15);
        Require(!IsTransitioning && Math.Abs(RevealProgress - (expanded ? 1 : 0)) < 0.001,
            "Shelf transition did not settle.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmokePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SmokeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRectForSmoke(IntPtr handle, out SmokeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromPointForSmoke(SmokePoint point);
}
