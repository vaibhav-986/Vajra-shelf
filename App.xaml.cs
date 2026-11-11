using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VajraShelf;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private int _handlingFatalError;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? smokeReportPath;
        try
        {
            smokeReportPath = ParseSmokeReportPath(e.Args);
        }
        catch (ArgumentException exception)
        {
            ReportError(exception, "startup arguments");
            Environment.ExitCode = 2;
            Shutdown(2);
            return;
        }

        try
        {
            _instanceMutex = new Mutex(
                initiallyOwned: true,
                AppIdentity.MutexName,
                out bool createdNew);
            _ownsMutex = createdNew;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            ReportError(exception, "single-instance initialization");
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        if (!_ownsMutex)
        {
            // The existing instance remains authoritative. No clipboard or file data
            // is sent between processes.
            if (smokeReportPath is not null)
            {
                var exception = new InvalidOperationException(
                    "Another Vajra Shelf instance is already running.");
                ReportError(exception, "smoke test startup");
                WriteSmokeFailure(smokeReportPath, exception);
                Environment.ExitCode = 2;
                Shutdown(2);
            }
            else
            {
                Shutdown(0);
            }
            return;
        }

        try
        {
            bool smokeTest = smokeReportPath is not null;
            MainWindow window = new(smokeTest);
            MainWindow = window;
            window.Show();

            if (smokeTest)
            {
                PrepareSmokeReportDirectory(smokeReportPath!);
                await Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                var results = await window.RunSmokeTestsAsync(smokeReportPath!).ConfigureAwait(true);
                await File.WriteAllLinesAsync(smokeReportPath!, results).ConfigureAwait(true);
                Shutdown(0);
                return;
            }
        }
        catch (Exception exception)
        {
            ReportError(exception, smokeReportPath is null ? "application startup" : "smoke test");
            if (smokeReportPath is not null)
            {
                WriteSmokeFailure(smokeReportPath, exception);
            }
            else
            {
                ShowFatalMessage();
            }

            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }

    internal static string? ParseSmokeReportPath(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            const string prefix = "--smoke-test=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string inlinePath = argument[prefix.Length..].Trim();
                if (inlinePath.Length == 0)
                {
                    throw new ArgumentException("--smoke-test requires a report path.", nameof(args));
                }

                return Path.GetFullPath(inlinePath);
            }

            if (!string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException("--smoke-test requires a report path.", nameof(args));
            }

            return Path.GetFullPath(args[index + 1]);
        }

        return null;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportError(e.Exception, "dispatcher");

        if (Interlocked.Exchange(ref _handlingFatalError, 1) == 0)
        {
            ShowFatalMessage();
            Environment.ExitCode = 1;
            Shutdown(1);
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException("An unknown fatal error occurred.");
        ReportError(exception, "application domain");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportError(e.Exception, "background task");
        e.SetObserved();
    }

    private static void ShowFatalMessage()
    {
        try
        {
            _ = MessageBox.Show(
                "Vajra Shelf encountered an unexpected error and must close. " +
                "A local error log was written without sending any data.",
                AppIdentity.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Error reporting must never replace the original failure.
        }
    }

    private static void ReportError(Exception exception, string context)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppIdentity.DataFolderName,
                "logs");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "errors.log");

            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
            {
                File.Delete(path);
            }

            string entry = $"[{DateTime.UtcNow:O}] {context}{Environment.NewLine}" +
                           exception + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(path, entry);
        }
        catch
        {
            // Logging is best effort and must be safe during process teardown.
        }
    }

    private static void PrepareSmokeReportDirectory(string reportPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void WriteSmokeFailure(string reportPath, Exception exception)
    {
        try
        {
            PrepareSmokeReportDirectory(reportPath);
            File.WriteAllText(
                Path.GetFullPath(reportPath),
                $"FAIL: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch
        {
            // The normal local error log still records a smoke-report failure.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex && _instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // It was already released during an unusual shutdown path.
            }
        }

        _ownsMutex = false;
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        base.OnExit(e);
    }
}
