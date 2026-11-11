using System;
using System.IO;

namespace VajraShelf.Models;

public enum ShelfItemKind { File, Folder, Text, Link, Color, Image }

public sealed class ShelfItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ShelfItemKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? OwnedCachePath { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public bool IsPinned { get; set; }
    public bool IsAvailable => Kind switch
    {
        ShelfItemKind.File => File.Exists(Payload),
        ShelfItemKind.Folder => Directory.Exists(Payload),
        ShelfItemKind.Image when !string.IsNullOrWhiteSpace(OwnedCachePath) => File.Exists(OwnedCachePath),
        _ => true
    };
    public string KindLabel => Kind.ToString();
    public string Glyph => Kind switch
    {
        ShelfItemKind.File => "",
        ShelfItemKind.Folder => "",
        ShelfItemKind.Link => "",
        ShelfItemKind.Color => "",
        ShelfItemKind.Image => "",
        _ => ""
    };
    public string AgeLabel
    {
        get
        {
            TimeSpan age = DateTime.UtcNow - CreatedUtc;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
            return $"{(int)age.TotalDays}d ago";
        }
    }
    public ShelfItem Clone() => (ShelfItem)MemberwiseClone();
}
