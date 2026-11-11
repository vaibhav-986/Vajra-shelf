using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class DropParseResult
{
    public DropParseResult(IEnumerable<ShelfItem>? candidates = null, IEnumerable<string>? rejectedMessages = null)
    {
        Candidates = (candidates ?? Array.Empty<ShelfItem>()).ToArray();
        RejectedMessages = (rejectedMessages ?? Array.Empty<string>()).ToArray();
    }

    public IReadOnlyList<ShelfItem> Candidates { get; }
    public IReadOnlyList<string> RejectedMessages { get; }
}

public sealed class DropParser
{
    public const int MaxCandidates = 100;
    public const int MaxFileReferences = 100;
    public const int MaxTextBytes = 1024 * 1024;
    private const int PreviewLength = 240;

    public DropParseResult Parse(IDataObject data, DateTime now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(data);

        var candidates = new List<ShelfItem>(MaxCandidates);
        var rejected = new List<string>();
        DateTime? expiresUtc = GetExpiration(now, lifetime);

        ParseFiles(data, now, expiresUtc, candidates, rejected);
        if (candidates.Count < MaxCandidates)
        {
            ParseText(data, now, expiresUtc, candidates, rejected);
        }

        return new DropParseResult(candidates, rejected);
    }

    private static void ParseFiles(
        IDataObject data,
        DateTime now,
        DateTime? expiresUtc,
        List<ShelfItem> candidates,
        List<string> rejected)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, false))
            {
                return;
            }

            if (data.GetData(DataFormats.FileDrop, false) is not string[] paths)
            {
                rejected.Add("The dropped file list was not in a supported format.");
                return;
            }

            if (paths.Length > MaxFileReferences)
            {
                rejected.Add($"Only the first {MaxFileReferences} file references were accepted.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string suppliedPath in paths.Take(MaxFileReferences))
            {
                if (candidates.Count >= MaxCandidates)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(suppliedPath))
                {
                    AddRejection(rejected, "An empty file reference was ignored.");
                    continue;
                }

                string path;
                try
                {
                    path = Path.GetFullPath(suppliedPath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    AddRejection(rejected, "An invalid file reference was ignored.");
                    continue;
                }

                if (!seen.Add(path))
                {
                    continue;
                }

                bool isDirectory = Directory.Exists(path);
                bool isFile = File.Exists(path);
                if (!isDirectory && !isFile)
                {
                    AddRejection(rejected, $"The referenced item is unavailable: {SafeName(path)}");
                    continue;
                }

                candidates.Add(new ShelfItem
                {
                    Kind = isDirectory ? ShelfItemKind.Folder : ShelfItemKind.File,
                    Title = SafeName(path),
                    Preview = path,
                    Payload = path,
                    CreatedUtc = now,
                    ExpiresUtc = expiresUtc
                });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            AddRejection(rejected, "The dropped file references could not be read.");
        }
    }

    private static void ParseText(
        IDataObject data,
        DateTime now,
        DateTime? expiresUtc,
        List<ShelfItem> candidates,
        List<string> rejected)
    {
        string? text = null;
        try
        {
            foreach (string format in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat })
            {
                if (!data.GetDataPresent(format, false))
                {
                    continue;
                }

                text = data.GetData(format, false) as string;
                if (text is not null)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            AddRejection(rejected, "The dropped text could not be read.");
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int byteCount;
        try
        {
            byteCount = Encoding.UTF8.GetByteCount(text);
        }
        catch (ArgumentException)
        {
            AddRejection(rejected, "The dropped text contained invalid characters.");
            return;
        }

        if (byteCount > MaxTextBytes)
        {
            AddRejection(rejected, "Text larger than 1 MiB was not accepted.");
            return;
        }

        string payload = text;
        string trimmed = text.Trim();
        ShelfItemKind kind;
        string title;
        string preview;

        if (TryNormalizeColor(trimmed, out string color))
        {
            kind = ShelfItemKind.Color;
            payload = color;
            title = color;
            preview = color;
        }
        else if (TryGetWebUri(trimmed, out Uri uri))
        {
            kind = ShelfItemKind.Link;
            payload = uri.AbsoluteUri;
            title = string.IsNullOrWhiteSpace(uri.Host) ? uri.AbsoluteUri : uri.Host;
            preview = Truncate(uri.AbsoluteUri, PreviewLength);
        }
        else
        {
            kind = ShelfItemKind.Text;
            title = FirstUsefulLine(trimmed);
            preview = Truncate(trimmed, PreviewLength);
        }

        candidates.Add(new ShelfItem
        {
            Kind = kind,
            Title = title,
            Preview = preview,
            Payload = payload,
            CreatedUtc = now,
            ExpiresUtc = expiresUtc
        });
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

    private static bool TryGetWebUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) &&
            (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool TryNormalizeColor(string value, out string color)
    {
        color = string.Empty;
        if (value.Length is not (4 or 5 or 7 or 9) || value[0] != '#')
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') ||
                  (c >= 'a' && c <= 'f') ||
                  (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }

        color = value.ToUpperInvariant();
        return true;
    }

    private static string FirstUsefulLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Text";
        }

        using var reader = new StringReader(value);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return Truncate(line.Trim(), 80);
            }
        }

        return "Text";
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static string SafeName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static void AddRejection(List<string> rejected, string message)
    {
        // Keep malformed IDataObject implementations from producing unbounded diagnostics.
        if (rejected.Count < MaxCandidates + 1)
        {
            rejected.Add(message);
        }
    }
}
