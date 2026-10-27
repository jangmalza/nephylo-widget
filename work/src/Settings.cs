using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace UsageWidget
{
    public sealed class WidgetSettings
    {
        internal const double MinimumHeight = 220;
        internal const double DefaultHeight = 280;
        public bool AlwaysOnTop = true;
        public bool StartWithWindows = false;
        public int RefreshSeconds = 120;
        public double Left = double.NaN;
        public double Top = double.NaN;
        public double Width = 320;
        public double Height = DefaultHeight;
        // Normal and compact bounds are independent. Compact mode never persists its
        // fixed 44-DIP height into the normal window's saved dimensions.
        public double CompactLeft = double.NaN;
        public double CompactTop = double.NaN;
        public double CompactWidth = 420;
        // Dashboard bounds are independent of the widget and its compact bar.
        public double DashboardLeft = double.NaN;
        public double DashboardTop = double.NaN;
        public double DashboardWidth = 1100;
        public double DashboardHeight = 760;
        public bool DashboardSidebarCollapsed = false;
        public string[] DashboardFavorites = new string[0];
        public Dictionary<string,string> DashboardPreferredMetrics = new Dictionary<string,string>(StringComparer.Ordinal);
        public int LayoutVersion = 2;
        public string CodexPath = "";
        public string CodexWslDistribution = "";
        public string ClaudeWslDistribution = "";
        public string ClaudeWindowsConfigDirectory = "";
        public bool ClaudeUseExistingStatusLine = false;
        public bool CompactView = false;
        public bool CompactAutoWidth = true;
        public bool FitContentHeight = true;
        public bool AnimationsEnabled = true;
        public bool AutoUpdateEnabled = true;
        public string Theme = "dark";
        public string Language = "ko";
        public string[] ServiceOrder = new string[] { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
        public string[] CollapsedServices = new string[0];
    }

    public static class SettingsStore
    {
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValue = "UsageWidget";
        private static readonly object Gate = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UsageWidget.ElevenLabs.ApiKey.v1");
        private static readonly string[] ServiceIds = new string[] { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };

        // Tests compile into the same assembly and point this at an isolated directory.
        // The application never sets it, and no external environment variable can redirect storage.
        internal static string DataDirectoryOverride = null;
        internal static string StartupRegistrySubKeyOverride = null;
        // Set when settings.json exists but could not be read; Save refuses that path.
        private static string blockedSettingsPath = null;

        private static string StartupRegistrySubKey
        {
            get
            {
                if (String.IsNullOrEmpty(StartupRegistrySubKeyOverride)) return StartupKey;
                // A test override cannot target Run or another application's registry values.
                if (!StartupRegistrySubKeyOverride.StartsWith(@"Software\UsageWidget\Tests\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The startup test registry location is invalid.");
                return StartupRegistrySubKeyOverride;
            }
        }

        public static string DataDirectory
        {
            get
            {
                if (!String.IsNullOrEmpty(DataDirectoryOverride))
                    return Path.GetFullPath(DataDirectoryOverride);
                return WidgetDataProfile.DataDirectory;
            }
        }

        // Executables and signed update staging keep their existing installation contract.
        public static string InstallDirectory
        {
            get { return !String.IsNullOrEmpty(DataDirectoryOverride) ? Path.GetFullPath(DataDirectoryOverride) : WidgetDataProfile.LegacyInstallationDirectory; }
        }

        public static WidgetSettings Load()
        {
            lock (Gate)
            {
                WidgetSettings result = new WidgetSettings();
                string path = Path.Combine(DataDirectory, "settings.json");
                if (String.Equals(blockedSettingsPath, path, StringComparison.OrdinalIgnoreCase))
                    blockedSettingsPath = null;
                try
                {
                    if (!File.Exists(path))
                        return result;
                    if (new FileInfo(path).Length > 65536)
                        return PreserveUnreadable(path);
                    JavaScriptSerializer serializer = CreateSerializer();
                    Dictionary<string, object> values = serializer.Deserialize<Dictionary<string, object>>(ReadText(path));
                    if (values == null)
                        return PreserveUnreadable(path);
                    result.AlwaysOnTop = ReadBoolean(values, "AlwaysOnTop", result.AlwaysOnTop);
                    result.StartWithWindows = ReadBoolean(values, "StartWithWindows", result.StartWithWindows);
                    result.CompactView = ReadBoolean(values, "CompactView", result.CompactView);
                    result.CompactAutoWidth = ReadBoolean(values, "CompactAutoWidth", result.CompactAutoWidth);
                    result.FitContentHeight = ReadBoolean(values, "FitContentHeight", result.FitContentHeight);
                    result.AnimationsEnabled = ReadBoolean(values, "AnimationsEnabled", result.AnimationsEnabled);
                    result.AutoUpdateEnabled = ReadBoolean(values, "AutoUpdateEnabled", result.AutoUpdateEnabled);
                    result.ServiceOrder = ReadServiceIds(values, "ServiceOrder");
                    result.CollapsedServices = ReadServiceIds(values, "CollapsedServices");
                    result.RefreshSeconds = ReadInterval(values, result.RefreshSeconds);
                    result.Left = ReadNumber(values, "Left", double.NaN);
                    result.Top = ReadNumber(values, "Top", double.NaN);
                    result.Width = ReadNumber(values, "Width", result.Width);
                    result.Height = ReadNumber(values, "Height", result.Height);
                    result.CompactLeft = ReadNumber(values, "CompactLeft", double.NaN);
                    result.CompactTop = ReadNumber(values, "CompactTop", double.NaN);
                    result.CompactWidth = ReadNumber(values, "CompactWidth", result.CompactWidth);
                    result.DashboardLeft = ReadNumber(values, "DashboardLeft", double.NaN);
                    result.DashboardTop = ReadNumber(values, "DashboardTop", double.NaN);
                    result.DashboardWidth = ReadNumber(values, "DashboardWidth", result.DashboardWidth);
                    result.DashboardHeight = ReadNumber(values, "DashboardHeight", result.DashboardHeight);
                    result.DashboardSidebarCollapsed = ReadBoolean(values, "DashboardSidebarCollapsed", false);
                    result.DashboardFavorites = ReadServiceIds(values, "DashboardFavorites");
                    result.DashboardPreferredMetrics = ReadDashboardPreferredMetrics(values);
                    result.LayoutVersion = ReadLayoutVersion(values);
                    object value;
                    if (values.TryGetValue("Theme", out value) && value is string)
                        result.Theme = (string)value;
                    if (values.TryGetValue("Language", out value) && value is string)
                        result.Language = (string)value;
                    if (values.TryGetValue("CodexPath", out value) && value is string)
                        result.CodexPath = (string)value;
                    if (values.TryGetValue("CodexWslDistribution", out value) && value is string)
                        result.CodexWslDistribution = (string)value;
                    if (values.TryGetValue("ClaudeWslDistribution", out value) && value is string)
                        result.ClaudeWslDistribution = (string)value;
                    if (values.TryGetValue("ClaudeWindowsConfigDirectory", out value) && value is string)
                        result.ClaudeWindowsConfigDirectory = (string)value;
                    result.ClaudeUseExistingStatusLine = ReadBoolean(values, "ClaudeUseExistingStatusLine", false);
                    return Normalize(result);
                }
                catch (FileNotFoundException) { return new WidgetSettings(); }
                catch (DirectoryNotFoundException) { return new WidgetSettings(); }
                catch (IOException) { blockedSettingsPath = path; return new WidgetSettings(); }
                catch (UnauthorizedAccessException) { blockedSettingsPath = path; return new WidgetSettings(); }
                catch (ArgumentException) { return PreserveUnreadable(path); }
                catch (InvalidOperationException) { return PreserveUnreadable(path); }
            }
        }

        // A scanner or sync client may briefly hold the file without read sharing.
        private static string ReadText(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return File.ReadAllText(path, Encoding.UTF8); }
                catch (IOException error)
                {
                    int code = error.HResult & 0xffff;
                    if (attempt >= 4 || (code != 32 && code != 33)) throw;
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
            }
        }

        // Keeps an unparseable file under a new name so defaults cannot replace it.
        // If it cannot be moved, saving stays blocked for this run instead.
        private static WidgetSettings PreserveUnreadable(string path)
        {
            string preserved = Path.Combine(Path.GetDirectoryName(path), "settings.unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            try { File.Move(path, preserved); }
            catch (IOException) { blockedSettingsPath = path; }
            catch (UnauthorizedAccessException) { blockedSettingsPath = path; }
            return new WidgetSettings();
        }

        public static void Save(WidgetSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");
            WidgetSettings normalized = Normalize(settings);
            Dictionary<string, object> values = new Dictionary<string, object>();
            values["AlwaysOnTop"] = normalized.AlwaysOnTop;
            values["StartWithWindows"] = normalized.StartWithWindows;
            values["CompactView"] = normalized.CompactView;
            values["CompactAutoWidth"] = normalized.CompactAutoWidth;
            values["FitContentHeight"] = normalized.FitContentHeight;
            values["AnimationsEnabled"] = normalized.AnimationsEnabled;
            values["AutoUpdateEnabled"] = normalized.AutoUpdateEnabled;
            values["Theme"] = normalized.Theme;
            values["Language"] = normalized.Language;
            values["ServiceOrder"] = normalized.ServiceOrder;
            values["CollapsedServices"] = normalized.CollapsedServices;
            values["RefreshSeconds"] = normalized.RefreshSeconds;
            // JSON null represents an unpositioned window; JSON has no NaN token.
            values["Left"] = IsFinite(normalized.Left) ? (object)normalized.Left : null;
            values["Top"] = IsFinite(normalized.Top) ? (object)normalized.Top : null;
            values["Width"] = normalized.Width;
            values["Height"] = normalized.Height;
            values["CompactLeft"] = IsFinite(normalized.CompactLeft) ? (object)normalized.CompactLeft : null;
            values["CompactTop"] = IsFinite(normalized.CompactTop) ? (object)normalized.CompactTop : null;
            values["CompactWidth"] = normalized.CompactWidth;
            values["DashboardLeft"] = IsFinite(normalized.DashboardLeft) ? (object)normalized.DashboardLeft : null;
            values["DashboardTop"] = IsFinite(normalized.DashboardTop) ? (object)normalized.DashboardTop : null;
            values["DashboardWidth"] = normalized.DashboardWidth;
            values["DashboardHeight"] = normalized.DashboardHeight;
            values["DashboardSidebarCollapsed"] = normalized.DashboardSidebarCollapsed;
            values["DashboardFavorites"] = normalized.DashboardFavorites;
            values["DashboardPreferredMetrics"] = normalized.DashboardPreferredMetrics;
            values["LayoutVersion"] = normalized.LayoutVersion;
            values["CodexPath"] = normalized.CodexPath;
            values["CodexWslDistribution"] = normalized.CodexWslDistribution;
            values["ClaudeWslDistribution"] = normalized.ClaudeWslDistribution;
            values["ClaudeWindowsConfigDirectory"] = normalized.ClaudeWindowsConfigDirectory;
            values["ClaudeUseExistingStatusLine"] = normalized.ClaudeUseExistingStatusLine;
            byte[] content = new UTF8Encoding(false).GetBytes(CreateSerializer().Serialize(values));
            lock (Gate)
            {
                string path = Path.Combine(DataDirectory, "settings.json");
                // Defaults shown after a failed read must not replace the user's real settings.
                if (String.Equals(blockedSettingsPath, path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("설정 파일을 읽지 못해 기존 설정을 보호하려고 저장하지 않았습니다. 위젯을 다시 실행해 주세요.");
                WriteAtomically(path, content);
            }
        }

        public static string LoadApiKey()
        {
            lock (Gate)
            {
                string path = Path.Combine(DataDirectory, "elevenlabs-key.dat");
                byte[] encrypted = null;
                byte[] decrypted = null;
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > 32768)
                        return String.Empty;
                    encrypted = File.ReadAllBytes(path);
                    decrypted = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decrypted);
                }
                catch (IOException) { return String.Empty; }
                catch (UnauthorizedAccessException) { return String.Empty; }
                catch (CryptographicException) { return String.Empty; }
                finally
                {
                    if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                    if (decrypted != null) Array.Clear(decrypted, 0, decrypted.Length);
                }
            }
        }

        public static void SaveApiKey(string key)
        {
            string normalized = (key ?? String.Empty).Trim();
            if (normalized.Length > 8192)
                throw new ArgumentException("The API key is too long.", "key");
            lock (Gate)
            {
                string path = Path.Combine(DataDirectory, "elevenlabs-key.dat");
                if (normalized.Length == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                byte[] clear = Encoding.UTF8.GetBytes(normalized);
                byte[] encrypted = null;
                try
                {
                    encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                    WriteAtomically(path, encrypted);
                }
                finally
                {
                    Array.Clear(clear, 0, clear.Length);
                    if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                }
            }
        }

        private static string CurrentStartupCommand()
        {
            Assembly entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string executable = Path.GetFullPath(entry.Location);
            if (executable.IndexOf('"') >= 0)
                throw new InvalidOperationException("The executable path is invalid.");
            return "\"" + executable + "\"";
        }

        // Reading startup state is passive. Null means the application-owned value is absent.
        public static string ReadStartupCommand()
        {
            lock (Gate)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistrySubKey, false))
                {
                    if (key == null) return null;
                    object value = key.GetValue(StartupValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value != null && !(value is string))
                        throw new InvalidOperationException("The UsageWidget startup command has an unsupported format.");
                    return value as string;
                }
            }
        }

        public static bool IsStartupAtCurrentPath()
        {
            return String.Equals(ReadStartupCommand(), CurrentStartupCommand(), StringComparison.OrdinalIgnoreCase);
        }

        // Call only from an explicit user action, never from Load or Save. The reserved value
        // belongs to this app even when it still references an executable in an older folder.
        public static void SetStartup(bool enabled)
        {
            RestoreStartupCommand(enabled ? CurrentStartupCommand() : null);
        }

        // Allows a settings transaction to restore the exact previous command after a failure.
        public static void RestoreStartupCommand(string command)
        {
            lock (Gate)
            {
                using (RegistryKey key = command != null
                    ? Registry.CurrentUser.CreateSubKey(StartupRegistrySubKey)
                    : Registry.CurrentUser.OpenSubKey(StartupRegistrySubKey, true))
                {
                    if (key == null)
                    {
                        if (command != null) throw new InvalidOperationException("Windows startup settings could not be opened.");
                        return;
                    }
                    if (command != null) key.SetValue(StartupValue, command, RegistryValueKind.String);
                    else key.DeleteValue(StartupValue, false);
                }
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 10 };
        }

        private static WidgetSettings Normalize(WidgetSettings settings)
        {
            return new WidgetSettings
            {
                AlwaysOnTop = settings.AlwaysOnTop,
                StartWithWindows = settings.StartWithWindows,
                CompactView = settings.CompactView,
                CompactAutoWidth = settings.CompactAutoWidth,
                FitContentHeight = settings.FitContentHeight,
                AnimationsEnabled = settings.AnimationsEnabled,
                AutoUpdateEnabled = settings.AutoUpdateEnabled,
                Theme = NormalizeTheme(settings.Theme),
                Language = settings.Language == "en" ? "en" : "ko",
                ServiceOrder = NormalizeServiceIds(settings.ServiceOrder, true),
                CollapsedServices = NormalizeServiceIds(settings.CollapsedServices, false),
                RefreshSeconds = settings.RefreshSeconds >= 30 && settings.RefreshSeconds <= 3600 ? settings.RefreshSeconds : 120,
                Left = IsFinite(settings.Left) && Math.Abs(settings.Left) <= 100000 ? settings.Left : double.NaN,
                Top = IsFinite(settings.Top) && Math.Abs(settings.Top) <= 100000 ? settings.Top : double.NaN,
                Width = IsFinite(settings.Width) && settings.Width >= 280 && settings.Width <= 1000 ? settings.Width : 320,
                Height = IsFinite(settings.Height) && settings.Height >= WidgetSettings.MinimumHeight && settings.Height <= 1400 ? settings.Height : WidgetSettings.DefaultHeight,
                CompactLeft = IsFinite(settings.CompactLeft) && Math.Abs(settings.CompactLeft) <= 100000 ? settings.CompactLeft : double.NaN,
                CompactTop = IsFinite(settings.CompactTop) && Math.Abs(settings.CompactTop) <= 100000 ? settings.CompactTop : double.NaN,
                CompactWidth = IsFinite(settings.CompactWidth) && settings.CompactWidth >= 240 && settings.CompactWidth <= 20000 ? settings.CompactWidth : 420,
                DashboardLeft = IsFinite(settings.DashboardLeft) && Math.Abs(settings.DashboardLeft) <= 100000 ? settings.DashboardLeft : double.NaN,
                DashboardTop = IsFinite(settings.DashboardTop) && Math.Abs(settings.DashboardTop) <= 100000 ? settings.DashboardTop : double.NaN,
                DashboardWidth = IsFinite(settings.DashboardWidth) && settings.DashboardWidth >= 320 && settings.DashboardWidth <= 20000 ? settings.DashboardWidth : 1100,
                DashboardHeight = IsFinite(settings.DashboardHeight) && settings.DashboardHeight >= 240 && settings.DashboardHeight <= 20000 ? settings.DashboardHeight : 760,
                DashboardSidebarCollapsed = settings.DashboardSidebarCollapsed,
                DashboardFavorites = NormalizeDashboardFavorites(settings.DashboardFavorites),
                DashboardPreferredMetrics = NormalizeDashboardPreferredMetrics(settings.DashboardPreferredMetrics),
                LayoutVersion = settings.LayoutVersion >= 0 && settings.LayoutVersion <= 2 ? settings.LayoutVersion : 2,
                CodexPath = settings.CodexPath != null && settings.CodexPath.Length <= 32767 ? settings.CodexPath.Trim() : String.Empty,
                CodexWslDistribution = NormalizeDistribution(settings.CodexWslDistribution),
                ClaudeWslDistribution = NormalizeDistribution(settings.ClaudeWslDistribution),
                // Invalid nonempty paths stay explicit so connection validation cannot
                // silently modify a different (default) Claude configuration.
                ClaudeWindowsConfigDirectory = settings.ClaudeWindowsConfigDirectory == null ? "" : settings.ClaudeWindowsConfigDirectory.Length <= 32767 ? settings.ClaudeWindowsConfigDirectory.Trim() : "[invalid Claude directory]",
                ClaudeUseExistingStatusLine = settings.ClaudeUseExistingStatusLine
            };
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string NormalizeDistribution(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            // Keep invalid nonempty targets explicit: never silently fall back to a different Windows account.
            if (value.Length > 256) return "[invalid WSL distribution]";
            foreach (char c in value) if (Char.IsControl(c) || "\\/:*?\"<>|".IndexOf(c) >= 0) return "[invalid WSL distribution]";
            return value;
        }

        private static string NormalizeTheme(string value)
        {
            return String.Equals((value ?? String.Empty).Trim(), "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        }

        internal static string[] NormalizeDashboardFavorites(IEnumerable<string> values)
        { return NormalizeServiceIds(values, false); }

        internal static Dictionary<string,string> NormalizeDashboardPreferredMetrics(IDictionary<string,string> values)
        {
            var result = new Dictionary<string,string>(StringComparer.Ordinal);
            if (values == null) return result;
            foreach (var item in values) {
                string id = (item.Key ?? String.Empty).Trim().ToLowerInvariant();
                if (Array.IndexOf(ServiceIds,id) >= 0 && ValidDashboardMetricLabel(item.Value)) result[id] = item.Value;
            }
            return result;
        }

        internal static bool ValidDashboardMetricLabel(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 160) return false;
            foreach (char character in value) if (Char.IsControl(character)) return false;
            return true;
        }

        private static Dictionary<string,string> ReadDashboardPreferredMetrics(Dictionary<string,object> values)
        {
            object raw;
            var result = new Dictionary<string,string>(StringComparer.Ordinal);
            if (!values.TryGetValue("DashboardPreferredMetrics",out raw)) return result;
            var pairs = raw as Dictionary<string,object>;
            if (pairs == null) return result;
            foreach (var item in pairs) if (item.Value is string) result[item.Key] = (string)item.Value;
            return NormalizeDashboardPreferredMetrics(result);
        }

        private static string[] ReadServiceIds(Dictionary<string, object> values, string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null || value is string) return null;
            System.Collections.IEnumerable items = value as System.Collections.IEnumerable;
            if (items == null || value is System.Collections.IDictionary) return null;
            List<string> result = new List<string>();
            foreach (object item in items)
                if (item is string) result.Add((string)item);
            return result.ToArray();
        }

        private static string[] NormalizeServiceIds(IEnumerable<string> values, bool includeMissing)
        {
            List<string> result = new List<string>();
            if (values != null)
            {
                foreach (string item in values)
                {
                    string id = (item ?? String.Empty).Trim().ToLowerInvariant();
                    if (Array.IndexOf(ServiceIds, id) >= 0 && !result.Contains(id)) result.Add(id);
                }
            }
            if (includeMissing)
                foreach (string id in ServiceIds)
                    if (!result.Contains(id)) result.Add(id);
            return result.ToArray();
        }

        private static bool ReadBoolean(Dictionary<string, object> values, string name, bool fallback)
        {
            object value;
            return values.TryGetValue(name, out value) && value is bool ? (bool)value : fallback;
        }

        private static double ReadNumber(Dictionary<string, object> values, string name, double fallback)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null || value is bool || value is string)
                return fallback;
            if (!(value is int || value is long || value is decimal || value is double || value is float))
                return fallback;
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int ReadInterval(Dictionary<string, object> values, int fallback)
        {
            double number = ReadNumber(values, "RefreshSeconds", fallback);
            return IsFinite(number) && number >= 30 && number <= 3600 && Math.Floor(number) == number ? (int)number : fallback;
        }

        private static int ReadLayoutVersion(Dictionary<string, object> values)
        {
            // Files written before compact layouts had no version. The window migrates them once.
            double number = ReadNumber(values, "LayoutVersion", 0);
            return IsFinite(number) && number >= 0 && number <= 2 && Math.Floor(number) == number ? (int)number : 2;
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                for (int attempt = 0; ; attempt++) {
                    try {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        break;
                    } catch (IOException error) {
                        int code = error.HResult & 0xffff;
                        // Antivirus/indexing may briefly hold a handle without delete sharing.
                        // Keep the atomic replacement and old file; never delete the destination.
                        if (attempt >= 4 || (code != 32 && code != 33 && code != 1175) || !File.Exists(temporary)) throw;
                        System.Threading.Thread.Sleep(50 * (attempt + 1));
                    }
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
