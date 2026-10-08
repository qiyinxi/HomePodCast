using System.Runtime.CompilerServices;

namespace HomePodCast.Tests;

internal static class TestLanguage
{
    /// <summary>
    /// UI tests assert the source (Simplified Chinese) texts; without this they would follow the
    /// Windows display language of whatever machine runs them.
    /// </summary>
    [ModuleInitializer]
    internal static void UseSourceLanguage() => L.Use(L.Source);
}
