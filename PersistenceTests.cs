using System;
using System.IO;
using VajraShelf.Models;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class PersistenceTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "VajraShelf-PersistenceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsAreValidatedAndWrittenAtomically(root);
            LoadsNeverRepairOrCreateSettings(root);
            OnlyValidPinnedClonesArePersisted(root);
            MalformedShelfDataIsSafeAndUnchanged(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SettingsAreValidatedAndWrittenAtomically(string root)
    {
        string path = Path.Combine(root, "settings.json");
        SettingsStore store = new(path);
        AppSettings source = new()
        {
            Theme = " glass ",
            Accent = "#abcdef",
            Scale = 4,
            TopOffset = -9,
            MonitorIndex = 99,
            TemporaryLifetimeMinutes = 12
        };

        store.Save(source);
        Assert(File.Exists(path), "The first settings save did not move the sibling temporary file into place.");
        Assert(source.Theme == " glass " && source.Scale == 4, "Settings validation mutated its caller.");

        AppSettings loaded = store.Load();
        Assert(loaded.Theme == "Glass", "A supported theme was not normalized tolerantly.");
        Assert(loaded.Accent == "#ABCDEF", "A valid accent was not retained.");
        Assert(loaded.Scale == 1.5, "Scale was not clamped to 1.5.");
        Assert(loaded.TopOffset == 0, "Top offset was not clamped to zero.");
        Assert(loaded.MonitorIndex == 32, "Monitor index was not clamped to 32.");
        Assert(loaded.TemporaryLifetimeMinutes == 1440, "An unsupported lifetime did not fall back to 1440.");

        source.Theme = "LIGHT";
        source.Accent = "not-a-color";
        source.Scale = double.NaN;
        source.TopOffset = double.PositiveInfinity;
        source.MonitorIndex = -1;
        source.TemporaryLifetimeMinutes = 60;
        store.Save(source); // Existing destination must take the File.Replace path.

        loaded = store.Load();
        Assert(loaded.Theme == "Light", "Light theme matching was not case-insensitive.");
        Assert(loaded.Accent == "#8FE3CF", "An invalid accent did not use the safe default.");
        Assert(loaded.Scale == 1 && loaded.TopOffset == 82, "Non-finite layout values were not defaulted.");
        Assert(loaded.MonitorIndex == 0, "A negative monitor index was not clamped.");
        Assert(loaded.TemporaryLifetimeMinutes == 60, "The 60-minute lifetime was not retained.");
        Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "An atomic-save sibling temporary file was left behind.");
    }

    private static void LoadsNeverRepairOrCreateSettings(string root)
    {
        string missingPath = Path.Combine(root, "missing", "settings.json");
        SettingsStore missingStore = new(missingPath);
        AppSettings defaults = missingStore.Load();
        Assert(defaults.Theme == "Dark", "A missing settings file did not return defaults.");
        Assert(!File.Exists(missingPath) && !Directory.Exists(Path.GetDirectoryName(missingPath)!),
            "Loading missing settings wrote to disk.");

        string malformedPath = Path.Combine(root, "malformed-settings.json");
        const string malformed = "{ definitely not json";
        File.WriteAllText(malformedPath, malformed);
        AppSettings recovered = new SettingsStore(malformedPath).Load();
        Assert(recovered.Theme == "Dark", "Malformed settings did not return defaults.");
        Assert(File.ReadAllText(malformedPath) == malformed, "Loading malformed settings repaired or replaced the file.");
    }

    private static void OnlyValidPinnedClonesArePersisted(string root)
    {
        string path = Path.Combine(root, "pinned-items.json");
        string cacheRoot = Path.Combine(root, "cache");
        string externalPath = Path.Combine(root, "external-source.txt");
        File.WriteAllText(externalPath, "must survive");
        ShelfStore store = new(path, cacheRoot);

        string ownedName = "vajra-clipboard-" + Guid.NewGuid().ToString("N") + ".png";
        string nonCanonicalCachePath = Path.Combine(cacheRoot, "nested", "..", ownedName);
        ShelfItem valid = new()
        {
            Id = Guid.NewGuid(),
            Kind = ShelfItemKind.Image,
            Title = "Pinned image",
            Preview = "preview",
            Payload = nonCanonicalCachePath,
            OwnedCachePath = nonCanonicalCachePath,
            CreatedUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            IsPinned = true
        };
        ShelfItem sessionOnly = new()
        {
            Id = Guid.NewGuid(),
            Kind = ShelfItemKind.Text,
            Title = "Session only",
            Payload = "not persisted",
            IsPinned = false
        };
        ShelfItem invalidId = new()
        {
            Id = Guid.Empty,
            Kind = ShelfItemKind.Text,
            Title = "Invalid id",
            Payload = "x",
            IsPinned = true
        };
        ShelfItem invalidKind = new()
        {
            Id = Guid.NewGuid(),
            Kind = (ShelfItemKind)999,
            Title = "Invalid kind",
            Payload = "x",
            IsPinned = true
        };
        ShelfItem oversized = new()
        {
            Id = Guid.NewGuid(),
            Kind = ShelfItemKind.Text,
            Title = new string('x', ShelfStore.MaxTitleLength + 1),
            Payload = "x",
            IsPinned = true
        };
        ShelfItem externalOwnedPath = new()
        {
            Id = Guid.NewGuid(),
            Kind = ShelfItemKind.Image,
            Title = "Unsafe cache path",
            Payload = externalPath,
            OwnedCachePath = externalPath,
            IsPinned = true
        };

        store.Save(new[] { valid, sessionOnly, invalidId, invalidKind, oversized, externalOwnedPath });
        Assert(valid.OwnedCachePath == nonCanonicalCachePath, "Shelf validation mutated the caller's cache path.");
        Assert(File.Exists(externalPath), "Shelf persistence deleted an external source file.");

        valid.Title = "mutated after save";
        var loaded = store.Load();
        Assert(loaded.Count == 1, "Shelf persistence did not filter unpinned or invalid items.");
        Assert(loaded[0].Id == valid.Id && loaded[0].Title == "Pinned image", "The pinned clone did not round-trip.");
        Assert(loaded[0].IsPinned, "A loaded persisted item was not pinned.");
        Assert(loaded[0].OwnedCachePath == Path.GetFullPath(nonCanonicalCachePath),
            "A cache-owned path was not canonicalized on the cloned item.");
        Assert(File.Exists(externalPath), "Shelf loading deleted an external source file.");
    }

    private static void MalformedShelfDataIsSafeAndUnchanged(string root)
    {
        string path = Path.Combine(root, "malformed-pinned-items.json");
        const string malformed = "[{\"Id\":";
        File.WriteAllText(path, malformed);
        ShelfStore store = new(path, Path.Combine(root, "cache"));
        Assert(store.Load().Count == 0, "Malformed shelf data did not return an empty collection.");
        Assert(File.ReadAllText(path) == malformed, "Loading malformed shelf data repaired or replaced the file.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
