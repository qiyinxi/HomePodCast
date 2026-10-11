using System.Diagnostics;
using System.Net;

namespace HomePodCast.UI;

// The speaker connects back to us (timing, events, resend requests), which Windows Firewall only lets through with
// our rule, and the rule only applies on Private networks. Checked at startup, when a connection is blocked, and after
// a network change; at most one message at a time, each at most once per run (FirewallAdvisor).
internal sealed partial class TrayApp
{
    /// <summary>After an address appears, Windows takes a few seconds to identify the network and give it a profile.</summary>
    private const int NetworkSettleMs = 10_000;

    /// <summary>After Settings was opened: how often, and how long, to look for the network becoming Private.</summary>
    private const int ProfileWatchMs = 3000, ProfileWatchTimes = 60;

    private readonly FirewallAdvisor _firewall = new(Firewall.Inspect, NetworkProfile.ForSpeaker);
    private System.Windows.Forms.Timer? _networkSettle;
    private CancellationTokenSource? _profileWatch;

    /// <summary>Called once from the constructor (UI thread).</summary>
    private void InitFirewall()
    {
        Controller.FirewallBlocked += () => _ui.Post(_ => CheckFirewall(FirewallTrigger.Blocked), null);
        _networkSettle = new System.Windows.Forms.Timer { Interval = NetworkSettleMs };
        _networkSettle.Tick += (_, _) =>
        {
            _networkSettle.Stop();
            if (_wantConnected) CheckFirewall(FirewallTrigger.NetworkChanged);
        };
    }

    private void DisposeFirewall()
    {
        _networkSettle?.Dispose();
        _profileWatch?.Cancel();
    }

    /// <summary>An address changed (UI thread): check the network once things have settled (restarted by every change).</summary>
    private void OnNetworkAddressChanged()
    {
        if (_quitting || !_wantConnected || _networkSettle == null) return;
        _networkSettle.Stop();
        _networkSettle.Start();
    }

    /// <summary>Reads the firewall and the speaker's network on the thread pool (COM), then shows what is wrong, if anything.</summary>
    private void CheckFirewall(FirewallTrigger trigger)
    {
        if (_quitting) return;
        var speaker = IPAddress.TryParse(Config.Host, out var ip) ? ip : null;
        Task.Run(async () =>
        {
            var (prompt, network) = _firewall.Check(trigger, speaker);
            if (trigger == FirewallTrigger.NetworkChanged && prompt == FirewallPrompt.PublicNetwork)
            {
                // A network Windows is still identifying is Public until it knows which one it is: make sure.
                await Task.Delay(NetworkSettleMs);
                (prompt, network) = _firewall.Check(trigger, speaker);
            }
            if (prompt != FirewallPrompt.None) _ui.Post(_ => ShowFirewallPrompt(prompt, network), null);
        });
    }

    private void ShowFirewallPrompt(FirewallPrompt prompt, NetworkProfile network)
    {
        if (_quitting || !_firewall.TryOpen(prompt, network)) return;
        try
        {
            switch (prompt)
            {
                case FirewallPrompt.PublicNetwork:
                    OfferNetworkSettings(network);
                    break;
                case FirewallPrompt.AddRule:
                    OfferFirewallRule();
                    break;
                case FirewallPrompt.OtherBlocker:
                    MessageBox.Show(
                        L.T("Windows 防火墙里已经有放行 HomePodCast 的规则，但音箱的连接还是到不了本程序。\n\n" +
                            "可能是其他防火墙或安全软件拦截了 HomePodCast.exe，也可能是 Windows 防火墙里另有一条阻止它的规则。请在对应的程序或防火墙设置里放行 HomePodCast.exe。"),
                        L.T("需要防火墙放行"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        }
        finally
        {
            _firewall.Close();
        }
    }

    private void OfferFirewallRule()
    {
        var answer = MessageBox.Show(
            L.T("HomePod 需要连回本程序（对时和丢包重传），但 Windows 防火墙还没有放行 HomePodCast。\n\n" +
                "点「是」会弹出管理员授权，添加一条只允许局域网、只在专用网络下生效的入站规则。"),
            L.T("需要防火墙放行"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        if (Firewall.RequestRule())
        {
            Log.Info("firewall rule added");
            if (_wantConnected) Connect();
        }
        else
        {
            MessageBox.Show(L.T("没有添加成功（可能取消了授权）。"), L.T("需要防火墙放行"));
        }
    }

    /// <summary>
    /// The speaker's network is Public. Only the user can say whether it is a network to trust, so we never change its
    /// category ourselves: Yes opens the Settings page where its "Network profile type" is.
    /// </summary>
    private void OfferNetworkSettings(NetworkProfile network)
    {
        var answer = MessageBox.Show(
            L.F("当前网络「{0}」是公用网络。HomePodCast 的防火墙放行只在专用网络下生效，HomePod 连不回本程序，所以无法推送。\n\n" +
                "如果这是你自己家里、可以信任的网络，请在 Windows 设置里把它的“网络配置文件类型”改成“专用网络”。咖啡馆、酒店等公共场所的网络请不要改。\n\n" +
                "点「是」打开 Windows 设置。", network.DisplayName),
            L.T("当前是公用网络"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        string uri = network.SettingsUri;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            Log.Info($"network: opened {uri} for \"{network.DisplayName}\"");
        }
        catch (Exception ex)
        {
            Log.Warn($"open {uri}: {ex.Message}");
            return;
        }
        WatchForPrivate();
    }

    /// <summary>
    /// While the user is in Settings: once the speaker's network is no longer Public, reconnect right away instead of
    /// waiting out the retry backoff (up to 30 s). Gives up after a few minutes.
    /// </summary>
    private async void WatchForPrivate()
    {
        _profileWatch?.Cancel();
        var cts = _profileWatch = new CancellationTokenSource();
        var speaker = IPAddress.TryParse(Config.Host, out var ip) ? ip : null;
        bool changed = false;
        try
        {
            for (int i = 0; i < ProfileWatchTimes; i++)
            {
                await Task.Delay(ProfileWatchMs, cts.Token);
                if (!changed)
                {
                    var now = await Task.Run(() => NetworkProfile.ForSpeaker(speaker), cts.Token);
                    if (now.Category is NetworkCategory.Public or NetworkCategory.Unknown) continue;
                    Log.Info($"network: now {now}");
                    changed = true;
                }
                if (_quitting || !_wantConnected || Controller.State is StreamState.Streaming or StreamState.Idle) return;
                if (Controller.State == StreamState.Retrying)
                {
                    Connect();
                    return;
                }
                // Connecting: an attempt begun before the change may still fail; look again next time.
            }
        }
        catch (OperationCanceledException) { }
    }
}
