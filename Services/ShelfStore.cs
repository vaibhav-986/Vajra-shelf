using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class ShelfStore
{
    public const int MaxPersistedItems = 100;
    public const int MaxTitleLength = 512;
    public const int MaxPreviewLength = 4096;
    public const int MaxPayloadLength = 1024 * 1024;
    public const int MaxCachePathLength = 32767;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly CacheService _cache;

    public ShelfStore(string? path = null, string? cacheRoot = null)
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.DataFolderName);

        _path = Path.GetFullPath(path ?? Path.Combine(dataDirectory, "pinned-items.json"));
        string inferredDataDirectory = Path.GetDirectoryName(_path) ?? dataDirectory;
        _cache = new CacheService(cacheRoot ?? Path.Combine(inferredDataDirectory, "cache"));
    }

    public IReadOnlyList<ShelfItem> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<ShelfItem>();

            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            List<PersistedShelfItem>? persisted = JsonSerializer.Deserialize<List<PersistedShelfItem>>(stream, JsonOptions);
            if (persisted is null)
                return Array.Empty<ShelfItem>();

            List<ShelfItem> result = new(Math.Min(persisted.Count, MaxPersistedItems));
            HashSet<Guid> ids = new();
            foreach (PersistedShelfItem? candidate in persisted)
            {
                if (result.Count == MaxPersistedItems)
                    break;
                if (candidate is null)
                    continue;

                ShelfItem? validated = Validate(candidate.ToModel(), ids);
                if (validated is not null)
                    result.Add(validated);
            }

            return result;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return Array.Empty<ShelfItem>();
        }
    }

    public void Save(IReadOnlyCollection<ShelfItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<PersistedShelfItem> persisted = new(Math.Min(items.Count, MaxPersistedItems));
        HashSet<Guid> ids = new();
        foreach (ShelfItem source in items)
        {
            if (persisted.Count == MaxPersistedItems)
                break;
            if (source is null || !source.IsPinned)
                continue;

            ShelfItem? validated = Validate(source.Clone(), ids);
            if (validated is not null)
                persisted.Add(PersistedShelfItem.FromModel(validated));
        }

        WriteAtomically(persisted);
    }

    private ShelfItem? Validate(ShelfItem candidate, HashSet<Guid> ids)
    {
        if (!candidate.IsPinned
            || candidate.Id == Guid.Empty
            || !Enum.IsDefined(candidate.Kind)
            || candidate.Title is null
            || candidate.Preview is null
            || candidate.Payload is null
            || candidate.Title.Length > MaxTitleLength
            || candidate.Preview.Length > MaxPreviewLength
            || candidate.Payload.Length > MaxPayloadLength
            || !ids.Add(candidate.Id))
        {
            return null;
        }

        if (candidate.OwnedCachePath is not null)
        {
            if (candidate.Kind != ShelfItemKind.Image
                || candidate.OwnedCachePath.Length == 0
                || candidate.OwnedCachePath.Length > MaxCachePathLength
                || !TryCanonicalCachePath(candidate.OwnedCachePath, out string? canonicalPath))
            {
                ids.Remove(candidate.Id);
                return null;
            }

            candidate.OwnedCachePath = canonicalPath;
        }

        return candidate.Clone();
    }

    private bool TryCanonicalCachePath(string path, out string? canonicalPath)
    {
        canonicalPath = null;
        if (!_cache.IsOwnedPath(path))
            return false;

        try
        {
            canonicalPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception error) when (error is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private void WriteAtomically(List<PersistedShelfItem> items)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The shelf path must have a parent directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, items, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class PersistedShelfItem
    {
        public PersistedShelfItem()
        {
        }

        public Guid Id { get; set; }
        public ShelfItemKind Kind { get; set; }
        public string? Title { get; set; }
        public string? Preview { get; set; }
        public string? Payload { get; set; }
        public string? OwnedCachePath { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public bool IsPinned { get; set; }

        public ShelfItem ToModel() => new()
        {
            Id = Id,
            Kind = Kind,
            Title = Title!,
            Preview = Preview!,
            Payload = Payload!,
            OwnedCachePath = OwnedCachePath,
            CreatedUtc = CreatedUtc,
            ExpiresUtc = ExpiresUtc,
            IsPinned = IsPinned
        };

        public static PersistedShelfItem FromModel(ShelfItem item) => new()
        {
            Id = item.Id,
            Kind = item.Kind,
            Title = item.Title,
            Preview = item.Preview,
            Payload = item.Payload,
            OwnedCachePath = item.OwnedCachePath,
            CreatedUtc = item.CreatedUtc,
            ExpiresUtc = item.ExpiresUtc,
            IsPinned = true
        };
    }
}
