namespace HomePodCast.Tests;

/// <summary>config.json on disk: a damaged file never costs every setting, and a save that cannot be written never throws into a handler.</summary>
public class ConfigFileTests
{
    private static string NewFolder() => Path.Combine(Path.GetTempPath(), $"hpc-config-{Guid.NewGuid():N}");

    private static AppConfig Good() => new()
    {
        DeviceId = "AA:BB:CC:DD:EE:FF", DeviceName = "卧室", Host = "192.0.2.10", AutoConnect = true, LatencyMs = 250,
        Hotkeys = { [HotkeyAction.VolumeUp] = "Ctrl+Alt+PageUp" },
    };

    [Fact]
    public void A_damaged_file_is_kept_and_the_last_good_start_is_used()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            Good().Save(path);
            Assert.Equal(250, AppConfig.Load(path, keepBackup: true).LatencyMs); // a good start keeps config.json.bak
            Assert.True(File.Exists(path + AppConfig.BackupSuffix));
            var damaged = new byte[4096]; // what a power cut right after an unflushed save can leave: NULs
            File.WriteAllBytes(path, damaged);

            var loaded = AppConfig.Load(path, keepBackup: true);

            Assert.Equal("AA:BB:CC:DD:EE:FF", loaded.DeviceId);
            Assert.Equal(250, loaded.LatencyMs);
            Assert.Equal("Ctrl+Alt+PageUp", loaded.Hotkeys[HotkeyAction.VolumeUp]);
            Assert.True(loaded.AutoConnect); // the user's own setting, for the user's own speaker
            var kept = Assert.Single(Directory.GetFiles(folder, "config.json" + AppConfig.DamagedSuffix + "*"));
            Assert.Equal(damaged, File.ReadAllBytes(kept));
            // config.json holds what this run uses: the next Load (Program's, then TrayApp's) keeps no second copy.
            Assert.Equal(250, AppConfig.Load(path, keepBackup: true).LatencyMs);
            Assert.Single(Directory.GetFiles(folder, "config.json" + AppConfig.DamagedSuffix + "*"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_file_from_a_newer_version_without_a_backup_never_connects_by_itself()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            Directory.CreateDirectory(folder);
            // One enum value this version does not know fails the whole file (a newer build's setting, then a downgrade).
            string newer = """{ "DeviceId": "AA:BB:CC:DD:EE:FF", "AutoConnect": true, "Scene": "SomethingNew" }""";
            File.WriteAllText(path, newer);

            var loaded = AppConfig.Load(path, keepBackup: true);

            Assert.Null(loaded.DeviceId);
            Assert.False(loaded.AutoConnect); // so the first HomePod the scan finds is selected, never connected
            Assert.Equal(newer, File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "config.json" + AppConfig.DamagedSuffix + "*"))));
            Assert.False(AppConfig.Load(path).AutoConnect);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_damaged_backup_also_falls_back_to_defaults_without_auto_connect()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "{ \"DeviceId\": ");
            File.WriteAllText(path + AppConfig.BackupSuffix, "");
            var loaded = AppConfig.Load(path);
            Assert.Null(loaded.DeviceId);
            Assert.False(loaded.AutoConnect);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_missing_file_is_a_fresh_start_even_with_a_backup_beside_it()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            Good().Save(path + AppConfig.BackupSuffix); // the user removed config.json to start over
            Assert.Null(AppConfig.Load(path).DeviceId);
            Assert.Empty(Directory.GetFiles(folder, "config.json" + AppConfig.DamagedSuffix + "*"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_save_leaves_no_temporary_file_and_reads_back()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            Good().Save(path);
            Good().Save(path); // over an existing file
            Assert.Equal(new[] { "config.json" }, Directory.GetFiles(folder).Select(Path.GetFileName));
            Assert.Equal("卧室", AppConfig.Load(path).DeviceName);
            Assert.DoesNotContain("\\u", File.ReadAllText(path)); // still readable by hand
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void The_surround_switch_is_off_in_an_older_file_and_reads_back_when_saved()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            Directory.CreateDirectory(folder);
            // 1.3 wrote no Surround51 (experimental since 1.4): such a file streams stereo as before.
            File.WriteAllText(path, """{ "DeviceId": "AA:BB:CC:DD:EE:FF", "AutoConnect": true, "Scene": "Movie" }""");
            var older = AppConfig.Load(path);
            Assert.False(older.Surround51);
            Assert.True(older.AutoConnect);

            older.Surround51 = true;
            older.Save(path);
            Assert.Contains("\"Surround51\": true", File.ReadAllText(path));
            Assert.True(AppConfig.Load(path).Surround51);
            older.Surround51 = false;
            older.Save(path);
            Assert.False(AppConfig.Load(path).Surround51);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TrySave_never_throws_while_another_program_holds_the_file()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            var cfg = Good();
            cfg.Save(path);
            cfg.LatencyMs = 300;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // no share-delete: the rename fails
            {
                var thrown = Record.Exception(() => cfg.Save(path)); // callers that roll back still see it
                Assert.True(thrown is IOException or UnauthorizedAccessException, thrown?.ToString());
                Assert.False(cfg.TrySave(path));
            }
            Assert.Equal(250, AppConfig.Load(path).LatencyMs);
            Assert.True(cfg.TrySave(path));
            Assert.Equal(300, AppConfig.Load(path).LatencyMs);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TrySave_tries_again_when_the_file_is_held_only_for_a_moment()
    {
        string folder = NewFolder(), path = Path.Combine(folder, "config.json");
        try
        {
            var cfg = Good();
            cfg.Save(path);
            cfg.LatencyMs = 300;
            var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var letGo = new Thread(() => { Thread.Sleep(AppConfig.SaveRetryMs / 2); held.Dispose(); });
            letGo.Start();
            Assert.True(cfg.TrySave(path));
            letGo.Join();
            Assert.Equal(300, AppConfig.Load(path).LatencyMs);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
