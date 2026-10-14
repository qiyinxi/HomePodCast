using System.Net.Http;
using System.Net.Sockets;
using HomePodCast.Update;

namespace HomePodCast.Tests;

/// <summary>A failed update check says what failed, also when the exception's own message is empty.</summary>
public class UpdateErrorTextTests
{
    [Fact]
    public void The_log_line_names_every_level_and_an_empty_message()
    {
        var ex = new HttpRequestException("", new SocketException((int)SocketError.AccessDenied));
        var text = UpdateFlow.Describe(ex);
        Assert.StartsWith("HttpRequestException: (no message) ← SocketException: ", text);
        Assert.DoesNotContain(": ←", text);
    }

    [Fact]
    public void A_single_exception_is_its_type_and_message()
    {
        Assert.Equal("UpdateCheckException: HTTP 403, rate limited until 18:30",
            UpdateFlow.Describe(new UpdateCheckException("HTTP 403, rate limited until 18:30")));
    }

    [Fact]
    public void An_assembly_that_could_not_be_loaded_is_named()
    {
        // 2026-10-14: what the empty "check failed: " was, once the runtime the app ran on had been uninstalled.
        const string http = "System.Net.Http, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
        Assert.Equal($"FileNotFoundException: (no message) [{http}]", UpdateFlow.Describe(new FileNotFoundException("", http)));
        // A message that names the file already is left as it is.
        var named = new FileNotFoundException($"Could not load file or assembly '{http}'.", http);
        Assert.Equal($"FileNotFoundException: {named.Message}", UpdateFlow.Describe(named));
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\", false, true)]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\", true, false)]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12", false, true)]
    // Self-contained (the Store package) or single-file: the runtime is the app's own, never removed under it.
    [InlineData(@"C:\Program Files\WindowsApps\qiyinxi.HomePodCast_1.0.0.0_x64__21rkx5qfj0b6t\", false, false)]
    [InlineData("", false, false)]
    public void A_shared_runtime_without_its_deps_file_was_uninstalled(string folder, bool depsFileThere, bool gone)
    {
        string? asked = null;
        Assert.Equal(gone, AppEnvironment.RuntimeGone(folder, path => { asked = path; return depsFileThere; }));
        if (asked != null) Assert.EndsWith(@"\Microsoft.NETCore.App.deps.json", asked);
    }

    [Fact]
    public void The_running_runtime_is_there()
    {
        Assert.False(AppEnvironment.RuntimeRemoved);
        Assert.NotEmpty(AppEnvironment.RuntimeFolder);
    }

    [Fact]
    public void Only_a_removed_runtime_asks_for_a_restart()
    {
        var (note, status) = UpdateFlow.RuntimeNote(true, @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12\");
        Assert.Equal(@"; the .NET runtime this copy started on (C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.12) " +
                     "has been removed by a .NET update since: restart HomePodCast", note);
        Assert.Equal(".NET 已更新，请重启 HomePodCast 后再试", status);
        Assert.Equal(("", (string?)null), UpdateFlow.RuntimeNote(false, @"C:\x\"));
    }
}
