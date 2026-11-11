using System;
using System.IO;
using Microsoft.Win32;
using VajraShelf.Services;

namespace VajraShelf.Tests;

public static class StartupServiceTests
{
    public static void Run()
    {
        ConstructionAndStatusAreReadOnly();
        EnablingIsExplicitAndUsesTheNamedExecutable();
        IneligibleHostsAreRejectedWithoutRegistryWrites();
        RawRegistrationsCanBeRolledBackExactly();
    }

    private static void ConstructionAndStatusAreReadOnly()
    {
        FakeStartupRegistry registry = new();
        string executable = Path.Combine(Path.GetTempPath(), "VajraShelf", AppIdentity.ExecutableName);
        StartupService service = new(executable, registry);

        Assert(registry.MutationCount == 0, "Constructing StartupService wrote to the registry boundary.");
        Assert(!service.IsEnabled, "A missing startup value was reported as enabled.");
        Assert(registry.MutationCount == 0, "Checking startup status wrote to the registry boundary.");
        _ = service.CaptureRegistration();
        Assert(registry.MutationCount == 0, "Capturing startup state wrote to the registry boundary.");
    }

    private static void EnablingIsExplicitAndUsesTheNamedExecutable()
    {
        FakeStartupRegistry registry = new();
        string executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Vajra Shelf", AppIdentity.ExecutableName));
        StartupService service = new(executable, registry);

        service.SetEnabled(true);
        StartupRegistration current = registry.Current;
        Assert(current.Exists, "Explicit startup opt-in did not write a registration.");
        Assert(current.ValueKind == RegistryValueKind.String, "Startup registration was not a REG_SZ value.");
        Assert((string?)current.Value == $"\"{executable}\"", "Startup command was not the quoted executable path.");
        Assert(registry.LastValueName == AppIdentity.StartupValueName, "The startup value name was not VajraShelf.");
        Assert(service.IsEnabled, "The exact startup command was not reported as enabled.");

        service.SetEnabled(false);
        Assert(!registry.Current.Exists, "Explicit startup disable did not remove the registration.");
    }

    private static void IneligibleHostsAreRejectedWithoutRegistryWrites()
    {
        FakeStartupRegistry dotnetRegistry = new();
        StartupService dotnet = new(Path.Combine(Path.GetTempPath(), "dotnet.exe"), dotnetRegistry);
        Assert(!dotnet.IsEnabled, "A dotnet host was reported as an enabled VajraShelf registration.");
        AssertThrows<InvalidOperationException>(() => dotnet.SetEnabled(true),
            "A dotnet host was allowed to opt in to startup.");
        AssertThrows<InvalidOperationException>(() => dotnet.SetEnabled(false),
            "A dotnet host was allowed to mutate startup registration.");
        Assert(dotnetRegistry.MutationCount == 0, "Rejecting a dotnet host wrote to the registry boundary.");

        FakeStartupRegistry otherRegistry = new();
        StartupService other = new(Path.Combine(Path.GetTempPath(), "Other.exe"), otherRegistry);
        AssertThrows<InvalidOperationException>(() => other.SetEnabled(true),
            "A non-VajraShelf executable was allowed to opt in to startup.");
        Assert(otherRegistry.MutationCount == 0, "Rejecting another executable wrote to the registry boundary.");
    }

    private static void RawRegistrationsCanBeRolledBackExactly()
    {
        FakeStartupRegistry registry = new();
        byte[] raw = { 0, 1, 127, 255 };
        registry.Seed(raw, RegistryValueKind.Binary);
        string executable = Path.Combine(Path.GetTempPath(), AppIdentity.ExecutableName);
        StartupService service = new(executable, registry);

        StartupRegistration snapshot = service.CaptureRegistration();
        raw[0] = 99; // The captured snapshot must own the original raw bytes.
        service.SetEnabled(true);
        service.RestoreRegistration(snapshot);

        StartupRegistration restored = registry.Current;
        Assert(restored.Exists && restored.ValueKind == RegistryValueKind.Binary,
            "Rollback did not restore the original registry value kind.");
        Assert(restored.Value is byte[] bytes
            && bytes.Length == 4
            && bytes[0] == 0
            && bytes[1] == 1
            && bytes[2] == 127
            && bytes[3] == 255,
            "Rollback did not restore the exact raw registry bytes.");

        FakeStartupRegistry missingRegistry = new();
        StartupService missingService = new(executable, missingRegistry);
        StartupRegistration missing = missingService.CaptureRegistration();
        missingService.SetEnabled(true);
        missingService.RestoreRegistration(missing);
        Assert(!missingRegistry.Current.Exists, "Rollback of an absent registration did not restore absence.");
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        private StartupRegistration _current = new(false, null, RegistryValueKind.Unknown);

        public int MutationCount { get; private set; }
        public string? LastValueName { get; private set; }
        public StartupRegistration Current => new(_current.Exists, _current.Value, _current.ValueKind);

        public StartupRegistration Read(string valueName)
        {
            LastValueName = valueName;
            return Current;
        }

        public void Write(string valueName, object value, RegistryValueKind valueKind)
        {
            MutationCount++;
            LastValueName = valueName;
            _current = new StartupRegistration(true, value, valueKind);
        }

        public void Delete(string valueName)
        {
            MutationCount++;
            LastValueName = valueName;
            _current = new StartupRegistration(false, null, RegistryValueKind.Unknown);
        }

        public void Seed(object value, RegistryValueKind valueKind)
        {
            _current = new StartupRegistration(true, value, valueKind);
        }
    }
}
