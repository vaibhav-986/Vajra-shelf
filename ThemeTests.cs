using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class ThemeTests
{
    private static readonly string[] RequiredBrushes =
    {
        "WindowBackgroundBrush", "SurfaceBrush", "CardBrush", "RaisedSurfaceBrush",
        "TextBrush", "MutedBrush", "AccentBrush", "AccentInkBrush", "BorderBrush",
        "ControlBorderBrush", "ButtonHoverBrush", "ButtonPressedBrush", "ErrorBrush",
        "SelectionBrush", "SelectionInkBrush", "PreviewBackdropBrush", "PreviewShapeBrush",
        "GlassSampleBrush", "ShelfHighlightBrush"
    };

    public static void Run()
    {
        NormalizesSupportedNames();
        InstallsCompleteDistinctPalettes();
        KeepsDictionariesAndBrushesIsolated();
        MaintainsReadableContrast();
        FallsBackForInvalidAccents();
    }

    private static void NormalizesSupportedNames()
    {
        Equal("Dark", ThemeService.NormalizeTheme(null), "Null theme should use Dark.");
        Equal("Dark", ThemeService.NormalizeTheme("unknown"), "Unknown theme should use Dark.");
        Equal("Dark", ThemeService.NormalizeTheme(" dark "), "Dark normalization failed.");
        Equal("Light", ThemeService.NormalizeTheme("LIGHT"), "Light normalization failed.");
        Equal("Glass", ThemeService.NormalizeTheme(" glass "), "Glass normalization failed.");
    }

    private static void InstallsCompleteDistinctPalettes()
    {
        var dark = Palette("Dark", ThemeService.DefaultAccent);
        var light = Palette("Light", ThemeService.DefaultAccent);
        var glass = Palette("Glass", ThemeService.DefaultAccent);

        foreach (string key in RequiredBrushes)
        {
            True(dark.Contains(key), $"Dark palette is missing {key}.");
            True(light.Contains(key), $"Light palette is missing {key}.");
            True(glass.Contains(key), $"Glass palette is missing {key}.");
            True(dark[key] is Brush, $"{key} must be a Brush.");
        }

        NotEqual(ColorOf(dark, "WindowBackgroundBrush"), ColorOf(light, "WindowBackgroundBrush"), "Dark and Light backgrounds should differ.");
        NotEqual(ColorOf(dark, "CardBrush"), ColorOf(glass, "CardBrush"), "Dark and Glass cards should differ.");
        True(ColorOf(glass, "SurfaceBrush").A < 255, "Glass surface should be genuinely translucent.");
        True(ColorOf(dark, "SurfaceBrush").A == 255, "Dark surface should be opaque.");
        True(ColorOf(light, "SurfaceBrush").A == 255, "Light surface should be opaque.");
    }

    private static void KeepsDictionariesAndBrushesIsolated()
    {
        var first = Palette("Dark", "#8FE3CF");
        var second = Palette("Light", "#F3A594");
        var firstAccent = (SolidColorBrush)first["AccentBrush"];
        Color original = firstAccent.Color;

        True(!ReferenceEquals(first["AccentBrush"], second["AccentBrush"]), "Separate palettes must not share accent brushes.");
        ThemeService.Apply(second, "Glass", "#8FC9F5");
        Equal(original, firstAccent.Color, "Applying another palette mutated the first palette.");

        Brush oldBrush = (Brush)second["AccentBrush"];
        ThemeService.Apply(second, "Dark", "#F2C879");
        True(!ReferenceEquals(oldBrush, second["AccentBrush"]), "Apply must replace, not mutate, existing brushes.");
        True(firstAccent.IsFrozen, "Installed solid brushes should be frozen.");
    }

    private static void MaintainsReadableContrast()
    {
        foreach (string theme in new[] { "Dark", "Light", "Glass" })
        {
            foreach (string requestedAccent in new[] { "#8FE3CF", "#FFFFFF", "#050505", "#F3A594" })
            {
                ResourceDictionary palette = Palette(theme, requestedAccent);
                Color window = ColorOf(palette, "WindowBackgroundBrush");
                var backgrounds = new[]
                {
                    window,
                    Composite(ColorOf(palette, "SurfaceBrush"), window),
                    Composite(ColorOf(palette, "CardBrush"), window),
                    ColorOf(palette, "RaisedSurfaceBrush"),
                    ColorOf(palette, "ButtonHoverBrush"),
                    ColorOf(palette, "ButtonPressedBrush")
                };
                Color text = ColorOf(palette, "TextBrush");
                Color muted = ColorOf(palette, "MutedBrush");
                Color accent = ColorOf(palette, "AccentBrush");
                Color accentInk = ColorOf(palette, "AccentInkBrush");

                for (int index = 0; index < backgrounds.Length; index++)
                {
                    AtLeast(4.5, Contrast(accent, backgrounds[index]), $"{theme} accent lacks contrast on background {index}.");
                }

                AtLeast(4.5, Contrast(text, backgrounds[0]), $"{theme} text lacks window contrast.");
                AtLeast(4.5, Contrast(text, backgrounds[1]), $"{theme} text lacks surface contrast.");
                AtLeast(4.5, Contrast(text, backgrounds[2]), $"{theme} text lacks card contrast.");
                AtLeast(4.5, Contrast(muted, backgrounds[2]), $"{theme} muted text lacks card contrast.");
                AtLeast(4.5, Contrast(accentInk, accent), $"{theme} accent ink lacks contrast.");
            }
        }
    }

    private static void FallsBackForInvalidAccents()
    {
        ResourceDictionary invalid = Palette("Dark", "mint");
        ResourceDictionary missing = Palette("Dark", string.Empty);
        ResourceDictionary validDefault = Palette("Dark", ThemeService.DefaultAccent);
        Equal(ColorOf(validDefault, "AccentBrush"), ColorOf(invalid, "AccentBrush"), "Invalid accent did not use the mint default.");
        Equal(ColorOf(validDefault, "AccentBrush"), ColorOf(missing, "AccentBrush"), "Missing accent did not use the mint default.");

        True(ThemeService.TryParseAccent("#8FE3CF", out Color mint) && mint == Color.FromRgb(0x8F, 0xE3, 0xCF), "Valid accent parsing failed.");
        True(!ThemeService.TryParseAccent("#FFF", out _), "Short accents must be rejected.");
        True(!ThemeService.TryParseAccent("8FE3CF", out _), "Accents without # must be rejected.");
        True(!ThemeService.TryParseAccent("#GGGGGG", out _), "Non-hex accents must be rejected.");
    }

    private static ResourceDictionary Palette(string theme, string accent)
    {
        var resources = new ResourceDictionary();
        ThemeService.Apply(resources, theme, accent);
        return resources;
    }

    private static Color ColorOf(ResourceDictionary resources, string key)
    {
        if (resources[key] is not SolidColorBrush brush)
        {
            throw new InvalidOperationException($"{key} is not a solid color brush.");
        }

        return brush.Color;
    }

    private static Color Composite(Color foreground, Color background)
    {
        double amount = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(background.R + ((foreground.R - background.R) * amount)),
            (byte)Math.Round(background.G + ((foreground.G - background.G) * amount)),
            (byte)Math.Round(background.B + ((foreground.B - background.B) * amount)));
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
    }

    private static void NotEqual<T>(T first, T second, string message) where T : notnull
    {
        if (EqualityComparer<T>.Default.Equals(first, second))
            throw new InvalidOperationException(message);
    }

    private static void AtLeast(double expected, double actual, string message)
    {
        if (actual + 0.0001 < expected)
            throw new InvalidOperationException($"{message} Expected at least {expected:0.00}, got {actual:0.00}.");
    }
}
