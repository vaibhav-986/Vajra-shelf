using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VajraShelf.Services;

/// <summary>Installs a complete semantic palette into one resource dictionary.</summary>
public static class ThemeService
{
    public const string DefaultAccent = "#8FE3CF";
    private const double MinimumContrast = 4.5;

    public static void Apply(ResourceDictionary target, string theme, string accent)
    {
        ArgumentNullException.ThrowIfNull(target);

        string normalized = NormalizeTheme(theme);
        bool light = normalized == "Light";
        bool glass = normalized == "Glass";

        Color window = Parse(light ? "#F1F7F4" : glass ? "#10191C" : "#0D1517");
        Color surface = Parse(light ? "#F9FCFA" : glass ? "#E8172528" : "#121D20");
        Color card = Parse(light ? "#FFFFFF" : glass ? "#F01C2B2E" : "#19272A");
        Color raised = Parse(light ? "#E8F1ED" : glass ? "#F426383B" : "#203235");
        Color hover = Parse(light ? "#E1ECE7" : glass ? "#354A4D" : "#273A3D");
        Color pressed = Parse(light ? "#D3E3DC" : glass ? "#40585B" : "#31474A");
        Color text = Parse(light ? "#162622" : "#F2FBF8");
        Color muted = Parse(light ? "#506760" : glass ? "#BCD0CA" : "#A9BEB8");
        Color border = Parse(light ? "#B8CBC4" : glass ? "#718885" : "#405653");

        Color requested = TryParseAccent(accent, out Color parsed) ? parsed : Parse(DefaultAccent);
        Color readableAccent = EnsureContrast(requested, new[] { window, Opaque(surface, window), Opaque(card, window), raised, hover, pressed }, light);
        Color accentInk = ContrastRatio(readableAccent, Colors.Black) >= ContrastRatio(readableAccent, Colors.White)
            ? Colors.Black
            : Colors.White;

        Set(target, "WindowBackgroundBrush", window);
        Set(target, "SurfaceBrush", surface);
        Set(target, "CardBrush", card);
        Set(target, "RaisedSurfaceBrush", raised);
        Set(target, "TextBrush", text);
        Set(target, "MutedBrush", muted);
        Set(target, "AccentBrush", readableAccent);
        Set(target, "AccentInkBrush", accentInk);
        Set(target, "BorderBrush", border);
        Set(target, "ControlBorderBrush", light ? Parse("#6A817A") : Parse("#829A94"));
        Set(target, "ButtonHoverBrush", hover);
        Set(target, "ButtonPressedBrush", pressed);
        Set(target, "ErrorBrush", light ? Parse("#982D43") : Parse("#FFB8C4"));
        Set(target, "SelectionBrush", readableAccent);
        Set(target, "SelectionInkBrush", accentInk);
        Set(target, "PreviewBackdropBrush", light ? Parse("#C9DDD5") : Parse("#274044"));
        Set(target, "PreviewShapeBrush", light ? Parse("#7CAFA1") : Parse("#659B8D"));
        Set(target, "GlassSampleBrush", Parse("#D9172528"));

        Color shine = glass ? Color.FromArgb(24, 255, 255, 255) : Colors.Transparent;
        var highlight = new LinearGradientBrush(shine, Colors.Transparent, new Point(0, 0), new Point(0, 1));
        highlight.Freeze();
        target["ShelfHighlightBrush"] = highlight;
    }

    public static string NormalizeTheme(string? theme) => theme?.Trim().ToUpperInvariant() switch
    {
        "LIGHT" => "Light",
        "GLASS" => "Glass",
        _ => "Dark"
    };

    public static bool TryParseAccent(string? accent, out Color color)
    {
        color = default;
        if (accent is null || accent.Length != 7 || accent[0] != '#'
            || !byte.TryParse(accent.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte red)
            || !byte.TryParse(accent.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte green)
            || !byte.TryParse(accent.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte blue))
        {
            return false;
        }

        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static Color EnsureContrast(Color accent, IReadOnlyList<Color> backgrounds, bool light)
    {
        if (IsReadable(accent, backgrounds))
        {
            return accent;
        }

        Color endpoint = light ? Colors.Black : Colors.White;
        Color passing = endpoint;
        double low = 0;
        double high = 1;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double amount = (low + high) / 2;
            Color candidate = Mix(accent, endpoint, amount);
            if (IsReadable(candidate, backgrounds))
            {
                passing = candidate;
                high = amount;
            }
            else
            {
                low = amount;
            }
        }

        return passing;
    }

    private static bool IsReadable(Color foreground, IReadOnlyList<Color> backgrounds)
    {
        for (int index = 0; index < backgrounds.Count; index++)
        {
            if (ContrastRatio(foreground, backgrounds[index]) < MinimumContrast)
            {
                return false;
            }
        }

        return true;
    }

    private static Color Opaque(Color foreground, Color background)
    {
        if (foreground.A == 255)
        {
            return foreground;
        }

        return Mix(background, Color.FromRgb(foreground.R, foreground.G, foreground.B), foreground.A / 255.0);
    }

    private static Color Mix(Color start, Color end, double amount) => Color.FromRgb(
        (byte)Math.Round(start.R + ((end.R - start.R) * amount)),
        (byte)Math.Round(start.G + ((end.G - start.G) * amount)),
        (byte)Math.Round(start.B + ((end.B - start.B) * amount)));

    private static double ContrastRatio(Color first, Color second)
    {
        double lighter = Math.Max(Luminance(first), Luminance(second));
        double darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static void Set(ResourceDictionary target, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        // Replace the entry rather than mutating a brush that may come from a merged dictionary.
        target[key] = brush;
    }
}
