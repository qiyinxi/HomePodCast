using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

// The language table is process-global. These tests must not switch it underneath another UI test.
[CollectionDefinition("ProfilesForm UI", DisableParallelization = true)]
public sealed class ProfilesFormUiCollection { }

/// <summary>Offscreen profile management with injected callbacks and synthetic settings only.</summary>
[Collection("ProfilesForm UI")]
public class ProfilesFormTests
{
    [Fact]
    public void A_new_profile_saves_the_name_and_becomes_the_selected_profile() => OnSta(L.Source, () =>
    {
        var cfg = new AppConfig();
        var saved = new List<(string? Id, string Name)>();
        int deleted = 0;
        using var form = new ProfilesForm(cfg, (id, name) =>
        {
            saved.Add((id, name));
            cfg.UsageProfiles.Add(new UsageProfile { Id = "new-profile", Name = name.Trim() });
        }, (_, _) => false, _ => deleted++);
        ShowInvisible(form);

        var combo = Find<FluentComboBox>(form);
        Assert.False(combo.Enabled);
        Assert.False(Button(form, "应用").Enabled);
        Assert.False(Button(form, "删除").Enabled);
        Find<TextBox>(form).Text = "  游戏和语音  ";
        Button(form, "保存当前设置").PerformClick();

        Assert.Equal([(null, "  游戏和语音  ")], saved);
        Assert.Equal(["游戏和语音"], combo.Items.Cast<string>());
        Assert.Equal(0, combo.SelectedIndex);
        Assert.True(combo.Enabled);
        Assert.True(Button(form, "应用").Enabled);
        Assert.True(Button(form, "删除").Enabled);
        Assert.Equal(0, deleted);
        Snapshot(form, "profiles-created-zh-CN");
    });

    [Fact]
    public void A_user_selection_applies_exactly_that_profile_and_passes_the_dialog_owner() => OnSta(L.Source, () =>
    {
        var cfg = ConfigWithProfiles();
        var applied = new List<(string Id, IWin32Window? Owner)>();
        using var form = new ProfilesForm(cfg, (_, _) => { }, (id, owner) =>
        {
            applied.Add((id, owner));
            return true;
        }, _ => { });
        ShowInvisible(form);
        var combo = Find<FluentComboBox>(form);
        Assert.Equal(-1, combo.SelectedIndex);
        Assert.False(Button(form, "应用").Enabled);

        Assert.True(combo.HandleKey(Keys.End));
        Assert.Equal("夜间电影", Find<TextBox>(form).Text);
        Button(form, "应用").PerformClick();

        var call = Assert.Single(applied);
        Assert.Equal("movie", call.Id);
        Assert.Same(form, call.Owner);
        Assert.Equal(DialogResult.OK, form.DialogResult);
    });

    [Fact]
    public void A_rejected_apply_keeps_the_dialog_and_selection_available() => OnSta(L.Source, () =>
    {
        var cfg = ConfigWithProfiles();
        int applied = 0;
        using var form = new ProfilesForm(cfg, (_, _) => { }, (_, _) => { applied++; return false; }, _ => { });
        ShowInvisible(form);
        var combo = Find<FluentComboBox>(form);
        combo.HandleKey(Keys.Home);
        Button(form, "应用").PerformClick();

        Assert.Equal(1, applied);
        Assert.Equal(DialogResult.None, form.DialogResult);
        Assert.Equal(0, combo.SelectedIndex);
        Assert.Equal("游戏和语音", Find<TextBox>(form).Text);
        Assert.True(Button(form, "应用").Enabled);
    });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_does_not_save_or_apply_a_profile(string name) => OnSta(L.Source, () =>
    {
        var cfg = new AppConfig();
        int saved = 0, applied = 0, deleted = 0;
        using var form = new ProfilesForm(cfg, (_, _) => saved++, (_, _) => { applied++; return true; }, _ => deleted++);
        ShowInvisible(form);
        var input = Find<TextBox>(form);
        input.Text = name;
        Button(form, "保存当前设置").PerformClick();
        Button(form, "应用").PerformClick();
        Button(form, "删除").PerformClick();

        Assert.Equal(0, saved);
        Assert.Equal(0, applied);
        Assert.Equal(0, deleted);
        Assert.Empty(cfg.UsageProfiles);
        Assert.True(input.Focused);
        Assert.False(Button(form, "应用").Enabled);
        Assert.False(Button(form, "删除").Enabled);
    });

    [Fact]
    public void New_clears_an_existing_selection_without_deleting_or_overwriting_it() => OnSta(L.Source, () =>
    {
        var cfg = ConfigWithProfiles();
        int saved = 0, applied = 0, deleted = 0;
        using var form = new ProfilesForm(cfg, (_, _) => saved++, (_, _) => { applied++; return true; }, _ => deleted++);
        ShowInvisible(form);
        var combo = Find<FluentComboBox>(form);
        combo.HandleKey(Keys.Home);
        Assert.True(Button(form, "删除").Enabled);

        Button(form, "新建").PerformClick();

        Assert.Equal(-1, combo.SelectedIndex);
        Assert.Equal("", Find<TextBox>(form).Text);
        Assert.False(Button(form, "应用").Enabled);
        Assert.False(Button(form, "删除").Enabled);
        Assert.Equal(2, cfg.UsageProfiles.Count);
        Assert.Equal((0, 0, 0), (saved, applied, deleted));
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("de")]
    [InlineData("fr")]
    public void Translated_dialog_keeps_long_names_help_and_actions_inside_their_layout(string language) => OnSta(language, () =>
    {
        var cfg = new AppConfig
        {
            UsageProfiles =
            [
                new UsageProfile { Id = "long", Name = "Filmabend im Wohnzimmer und Kopfhörer für die Sprachausgabe" },
                new UsageProfile { Id = "night", Name = "Cinéma du soir et conversation dans le casque sans interruption" },
            ],
        };
        using var form = new ProfilesForm(cfg, (_, _) => { }, (_, _) => false, _ => { });
        ShowInvisible(form);
        Find<FluentComboBox>(form).HandleKey(Keys.End);
        form.PerformLayout();
        Application.DoEvents();

        Assert.Equal(L.T("使用配置"), form.Text);
        Assert.Equal(UsageProfiles.MaxNameLength, Find<TextBox>(form).MaxLength);
        Assert.All(All<Control>(form), control =>
        {
            if (control.Parent is { } parent)
                Assert.True(parent.ClientRectangle.Contains(control.Bounds),
                    $"{language}, {form.DeviceDpi} DPI: {control.GetType().Name} '{control.Text}' extends outside {parent.GetType().Name}.");
        });
        Assert.All(All<FluentButton>(form), button =>
            Assert.True(button.Width >= button.GetPreferredSize(Size.Empty).Width,
                $"{language}, {form.DeviceDpi} DPI: action '{button.Text}' is clipped."));
        Assert.All(All<TextBlock>(form).Where(text => text.Wrap), text =>
            Assert.True(text.Height >= text.GetPreferredSize(new Size(text.Width, 0)).Height,
                $"{language}, {form.DeviceDpi} DPI: wrapped help is clipped."));
        Snapshot(form, "profiles-selected-" + language);
    });

    private static AppConfig ConfigWithProfiles() => new()
    {
        UsageProfiles =
        [
            new UsageProfile { Id = "game", Name = "游戏和语音" },
            new UsageProfile { Id = "movie", Name = "夜间电影" },
        ],
    };

    private static FluentButton Button(Form form, string source) => Find<FluentButton>(form, L.T(source));

    private static T Find<T>(Control root, string? text = null) where T : Control =>
        All<T>(root).Single(c => text == null || c.Text == text);

    private static List<T> All<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(c => (c is T t ? [t] : Array.Empty<T>()).Concat(All<T>(c))).ToList();

    private static void ShowInvisible(Form form)
    {
        // The dialog has no audio, network or settings startup. Show it only to exercise real layout and focus.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
    }

    /// <summary>Same DrawToBitmap approach as GroupFormTests; the filename records actual device DPI.</summary>
    private static void Snapshot(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("HPC_UI_SNAPSHOT_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, $"{name}-dpi{form.DeviceDpi}.png"));
    }

    private static void OnSta(string language, Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string previous = L.Language;
            try { L.Use(language); body(); }
            catch (Exception ex) { error = ex; }
            finally { L.Use(previous); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The synthetic profile dialog test did not finish.");
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
