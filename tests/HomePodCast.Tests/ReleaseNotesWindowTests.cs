using System.Runtime.ExceptionServices;
using System.Drawing;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.Tests;

[Collection("UpdateStatusRow UI")]
public class ReleaseNotesWindowTests
{
    [Fact]
    public void Download_cancel_failure_retry_and_close_share_the_controller_state_without_confirmation()
    {
        OnSta(() =>
        {
            string old = L.Language;
            try
            {
                L.Use("en");
                var release = new Release(ReleaseVersion.Parse("9.0.0")!, "v9.0.0", GitHubReleases.ReleasesPage, false, null,
                    "## New\n\n- **Better** playback.\n## 简体中文\n### 新功能\n- 更好", DateTimeOffset.UtcNow);
                int installs = 0, cancels = 0, skips = 0;
                using var window = new ReleaseNotesWindow(release, new([release], false), "en", () => installs++, () => cancels++, () => skips++, _ => { });
                // This is an isolated control host, never TrayApp or the app's discovery/audio startup.
                window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-20000, -20000);
                window.Opacity = 0; window.Show();
                UpdateView View(bool downloading = false, double progress = 0, string status = "New version") =>
                    new(status, TextRole.Secondary, release, false, downloading, false, true, false, progress);
                window.ShowState(View());
                Assert.Contains("SHA-256", window.ConsentText.Text);
                Assert.Contains("administrator", window.ConsentText.Text);
                Assert.Contains("restarts", window.ConsentText.Text);
                Assert.Contains("minute", window.ConsentText.Text);
                Assert.Equal("Update", window.InstallButton.Text);
                window.InstallButton.PerformClick(); Assert.Equal(1, installs);
                window.ShowState(View(true, .42, "42%"));
                Assert.Equal("Cancel", window.InstallButton.Text); Assert.False(window.SkipButton.Enabled);
                Assert.Equal(.42, window.ProgressBar.Value);
                window.InstallButton.PerformClick(); Assert.Equal(1, cancels);
                foreach (var error in new[] { "Download failed", "SHA-256 mismatch", "Download cancelled" })
                {
                    var state = View(status: error) with { ErrorDetail = error == "Download cancelled" ? null : "HttpRequestException: HTTP 404 Not Found" };
                    window.ShowState(state);
                    Assert.Equal(error, window.StatusText.Text);
                    Assert.Equal(state.ErrorDetail ?? "", window.ErrorText.Text);
                    Assert.Equal(state.ErrorDetail == null, window.ErrorText.Collapsed);
                    Assert.Equal("Update", window.InstallButton.Text); Assert.True(window.SkipButton.Enabled);
                    window.InstallButton.PerformClick();
                }
                Assert.Equal(4, installs); Assert.Equal(0, skips);
                window.ShowState(View(true, .6));
                window.Close();
                Assert.Equal(1, cancels); // closing does not cancel a download
                using var reopened = new ReleaseNotesWindow(release, new([release], false), "en", () => { }, () => cancels++, () => { }, _ => { });
                reopened.ShowState(View(true, .7, "70%"));
                Assert.Equal(.7, reopened.ProgressBar.Value); Assert.Equal("70%", reopened.StatusText.Text);
                Assert.Equal("Cancel", reopened.InstallButton.Text);
            }
            finally { L.Use(old); }
        });
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The isolated notes-window test did not finish.");
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
