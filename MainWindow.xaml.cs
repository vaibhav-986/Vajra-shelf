using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VajraShelf.Models;
using VajraShelf.Services;

namespace VajraShelf;

public partial class MainWindow : Window, IDisposable
{
    private readonly bool _smoke;
    private readonly string? _smokeRoot;
    private readonly SettingsStore _settingsStore;
    private readonly ShelfStore _shelfStore;
    private readonly CacheService _cache;
    private readonly ShelfService _shelf;
    private readonly DropParser _dropParser = new();
    private readonly ClipboardService _clipboard;
    private readonly DragOutService _dragOut = new();
    private readonly FileActionService _fileActions = new();
    private readonly StartupService _startup = new();
    private readonly DesktopService _desktop = new();
    private readonly ShelfTransitionController _transition = new();
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer _dropDwellTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _maintenanceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private HwndSource? _windowSource;
    private ShelfItem? _dragItem;
    private Point _dragStart;
    private bool _expanded;
    private bool _pinned;
    private bool _hostExpanded;
    private bool _shellHovered;
    private bool _externalDrag;
    private bool _hiddenByFullscreen;
    private bool _positioning;
    private bool _positionQueued;
    private bool _disposed;
    private DateTime _noticeUntil;
    private int _maintenanceTicks;

    internal double RevealProgress => _transition.Progress;
    internal bool IsTransitioning => _transition.IsTransitioning;
    internal bool IsBodyInteractive => ExpandedContent.IsEnabled && ExpandedContent.IsHitTestVisible;
    internal int PlacementCount { get; private set; }
    private bool MotionEnabled => !_settings.ReduceMotion && SystemParameters.ClientAreaAnimation && IsVisible;

    public MainWindow(bool smoke = false)
    {
        _smoke = smoke;
        if (smoke)
        {
            _smokeRoot = Path.Combine(Path.GetTempPath(), "VajraShelf-smoke-" + Guid.NewGuid().ToString("N"));
            string cachePath = Path.Combine(_smokeRoot, "cache");
            _settingsStore = new SettingsStore(Path.Combine(_smokeRoot, "settings.json"));
            _shelfStore = new ShelfStore(Path.Combine(_smokeRoot, "pinned-items.json"), cachePath);
            _cache = new CacheService(cachePath);
        }
        else
        {
            _settingsStore = new SettingsStore();
            _shelfStore = new ShelfStore();
            _cache = new CacheService();
        }

        _settings = _settingsStore.Load();
        if (!smoke) _settings.StartAtLogin = _startup.IsEnabled;
        IReadOnlyList<ShelfItem> pinned = _shelfStore.Load();
        _shelf = new ShelfService(_cache, pinned);
        _clipboard = new ClipboardService(_cache);
        _cache.CleanupUnreferenced(_shelf.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.OwnedCachePath))
            .Select(item => item.OwnedCachePath!));

        InitializeComponent();
        Title = AppIdentity.DisplayName;
        ShellSurface.Data = ShellClip;
        ShelfItems.ItemsSource = _shelf.Items;
        _shelf.Items.CollectionChanged += Items_CollectionChanged;
        _shelf.PinnedItemsChanged += Shelf_PinnedItemsChanged;
        _transition.ProgressChanged += ApplyRevealProgress;
        _transition.Settled += Transition_Settled;
        SystemParameters.StaticPropertyChanged += SystemAnimationPreferenceChanged;
        IsVisibleChanged += ShelfVisibilityChanged;
        ShelfCanvas.SizeChanged += (_, _) => UpdateShellOffset(ShelfCanvas.ActualWidth);

        _collapseTimer.Tick += CollapseTimer_Tick;
        _hoverTimer.Tick += HoverTimer_Tick;
        _dropDwellTimer.Tick += DropDwellTimer_Tick;
        _maintenanceTimer.Tick += MaintenanceTimer_Tick;
        SourceInitialized += SourceInitializedHandler;
        Loaded += LoadedHandler;
        ApplyAppearance();
        UpdateShelfView();

        if (!smoke)
        {
            _tray = new TrayService(Dispatcher, ShowShelf, AddClipboardFromShellAsync, ClearTemporary, OpenSettings,
                () => Application.Current.Shutdown());
            _hotkeys = new HotkeyService(this, ToggleShelf, AddClipboardFromShellAsync);
        }
    }

    private void LoadedHandler(object sender, RoutedEventArgs e)
    {
        PositionShelf();
        _maintenanceTimer.Start();
        if (!_smoke && _hotkeys is { ToggleRegistered: false, AddClipboardRegistered: false })
            Notice("Global shortcuts unavailable; use the tray menu.");
    }

    private void SourceInitializedHandler(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(DesktopMessage);
        PositionShelf();
    }

    private TimeSpan TemporaryLifetime => _settings.TemporaryLifetimeMinutes <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(_settings.TemporaryLifetimeMinutes);

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateShelfView();

    private void Shelf_PinnedItemsChanged(object? sender, EventArgs e)
    {
        SavePinnedItems();
        UpdateShelfView();
    }

    private void SavePinnedItems()
    {
        try { _shelfStore.Save(_shelf.Items); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Notice("Pinned items could not be saved: " + error.Message);
        }
    }

    private void UpdateShelfView()
    {
        if (!IsInitialized) return;
        string query = SearchBox.Text;
        IReadOnlyList<ShelfItem> filtered = _shelf.Filtered(query);
        ShelfItems.ItemsSource = filtered;
        int count = _shelf.Items.Count;
        CompactCount.Text = count == 1 ? "1 item" : $"{count} items";
        FooterCount.Text = filtered.Count == count
            ? CompactCount.Text
            : $"{filtered.Count} of {count}";
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => UpdateShelfView();

    private void ApplyAppearance()
    {
        ThemeService.Apply(Resources, _settings.Theme, _settings.Accent);
        _transition.Settle();
        ShellScale.ScaleX = ShellScale.ScaleY = _settings.Scale;
        ApplyRevealProgress(RevealProgress);
        PositionShelf();
    }

    private void PositionShelf()
    {
        if (_disposed || _positioning) return;
        _positioning = true;
        try
        {
            Rect target = _desktop.PlaceShelf(_hostExpanded, _settings);
            if (Math.Abs(Width - target.Width) > 0.001) Width = target.Width;
            if (Math.Abs(Height - target.Height) > 0.001) Height = target.Height;
            UpdateShellOffset(target.Width);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                Left = target.X;
                Top = target.Y;
                return;
            }
            PlacementCount++;
            if (!_desktop.TryPlaceShelfWindow(handle, _hostExpanded, _settings))
            {
                Left = target.X;
                Top = target.Y;
            }
        }
        finally { _positioning = false; }
    }

    private void UpdateShellOffset(double clientWidth)
    {
        Canvas.SetLeft(Shell, (clientWidth - ShelfGeometry.ExpandedWidth * _settings.Scale) / 2);
        Canvas.SetTop(Shell, ShelfGeometry.Gutter);
    }

    private IntPtr DesktopMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((message == 0x02E0 || message == 0x007E || message == 0x001A) && !_disposed && !_positionQueued)
        {
            _positionQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _positionQueued = false;
                if (_disposed) return;
                _transition.Settle();
                PositionShelf();
            }));
        }
        return IntPtr.Zero;
    }

    private void SetExpanded(bool value)
    {
        if (_disposed || (_expanded == value && (!IsTransitioning || MotionEnabled))) return;
        _expanded = value;
        _collapseTimer.Stop();
        _hoverTimer.Stop();
        SetBodyInteractive(false);
        CompactContent.IsHitTestVisible = false;
        if (value)
        {
            ExpandedContent.Visibility = Visibility.Visible;
            SetHostExpanded(true);
        }
        _transition.SetExpanded(value, MotionEnabled);
    }

    private void ApplyRevealProgress(double progress)
    {
        Rect silhouette = ShelfGeometry.Silhouette(progress);
        ShellClip.Rect = silhouette;
        ShellClip.RadiusX = ShellClip.RadiusY = ShelfGeometry.CornerRadius(progress);
        ExpandedContent.Opacity = ShelfGeometry.BodyOpacity(progress);
        BodyTranslation.Y = ShelfGeometry.BodyOffset(progress);
        CompactContent.Opacity = ShelfGeometry.CompactOpacity(progress);
        UpdateShellHover();
    }

    private void Transition_Settled(bool expanded)
    {
        if (_disposed) return;
        ApplyRevealProgress(expanded ? 1 : 0);
        ExpandedContent.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        SetBodyInteractive(expanded && IsVisible);
        CompactContent.IsHitTestVisible = !expanded && IsVisible;
        SetHostExpanded(expanded);
        if (expanded && SearchBox.IsKeyboardFocusWithin) SearchBox.Focus();
    }

    private void SetHostExpanded(bool expanded)
    {
        if (_hostExpanded == expanded) return;
        _hostExpanded = expanded;
        PositionShelf();
    }

    private void SetBodyInteractive(bool interactive)
    {
        if (!interactive)
        {
            if (ExpandedContent.IsKeyboardFocusWithin)
            {
                FocusManager.SetFocusedElement(this, CompactButton);
                Keyboard.ClearFocus();
            }
            if (ExpandedContent.IsMouseCaptureWithin) Mouse.Capture(null);
        }
        ExpandedContent.IsHitTestVisible = interactive;
        ExpandedContent.IsEnabled = interactive;
        KeyboardNavigation.SetTabNavigation(ExpandedContent,
            interactive ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(ExpandedContent,
            interactive ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(ExpandedContent,
            interactive ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private void SystemAnimationPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName != nameof(SystemParameters.ClientAreaAnimation))) return;
        void ApplyPolicy()
        {
            if (!_disposed && !MotionEnabled) _transition.Settle();
        }
        if (Dispatcher.CheckAccess()) ApplyPolicy();
        else Dispatcher.BeginInvoke(new Action(ApplyPolicy));
    }

    private void ShelfVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (!IsVisible)
        {
            StopIntentTimers();
            _shellHovered = false;
            _transition.Settle();
            SetBodyInteractive(false);
            CompactContent.IsHitTestVisible = false;
        }
        else if (!IsTransitioning)
        {
            SetBodyInteractive(_expanded);
            CompactContent.IsHitTestVisible = !_expanded;
        }
    }

    private void StopIntentTimers()
    {
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        _dropDwellTimer.Stop();
    }

    private bool IsPointerOverShell() => IsVisible && Shell.IsMouseOver &&
        ShellClip.FillContains(Mouse.GetPosition(Shell));

    private void UpdateShellHover()
    {
        if (_disposed) return;
        bool hovered = IsPointerOverShell();
        if (hovered == _shellHovered) return;
        _shellHovered = hovered;
        if (hovered)
        {
            _collapseTimer.Stop();
            if (_settings.ExpandOnHover && !_expanded && !_externalDrag && _settingsWindow == null)
                _hoverTimer.Start();
        }
        else
        {
            _hoverTimer.Stop();
            if (_expanded && !_pinned && !_externalDrag && _settingsWindow == null)
                _collapseTimer.Start();
        }
    }

    private void Shell_MouseEnter(object sender, MouseEventArgs e) => UpdateShellHover();
    private void Shell_MouseLeave(object sender, MouseEventArgs e) => UpdateShellHover();
    private void Shell_MouseMove(object sender, MouseEventArgs e) => UpdateShellHover();

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (IsPointerOverShell() && _settings.ExpandOnHover && !_externalDrag && _settingsWindow == null)
            SetExpanded(true);
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (!IsPointerOverShell() && !_pinned && !_externalDrag && _settingsWindow == null)
            SetExpanded(false);
    }

    private void DropDwellTimer_Tick(object? sender, EventArgs e)
    {
        _dropDwellTimer.Stop();
        if (_externalDrag) SetExpanded(true);
    }

    private static bool HasSupportedDrop(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop, false) ||
                   data.GetDataPresent(DataFormats.UnicodeText, false) ||
                   data.GetDataPresent(DataFormats.Text, false) ||
                   data.GetDataPresent(DataFormats.StringFormat, false);
        }
        catch (Exception) { return false; }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (!HasSupportedDrop(e.Data))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        _externalDrag = true;
        _collapseTimer.Stop();
        DropFeedback.Visibility = Visibility.Visible;
        CompactLabel.Text = "Release to add";
        if (!_expanded) _dropDwellTimer.Start();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool supported = HasSupportedDrop(e.Data);
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        if (supported)
        {
            _externalDrag = true;
            DropFeedback.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        EndExternalDrag();
        e.Handled = true;
    }

    private void EndExternalDrag()
    {
        _externalDrag = false;
        _dropDwellTimer.Stop();
        DropFeedback.Visibility = Visibility.Collapsed;
        CompactLabel.Text = "Drop here";
        UpdateShellHover();
        if (_expanded && !_pinned && !IsPointerOverShell() && _settingsWindow == null)
            _collapseTimer.Start();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        bool supported = HasSupportedDrop(e.Data);
        EndExternalDrag();
        if (!supported)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        try
        {
            DropParseResult result = _dropParser.Parse(e.Data, DateTime.UtcNow, TemporaryLifetime);
            int added = _shelf.AddCandidates(result.Candidates);
            ReportIngestion(added, result.RejectedMessages);
            if (added > 0) SetExpanded(true);
            e.Effects = DragDropEffects.Copy;
        }
        catch (Exception error)
        {
            Notice("Drop could not be read: " + error.Message);
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void ReportIngestion(int added, IReadOnlyList<string> rejected)
    {
        UpdateShelfView();
        if (added > 0 && rejected.Count == 0)
            Notice(added == 1 ? "Added 1 item" : $"Added {added} items");
        else if (added > 0)
            Notice($"Added {added}; {rejected[0]}");
        else
            Notice(rejected.Count > 0 ? rejected[0] : "That item is already on the shelf.");
    }

    private async Task AddClipboardAsync()
    {
        if (_disposed) return;
        try
        {
            DropParseResult result = await _clipboard.ReadCandidatesAsync(DateTime.UtcNow, TemporaryLifetime);
            if (_disposed) return;
            int added = _shelf.AddCandidates(result.Candidates);
            ReportIngestion(added, result.RejectedMessages);
            if (added > 0) SetExpanded(true);
        }
        catch (Exception error)
        {
            if (!_disposed) Notice("Clipboard is busy: " + error.Message);
        }
    }

    public Task AddClipboardFromShellAsync() => AddClipboardAsync();
    private async void Paste_Click(object sender, RoutedEventArgs e) => await AddClipboardAsync();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) != null)
        {
            _dragItem = null;
            return;
        }
        if (sender is FrameworkElement element && element.Tag is ShelfItem item)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(this);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement card) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        ShelfItem item = _dragItem;
        _dragItem = null;
        try
        {
            IDataObject data = _dragOut.CreateDataObject(item);
            if (data.GetFormats(false).Length > 0)
                DragDrop.DoDragDrop(card, data, DragDropEffects.Copy);
        }
        catch (Exception error) { Notice("Item could not be dragged: " + error.Message); }
    }

    private ShelfItem? ItemFrom(object sender) => (sender as FrameworkElement)?.Tag as ShelfItem;

    private void ItemPin_Click(object sender, RoutedEventArgs e)
    {
        ShelfItem? item = ItemFrom(sender);
        if (item == null || !_shelf.TogglePin(item)) return;
        if (!item.IsPinned)
            item.ExpiresUtc = TemporaryLifetime <= TimeSpan.Zero ? null : DateTime.UtcNow + TemporaryLifetime;
        Notice(item.IsPinned ? "Item pinned" : "Item unpinned");
        UpdateShelfView();
    }

    private void ItemCopy_Click(object sender, RoutedEventArgs e)
    {
        ShelfItem? item = ItemFrom(sender);
        if (item == null) return;
        try { _clipboard.Copy(item); Notice("Copied to clipboard"); }
        catch (Exception error) { Notice("Could not copy: " + error.Message); }
    }

    private bool ConfirmDangerous(string path) => MessageBox.Show(this,
        $"This file type can run code and may be unsafe. Open it?\n\n{path}",
        "Confirm open", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void ItemOpen_Click(object sender, RoutedEventArgs e)
    {
        ShelfItem? item = ItemFrom(sender);
        if (item == null) return;
        if (!_fileActions.Open(item, ConfirmDangerous)) Notice("This item could not be opened.");
    }

    private void ItemReveal_Click(object sender, RoutedEventArgs e)
    {
        ShelfItem? item = ItemFrom(sender);
        if (item == null) return;
        if (!_fileActions.Reveal(item)) Notice("This item cannot be shown in File Explorer.");
    }

    private void ItemRemove_Click(object sender, RoutedEventArgs e)
    {
        ShelfItem? item = ItemFrom(sender);
        if (item == null) return;
        if (item.IsPinned && MessageBox.Show(this,
            "Remove this pinned item from Vajra Shelf?\n\nThe original file or folder will not be deleted.",
            "Remove pinned item", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool wasPinned = item.IsPinned;
        if (!_shelf.Remove(item)) return;
        if (!wasPinned) SavePinnedItems();
        Notice("Item removed");
    }

    public void ClearTemporary()
    {
        int removed = _shelf.ClearTemporary();
        SavePinnedItems();
        Notice(removed == 0 ? "No temporary items to clear" : $"Cleared {removed} temporary item{(removed == 1 ? string.Empty : "s")}");
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearTemporary();

    private void MaintenanceTimer_Tick(object? sender, EventArgs e)
    {
        if (++_maintenanceTicks % 30 == 0)
        {
            int expired = _shelf.Expire(DateTime.UtcNow);
            UpdateShelfView();
            if (expired > 0) Notice($"Expired {expired} temporary item{(expired == 1 ? string.Empty : "s")}");
        }
        if (DateTime.UtcNow >= _noticeUntil) StatusText.Text = "Ready";
        if (!_smoke)
        {
            bool fullscreen = _settings.HideInFullscreen &&
                _desktop.IsFullscreenApp(new WindowInteropHelper(this).Handle);
            if (fullscreen && IsVisible)
            {
                _hiddenByFullscreen = true;
                Hide();
            }
            else if (!fullscreen && _hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                Show();
                PositionShelf();
            }
        }
    }

    private void ToggleShelf()
    {
        if (!IsVisible)
        {
            ShowShelf();
            return;
        }
        SetExpanded(!_expanded);
        if (_expanded) Activate();
    }

    public void ShowShelf()
    {
        if (_disposed) return;
        _hiddenByFullscreen = false;
        Show();
        PositionShelf();
        SetExpanded(true);
        Activate();
    }

    public void OpenSettings()
    {
        if (_disposed) return;
        StopIntentTimers();
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow(_settings) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _settingsWindow = window;
        window.SettingsSaved += SaveSettings;
        window.Closed += (_, _) =>
        {
            window.SettingsSaved -= SaveSettings;
            _settingsWindow = null;
            if (!_disposed && !_pinned && !IsPointerOverShell()) _collapseTimer.Start();
        };
        window.Show();
    }

    private void SaveSettings(AppSettings requested)
    {
        AppSettings next = _settingsStore.Validate(requested);
        bool startupChanged = !_smoke && next.StartAtLogin != _settings.StartAtLogin;
        StartupRegistration? snapshot = null;
        if (startupChanged)
        {
            snapshot = _startup.CaptureRegistration();
            _startup.SetEnabled(next.StartAtLogin);
        }
        try { _settingsStore.Save(next); }
        catch (Exception saveError)
        {
            if (startupChanged && snapshot != null)
            {
                try { _startup.RestoreRegistration(snapshot); }
                catch (Exception rollbackError)
                {
                    throw new IOException("Settings could not be saved and startup could not be restored. " + rollbackError.Message, saveError);
                }
            }
            throw;
        }
        _settings = next;
        ApplyAppearance();
        Notice("Settings saved");
    }

    private void Compact_Click(object sender, RoutedEventArgs e) => SetExpanded(!_expanded);
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void PinShelf_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinShelfButton.SetResourceReference(ForegroundProperty, _pinned ? "AccentBrush" : "MutedBrush");
        PinShelfButton.ToolTip = _pinned ? "Allow automatic collapse" : "Keep shelf expanded";
        if (_pinned) _collapseTimer.Stop();
        else if (!IsPointerOverShell() && _settingsWindow == null) _collapseTimer.Start();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetExpanded(false);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _expanded)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void Notice(string text)
    {
        if (_disposed) return;
        StatusText.Text = text;
        _noticeUntil = DateTime.UtcNow.AddSeconds(5);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
        if (Application.Current is { } app && !app.Dispatcher.HasShutdownStarted)
            app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIntentTimers();
        _maintenanceTimer.Stop();
        _shelf.Items.CollectionChanged -= Items_CollectionChanged;
        _shelf.PinnedItemsChanged -= Shelf_PinnedItemsChanged;
        SystemParameters.StaticPropertyChanged -= SystemAnimationPreferenceChanged;
        IsVisibleChanged -= ShelfVisibilityChanged;
        _transition.ProgressChanged -= ApplyRevealProgress;
        _transition.Settled -= Transition_Settled;
        _transition.Dispose();
        if (_windowSource != null && !_windowSource.IsDisposed) _windowSource.RemoveHook(DesktopMessage);
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _cache.ClearSession(_shelf.Items.Where(item => !item.IsPinned));
        if (_smokeRoot != null)
        {
            try { Directory.Delete(_smokeRoot, recursive: true); }
            catch (Exception) { }
        }
    }

    internal async Task<List<string>> RunSmokeTestsAsync(string reportPath)
    {
        if (!_smoke) throw new InvalidOperationException("Smoke tests require the smoke constructor.");
        var results = new List<string>();
        _pinned = true;
        var synthetic = new ShelfItem
        {
            Kind = ShelfItemKind.Text,
            Title = "Smoke item",
            Preview = "Synthetic smoke-only content",
            Payload = "Synthetic smoke-only content",
            CreatedUtc = DateTime.UtcNow
        };
        _shelf.AddCandidates(new[] { synthetic });
        UpdateShelfView();
        if (_shelf.Items.Count != 1) throw new InvalidOperationException("Synthetic item did not enter the shelf.");
        results.Add("PASS: shelf service is bound to the responsive card grid.");

        SetExpanded(true);
        await Task.Delay(MotionEnabled ? 390 : 30);
        if (IsTransitioning || RevealProgress != 1 || !IsBodyInteractive ||
            ShellClip.Rect != ShelfGeometry.Silhouette(1))
            throw new InvalidOperationException("Shelf did not settle at its expanded endpoint.");
        CaptureTo(Path.ChangeExtension(reportPath, ".expanded.png"));
        results.Add("PASS: expanded clip, body interaction and fixed scene settled.");

        SetExpanded(false);
        await Task.Delay(MotionEnabled ? 320 : 30);
        if (IsTransitioning || RevealProgress != 0 || IsBodyInteractive ||
            ShellClip.Rect != ShelfGeometry.Silhouette(0))
            throw new InvalidOperationException("Shelf did not settle at its compact endpoint.");
        CaptureTo(Path.ChangeExtension(reportPath, ".compact.png"));
        results.Add("PASS: compact pill settled without per-frame host placement.");
        results.Add($"PASS: endpoint placement count={PlacementCount}.");
        await RunExtendedSmokeTestsAsync(reportPath, results);
        _shelf.Remove(_shelf.Items.First(item => item.Id == synthetic.Id));
        return results;
    }

    internal void CaptureTo(string path) => CaptureElement(this, path);

    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * 1.5));
        int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * 1.5));
        var bitmap = new RenderTargetBitmap(width, height, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
