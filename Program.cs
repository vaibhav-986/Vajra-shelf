using System;

namespace VajraShelf.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        int failed = 0;
        (string Name, Action Run)[] suites =
        {
            ("Item ingestion, dedupe, expiry and drag formats", ItemPipelineTests.Run),
            ("Owned cache containment and source-file safety", CacheSafetyTests.Run),
            ("Atomic settings and pinned-item persistence", PersistenceTests.Run),
            ("Opt-in startup transactions with fake registry", StartupServiceTests.Run),
            ("Accessible Light, Dark and Glass palettes", ThemeTests.Run),
            ("Interruptible shelf geometry and transitions", TransitionTests.Run),
            ("Shell icon and registered hotkey definitions", ShellIntegrationTests.Run)
        };
        foreach (var suite in suites)
        {
            try { suite.Run(); Console.WriteLine("PASS: " + suite.Name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL: " + suite.Name + "\n" + error); }
        }
        Console.WriteLine($"{suites.Length - failed}/{suites.Length} suites passed.");
        return failed == 0 ? 0 : 1;
    }
}
