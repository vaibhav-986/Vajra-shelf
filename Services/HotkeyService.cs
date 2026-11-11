using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VajraShelf.Services;

/// <summary>Registers the two opt-in shell shortcuts for the lifetime of a window.</summary>
public sealed class HotkeyService : IDisposable
{
    public const int ToggleHotkeyId = 0x5653;
    public const int AddClipboardHotkeyId = 0x5053;

    public const uint ModifierAlt = 0x0001;
    public const uint ModifierControl = 0x0002;
    public const uint ModifierNoRepeat = 0x4000;
    public const uint ModifierFlags = ModifierControl | ModifierAlt | ModifierNoRepeat;

    public const uint VirtualKeyV = 0x56;
    public const uint VirtualKeyP = 0x50;

    private const int WmHotkey = 0x0312;

    private readonly Window _window;
    private readonly Action _toggleShelf;
    private readonly Func<Task> _addClipboardAsync;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _toggleRegistered;
    private bool _addRegistered;
    private bool _disposed;

    public HotkeyService(Window window, Action toggleShelf, Func<Task> addClipboardAsync)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _toggleShelf = toggleShelf ?? throw new ArgumentNullException(nameof(toggleShelf));
        _addClipboardAsync = addClipboardAsync ?? throw new ArgumentNullException(nameof(addClipboardAsync));

        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnWindowClosed;

        IntPtr existingHandle = new WindowInteropHelper(_window).Handle;
        if (existingHandle != IntPtr.Zero)
        {
            Initialize(existingHandle);
        }
    }

    public bool ToggleRegistered => _toggleRegistered;
    public bool AddClipboardRegistered => _addRegistered;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Initialize(new WindowInteropHelper(_window).Handle);
    }

    private void Initialize(IntPtr handle)
    {
        if (_disposed || handle == IntPtr.Zero || _source is not null)
        {
            return;
        }

        _handle = handle;
        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            return;
        }

        _source.AddHook(WindowProcedure);

        // Either shortcut can be owned by another application. Keep the one that
        // succeeds and leave the unavailable one disabled without failing startup.
        _toggleRegistered = RegisterHotKey(
            _handle,
            ToggleHotkeyId,
            ModifierFlags,
            VirtualKeyV);
        _addRegistered = RegisterHotKey(
            _handle,
            AddClipboardHotkeyId,
            ModifierFlags,
            VirtualKeyP);
    }

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        int id = unchecked((int)wParam.ToInt64());
        if (id == ToggleHotkeyId && _toggleRegistered)
        {
            handled = true;
            Dispatch(_toggleShelf);
        }
        else if (id == AddClipboardHotkeyId && _addRegistered)
        {
            handled = true;
            DispatchAsync(_addClipboardAsync);
        }

        return IntPtr.Zero;
    }

    private void Dispatch(Action callback)
    {
        Dispatcher dispatcher = _window.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            callback();
        }
        else
        {
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
        }
    }

    private void DispatchAsync(Func<Task> callback)
    {
        Dispatcher dispatcher = _window.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            InvokeAsync(callback);
        }
        else
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => InvokeAsync(callback)));
        }
    }

    // Intentionally async-void: exceptions are returned to WPF's dispatcher and
    // handled by App's last-resort DispatcherUnhandledException policy.
    private static async void InvokeAsync(Func<Task> callback)
    {
        await callback().ConfigureAwait(true);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;

        if (_handle != IntPtr.Zero)
        {
            if (_toggleRegistered)
            {
                _ = UnregisterHotKey(_handle, ToggleHotkeyId);
            }

            if (_addRegistered)
            {
                _ = UnregisterHotKey(_handle, AddClipboardHotkeyId);
            }
        }

        _toggleRegistered = false;
        _addRegistered = false;

        if (_source is not null)
        {
            if (!_source.IsDisposed)
            {
                _source.RemoveHook(WindowProcedure);
            }
            _source = null;
        }

        _handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
