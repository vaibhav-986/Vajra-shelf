using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class CacheService
{
    private const string OwnedPrefix = "vajra-clipboard-";
    private const string OwnedExtension = ".png";
    private readonly string _rootWithSeparator;

    public CacheService(string? rootPath = null)
    {
        string selectedRoot = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VajraShelf",
            "cache");

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedRoot));
        _rootWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;
    }

    public string RootPath { get; }

    public string SaveClipboardImage(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        EnsureUsableRoot();

        string path;
        do
        {
            path = Path.Combine(RootPath, OwnedPrefix + Guid.NewGuid().ToString("N") + OwnedExtension);
        }
        while (File.Exists(path));

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough);
            encoder.Save(stream);
            stream.Flush(true);
            return path;
        }
        catch
        {
            // A partially encoded file still has an app-generated name inside the verified root.
            DeleteOwned(path);
            throw;
        }
    }

    public bool DeleteOwned(string? path)
    {
        if (!TryGetOwnedCanonicalPath(path, out string? canonical))
        {
            return false;
        }

        try
        {
            if (!File.Exists(canonical))
            {
                return false;
            }

            if (IsReparsePoint(canonical))
            {
                return false;
            }

            File.Delete(canonical);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public int CleanupUnreferenced(IEnumerable<string> referencedPaths)
    {
        ArgumentNullException.ThrowIfNull(referencedPaths);
        if (!TryEnsureUsableRoot())
        {
            return 0;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in referencedPaths)
        {
            if (TryGetOwnedCanonicalPath(path, out string? canonical))
            {
                referenced.Add(canonical);
            }
        }

        int deleted = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(RootPath, OwnedPrefix + "*" + OwnedExtension, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string file in files)
        {
            if (!TryGetOwnedCanonicalPath(file, out string? canonical) || referenced.Contains(canonical))
            {
                continue;
            }

            if (DeleteOwned(canonical))
            {
                deleted++;
            }
        }

        return deleted;
    }

    public int ClearSession(IEnumerable<ShelfItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int deleted = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ShelfItem item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.OwnedCachePath))
            {
                continue;
            }

            if (!TryGetOwnedCanonicalPath(item.OwnedCachePath, out string? canonical) || !seen.Add(canonical))
            {
                continue;
            }

            if (DeleteOwned(canonical))
            {
                deleted++;
            }
        }

        return deleted;
    }

    public bool IsOwnedPath(string? path) => TryGetOwnedCanonicalPath(path, out _);

    private bool TryGetOwnedCanonicalPath(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            canonical = Path.GetFullPath(path);
            if (!canonical.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Cache images are direct children. This also prevents traversal through child junctions.
            if (!string.Equals(Path.GetDirectoryName(canonical), RootPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string name = Path.GetFileName(canonical);
            if (!HasOwnedName(name))
            {
                return false;
            }

            if (Directory.Exists(RootPath) && ContainsReparsePointInRootChain(RootPath))
            {
                return false;
            }

            return !File.Exists(canonical) || !IsReparsePoint(canonical);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    private static bool HasOwnedName(string name)
    {
        if (!name.StartsWith(OwnedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(OwnedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string id = name.Substring(OwnedPrefix.Length, name.Length - OwnedPrefix.Length - OwnedExtension.Length);
        return id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    }

    private void EnsureUsableRoot()
    {
        Directory.CreateDirectory(RootPath);
        if (ContainsReparsePointInRootChain(RootPath))
        {
            throw new IOException("The cache root cannot traverse a reparse point.");
        }
    }

    private bool TryEnsureUsableRoot()
    {
        try
        {
            EnsureUsableRoot();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePointInRootChain(string path)
    {
        DirectoryInfo? directory = new(path);
        while (directory is not null)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
