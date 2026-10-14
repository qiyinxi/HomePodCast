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
}
