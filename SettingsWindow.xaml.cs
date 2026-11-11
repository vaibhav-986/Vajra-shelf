using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VajraShelf.Models;
using VajraShelf.Services;

namespace VajraShelf;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _draft;
    private string _previewTheme;
    private string _previewAccent;
    private bool _initialized;
    private bool _syncingAccent;
    private bool _saving;

    public event Action<AppSettings>? SettingsSaved;

    public SettingsWindow(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Validation is deliberately given a clone; even an in-place normalizer cannot touch the caller.
        _draft = _settingsStore.Validate(current.Clone());
        _previewTheme = ThemeService.NormalizeTheme(_draft.Theme);
        _previewAccent = _draft.Accent;

        InitializeComponent();
        Title = AppIdentity.DisplayName + " · Settings";
        LimitHeight(SystemParameters.WorkArea.Height);

        ThemeDarkChoice.IsChecked = _previewTheme == "Dark";
        ThemeLightChoice.IsChecked = _previewTheme == "Light";
        ThemeGlassChoice.IsChecked = _previewTheme == "Glass";
        AccentInput.Text = _draft.Accent;
        ScaleSlider.Value = _draft.Scale;
        OffsetSlider.Value = _draft.TopOffset;
        HoverCheck.IsChecked = _draft.ExpandOnHover;
        MotionCheck.IsChecked = _draft.ReduceMotion;
        FullscreenCheck.IsChecked = _draft.HideInFullscreen;
        StartupCheck.IsChecked = _draft.StartAtLogin;

        PopulateMonitors();
        PopulateExpiryChoices();

        _initialized = true;
        RefreshAccent();
        ApplyPreview();
    }

    private void PopulateMonitors()
    {
        System.Windows.Forms.Screen[] screens;
        try
        {
            screens = System.Windows.Forms.Screen.AllScreens;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"{AppIdentity.DisplayName} could not enumerate displays: {exception}");
            screens = Array.Empty<System.Windows.Forms.Screen>();
        }

        var choices = new List<MonitorChoice>();
        for (int index = 0; index <= 32; index++)
        {
            string state = index < screens.Length
                ? screens[index].Primary ? " · primary" : " · connected"
                : " · not connected";
            if (screens.Length == 0 && index == 0)
            {
                state = " · default";
            }

            choices.Add(new MonitorChoice(index, $"Display {index + 1}{state}"));
        }

        MonitorSelector.DisplayMemberPath = nameof(MonitorChoice.Label);
        MonitorSelector.SelectedValuePath = nameof(MonitorChoice.Index);
        MonitorSelector.ItemsSource = choices;
        MonitorSelector.SelectedValue = _draft.MonitorIndex;
    }

    private void PopulateExpiryChoices()
    {
        var choices = new[]
        {
            new ExpiryChoice(0, "Session"),
            new ExpiryChoice(60, "1 hour"),
            new ExpiryChoice(1440, "24 hours")
        };
        ExpirySelector.DisplayMemberPath = nameof(ExpiryChoice.Label);
        ExpirySelector.SelectedValuePath = nameof(ExpiryChoice.Minutes);
        ExpirySelector.ItemsSource = choices;
        ExpirySelector.SelectedValue = _draft.TemporaryLifetimeMinutes;
        if (ExpirySelector.SelectedIndex < 0)
        {
            ExpirySelector.SelectedValue = 1440;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            Matrix transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Vector workSize = transform.Transform(new Vector(screen.WorkingArea.Width, screen.WorkingArea.Height));
            LimitHeight(workSize.Y);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"{AppIdentity.DisplayName} could not measure the settings display: {exception}");
        }

        RadioButton selected = _previewTheme == "Light"
            ? ThemeLightChoice
            : _previewTheme == "Glass" ? ThemeGlassChoice : ThemeDarkChoice;
        selected.Focus();
        SettingsScroll.ScrollToTop();
    }

    private void LimitHeight(double workAreaHeight)
    {
        double available = Math.Max(240, workAreaHeight - 32);
        MinHeight = Math.Min(360, available);
        MaxHeight = available;
        Height = Math.Min(Height, available);
    }

    private void ThemeChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton choice || choice.Tag is not string theme)
        {
            return;
        }

        _previewTheme = ThemeService.NormalizeTheme(theme);
        ApplyPreview();
        RaiseLiveRegion(ThemeMaterialHint);
    }

    private void AccentChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingAccent || sender is not RadioButton choice || choice.Tag is not string accent)
        {
            return;
        }

        AccentInput.Text = accent;
    }

    private void AccentInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            RefreshAccent();
        }
    }

    private void RefreshAccent()
    {
        string accent = AccentInput.Text.Trim().ToUpperInvariant();
        _syncingAccent = true;
        try
        {
            foreach (UIElement element in AccentChoices.Children)
            {
                if (element is RadioButton choice)
                {
                    choice.IsChecked = string.Equals(choice.Tag as string, accent, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            _syncingAccent = false;
        }

        // While a custom value is being typed, keep showing the last complete, valid accent.
        if (!ThemeService.TryParseAccent(accent, out _))
        {
            return;
        }

        _previewAccent = accent;
        ApplyPreview();
    }

    private void ApplyPreview()
    {
        // Resource replacement is window-local. The owner, app, and merged dictionary remain untouched.
        ThemeService.Apply(Resources, _previewTheme, _previewAccent);
        ThemePreviewLabel.Text = _previewTheme switch
        {
            "Light" => "Light · clean paper",
            "Glass" => "Glass · tinted layer",
            _ => "Dark · grounded"
        };
        ThemeMaterialHint.Text = _previewTheme switch
        {
            "Light" => "Soft paper-like surfaces with an accent darkened only as much as readability needs.",
            "Glass" => "A translucent tinted layer, not desktop blur. The sample shows it over a backdrop.",
            _ => "Deep opaque surfaces with a crisp mint signal."
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving)
        {
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        string accent = AccentInput.Text.Trim().ToUpperInvariant();
        if (!ThemeService.TryParseAccent(accent, out _))
        {
            ShowError("Enter a six-digit hex color, such as #8FE3CF.");
            AccentInput.Focus();
            AccentInput.SelectAll();
            AccentInput.BringIntoView();
            return;
        }

        if (MonitorSelector.SelectedValue is not int monitorIndex)
        {
            ShowError("Choose a display for the shelf.");
            MonitorSelector.Focus();
            MonitorSelector.BringIntoView();
            return;
        }

        if (ExpirySelector.SelectedValue is not int expiryMinutes)
        {
            ShowError("Choose how long temporary items should remain.");
            ExpirySelector.Focus();
            ExpirySelector.BringIntoView();
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        try
        {
            AppSettings candidate = _draft.Clone();
            candidate.Theme = _previewTheme;
            candidate.Accent = accent;
            candidate.Scale = ScaleSlider.Value;
            candidate.TopOffset = OffsetSlider.Value;
            candidate.MonitorIndex = monitorIndex;
            candidate.ExpandOnHover = HoverCheck.IsChecked == true;
            candidate.ReduceMotion = MotionCheck.IsChecked == true;
            candidate.HideInFullscreen = FullscreenCheck.IsChecked == true;
            candidate.TemporaryLifetimeMinutes = expiryMinutes;
            candidate.StartAtLogin = StartupCheck.IsChecked == true;

            AppSettings validated = _settingsStore.Validate(candidate);
            // Persistence and startup registration are caller responsibilities. If either subscriber
            // throws, the catch below keeps this window and all edits open.
            SettingsSaved?.Invoke(validated.Clone());
            Close();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"{AppIdentity.DisplayName} could not apply settings: {exception}");
            ShowError("We couldn't apply your settings. Your edits are still here.\n" + exception.Message);
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        RaiseLiveRegion(ErrorText);
    }

    private static void RaiseLiveRegion(TextBlock textBlock)
    {
        AutomationPeer peer = UIElementAutomationPeer.FromElement(textBlock) ?? new TextBlockAutomationPeer(textBlock);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Every editable value lives in controls or the private clone.
        Close();
    }

    private sealed record MonitorChoice(int Index, string Label);
    private sealed record ExpiryChoice(int Minutes, string Label);
}
