using System;
using System.IO;
using System.Text.Json;
using VajraShelf.Models;

namespace VajraShelf.Services;

public sealed class SettingsStore
{
    private const string DefaultTheme = "Dark";
    private const string DefaultAccent = "#8FE3CF";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.DataFolderName,
            "settings.json"));
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            return Validate(settings);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings validated = Validate(settings);
        WriteAtomically(validated);
    }

    public AppSettings Validate(AppSettings? settings)
    {
        if (settings is null)
            return new AppSettings();

        AppSettings validated = settings.Clone();
        validated.Theme = NormalizeTheme(validated.Theme);
        validated.Accent = NormalizeAccent(validated.Accent);
        validated.Scale = ClampFinite(validated.Scale, 0.8, 1.5, 1.0);
        validated.TopOffset = ClampFinite(validated.TopOffset, 0.0, 200.0, 82.0);
        validated.MonitorIndex = Math.Clamp(validated.MonitorIndex, 0, 32);
        validated.TemporaryLifetimeMinutes = validated.TemporaryLifetimeMinutes switch
        {
            0 or 60 or 1440 => validated.TemporaryLifetimeMinutes,
            _ => 1440
        };
        return validated;
    }

    private static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme?.Trim(), "Light", StringComparison.OrdinalIgnoreCase))
            return "Light";
        if (string.Equals(theme?.Trim(), "Glass", StringComparison.OrdinalIgnoreCase))
            return "Glass";
        return "Dark";
    }

    private static string NormalizeAccent(string? accent)
    {
        string candidate = accent?.Trim() ?? string.Empty;
        if (candidate.Length != 7 || candidate[0] != '#')
            return DefaultAccent;

        for (int index = 1; index < candidate.Length; index++)
        {
            char value = candidate[index];
            bool isHex = value is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHex)
                return DefaultAccent;
        }

        return candidate.ToUpperInvariant();
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return fallback;
        return Math.Clamp(value, minimum, maximum);
    }

    private void WriteAtomically(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The settings path must have a parent directory.");

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
                JsonSerializer.Serialize(stream, settings, JsonOptions);
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
}
