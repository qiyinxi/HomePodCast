using System.Windows.Forms;
using HomePodCast.UI;

namespace HomePodCast.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+PageUp", Keys.Control | Keys.Alt, Keys.PageUp)]
    [InlineData("ctrl+alt+pagedown", Keys.Control | Keys.Alt, Keys.PageDown)]
    [InlineData("Ctrl+Alt+M", Keys.Control | Keys.Alt, Keys.M)]
    [InlineData("Alt+Shift+5", Keys.Alt | Keys.Shift, Keys.D5)]
    [InlineData("Ctrl+F9", Keys.Control, Keys.F9)]
    [InlineData("Ctrl+Alt+=", Keys.Control | Keys.Alt, Keys.Oemplus)]
    [InlineData("Ctrl+Alt+Num7", Keys.Control | Keys.Alt, Keys.NumPad7)]
    public void Parses_and_formats_back(string text, Keys mods, Keys key)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(new Hotkey(mods, key), hk);
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]               // no modifier: would steal plain typing
    [InlineData("Shift+A")]         // Shift alone is not enough
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Alt+Foo")]
    [InlineData("Ctrl+Alt+Shift")]  // modifier as the key
    [InlineData("Win+A")]
    [InlineData("Ctrl+Alt+123")]
    public void Rejects_unusable_combinations(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Defaults_parse_and_stay_off_the_combinations_games_and_rdp_use()
    {
        foreach (var (action, text) in Hotkey.Defaults)
        {
            Assert.True(Hotkey.TryParse(text, out var hk), $"{action}: {text}");
            Assert.True((hk.Modifiers & Keys.Control) != 0 && (hk.Modifiers & Keys.Alt) != 0, text);
            Assert.DoesNotContain(hk.Key, new[] { Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.End, Keys.Home, Keys.Delete });
        }
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, Hotkey.Defaults.Values.Distinct().Count());
    }

    [Fact]
    public void A_combination_held_by_someone_else_reports_unavailable_and_is_released_on_dispose()
    {
        var key = new Hotkey(Keys.Control | Keys.Alt | Keys.Shift, Keys.F24); // no real keyboard has F24
        using (var first = new GlobalHotkeys())
        using (var second = new GlobalHotkeys())
        {
            if (!second.Probe(key)) return; // somebody really holds it: nothing to learn here
            Assert.True(first.Register(HotkeyAction.NextScene, key));
            Assert.False(second.Probe(key));                          // taken → shown as unavailable
            Assert.False(second.Register(HotkeyAction.NextScene, key));
        }
        using var third = new GlobalHotkeys();
        Assert.True(third.Probe(key));                                // let go again
    }
}
