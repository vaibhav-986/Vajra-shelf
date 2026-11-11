using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using VajraShelf.Models;

namespace VajraShelf.Services;

public interface IProcessLauncher
{
    bool Start(ProcessStartInfo startInfo);
}

public sealed class SystemProcessLauncher : IProcessLauncher
{
    public bool Start(ProcessStartInfo startInfo) => Process.Start(startInfo) is not null;
}

public sealed class FileActionService
{
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse",
        ".wsf", ".scr", ".com", ".lnk", ".url"
    };

    private readonly IProcessLauncher _launcher;

    public FileActionService(IProcessLauncher? launcher = null)
    {
        _launcher = launcher ?? new SystemProcessLauncher();
    }

    public bool Open(ShelfItem item, Func<string, bool>? confirmDangerous = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? target = GetOpenTarget(item);
        if (target is null)
        {
            return false;
        }

        if (item.Kind != ShelfItemKind.Link)
        {
            bool exists = item.Kind == ShelfItemKind.Folder ? Directory.Exists(target) : File.Exists(target);
            if (!exists)
            {
                return false;
            }

            if (IsDangerousExtension(target) && (confirmDangerous is null || !confirmDangerous(target)))
            {
                return false;
            }
        }

        return TryStart(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    public bool Reveal(ShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? target = GetRevealTarget(item);
        if (target is null || (!File.Exists(target) && !Directory.Exists(target)))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("/select," + target);
        return TryStart(startInfo);
    }

    public static bool IsDangerousExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return DangerousExtensions.Contains(Path.GetExtension(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? GetOpenTarget(ShelfItem item)
    {
        switch (item.Kind)
        {
            case ShelfItemKind.File:
            case ShelfItemKind.Folder:
                return CanonicalPath(item.Payload);
            case ShelfItemKind.Image:
                return CanonicalPath(!string.IsNullOrWhiteSpace(item.OwnedCachePath)
                    ? item.OwnedCachePath
                    : item.Payload);
            case ShelfItemKind.Link:
                return Uri.TryCreate(item.Payload, UriKind.Absolute, out Uri? uri) &&
                       (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    ? uri.AbsoluteUri
                    : null;
            default:
                return null;
        }
    }

    private static string? GetRevealTarget(ShelfItem item) => item.Kind switch
    {
        ShelfItemKind.File or ShelfItemKind.Folder => CanonicalPath(item.Payload),
        ShelfItemKind.Image => CanonicalPath(!string.IsNullOrWhiteSpace(item.OwnedCachePath)
            ? item.OwnedCachePath
            : item.Payload),
        _ => null
    };

    private static string? CanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private bool TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            return _launcher.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
