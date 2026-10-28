using System.Runtime.CompilerServices;

namespace HomePodCast.Tests;

internal static class TestLanguage
{
    /// <summary>
    /// UI tests assert the source (Simplified Chinese) texts; without this they would follow the
    /// Windows display language of whatever machine runs them.
    /// </summary>
    [ModuleInitializer]
    internal static void UseSourceLanguage()
    {
        L.Use(L.Source);
        // Anything a test saves (dialogs call Config.Save) goes to %APPDATA%\HomePodCast-unit-tests, never
        // to the real config of whoever runs the tests. HOMEPODCAST_TEST_PROFILE names another one, so that test runs in
        // several worktrees at once don't share (and overwrite) one config file.
        string profile = Environment.GetEnvironmentVariable("HOMEPODCAST_TEST_PROFILE") is { Length: > 0 } own ? own : "unit-tests";
        Environment.SetEnvironmentVariable("HOMEPODCAST_PROFILE", profile);
    }
}
