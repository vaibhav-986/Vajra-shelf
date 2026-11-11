using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VajraShelf.Services;

/// <summary>Owns the notification-area icon and marshals every callback to WPF.</summary>
public sealed class TrayService : IDisposable
{
    public const string ShowText = "Show";
    public const string AddClipboardText = "Add clipboard";
    public const string ClearTemporaryText = "Clear temporary";
    public const string SettingsText = "Settings";
    public const string ExitText = "Exit";

    private readonly Dispatcher _dispatcher;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayService(
        Dispatcher dispatcher,
        Action show,
        Func<Task> addClipboardAsync,
        Action clearTemporary,
        Action openSettings,
        Action exit)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(addClipboardAsync);
        ArgumentNullException.ThrowIfNull(clearTemporary);
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(exit);

        _icon = ShelfIcon.Create();
        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false
        };

        _menu.Items.Add(CreateItem(ShowText, () => Dispatch(show)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem(AddClipboardText, () => DispatchAsync(addClipboardAsync)));
        _menu.Items.Add(CreateItem(ClearTemporaryText, () => Dispatch(clearTemporary)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem(SettingsText, () => Dispatch(openSettings)));
        _menu.Items.Add(CreateItem(ExitText, () => Dispatch(exit)));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Icon = _icon,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatch(show);
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, Action callback)
    {
        Forms.ToolStripMenuItem item = new(text);
        item.Click += (_, _) => callback();
        return item;
    }

    private void Dispatch(Action callback)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            callback();
        }
        else
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
        }
    }

    private void DispatchAsync(Func<Task> callback)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            InvokeAsync(callback);
        }
        else
        {
            _ = _dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => InvokeAsync(callback)));
        }
    }

    // This returns failures to WPF's DispatcherUnhandledException handler instead
    // of losing them in an unobserved fire-and-forget task.
    private static async void InvokeAsync(Func<Task> callback)
    {
        await callback().ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }
}
