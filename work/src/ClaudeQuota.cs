using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // The official Agent SDK 0.3.304 experimental get_usage contract. Only control
    // requests are sent: no prompt, tools, transcript scan, credential file or OAuth HTTP client.
    internal sealed class ClaudeQuotaService
    {
        const int MaxOutput = 1048576;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        ClaudeUsageResult cached;
        string cachedTarget;
        sealed class Capabilities
        {
            internal string Target, ExecutableStamp;
            internal Version Version;
            internal DateTimeOffset CheckedAt;
        }
        volatile Capabilities capabilities, lastVersion;
        internal static Func<DateTimeOffset> UtcNowOverride = null;
        static DateTimeOffset Now() { var clock = UtcNowOverride; return clock == null ? DateTimeOffset.UtcNow : clock(); }
        internal static Func<string, string, string[], ProcessStartInfo> StartInfoOverride = null;
        internal static Func<string, string, CancellationToken, Task<ClaudeUsageResult>> ReadOverride = null;
        internal static int TimeoutMilliseconds = 30000;

        internal async Task<ClaudeUsageResult> ReadAsync(string distribution, string directory, CancellationToken cancellation)
        {
            var replacement = ReadOverride;
            if (replacement != null) return await replacement(distribution, directory, cancellation);
            if (SettingsStore.DataDirectoryOverride != null && StartInfoOverride == null) throw new InvalidOperationException("Isolated Claude quota tests require a fake CLI.");
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await Task.Run(() => Read(distribution, directory, cancellation), cancellation).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        ClaudeUsageResult Read(string distribution, string directory, CancellationToken cancellation)
        {
            string target = Target(distribution, directory);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                deadline.CancelAfter(TimeoutMilliseconds);
                var token = deadline.Token;
                string temporary = Path.Combine(Path.GetTempPath(), "NephyloClaudeQuota-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                try {
                    EnsureCapabilities(distribution, directory, temporary, target, token);
                    var before = Account(Probe(distribution, directory, temporary, new[] { "--safe-mode", "--setting-sources", "", "auth", "status", "--json" }, token));
                    var now = Now();
                    // Recheck identity on every read, including rapid repeated clicks.
                    if (cached != null && cachedTarget == target && cached.AccountFingerprint == before.Fingerprint && now >= cached.ObservedAt && now - cached.ObservedAt < TimeSpan.FromSeconds(15)
                        && cached.Snapshot.Rows.All(row => !row.ResetsAt.HasValue || row.ResetsAt.Value > now)) return cached;
                    cached = null;
                    string session = Guid.NewGuid().ToString("D");
                    Dictionary<string, object> response;
                    using (var child = Start(distribution, directory, temporary, Arguments(session)))
                    using (token.Register(() => Kill(child))) {
                        var errors = Task.Run(() => Drain(child.StandardError));
                        try {
                            string initId = Guid.NewGuid().ToString("N");
                            Write(child, initId, new { subtype = "initialize", hooks = new { }, skills = new string[0] });
                            var init = Reply(child.StandardOutput, initId, session);
                            var account = Object(init, "account");
                            if (Text(init, "current_permission_mode") != "dontAsk" || Text(account, "apiProvider") != "firstParty" || HashEmail(Text(account, "email")) != before.EmailHash) throw Problem("account");
                            string requestId = Guid.NewGuid().ToString("N");
                            Write(child, requestId, new { subtype = "get_usage", skip_behaviors = true });
                            response = Reply(child.StandardOutput, requestId, session);
                        } finally { Kill(child); Observe(errors); }
                    }
                    var after = Account(Probe(distribution, directory, temporary, new[] { "--safe-mode", "--setting-sources", "", "auth", "status", "--json" }, token));
                    if (before.Fingerprint != after.Fingerprint) throw Problem("account");
                    token.ThrowIfCancellationRequested();
                    var result = Parse(response, Now(), before.Fingerprint);
                    cachedTarget = target; cached = result; return result;
                } catch (OperationCanceledException) { InvalidateCaches(); cancellation.ThrowIfCancellationRequested(); throw Problem("timeout"); }
                catch (ConnectionException) { InvalidateCaches(); if (token.IsCancellationRequested) { cancellation.ThrowIfCancellationRequested(); throw Problem("timeout"); } throw; }
                catch { InvalidateCaches(); if (token.IsCancellationRequested) { cancellation.ThrowIfCancellationRequested(); throw Problem("timeout"); } throw Problem("response"); }
                finally { try { Directory.Delete(temporary, false); } catch { } }
            }
        }

        static string Target(string distribution, string directory)
        { return String.IsNullOrEmpty(distribution) ? "win:" + ClaudeUsageService.ResolveWindowsConfigDirectory(directory) : ClaudeUsageService.GetSourceId(distribution); }

        // A diagnostic reads already observed numeric metadata, never launches a CLI.
        internal string KnownVersion(string distribution, string directory)
        {
            var known = lastVersion;
            try { return known != null && known.Target == Target(distribution, directory) ? known.Version.ToString(3) : null; }
            catch { return null; }
        }

        void InvalidateCaches() { capabilities = null; cached = null; }

        static string ExecutableStamp(string executable)
        {
            try {
                var file = new FileInfo(executable);
                return file.Exists ? file.FullName.ToUpperInvariant() + "|" + file.Length.ToString(CultureInfo.InvariantCulture) + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : null;
            } catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        void EnsureCapabilities(string distribution, string directory, string home, string target, CancellationToken token)
        {
            var now = Now();
            var previous = capabilities;
            var versionArguments = new[] { "--version" };
            var versionInfo = CreateInfo(distribution, directory, home, versionArguments);
            bool windows = String.IsNullOrEmpty(distribution);
            string stamp = windows ? ExecutableStamp(versionInfo.FileName) : null;
            bool fresh = previous != null && previous.Target == target && now >= previous.CheckedAt && now - previous.CheckedAt < TimeSpan.FromMinutes(5);
            // Local executable replacement, target changes and expiry invalidate the check.
            // Account checks below remain mandatory even when metadata is cached.
            if (windows && fresh && stamp != null && previous.ExecutableStamp == stamp) return;
            lastVersion = null;
            string version = Probe(versionInfo, versionArguments, token);
            var match = Regex.Match(version ?? "", @"^(\d+)\.(\d+)\.(\d+)"); Version parsed;
            if (!match.Success || !Version.TryParse(match.Value, out parsed)) throw Problem("version");
            lastVersion = new Capabilities { Target = target, Version = parsed, CheckedAt = now };
            if (parsed < new Version(2, 1, 304)) throw Problem("version");
            // WSL executable metadata is not available locally: check its version every
            // time, reusing only the help result for this distribution/profile/version.
            if (!windows && fresh && previous.Version.Equals(parsed)) return;
            string help = Probe(distribution, directory, home, new[] { "--help" }, token);
            foreach (string flag in RequiredFlags) if (!help.Contains(flag)) throw Problem("version");
            capabilities = new Capabilities { Target = target, ExecutableStamp = stamp, Version = parsed, CheckedAt = now };
        }

        static readonly string[] RequiredFlags = { "--safe-mode", "--restricted", "--tools", "--disallowedTools", "--permission-mode", "--permission-prompts", "--strict-mcp-config", "--mcp-config", "--setting-sources", "--settings", "--disable-slash-commands", "--no-chrome", "--input-format", "--output-format", "--session-id", "--no-session-persistence" };
        internal static string[] Arguments(string session)
        {
            return new[] { "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--safe-mode", "--restricted", "--tools", "", "--disallowedTools", "*", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--setting-sources", "", "--settings", "{\"disableAllHooks\":true,\"autoMemoryEnabled\":false}", "--disable-slash-commands", "--no-chrome", "--model", "haiku", "--session-id", session, "--no-session-persistence" };
        }

        static readonly string[] RemovedEnvironment = { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_SDK_URL", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SYNC_PLUGIN_INSTALL", "CLAUDE_CODE_REMOTE", "CLAUDE_CODE_REMOTE_ENVIRONMENT_TYPE", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" };
        internal static ProcessStartInfo Info(string distribution, string directory, string home, string[] arguments)
        {
            ProcessStartInfo info;
            if (!String.IsNullOrEmpty(distribution)) {
                // Fixed script; distribution and argv are separate wsl.exe arguments.
                const string script = "set -eu\ncli=\"$HOME/.local/bin/claude\"\nif [ ! -x \"$cli\" ]; then cli=$(command -v claude) || exit 127; fi\ncd /tmp\nunset ANTHROPIC_API_KEY ANTHROPIC_AUTH_TOKEN ANTHROPIC_BASE_URL CLAUDE_CODE_SDK_URL CLAUDE_CODE_SESSION_ID CLAUDE_CODE_ENTRYPOINT CLAUDE_CODE_SYNC_PLUGIN_INSTALL CLAUDE_CODE_REMOTE CLAUDE_CODE_REMOTE_ENVIRONMENT_TYPE CLAUDE_CODE_USE_BEDROCK CLAUDE_CODE_USE_VERTEX CLAUDE_CODE_USE_FOUNDRY\nexport CLAUDE_CODE_SAFE_MODE=1\nexec \"$cli\" \"$@\"";
                info = WslRuntime.CreateScriptStartInfo(distribution, script, arguments);
            } else {
                string path = FindExecutable();
                info = new ProcessStartInfo(path, String.Join(" ", arguments.Select(Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = home };
            }
            ConfigureEnvironment(info, distribution, directory);
            return info;
        }
        internal static void ConfigureEnvironment(ProcessStartInfo info, string distribution, string directory)
        {
            if (String.IsNullOrEmpty(distribution)) {
                string config = ClaudeUsageService.ResolveWindowsConfigDirectory(directory);
                string defaultConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                // Explicitly setting even the default path changes where this CLI reads
                // account metadata. Preserve its normal default-profile semantics.
                if (String.Equals(config, defaultConfig, StringComparison.OrdinalIgnoreCase)) info.EnvironmentVariables.Remove("CLAUDE_CONFIG_DIR");
                else info.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = config;
            }
            foreach (string name in RemovedEnvironment) info.EnvironmentVariables.Remove(name);
            info.EnvironmentVariables["CLAUDE_CODE_SAFE_MODE"] = "1";
        }
        static string FindExecutable()
        {
            var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe") };
            foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
                try { string root = part.Trim().Trim('"'); if (Path.IsPathRooted(root)) candidates.Add(Path.Combine(root, "claude.exe")); } catch { }
            }
            foreach (string path in candidates) if (File.Exists(path)) return Path.GetFullPath(path);
            throw Problem("installation");
        }
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value ?? "") { if (c == '\\') { slashes++; continue; } if (c == '"') result.Append('\\', slashes * 2 + 1); else result.Append('\\', slashes); result.Append(c); slashes = 0; }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
        static Process Start(string distribution, string directory, string home, string[] arguments)
        {
            return Start(CreateInfo(distribution, directory, home, arguments));
        }
        static ProcessStartInfo CreateInfo(string distribution, string directory, string home, string[] arguments)
        {
            var factory = StartInfoOverride;
            return factory == null ? Info(distribution, directory, home, arguments) : factory(distribution, directory, arguments);
        }
        static Process Start(ProcessStartInfo info)
        {
            try { var process = Process.Start(info); process.StandardInput.AutoFlush = true; return process; }
            catch { throw Problem("installation"); }
        }
        static string Probe(string distribution, string directory, string home, string[] args, CancellationToken cancellation)
        {
            return Probe(CreateInfo(distribution, directory, home, args), args, cancellation);
        }
        static string Probe(ProcessStartInfo info, string[] args, CancellationToken cancellation)
        {
            using (var child = Start(info))
            using (cancellation.Register(() => Kill(child))) {
                var errors = Task.Run(() => Drain(child.StandardError));
                try {
                    child.StandardInput.Close(); string text = ReadBounded(child.StandardOutput);
                    while (!child.WaitForExit(100)) cancellation.ThrowIfCancellationRequested();
                    cancellation.ThrowIfCancellationRequested();
                    // auth status returns a structured logged-out result with a nonzero exit.
                    if (child.ExitCode != 0 && !args.Contains("auth")) throw Problem("version");
                    return text;
                } finally { Kill(child); Observe(errors); }
            }
        }
        static void Write(Process child, string id, object request)
        {
            child.StandardInput.WriteLine(Json().Serialize(new { type = "control_request", request_id = id, request = request }));
        }
        static Dictionary<string, object> Reply(StreamReader stdout, string id, string session)
        {
            int total = 0;
            for (int frames = 0; frames < 32; frames++) {
                var line = new StringBuilder(); int c;
                while ((c = stdout.Read()) >= 0 && c != '\n') { if (++total > MaxOutput) throw Problem("response"); line.Append((char)c); }
                if (c < 0) throw Problem("response");
                var frame = Json().DeserializeObject(line.ToString()) as Dictionary<string, object>;
                string sid = Text(frame, "session_id"); if (sid != null && sid != session) throw Problem("response");
                if (Text(frame, "type") == "system" && Text(frame, "subtype") != "init") continue;
                if (Text(frame, "type") != "control_response") throw Problem("response");
                var envelope = Object(frame, "response");
                if (Text(envelope, "request_id") != id || Text(envelope, "subtype") != "success" || Object(envelope, "response") == null) throw Problem("response");
                return Object(envelope, "response");
            }
            throw Problem("response");
        }
        static string ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) { if (text.Length + count > MaxOutput) throw Problem("response"); text.Append(buffer, 0, count); }
            return text.ToString();
        }
        static void Drain(StreamReader reader) { try { var buffer = new char[4096]; while (reader.Read(buffer, 0, buffer.Length) > 0) { } } catch { } }
        static void Kill(Process child) { try { if (!child.HasExited) { child.Kill(); child.WaitForExit(1500); } } catch { } }
        static void Observe(Task task) { task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaxOutput, RecursionLimit = 32 }; }
        static string Text(Dictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key, out value) ? value as string : null; }
        static Dictionary<string, object> Object(Dictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        static bool True(Dictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key, out value) && System.Object.Equals(value, true); }
        sealed class AccountState { internal string EmailHash, Fingerprint; }
        static string Hash(string value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        static string HashEmail(string email) { return String.IsNullOrWhiteSpace(email) ? null : Hash(email.Trim().ToLowerInvariant()); }
        static AccountState Account(string json)
        {
            var row = Json().DeserializeObject(json) as Dictionary<string, object>;
            string method = Text(row, "authMethod"), emailHash = HashEmail(Text(row, "email"));
            if (!True(row, "loggedIn") || emailHash == null || !(method == "claude.ai" || method == "oauth") || Text(row, "apiProvider") != "firstParty") throw Problem("authentication");
            return new AccountState { EmailHash = emailHash, Fingerprint = Hash(method + "|" + emailHash + "|" + (Text(row, "orgId") ?? "")) };
        }

        internal static ClaudeUsageResult Parse(Dictionary<string, object> response, DateTimeOffset observed, string fingerprint)
        {
            if (!True(response, "rate_limits_available") || !new[] { "pro", "max", "team", "enterprise" }.Contains(Text(response, "subscription_type"))) throw Problem("unavailable");
            var windows = new Dictionary<string, object>();
            foreach (string name in new[] { "five_hour", "seven_day" }) {
                var row = Object(Object(response, "rate_limits"), name); object raw;
                if (row == null) continue;
                if (!row.TryGetValue("utilization", out raw) || raw == null) continue;
                if (!(raw is int || raw is long || raw is double || raw is decimal)) throw Problem("response");
                double used = Convert.ToDouble(raw, CultureInfo.InvariantCulture); DateTimeOffset reset; string date = Text(row, "resets_at");
                if (Double.IsNaN(used) || Double.IsInfinity(used) || used < 0 || used > 100) throw Problem("response");
                var clean = new Dictionary<string, object> { { "used_percentage", used } };
                if (row.TryGetValue("resets_at", out raw) && raw != null) {
                    if (date == null || date.Length > 64 || !Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out reset) || reset <= observed || reset > observed.AddMinutes(name == "five_hour" ? 305 : 10085)) throw Problem("response");
                    clean["resets_at"] = (long)(reset - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
                }
                windows[name] = clean;
            }
            if (windows.Count == 0) throw Problem("unavailable");
            string quotaJson = Json().Serialize(new Dictionary<string, object> { { "rate_limits", windows } });
            var result = ClaudeUsageService.ParseCache(Json().Serialize(new Dictionary<string, object> { { "observedAt", observed.ToString("o", CultureInfo.InvariantCulture) }, { "rate_limits", windows }, { "queryAccount", fingerprint } }), observed);
            result.QuotaJson = quotaJson;
            return result;
        }
        internal static ConnectionException Problem(string kind)
        {
            string message = kind == "installation" ? "공식 Claude Code를 설치한 뒤 다시 조회해 주세요." : kind == "version" ? "대화 없는 잔여량 조회에는 Claude Code 2.1.304 이상이 필요합니다. CLI를 업데이트한 뒤 다시 조회해 주세요." :
                kind == "authentication" ? "Claude Code에서 구독 계정으로 로그인한 뒤 다시 조회해 주세요." : kind == "account" ? "조회 중 Claude 계정이 변경되었습니다. 새로고침하여 현재 계정을 다시 확인해 주세요." :
                kind == "unavailable" ? "현재 Claude 계정은 구독 한도를 제공하지 않습니다. Free·API 키 계정은 수동 기록을 이용해 주세요." : kind == "timeout" ? "Claude 잔여량 조회 시간이 초과되었습니다. 연결 상태를 확인한 뒤 다시 조회해 주세요." :
                "Claude 잔여량 응답을 확인하지 못했습니다. 잠시 후 새로고침해 주세요. 계속되면 Claude Code 업데이트와 로그인을 확인해 주세요.";
            var error = new ConnectionException(new ConnectionIssue { Title = "Claude 잔여량을 확인해 주세요", Message = message, ActionLabel = "Claude 연결 안내", ActionUrl = "https://code.claude.com/docs/en/setup" });
            error.Data["ClaudeQuotaKind"] = kind; return error;
        }
    }
}
