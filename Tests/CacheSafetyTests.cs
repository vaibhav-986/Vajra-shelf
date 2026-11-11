using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VajraShelf.Models;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class CacheSafetyTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "VajraShelf-Cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RejectsExternalAndUnownedFiles(root);
            CleansOnlyUnreferencedOwnedImages(root);
            ClearSessionNeverDeletesExternalSource(root);
            RejectsReparseOwnedFile(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void RejectsExternalAndUnownedFiles(string root)
    {
        string cacheRoot = Path.Combine(root, "cache-a");
        Directory.CreateDirectory(cacheRoot);
        var cache = new CacheService(cacheRoot);

        string external = Path.Combine(root, "vajra-clipboard-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(external, new byte[] { 1, 2, 3 });
        Assert(!cache.DeleteOwned(external), "An external generated-looking path was accepted as owned.");
        Assert(File.Exists(external), "DeleteOwned deleted an external file.");

        string traversed = Path.Combine(cacheRoot, "..", Path.GetFileName(external));
        Assert(!cache.DeleteOwned(traversed), "A traversal path escaped canonical cache containment.");
        Assert(File.Exists(external), "A canonical traversal deleted an external file.");

        string unowned = Path.Combine(cacheRoot, "user-file.png");
        File.WriteAllBytes(unowned, new byte[] { 4, 5, 6 });
        Assert(!cache.DeleteOwned(unowned), "A non-generated file inside the cache was treated as owned.");
        Assert(cache.CleanupUnreferenced(Array.Empty<string>()) == 0,
            "Cleanup removed a file without an app-owned cache name.");
        Assert(File.Exists(unowned), "Cleanup deleted an unowned file inside the cache root.");
    }

    private static void CleansOnlyUnreferencedOwnedImages(string root)
    {
        var cache = new CacheService(Path.Combine(root, "cache-b"));
        string keep = cache.SaveClipboardImage(MakeBitmap());
        string remove = cache.SaveClipboardImage(MakeBitmap());

        int deleted = cache.CleanupUnreferenced(new[] { keep, Path.Combine(root, "outside.png") });
        Assert(deleted == 1, "Cleanup did not report exactly one unreferenced owned image.");
        Assert(File.Exists(keep), "Cleanup deleted a referenced owned image.");
        Assert(!File.Exists(remove), "Cleanup retained an unreferenced owned image.");
    }

    private static void ClearSessionNeverDeletesExternalSource(string root)
    {
        var cache = new CacheService(Path.Combine(root, "cache-c"));
        string owned = cache.SaveClipboardImage(MakeBitmap());
        string external = Path.Combine(root, "external-image.png");
        File.WriteAllBytes(external, new byte[] { 7, 8, 9 });

        var ownedItem = new ShelfItem
        {
            Kind = ShelfItemKind.Image,
            Payload = owned,
            OwnedCachePath = owned
        };
        var maliciousItem = new ShelfItem
        {
            Kind = ShelfItemKind.File,
            Payload = external,
            OwnedCachePath = external
        };

        Assert(cache.ClearSession(new[] { ownedItem, maliciousItem }) == 1,
            "ClearSession did not restrict deletion to one owned image.");
        Assert(!File.Exists(owned), "ClearSession retained an owned image.");
        Assert(File.Exists(external), "ClearSession deleted an external source.");
    }

    private static void RejectsReparseOwnedFile(string root)
    {
        string cacheRoot = Path.Combine(root, "cache-d");
        Directory.CreateDirectory(cacheRoot);
        var cache = new CacheService(cacheRoot);
        string external = Path.Combine(root, "reparse-target.png");
        File.WriteAllBytes(external, new byte[] { 10, 11, 12 });
        string link = Path.Combine(cacheRoot, "vajra-clipboard-" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            File.CreateSymbolicLink(link, external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation is optional on Windows configurations without Developer Mode.
        }

        Assert(!cache.DeleteOwned(link), "A reparse-point cache entry was followed or accepted for deletion.");
        Assert(File.Exists(external), "Rejecting a reparse-point entry damaged its external target.");
    }

    private static BitmapSource MakeBitmap()
    {
        byte[] pixels = { 0x11, 0x22, 0x33, 0xff };
        BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
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
}
