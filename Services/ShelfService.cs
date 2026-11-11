using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class ShelfService
{
    public const int MaxItems = 100;
    private readonly CacheService _cache;

    public ShelfService(CacheService cache, IEnumerable<ShelfItem>? initialItems = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Items = new ObservableCollection<ShelfItem>();
        if (initialItems is not null)
        {
            AddCandidates(initialItems);
        }
    }

    public ObservableCollection<ShelfItem> Items { get; }

    public event EventHandler? PinnedItemsChanged;

    public IReadOnlyList<ShelfItem> Filtered(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Items.ToArray();
        }

        string needle = query.Trim();
        return Items.Where(item =>
                Contains(item.Title, needle) ||
                Contains(item.Preview, needle) ||
                Contains(item.Payload, needle) ||
                Contains(item.KindLabel, needle))
            .ToArray();
    }

    public int AddCandidates(IEnumerable<ShelfItem> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        int added = 0;
        var keys = new HashSet<string>(Items.Select(GetDedupeKey), StringComparer.OrdinalIgnoreCase);

        foreach (ShelfItem source in candidates)
        {
            if (source is null)
            {
                continue;
            }

            string key = GetDedupeKey(source);
            if (!keys.Add(key))
            {
                continue;
            }

            while (Items.Count >= MaxItems)
            {
                ShelfItem? eviction = Items
                    .Where(item => !item.IsPinned)
                    .OrderBy(item => item.CreatedUtc)
                    .FirstOrDefault();
                if (eviction is null)
                {
                    keys.Remove(key);
                    if (!string.IsNullOrWhiteSpace(source.OwnedCachePath))
                    {
                        _cache.DeleteOwned(source.OwnedCachePath);
                    }
                    break;
                }

                keys.Remove(GetDedupeKey(eviction));
                RemoveCore(eviction);
            }

            if (Items.Count >= MaxItems)
            {
                continue;
            }

            Items.Insert(0, source.Clone());
            added++;
        }

        return added;
    }

    public bool TogglePin(Guid id)
    {
        ShelfItem? item = Items.FirstOrDefault(candidate => candidate.Id == id);
        return item is not null && TogglePin(item);
    }

    public bool TogglePin(ShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
        {
            return false;
        }

        item.IsPinned = !item.IsPinned;
        item.ExpiresUtc = null;
        // ShelfItem is intentionally a plain mutable model, so raise a collection replace to refresh WPF bindings.
        Items[Items.IndexOf(item)] = item;
        PinnedItemsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(Guid id)
    {
        ShelfItem? item = Items.FirstOrDefault(candidate => candidate.Id == id);
        return item is not null && Remove(item);
    }

    public bool Remove(ShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
        {
            return false;
        }

        bool wasPinned = item.IsPinned;
        RemoveCore(item);
        if (wasPinned)
        {
            PinnedItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    public int ClearTemporary()
    {
        ShelfItem[] temporary = Items.Where(item => !item.IsPinned).ToArray();
        foreach (ShelfItem item in temporary)
        {
            RemoveCore(item);
        }

        return temporary.Length;
    }

    public int Expire(DateTime now)
    {
        ShelfItem[] expired = Items
            .Where(item => !item.IsPinned && item.ExpiresUtc is DateTime expiration && expiration <= now)
            .ToArray();
        foreach (ShelfItem item in expired)
        {
            RemoveCore(item);
        }

        return expired.Length;
    }

    private void RemoveCore(ShelfItem item)
    {
        if (!Items.Remove(item))
        {
            return;
        }

        // The cache service rejects non-owned names, paths outside its canonical root and reparse points.
        if (!string.IsNullOrWhiteSpace(item.OwnedCachePath))
        {
            _cache.DeleteOwned(item.OwnedCachePath);
        }
    }

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string GetDedupeKey(ShelfItem item)
    {
        string payload = item.Payload ?? string.Empty;
        if (item.Kind is ShelfItemKind.File or ShelfItemKind.Folder or ShelfItemKind.Image)
        {
            string selected = !string.IsNullOrWhiteSpace(item.OwnedCachePath)
                ? item.OwnedCachePath
                : payload;
            try
            {
                payload = Path.GetFullPath(selected);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                payload = selected;
            }
        }
        else if (item.Kind == ShelfItemKind.Link && Uri.TryCreate(payload, UriKind.Absolute, out Uri? uri))
        {
            payload = uri.AbsoluteUri;
        }

        return ((int)item.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + payload;
    }
}
