using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using UsageWidget;

internal static class StorageTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    public static int Main()
    {
        string isolated = Path.Combine(Path.GetDirectoryName(typeof(StorageTests).Assembly.Location), "storage-test-data-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        string isolatedRegistry = @"Software\UsageWidget\Tests\storage-" + Guid.NewGuid().ToString("N");
        SettingsStore.StartupRegistrySubKeyOverride = isolatedRegistry;
        try
        {
            WidgetSettings fresh = SettingsStore.Load();
            Check(fresh.RefreshSeconds == 120 && fresh.Width == 320 && fresh.Height == 280 && fresh.LayoutVersion == 2, "Missing settings compact defaults");
            Check(double.IsNaN(fresh.Left) && double.IsNaN(fresh.Top), "New window unpositioned");
            Check(!fresh.CompactView && String.Join(",", fresh.ServiceOrder) == "codex,eleven,openai,claude,gemini,cursor,higgsfield,higgsfield-api" && fresh.CollapsedServices.Length == 0, "New layout preference defaults");
            Check(fresh.Theme == "dark", "New installations use the preferred dark theme");
            Check(fresh.AnimationsEnabled, "New installations enable animations by default");
            Check(fresh.CompactAutoWidth, "New installations automatically fit minibar width to services");
            Check(fresh.CodexWslDistribution == "" && fresh.ClaudeWslDistribution == "", "Legacy installations preserve Windows connections by default");
            Check(fresh.ClaudeWindowsConfigDirectory == "" && !fresh.ClaudeUseExistingStatusLine, "Legacy Claude default directory and explicit custom bridge consent are preserved");
            Check(SettingsStore.LoadApiKey() == "", "Missing key");

            WidgetSettings custom = new WidgetSettings { AlwaysOnTop = false, StartWithWindows = true, RefreshSeconds = 300, Left = -1500, Top = 120.5, Width = 490, Height = 680, CodexPath = @"C:\Program Files\Codex\codex.exe" };
            SettingsStore.Save(custom);
            WidgetSettings loaded = SettingsStore.Load();
            Check(!loaded.AlwaysOnTop && loaded.StartWithWindows && loaded.RefreshSeconds == 300, "Settings round trip");
            Check(loaded.Left == -1500 && loaded.Top == 120.5 && loaded.Width == 490 && loaded.Height == 680, "Multiple-monitor geometry round trip");
            Check(loaded.CodexPath == custom.CodexPath, "Codex path round trip");
            Check(loaded.LayoutVersion == 2, "Current layout version round trip");
            custom.CodexWslDistribution = "Ubuntu"; custom.ClaudeWslDistribution = "kali-linux";
            SettingsStore.Save(custom); loaded = SettingsStore.Load();
            Check(loaded.CodexWslDistribution == "Ubuntu" && loaded.ClaudeWslDistribution == "kali-linux" && loaded.CodexPath == custom.CodexPath, "Each service retains its separate environment and Windows executable path");
            custom.CodexWslDistribution = "bad/target"; SettingsStore.Save(custom);
            Check(SettingsStore.Load().CodexWslDistribution.Length > 0, "Invalid nonempty WSL settings never silently select a Windows account");
            custom.CodexWslDistribution = "Ubuntu";
            custom.ClaudeWindowsConfigDirectory = Path.Combine(isolated,"separate-claude"); custom.ClaudeUseExistingStatusLine = true;
            SettingsStore.Save(custom); loaded = SettingsStore.Load();
            Check(loaded.ClaudeWindowsConfigDirectory == custom.ClaudeWindowsConfigDirectory && loaded.ClaudeUseExistingStatusLine,"Claude explicit directory and custom bridge choice survive restart");
            custom.ClaudeWindowsConfigDirectory = new string('x',32768); SettingsStore.Save(custom);
            Check(SettingsStore.Load().ClaudeWindowsConfigDirectory.Length > 0,"Invalid explicit Claude directory never silently falls back to the default");
            custom.ClaudeWindowsConfigDirectory = ""; custom.ClaudeUseExistingStatusLine = false;

            CheckThemePersistence(isolated, custom);
            CheckAnimationPersistence(isolated);
            CheckCompactGeometryPersistence(isolated);
            CheckCompactAutoWidthPersistence(isolated);

            custom.CompactView = true;
            custom.ServiceOrder = new string[] { "cursor", "eleven", "codex", "gemini", "claude", "openai" };
            custom.CollapsedServices = new string[] { "eleven", "openai" };
            SettingsStore.Save(custom);
            loaded = SettingsStore.Load();
            Check(loaded.CompactView && String.Join(",", loaded.ServiceOrder) == "cursor,eleven,codex,gemini,claude,openai,higgsfield,higgsfield-api", "Compact mode and reordered services survive restart");
            Check(String.Join(",", loaded.CollapsedServices) == "eleven,openai", "Collapsed services survive restart independently of order");
            custom.ServiceOrder = new string[] { " cursor ", "CURSOR", "invalid", null, "Codex" };
            custom.CollapsedServices = new string[] { " ELEVEN ", "eleven", "unknown", null };
            SettingsStore.Save(custom);
            loaded = SettingsStore.Load();
            Check(String.Join(",", loaded.ServiceOrder) == "cursor,codex,eleven,openai,claude,gemini,higgsfield,higgsfield-api", "Order removes duplicate and unsupported IDs and appends missing services");
            Check(String.Join(",", loaded.CollapsedServices) == "eleven", "Collapse preferences normalize IDs without hiding missing services");

            File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"Width\":370,\"Height\":690,\"AlwaysOnTop\":false,\"RefreshSeconds\":300}");
            loaded = SettingsStore.Load();
            Check(loaded.LayoutVersion == 0 && loaded.Width == 370 && loaded.Height == 690 && !loaded.AlwaysOnTop && loaded.RefreshSeconds == 300, "Legacy layout marked for window migration without changing preferences");
            Check(!loaded.CompactView && String.Join(",", loaded.ServiceOrder) == "codex,eleven,openai,claude,gemini,cursor,higgsfield,higgsfield-api" && loaded.CollapsedServices.Length == 0, "Legacy files receive layout preference defaults");
            Check(loaded.Theme == "dark", "Existing installations without a theme use dark without disturbing layout migration");
            File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"CompactView\":\"true\",\"ServiceOrder\":[\"gemini\",42,null,\"gemini\",\"bad\",\"eleven\"],\"CollapsedServices\":\"codex\"}");
            loaded = SettingsStore.Load();
            Check(!loaded.CompactView && String.Join(",", loaded.ServiceOrder) == "gemini,eleven,codex,openai,claude,cursor,higgsfield,higgsfield-api", "Malformed layout fields recover independently while retaining valid service order");
            Check(loaded.CollapsedServices.Length == 0, "A malformed collapse field cannot hide all services");
            SettingsStore.Save(new WidgetSettings { Width = 280, Height = 260, LayoutVersion = 1 });
            loaded = SettingsStore.Load();
            Check(loaded.Width == 280 && loaded.Height == 260 && loaded.LayoutVersion == 1, "Compact minimum dimensions and prior version preserved");
            SettingsStore.Save(new WidgetSettings { Width = 320, Height = 220, FitContentHeight = false });
            loaded = SettingsStore.Load();
            Check(loaded.Height == 220 && !loaded.FitContentHeight,
                "A new smaller normal height is preserved without replacing a user's fixed-height choice");

            SettingsStore.Save(new WidgetSettings { Width = double.PositiveInfinity, Height = -2, RefreshSeconds = 0, Left = double.NaN, Top = 1e10 });
            string json = File.ReadAllText(Path.Combine(isolated, "settings.json"));
            Check(json.IndexOf("NaN", StringComparison.Ordinal) < 0 && json.IndexOf("Infinity", StringComparison.Ordinal) < 0, "Standards-compliant JSON numbers");
            new JavaScriptSerializer().DeserializeObject(json);
            loaded = SettingsStore.Load();
            Check(loaded.Width == 320 && loaded.Height == 280 && loaded.RefreshSeconds == 120, "Invalid values normalized");
            Check(double.IsNaN(loaded.Left) && double.IsNaN(loaded.Top), "Invalid position normalized");

            File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"Width\":\"999\",\"Height\":null,\"RefreshSeconds\":3.5,\"AlwaysOnTop\":\"false\",\"Left\":-200}");
            loaded = SettingsStore.Load();
            Check(loaded.Width == 320 && loaded.Height == 280 && loaded.RefreshSeconds == 120 && loaded.AlwaysOnTop && loaded.Left == -200, "Invalid JSON field types ignored independently");
            string settingsPath = Path.Combine(isolated, "settings.json");
            File.WriteAllText(settingsPath, "{not json");
            Check(SettingsStore.Load().RefreshSeconds == 120, "Corrupt settings recovery");
            Check(!File.Exists(settingsPath) && Directory.GetFiles(isolated, "settings.unreadable-*.json").Length == 1, "Corrupt settings are moved aside instead of being overwritten by defaults");
            File.WriteAllText(settingsPath, "null");
            Check(SettingsStore.Load().Width == 320, "Null settings recovery");
            File.WriteAllText(settingsPath, new String('x', 65537));
            Check(SettingsStore.Load().Width == 320, "Oversized settings recovery");
            Check(Directory.GetFiles(isolated, "settings.unreadable-*.json").Length == 3, "Null and oversized settings are preserved too");
            SettingsStore.Save(new WidgetSettings { RefreshSeconds = 600 });
            Check(SettingsStore.Load().RefreshSeconds == 600, "Settings save normally after an unreadable file was preserved");
            using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(SettingsStore.Load().RefreshSeconds == 120, "A settings file that stays locked loads defaults for display");
            bool refused = false;
            try { SettingsStore.Save(new WidgetSettings()); } catch (IOException) { refused = true; }
            Check(refused && SettingsStore.Load().RefreshSeconds == 600, "Defaults from a failed read never overwrite the real settings file");
            SettingsStore.Save(new WidgetSettings { RefreshSeconds = 600, Width = 400 });
            Check(SettingsStore.Load().Width == 400, "Saving resumes once the settings file has been read");
            using (var held = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var release = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(120); held.Dispose(); });
                release.Start();
                try { Check(SettingsStore.Load().Width == 400, "A briefly locked settings file is read after a retry"); }
                finally { release.Join(); }
            }

            const string testKey = "synthetic-test-key-12345-never-a-real-credential";
            SettingsStore.SaveApiKey("  " + testKey + "  ");
            Check(SettingsStore.LoadApiKey() == testKey, "DPAPI key round trip and trimming");
            byte[] encrypted = File.ReadAllBytes(Path.Combine(isolated, "elevenlabs-key.dat"));
            Check(Encoding.UTF8.GetString(encrypted).IndexOf(testKey, StringComparison.Ordinal) < 0, "API key not plaintext at rest");
            SettingsStore.SaveApiKey("replacement-test-key");
            Check(SettingsStore.LoadApiKey() == "replacement-test-key", "Atomic secret replacement");
            Check(Directory.GetFiles(isolated, "*.tmp").Length == 0, "No staging files left");
            File.WriteAllText(Path.Combine(isolated, "elevenlabs-key.dat"), "corrupt secret");
            Check(SettingsStore.LoadApiKey() == "", "Corrupt secret recovery");
            File.WriteAllBytes(Path.Combine(isolated, "elevenlabs-key.dat"), new byte[0]);
            Check(SettingsStore.LoadApiKey() == "", "Empty secret recovery");
            bool excessiveKeyRejected = false;
            try { SettingsStore.SaveApiKey(new String('x', 8193)); }
            catch (ArgumentException) { excessiveKeyRejected = true; }
            Check(excessiveKeyRejected, "Excessive key rejected");
            SettingsStore.SaveApiKey(null);
            Check(!File.Exists(Path.Combine(isolated, "elevenlabs-key.dat")), "Empty key removes encrypted file");
            Check(SettingsStore.LoadApiKey() == "", "Removed key stays empty");

            CheckStartupReconciliation(isolatedRegistry);
            SettingsStore.Save(new WidgetSettings { FitContentHeight = false });
            Check(!SettingsStore.Load().FitContentHeight,"Manual height preference survives restart");
            SettingsStore.Save(new WidgetSettings { FitContentHeight = true });
            Check(SettingsStore.Load().FitContentHeight,"Automatic height preference survives restart");

            Console.WriteLine("Storage tests passed: " + assertions + " assertions. No real settings or startup registry were changed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Storage test failed: " + ex);
            return 1;
        }
        finally
        {
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            if (isolatedRegistry.StartsWith(@"Software\UsageWidget\Tests\storage-", StringComparison.Ordinal)
                && isolatedRegistry.Length == @"Software\UsageWidget\Tests\storage-".Length + 32)
                Registry.CurrentUser.DeleteSubKeyTree(isolatedRegistry, false);
            string testParent = Path.GetFullPath(Path.GetDirectoryName(typeof(StorageTests).Assembly.Location)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullIsolated = Path.GetFullPath(isolated);
            if (fullIsolated.StartsWith(testParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fullIsolated).StartsWith("storage-test-data-", StringComparison.Ordinal) && Directory.Exists(fullIsolated))
                Directory.Delete(fullIsolated, true);
        }
    }

    private static void CheckThemePersistence(string isolated, WidgetSettings custom)
    {
        foreach (string theme in new string[] { "light", "dark" })
        {
            custom.Theme = theme;
            SettingsStore.Save(custom);
            WidgetSettings loaded = SettingsStore.Load();
            Check(loaded.Theme == theme, "Selected " + theme + " theme survives restart");
            Check(loaded.AlwaysOnTop == custom.AlwaysOnTop && loaded.StartWithWindows == custom.StartWithWindows
                && loaded.RefreshSeconds == custom.RefreshSeconds && loaded.CodexPath == custom.CodexPath,
                "Changing theme preserves connection, refresh, and startup preferences");
            Check(loaded.Left == custom.Left && loaded.Top == custom.Top && loaded.Width == custom.Width
                && loaded.Height == custom.Height && loaded.LayoutVersion == custom.LayoutVersion,
                "Changing theme preserves saved window geometry and migration version");
            System.Collections.Generic.Dictionary<string, object> saved = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(Path.Combine(isolated, "settings.json")));
            Check((string)saved["Theme"] == theme, "Theme is persisted using an exact supported value");
        }

        custom.Theme = "  LIGHT ";
        SettingsStore.Save(custom);
        Check(SettingsStore.Load().Theme == "light", "Saving theme normalizes case and surrounding whitespace");
        foreach (string invalidTheme in new string[] { null, "", "unknown" })
        {
            custom.Theme = invalidTheme;
            SettingsStore.Save(custom);
            Check(SettingsStore.Load().Theme == "dark", "Saving an unsupported theme falls back to dark");
        }

        foreach (string themeJson in new string[] { "null", "42", "true", "[]", "{}", "\"unknown\"" })
        {
            File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"Theme\":" + themeJson + ",\"CompactView\":true,\"ServiceOrder\":[\"eleven\",\"codex\"],\"CollapsedServices\":[\"codex\"],\"RefreshSeconds\":300}");
            WidgetSettings loaded = SettingsStore.Load();
            Check(loaded.Theme == "dark" && loaded.CompactView && loaded.RefreshSeconds == 300
                && loaded.ServiceOrder[0] == "eleven" && String.Join(",", loaded.CollapsedServices) == "codex",
                "Malformed theme recovers independently while preserving valid layout and refresh preferences");
        }
        File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"Theme\":\" LiGhT \"}");
        Check(SettingsStore.Load().Theme == "light", "Loading theme normalizes case and surrounding whitespace");
        custom.Theme = "dark";
    }

    private static void CheckAnimationPersistence(string isolated)
    {
        WidgetSettings custom = new WidgetSettings
        {
            Theme = "light", CompactView = true, RefreshSeconds = 300,
            ServiceOrder = new string[] { "eleven", "codex", "cursor", "openai", "claude", "gemini", "higgsfield", "higgsfield-api" },
            CollapsedServices = new string[] { "codex", "cursor" }
        };
        foreach (bool enabled in new bool[] { false, true })
        {
            custom.AnimationsEnabled = enabled;
            SettingsStore.Save(custom);
            WidgetSettings loaded = SettingsStore.Load();
            Check(loaded.AnimationsEnabled == enabled, "Animation preference survives restart when " + enabled);
            Check(loaded.Theme == "light" && loaded.CompactView && loaded.RefreshSeconds == 300
                && String.Join(",", loaded.ServiceOrder) == String.Join(",", custom.ServiceOrder)
                && String.Join(",", loaded.CollapsedServices) == "codex,cursor",
                "Changing animation preference preserves theme, service order, collapse and refresh settings");
        }

        string retained = "\"Theme\":\"light\",\"CompactView\":true,\"ServiceOrder\":[\"eleven\",\"codex\"],\"CollapsedServices\":[\"codex\"],\"RefreshSeconds\":300";
        foreach (string animationJson in new string[] { null, "null", "0", "\"false\"", "[]", "{}" })
        {
            string field = animationJson == null ? "" : "\"AnimationsEnabled\":" + animationJson + ",";
            File.WriteAllText(Path.Combine(isolated, "settings.json"), "{" + field + retained + "}");
            WidgetSettings loaded = SettingsStore.Load();
            Check(loaded.AnimationsEnabled, "Missing or invalid animation preference falls back to enabled");
            Check(loaded.Theme == "light" && loaded.CompactView && loaded.RefreshSeconds == 300
                && loaded.ServiceOrder[0] == "eleven" && String.Join(",", loaded.CollapsedServices) == "codex",
                "Invalid animation preference recovers without losing existing appearance and service settings");
        }
    }

    private static void CheckCompactAutoWidthPersistence(string isolated)
    {
        string path = Path.Combine(isolated, "settings.json");
        File.WriteAllText(path, "{\"CompactView\":true,\"CompactWidth\":900,\"Width\":370,\"Height\":690}");
        WidgetSettings loaded = SettingsStore.Load();
        Check(loaded.CompactAutoWidth && loaded.CompactView && loaded.CompactWidth == 900
            && loaded.Width == 370 && loaded.Height == 690,
            "Existing minibar settings default to automatic width without changing saved bounds");
        loaded.CompactAutoWidth = false;
        SettingsStore.Save(loaded);
        loaded = SettingsStore.Load();
        Check(!loaded.CompactAutoWidth && loaded.CompactWidth == 900 && loaded.Width == 370 && loaded.Height == 690,
            "Manual minibar width choice and independent window dimensions survive restart");
        loaded.CompactAutoWidth = true;
        SettingsStore.Save(loaded);
        Check(SettingsStore.Load().CompactAutoWidth, "Re-enabled automatic minibar width survives restart");
        foreach (string malformed in new[] { "null", "\"false\"", "0", "[]", "{}" }) {
            File.WriteAllText(path, "{\"CompactAutoWidth\":" + malformed + ",\"CompactWidth\":740,\"CompactView\":true}");
            loaded = SettingsStore.Load();
            Check(loaded.CompactAutoWidth && loaded.CompactWidth == 740 && loaded.CompactView,
                "Malformed automatic width field safely defaults without discarding other preferences: " + malformed);
        }
    }

    private static void CheckCompactGeometryPersistence(string isolated)
    {
        // Files from the previous compact layout must keep their normal dimensions.
        File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"CompactView\":true,\"LayoutVersion\":2,\"Left\":-1220,\"Top\":120,\"Width\":410,\"Height\":580}");
        WidgetSettings loaded = SettingsStore.Load();
        Check(loaded.CompactView && loaded.Width == 410 && loaded.Height == 580 && loaded.Left == -1220 && loaded.Top == 120,
            "Legacy compact settings retain normal geometry without a destructive migration");
        Check(double.IsNaN(loaded.CompactLeft) && double.IsNaN(loaded.CompactTop) && loaded.CompactWidth == 420,
            "Legacy compact settings receive an independent unpositioned minibar");
        loaded.CompactLeft = 180; loaded.CompactTop = 36; loaded.CompactWidth = 940;
        SettingsStore.Save(loaded);
        loaded = SettingsStore.Load();
        Check(loaded.CompactLeft == 180 && loaded.CompactTop == 36 && loaded.CompactWidth == 940,
            "Minibar position and width survive restart");
        Check(loaded.Left == -1220 && loaded.Top == 120 && loaded.Width == 410 && loaded.Height == 580,
            "Saving compact geometry never replaces normal bounds with minibar bounds");
        loaded.CompactView = false;
        SettingsStore.Save(loaded);
        loaded = SettingsStore.Load();
        Check(!loaded.CompactView && loaded.CompactWidth == 940 && loaded.Height == 580,
            "Returning to normal mode retains both geometries");
        loaded.CompactWidth = 1800;
        SettingsStore.Save(loaded);
        Check(SettingsStore.Load().CompactWidth == 1800, "Multi-service minibar can retain a width greater than the normal-mode maximum");

        foreach (double invalid in new double[] { double.NaN, double.PositiveInfinity, -1, 239, 20001 }) {
            loaded.CompactWidth = invalid;
            loaded.CompactLeft = double.NegativeInfinity; loaded.CompactTop = 100001;
            SettingsStore.Save(loaded);
            WidgetSettings normalized = SettingsStore.Load();
            Check(normalized.CompactWidth == 420 && double.IsNaN(normalized.CompactLeft) && double.IsNaN(normalized.CompactTop),
                "Invalid compact geometry falls back independently");
            Check(normalized.Width == 410 && normalized.Height == 580, "Invalid compact geometry does not reset normal dimensions");
            string json = File.ReadAllText(Path.Combine(isolated, "settings.json"));
            Check(!json.Contains("NaN") && !json.Contains("Infinity"), "Compact geometry serializes as standards-compliant JSON");
        }
        File.WriteAllText(Path.Combine(isolated, "settings.json"), "{\"CompactLeft\":\"10\",\"CompactTop\":true,\"CompactWidth\":null,\"Width\":400,\"Height\":600}");
        loaded = SettingsStore.Load();
        Check(double.IsNaN(loaded.CompactLeft) && double.IsNaN(loaded.CompactTop) && loaded.CompactWidth == 420 && loaded.Width == 400 && loaded.Height == 600,
            "Malformed compact fields recover without losing normal geometry");
    }

    private static void CheckStartupReconciliation(string isolatedRegistry)
    {
        Check(SettingsStore.ReadStartupCommand() == null && !SettingsStore.IsStartupAtCurrentPath(), "Absent startup command reads as disabled");
        using (RegistryKey missing = Registry.CurrentUser.OpenSubKey(isolatedRegistry, false))
            Check(missing == null, "Reading startup status creates no registry key");
        SettingsStore.SetStartup(false);
        using (RegistryKey missing = Registry.CurrentUser.OpenSubKey(isolatedRegistry, false))
            Check(missing == null, "Disabling absent startup creates no registry key");

        const string oldCommand = "\"C:\\Previous Folder\\UsageWidget.exe\"";
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(isolatedRegistry))
        {
            key.SetValue("UsageWidget", oldCommand, RegistryValueKind.String);
            key.SetValue("UnrelatedApplication", "untouched", RegistryValueKind.String);
        }
        Check(SettingsStore.ReadStartupCommand() == oldCommand && !SettingsStore.IsStartupAtCurrentPath(), "Relocated executable is detected without modifying its saved command");
        SettingsStore.Save(new WidgetSettings { StartWithWindows = true });
        SettingsStore.Load();
        Check(SettingsStore.ReadStartupCommand() == oldCommand, "Saving and loading preferences never mutates startup registration");
        string snapshot = SettingsStore.ReadStartupCommand();
        SettingsStore.SetStartup(true);
        Check(SettingsStore.IsStartupAtCurrentPath(), "Explicit enable repairs relocated startup path");
        Check(SettingsStore.ReadStartupCommand() == "\"" + Path.GetFullPath(typeof(StorageTests).Assembly.Location) + "\"", "Startup executable path is quoted");
        SettingsStore.RestoreStartupCommand(snapshot);
        Check(SettingsStore.ReadStartupCommand() == oldCommand, "Failed settings transaction can restore previous relocated command");
        SettingsStore.SetStartup(false);
        Check(SettingsStore.ReadStartupCommand() == null, "Explicit disable removes stale command after relocation");
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(isolatedRegistry, false))
            Check((string)key.GetValue("UnrelatedApplication") == "untouched", "Reconciliation leaves other startup values untouched");

        SettingsStore.SetStartup(true);
        SettingsStore.RestoreStartupCommand(null);
        Check(SettingsStore.ReadStartupCommand() == null, "Rollback can restore absent startup registration");
        SettingsStore.RestoreStartupCommand("");
        Check(SettingsStore.ReadStartupCommand() == "", "An existing empty command differs from an absent registration");
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(isolatedRegistry, true))
            key.SetValue("UsageWidget", @"%TEMP%\Previous Folder\UsageWidget.exe", RegistryValueKind.ExpandString);
        Check(SettingsStore.ReadStartupCommand() == @"%TEMP%\Previous Folder\UsageWidget.exe", "Command snapshots are read without environment expansion");
        SettingsStore.SetStartup(false);
    }
}
