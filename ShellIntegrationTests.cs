using System;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class ShellIntegrationTests
{
    public static void Run()
    {
        Assert(HotkeyService.ToggleHotkeyId != HotkeyService.AddClipboardHotkeyId,
            "Hotkey IDs must be distinct.");
        Assert(HotkeyService.ModifierFlags ==
               (HotkeyService.ModifierControl | HotkeyService.ModifierAlt | HotkeyService.ModifierNoRepeat),
            "Hotkeys must use Ctrl+Alt with MOD_NOREPEAT.");
        Assert(HotkeyService.VirtualKeyV == (uint)'V', "Toggle hotkey must use V.");
        Assert(HotkeyService.VirtualKeyP == (uint)'P', "Clipboard hotkey must use P.");
        Assert(ShelfIcon.DefaultSize == 32, "The generated shell icon should default to 32 px.");
        Assert(TrayService.ShowText == "Show" && TrayService.AddClipboardText == "Add clipboard" &&
               TrayService.ClearTemporaryText == "Clear temporary" && TrayService.SettingsText == "Settings" &&
               TrayService.ExitText == "Exit", "Tray commands must retain their shell contract labels.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
