using System;
using System.IO;
using Microsoft.Win32;

namespace VajraShelf.Services;

public interface IStartupRegistry
{
    StartupRegistration Read(string valueName);
    void Write(string valueName, object value, RegistryValueKind valueKind);
    void Delete(string valueName);
}

public sealed class StartupRegistration
{
    private readonly object? _value;

    public StartupRegistration(bool exists, object? value, RegistryValueKind valueKind)
    {
        Exists = exists;
        _value = CloneValue(value);
        ValueKind = valueKind;
    }

    public bool Exists { get; }
    public object? Value => CloneValue(_value);
    public RegistryValueKind ValueKind { get; }

    internal object? GetValueForRestore() => CloneValue(_value);

    private static object? CloneValue(object? value) => value switch
    {
        byte[] bytes => bytes.Clone(),
        string[] strings => strings.Clone(),
        _ => value
    };
}

public sealed class StartupService
{
    private readonly string _executablePath;
    private readonly IStartupRegistry _registry;

    public StartupService()
        : this(Environment.ProcessPath ?? string.Empty, new WindowsStartupRegistry())
    {
    }

    public StartupService(string executablePath)
        : this(executablePath, new WindowsStartupRegistry())
    {
    }

    public StartupService(string executablePath, IStartupRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executablePath = executablePath.Length == 0
            ? string.Empty
            : Path.GetFullPath(executablePath);
    }

    public StartupService(IStartupRegistry registry, string executablePath)
        : this(executablePath, registry)
    {
    }

    public bool IsEnabled
    {
        get
        {
            if (!IsEligibleExecutable(_executablePath))
                return false;

            StartupRegistration registration = _registry.Read(AppIdentity.StartupValueName);
            return registration.Exists
                && registration.ValueKind == RegistryValueKind.String
                && registration.Value is string command
                && string.Equals(command, BuildRunCommand(_executablePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsEligibleExecutable(_executablePath))
        {
            throw new InvalidOperationException(
                $"Startup can only be changed by {AppIdentity.ExecutableName}, not a dotnet host or another executable.");
        }

        if (!enabled)
        {
            _registry.Delete(AppIdentity.StartupValueName);
            return;
        }

        _registry.Write(
            AppIdentity.StartupValueName,
            BuildRunCommand(_executablePath),
            RegistryValueKind.String);
    }

    public StartupRegistration CaptureRegistration() =>
        _registry.Read(AppIdentity.StartupValueName);

    public void RestoreRegistration(StartupRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!registration.Exists)
        {
            _registry.Delete(AppIdentity.StartupValueName);
            return;
        }

        object? value = registration.GetValueForRestore();
        if (value is null)
            throw new InvalidOperationException("An existing startup registration must have a raw value.");

        _registry.Write(AppIdentity.StartupValueName, value, registration.ValueKind);
    }

    private static bool IsEligibleExecutable(string executablePath) =>
        executablePath.Length > 0
        && Path.IsPathFullyQualified(executablePath)
        && string.Equals(
            Path.GetFileName(executablePath),
            AppIdentity.ExecutableName,
            StringComparison.OrdinalIgnoreCase);

    private static string BuildRunCommand(string executablePath) => $"\"{executablePath}\"";

    private sealed class WindowsStartupRegistry : IStartupRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public StartupRegistration Read(string valueName)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null || !ContainsValue(key, valueName))
                return new StartupRegistration(false, null, RegistryValueKind.Unknown);

            RegistryValueKind kind = key.GetValueKind(valueName);
            object? value = key.GetValue(
                valueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new StartupRegistration(true, value, kind);
        }

        public void Write(string valueName, object value, RegistryValueKind valueKind)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The current-user startup registry key could not be opened.");
            key.SetValue(valueName, value, valueKind);
        }

        public void Delete(string valueName)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        private static bool ContainsValue(RegistryKey key, string valueName)
        {
            foreach (string existingName in key.GetValueNames())
            {
                if (string.Equals(existingName, valueName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
