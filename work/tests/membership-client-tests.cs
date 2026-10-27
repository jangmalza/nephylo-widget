using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class MembershipClientTests
{
    private const string Id = "19bd679d-8d7e-4865-9657-2400c3503e38";
    private const string OtherId = "109bb8de-d519-42a6-bb2e-e626d64182aa";
    private const string Address = "member@example.com";
    private const string ServerTime = "2026-10-11T00:00:00Z";
    private static int checks;
    private static string root;
    private static int dirs;
    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "UsageWidget-Membership-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + checks + " membership client checks"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static string DirectoryForTest() { string result = Path.Combine(root, (++dirs).ToString()); Directory.CreateDirectory(result); return result; }
    private static MembershipConfiguration Config(string origin) { return MembershipConfiguration.FromValues(origin, "sb_publishable_test_public_key"); }
    private static string Jwt(string role) { return "eyJhbGciOiJIUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"role\":\"" + role + "\"}" )).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".testsignature"; }
    private static HttpResponseMessage Json(object data) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(data), Encoding.UTF8, "application/json") }; }
    private static Dictionary<string, object> Session(string id)
    {
        return new Dictionary<string, object> { { "access_token", "access-test-token" }, { "refresh_token", "refresh-secret-token" }, { "expires_in", 3600 },
            { "user", new Dictionary<string, object> { { "id", id }, { "email", Address } } } };
    }
    private static Dictionary<string, object> Membership(string plan, string status, string expiry, string id)
    {
        return new Dictionary<string, object> { { "user_id", id }, { "email", Address }, { "plan", plan }, { "status", status }, { "expires_at", expiry },
            { "server_time", ServerTime }, { "is_admin", false }, { "recheck_after_seconds", 300 } };
    }
    private static void LoginReplies(FakeHandler handler, string plan, string state, string expiry)
    { handler.Reply(Session(Id)); handler.Reply(Membership(plan, state, expiry, Id)); }
    private static async Task ExpectSafeFailure(Func<Task> action)
    {
        try { await action(); throw new Exception("Expected safe failure"); }
        catch (InvalidOperationException error) { Check(!error.Message.Contains("secret") && !error.Message.Contains("access-test"), "failure must not expose tokens/provider text"); }
    }
    private static async Task Run()
    {
        await StorageRestoreFailures();
        await ReauthenticationNotices();
        await RateLimits();
        await VerificationRecoveryHints();
        Check(!MembershipConfiguration.LoadEmbedded().IsConfigured, "missing embedded configuration is unconfigured");
        Check(Config("https://test.supabase.co/").IsConfigured, "fixed HTTPS origin accepted");
        Check(Config("https://test.supabase.co/").EmailEnabled, "legacy configuration defaults to email enabled");
        foreach (string url in new[] { "http://test.supabase.co", "https://user:pass@test.supabase.co", "https://test.supabase.co/path", "https://test.supabase.co/?key=1", "https://test.supabase.co/#a", "https://localhost", "https://127.0.0.1", "https://[::1]", "file:///a", "" })
            Check(!Config(url).IsConfigured, "unsafe origin rejected: " + url);
        foreach (string key in new[] { "sb_secret_actual-secret", Jwt("service_role"), "not-a-key", "sb_publishable_header\r\ninjection", "sb_publishable_short", "sb_publishable_bad#character" })
            Check(!MembershipConfiguration.FromValues("https://test.supabase.co", key).IsConfigured, "nonpublic configuration key rejected");
        Check(MembershipConfiguration.FromValues("https://test.supabase.co", Jwt("anon")).IsConfigured, "legacy anon key accepted");

        var clock = new Clock(); var handler = new FakeHandler(); string directory = DirectoryForTest();
        using (var blocked = new MembershipClient(DirectoryForTest(), false, MembershipConfiguration.FromValues("https://test.supabase.co", "sb_publishable_test_public_key", true, false), handler, clock.Read)) {
            Check(blocked.IsConfigured && blocked.IsGoogleConfigured && !blocked.IsEmailConfigured, "email readiness does not disable configured server or Google");
            await ExpectSafeFailure(() => blocked.SendCodeAsync(Address));
            await ExpectSafeFailure(() => blocked.VerifyCodeAsync(Address, "123456"));
            Check(handler.Calls == 0 && !blocked.IsSignedIn, "unavailable email flow is blocked before auth requests");
        }
        handler = new FakeHandler();
        using (var guest = new MembershipClient(directory, false, Config(""), handler, clock.Read)) {
            await guest.RestoreAsync(); await guest.RefreshAsync();
            Check(!guest.IsConfigured && !guest.IsSignedIn && !guest.HasPro && handler.Calls == 0, "unconfigured guest has no auth/network");
            await ExpectSafeFailure(() => guest.SendCodeAsync(Address));
            Check(Directory.GetFiles(directory).Length == 0, "guest writes no credentials");
        }
        handler = new FakeHandler();
        using (var guest = new MembershipClient(DirectoryForTest(), false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await guest.RestoreAsync(); await guest.RefreshAsync();
            Check(!guest.IsSignedIn && handler.Calls == 0, "configured guest has no auth/network");
        }
        handler = new FakeHandler();
        using (var preview = new MembershipClient(DirectoryForTest(), true, Config("https://test.supabase.co"), handler, clock.Read)) {
            await preview.RestoreAsync(); await preview.RefreshAsync();
            Check(!preview.IsConfigured && !preview.HasPro && handler.Calls == 0, "demo cannot acquire real Pro or credentials");
        }

        handler = new FakeHandler(); directory = DirectoryForTest(); clock = new Clock();
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await ExpectSafeFailure(() => client.SendCodeAsync("Name <member@example.com>"));
            Check(handler.Calls == 0, "bad email blocked before request");
            handler.Add(async request => {
                Check(request.RequestUri.AbsolutePath == "/auth/v1/otp", "OTP uses email endpoint");
                Check(request.Headers.Authorization == null, "OTP does not leak existing bearer");
                Check(request.Headers.Contains("apikey"), "public key sent to configured server");
                var body = MembershipClient.ParseJson(await request.Content.ReadAsStringAsync());
                Check((string)body["email"] == Address && body.ContainsKey("create_user") && !(bool)body["create_user"], "OTP never registers a new account");
                return Json(new Dictionary<string, object>());
            });
            await client.SendCodeAsync(Address);
            Check(!client.IsSignedIn && !client.HasPro, "sending email is not login or Pro");
            await ExpectSafeFailure(() => client.VerifyCodeAsync(Address, "bad-code"));
            await ExpectSafeFailure(() => client.VerifyCodeAsync(Address, "12345678"));
            LoginReplies(handler, "free", "free", null);
            await client.VerifyCodeAsync(Address, "123456");
            Check(client.IsSignedIn && client.Email == Address && !client.HasPro && client.Status == "free", "verified member remains free without entitlement");
            Check(File.Exists(Path.Combine(directory, "membership-session.dat")), "refresh token persists");
            string ciphertext = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "membership-session.dat")));
            Check(!ciphertext.Contains("refresh-secret-token") && !ciphertext.Contains(Address), "session file is encrypted");
            byte[] decrypted = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(directory, "membership-session.dat")), Encoding.UTF8.GetBytes("UsageWidget.Membership.v1|https://test.supabase.co/"), DataProtectionScope.CurrentUser);
            string stored = Encoding.UTF8.GetString(decrypted);
            Check(!stored.Contains("access-test-token") && !stored.Contains("plan") && !stored.Contains("expires_at"), "no access token or paid entitlement on disk");
            handler.Reply(Membership("pro", "active", "2026-10-12T00:00:00Z", Id));
            await client.RefreshAsync(); Check(client.HasPro && client.Status == "active", "only matching server grant activates Pro");
            Check(!client.VerificationDelayed && client.LastVerifiedAt == DateTimeOffset.Parse(ServerTime), "Successful confirmation exposes its server timestamp");
            clock.Milliseconds = 5 * 60 * 1000;
            handler.Fail(); await client.RefreshAsync(); Check(client.HasPro, "recent verified Pro survives temporary network loss");
            Check(client.VerificationDelayed && client.LastVerifiedAt == DateTimeOffset.Parse(ServerTime), "Connection failure retains the real last confirmation time and signals delay");
            clock.Milliseconds = 15 * 60 * 1000;
            Check(!client.HasPro && client.Status == "unverified", "offline grace ends after 15 minutes of monotonic time");
            handler.Reply(Membership("pro", "canceled", "2026-10-12T00:00:00Z", Id));
            await client.RefreshAsync(); Check(client.HasPro && client.Status == "canceled", "canceled subscription retains server-paid time");
            Check(!client.VerificationDelayed, "Successful recovery clears the delayed state");
            handler.Reply(Membership("free", "revoked", null, Id));
            await client.RefreshAsync(); Check(!client.HasPro && client.Status == "revoked", "revocation immediately closes Pro");
        }

        handler = new FakeHandler(); handler.Fail();
        using (var restored = new MembershipClient(directory, false, MembershipConfiguration.FromValues("https://test.supabase.co", "sb_publishable_test_public_key", false, false), handler, clock.Read)) {
            await restored.RestoreAsync();
            Check(restored.IsConfigured && restored.IsSignedIn && !restored.IsEmailConfigured && handler.Calls == 1, "disabled sign-in providers preserve existing session restoration and entitlement verification");
        }
        handler = new FakeHandler(); handler.Fail();
        using (var restored = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await restored.RestoreAsync(); Check(restored.IsSignedIn && !restored.HasPro && handler.Calls == 1, "offline cold start never restores Pro from disk");
            Check(!restored.SessionRestoreFailed, "transient refresh failure is not mislabeled as damaged local storage");
            Check(restored.VerificationDelayed && !restored.LastVerifiedAt.HasValue, "Offline restart never invents a persisted verification time");
        }
        handler = new FakeHandler();
        using (var wrongIssuer = new MembershipClient(directory, false, Config("https://other.supabase.co"), handler, clock.Read)) {
            await wrongIssuer.RestoreAsync(); Check(!wrongIssuer.IsSignedIn && handler.Calls == 0, "DPAPI session is bound to server origin");
        }
        handler = new FakeHandler(); handler.Reply(Session(Id)); handler.Reply(Membership("pro", "active", "2026-10-11T00:00:03Z", Id)); clock = new Clock();
        using (var restored = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await restored.RestoreAsync(); Check(restored.HasPro && handler.Paths[0].Contains("grant_type=refresh_token"), "restore refreshes identity then reads server entitlement");
            clock.Milliseconds = 3000; Check(!restored.HasPro && restored.Status == "expired", "server expiry bounds offline grace");
            handler.Reply(Membership("pro", "active", "2026-10-12T00:00:00Z", OtherId));
            await restored.RefreshAsync(); Check(!restored.HasPro, "another user's response cannot grant Pro");
            handler.Reply(Membership("pro", "active", "2026-10-12T00:00:00Z", Id)); await restored.RefreshAsync(); Check(restored.HasPro, "fresh valid grant restores Pro");
            handler.Status(HttpStatusCode.Forbidden); await restored.RefreshAsync();
            Check(!restored.HasPro && !restored.IsSignedIn && !File.Exists(Path.Combine(directory, "membership-session.dat")), "forbidden identity loses session and privilege immediately");
            Check(!restored.LastVerifiedAt.HasValue && !restored.VerificationDelayed, "Cleared identity cannot retain another user's confirmation metadata");
        }

        handler = new FakeHandler(); directory = DirectoryForTest(); clock = new Clock(); LoginReplies(handler, "pro", "active", "2026-10-12T00:00:00Z");
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await client.VerifyCodeAsync(Address, "123456");
            handler.Status(HttpStatusCode.Unauthorized); handler.Fail();
            await client.RefreshAsync(); Check(!client.HasPro, "401 invalidates cache before failed refresh");
            handler.Reply(Session(Id)); handler.Reply(Membership("pro", "active", "2026-10-12T00:00:00Z", Id));
            await client.VerifyCodeAsync(Address, "123456");
            var deferred = new TaskCompletionSource<HttpResponseMessage>();
            handler.Add(request => deferred.Task);
            Task checking = client.RefreshAsync();
            handler.Status(HttpStatusCode.NoContent);
            await client.SignOutAsync(); Check(!client.HasPro && !client.IsSignedIn, "logout gates immediately before pending network finishes");
            deferred.SetResult(Json(Membership("pro", "active", "2026-10-12T00:00:00Z", Id)));
            await checking;
            Check(!client.HasPro && !client.IsSignedIn && !File.Exists(Path.Combine(directory, "membership-session.dat")), "late refresh cannot undo logout");
        }

        handler = new FakeHandler(); directory = DirectoryForTest();
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            var deferred = new TaskCompletionSource<HttpResponseMessage>(); handler.Add(request => deferred.Task);
            Task verification = client.VerifyCodeAsync(Address, "123456");
            await client.SignOutAsync(); deferred.SetResult(Json(Session(Id))); await verification;
            Check(!client.IsSignedIn && !client.HasPro && !File.Exists(Path.Combine(directory, "membership-session.dat")), "late verification cannot restore logged out account");
        }

        handler = new FakeHandler(); directory = DirectoryForTest(); LoginReplies(handler, "pro", "active", "2026-10-12T00:00:00Z");
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) { await client.VerifyCodeAsync(Address, "123456"); }
        string path = Path.Combine(directory, "membership-session.dat"); byte[] damaged = File.ReadAllBytes(path); damaged[damaged.Length / 2] ^= 0x20; File.WriteAllBytes(path, damaged);
        handler = new FakeHandler();
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await client.RestoreAsync(); Check(!client.IsSignedIn && !client.HasPro && handler.Calls == 0, "tampered encrypted credentials rejected before network");
        }
        File.WriteAllBytes(path, new byte[65537]); handler = new FakeHandler();
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await client.RestoreAsync(); Check(!client.IsSignedIn && handler.Calls == 0, "oversized credentials rejected before read/decrypt");
        }

        handler = new FakeHandler(); directory = DirectoryForTest();
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            var bad = Session(Id); bad.Remove("expires_in"); handler.Reply(bad);
            await ExpectSafeFailure(() => client.VerifyCodeAsync(Address, "123456")); Check(!client.IsSignedIn && !client.HasPro, "missing token expiry is invalid");
            handler.Add(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 262145)) }));
            await ExpectSafeFailure(() => client.SendCodeAsync(Address)); Check(!client.IsSignedIn, "oversized server response is bounded");
            handler.Status(HttpStatusCode.Redirect); await ExpectSafeFailure(() => client.SendCodeAsync(Address));
            Check(!client.HasPro, "redirect is not an authentication success");
        }

        handler = new FakeHandler(); directory = DirectoryForTest(); clock = new Clock(); handler.Reply(Session(Id));
        handler.Add(request => {
            Check(request.Headers.Authorization != null && request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "access-test-token", "membership RPC uses user access token");
            clock.Milliseconds += 5000;
            return Task.FromResult(Json(Membership("pro", "active", "2026-10-11T00:00:10Z", Id)));
        });
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await client.VerifyCodeAsync(Address, "123456"); Check(client.HasPro, "short server entitlement initially valid");
            clock.Milliseconds = 10000; Check(!client.HasPro, "network transit cannot extend server expiry");
            handler.Reply(Membership("pro", "active", null, Id)); await client.RefreshAsync();
            Check(!client.HasPro, "Pro requires explicit valid server expiration");
        }

        handler = new FakeHandler(); directory = DirectoryForTest(); clock = new Clock(); LoginReplies(handler, "pro", "active", "2026-10-12T00:00:00Z");
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await client.VerifyCodeAsync(Address, "123456");
            clock.Milliseconds = 3600000;
            var deferred = new TaskCompletionSource<HttpResponseMessage>(); handler.Add(request => deferred.Task);
            handler.Reply(Membership("pro", "active", "2026-10-12T00:00:00Z", Id));
            handler.Reply(Membership("pro", "active", "2026-10-12T00:00:00Z", Id));
            Task first = client.RefreshAsync(); Task second = client.RefreshAsync();
            Check(handler.Calls == 3, "concurrent refresh waits behind token renewal");
            deferred.SetResult(Json(Session(Id))); await Task.WhenAll(first, second);
            int renewals = handler.Paths.FindAll(pathName => pathName.Contains("grant_type=refresh_token")).Count;
            Check(renewals == 1 && client.HasPro, "concurrent refresh serializes refresh-token rotation");
        }

        handler = new FakeHandler(); directory = DirectoryForTest(); clock = new Clock(); LoginReplies(handler, "pro", "active", "2026-10-12T00:00:00Z");
        string blockedSession = Path.Combine(directory, "membership-session.dat"); Directory.CreateDirectory(blockedSession);
        using (var client = new MembershipClient(directory, false, Config("https://test.supabase.co"), handler, clock.Read)) {
            await client.VerifyCodeAsync(Address, "123456");
            Check(client.IsSignedIn && client.HasPro && client.LastError.Contains("저장하지 못"), "session write failure remains visible after successful entitlement check");
        }
    }
    private static async Task ReauthenticationNotices()
    {
        const string origin = "https://test.supabase.co/";
        foreach (var code in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden }) {
            string directory = DirectoryForTest(), path = Path.Combine(directory, "membership-reauth-required.json");
            var seed = new FakeHandler(); LoginReplies(seed, "pro", "active", "2026-10-12T00:00:00Z");
            using (var client = new MembershipClient(directory, false, Config(origin), seed, () => 0))
                await client.VerifyCodeAsync(Address, "123456");
            var rejected = new FakeHandler(); rejected.Status(code);
            using (var client = new MembershipClient(directory, false, Config(origin), rejected, () => 0)) {
                await client.RestoreAsync();
                Check(!client.IsSignedIn && !client.HasPro && !client.IsAdmin && client.ReauthenticationRequired, "rejected restored identity immediately loses privileges and requests reauthentication: " + code);
                Check(!client.SessionRestoreFailed && client.LastError == MembershipClient.ReauthenticationRequiredMessage, "server rejection is distinguished from unreadable local storage");
                Check(rejected.Calls == 1 && !File.Exists(Path.Combine(directory, "membership-session.dat")), "rejected token is erased without retry loop");
            }
            string marker = File.ReadAllText(path);
            Check(!marker.Contains(Id) && !marker.Contains(Address) && !marker.Contains("token") && !marker.Contains("pro") && marker.Contains(origin), "advisory marker is origin-bound and contains no identity, credentials or paid entitlement");
            var afterRestart = new FakeHandler();
            using (var client = new MembershipClient(directory, false, Config(origin), afterRestart, () => 0)) {
                await client.RestoreAsync();
                Check(client.ReauthenticationRequired && client.LastError == MembershipClient.ReauthenticationRequiredMessage && !client.HasPro && afterRestart.Calls == 0, "restart retains actionable notice without resurrecting rejected session or making requests");
                afterRestart.Status(HttpStatusCode.BadRequest);
                await ExpectSafeFailure(() => client.VerifyCodeAsync(Address, "123456"));
                Check(client.ReauthenticationRequired && File.Exists(path), "failed re-login preserves the existing recovery notice");
                LoginReplies(afterRestart, "free", "free", null);
                await client.VerifyCodeAsync(Address, "123456");
                Check(!client.ReauthenticationRequired && !File.Exists(path) && client.IsSignedIn && !client.HasPro, "valid re-login clears the notice without granting unverified Pro");
                afterRestart.Status(HttpStatusCode.Forbidden); await client.RefreshAsync();
                Check(client.ReauthenticationRequired && File.Exists(path), "rejection during an active session also persists recovery guidance");
                await client.SignOutAsync();
                Check(!client.ReauthenticationRequired && !File.Exists(path) && String.IsNullOrEmpty(client.LastError), "intentional local logout clears recovery guidance");
            }
            var cleanRestart = new FakeHandler();
            using (var client = new MembershipClient(directory, false, Config(origin), cleanRestart, () => 0)) {
                await client.RestoreAsync();
                Check(!client.ReauthenticationRequired && !client.SessionRestoreFailed && cleanRestart.Calls == 0, "restart after deliberate logout remains an ordinary guest");
                cleanRestart.Status(HttpStatusCode.BadRequest);
                await ExpectSafeFailure(() => client.VerifyCodeAsync(Address, "123456"));
                Check(!client.ReauthenticationRequired && !File.Exists(path), "invalid guest OTP does not masquerade as an expired stored session");
            }
        }
        string invalidDirectory = DirectoryForTest(), invalidPath = Path.Combine(invalidDirectory, "membership-reauth-required.json");
        foreach (string invalid in new[] { "", "not-json", "{\"version\":1,\"issuer\":\"https://other.supabase.co/\"}", new string('x', 4097) }) {
            File.WriteAllText(invalidPath, invalid); var handler = new FakeHandler();
            using (var client = new MembershipClient(invalidDirectory, false, Config(origin), handler, () => 0)) {
                await client.RestoreAsync();
                Check(!client.ReauthenticationRequired && !client.HasPro && handler.Calls == 0, "damaged or foreign advisory marker never blocks startup or changes authority");
            }
        }
        string lateDirectory = DirectoryForTest(); var login = new FakeHandler(); LoginReplies(login, "free", "free", null);
        using (var seed = new MembershipClient(lateDirectory, false, Config(origin), login, () => 0)) await seed.VerifyCodeAsync(Address, "123456");
        var pending = new TaskCompletionSource<HttpResponseMessage>(); var delayed = new FakeHandler(); delayed.Add(request => pending.Task);
        using (var client = new MembershipClient(lateDirectory, false, Config(origin), delayed, () => 0)) {
            Task restoring = client.RestoreAsync(); await client.SignOutAsync(); pending.SetResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); await restoring;
            Check(!client.ReauthenticationRequired && !File.Exists(Path.Combine(lateDirectory, "membership-reauth-required.json")), "late unauthorized restore cannot undo deliberate logout or create a stale banner");
        }
    }

    private static async Task StorageRestoreFailures()
    {
        const string origin = "https://test.supabase.co/";
        var encryptedInvalidIssuer = new Dictionary<string, object> { { "version", 1 }, { "issuer", "https://other.supabase.co/" },
            { "user_id", Id }, { "email", Address }, { "refresh_token", "fixture-refresh" } };
        byte[] entropy = Encoding.UTF8.GetBytes("UsageWidget.Membership.v1|" + origin);
        byte[][] invalidFiles = {
            new byte[65537], Encoding.UTF8.GetBytes("truncated-not-dpapi"),
            ProtectedData.Protect(Encoding.UTF8.GetBytes("not-json"), entropy, DataProtectionScope.CurrentUser),
            ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(encryptedInvalidIssuer)), entropy, DataProtectionScope.CurrentUser),
            ProtectedData.Protect(Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("wrong-entropy"), DataProtectionScope.CurrentUser)
        };
        foreach (byte[] invalid in invalidFiles) {
            string directory = DirectoryForTest(), path = Path.Combine(directory, "membership-session.dat");
            File.WriteAllBytes(path, invalid);
            var handler = new FakeHandler();
            using (var client = new MembershipClient(directory, false, Config(origin), handler, () => 0)) {
                int changes = 0; client.Changed += (s, e) => changes++;
                await client.RestoreAsync();
                Check(client.SessionRestoreFailed && client.LastError == MembershipClient.SessionRestoreFailureMessage, "invalid local session exposes a bounded actionable storage error");
                Check(!client.IsSignedIn && !client.HasPro && !client.IsAdmin && handler.Calls == 0, "storage failure never requests auth or grants privileges");
                Check(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(invalid), "failed restoration preserves the original session byte-for-byte");
                Check(changes > 0, "storage failure notifies the UI even though no identity was restored");
                await client.SignOutAsync();
                Check(!client.SessionRestoreFailed && String.IsNullOrEmpty(client.LastError) && !File.Exists(path), "deliberate signout clears failed-restoration state and session");
            }
        }
        string absent = Path.Combine(DirectoryForTest(), "not-created");
        var unused = new FakeHandler();
        using (var client = new MembershipClient(absent, false, Config(origin), unused, () => 0)) {
            await client.RestoreAsync();
            Check(!client.SessionRestoreFailed && String.IsNullOrEmpty(client.LastError) && unused.Calls == 0, "new installation with no session directory remains a normal guest");
        }
        string signedOutDirectory = DirectoryForTest(), tombstone = Path.Combine(signedOutDirectory, "membership-session.dat");
        File.WriteAllBytes(tombstone, new byte[0]);
        var signedOutHandler = new FakeHandler();
        using (var signedOut = new MembershipClient(signedOutDirectory, false, Config(origin), signedOutHandler, () => 0)) {
            await signedOut.RestoreAsync();
            Check(!signedOut.IsSignedIn && !signedOut.HasPro && !signedOut.IsAdmin && signedOutHandler.Calls == 0, "empty logout tombstone never restores identity, privileges, or sends auth requests");
            Check(!signedOut.SessionRestoreFailed && String.IsNullOrEmpty(signedOut.LastError), "intentional logout tombstone is a normal guest without a restoration warning");
            Check(File.Exists(tombstone) && new FileInfo(tombstone).Length == 0, "restoration preserves the logout tombstone exactly");
        }
        string lockedDirectory = DirectoryForTest(), lockedPath = Path.Combine(lockedDirectory, "membership-session.dat");
        byte[] original = Encoding.UTF8.GetBytes("locked-session-fixture"); File.WriteAllBytes(lockedPath, original);
        var recovery = new FakeHandler();
        using (var client = new MembershipClient(lockedDirectory, false, Config(origin), recovery, () => 0)) {
            using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                await client.RestoreAsync();
                Check(client.SessionRestoreFailed && recovery.Calls == 0 && !client.HasPro, "temporarily unreadable session reports storage failure without deletion attempts");
            }
            Check(Convert.ToBase64String(File.ReadAllBytes(lockedPath)) == Convert.ToBase64String(original), "locked session survives the failed restore unchanged");
            LoginReplies(recovery, "free", "free", null);
            await client.VerifyCodeAsync(Address, "123456");
            Check(client.IsSignedIn && !client.SessionRestoreFailed && String.IsNullOrEmpty(client.LastError), "successful sign-in and storage clear the restore warning");
            Check(!client.HasPro, "recovering a saved sign-in never implies a paid entitlement");
        }
    }

    private static HttpResponseMessage Limited(string body, int status = 429, int? seconds = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (seconds.HasValue) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds.Value));
        return response;
    }
    private static async Task<MembershipRateLimitException> RateFailure(Func<Task> action)
    {
        try { await action(); throw new Exception("Expected rate limit"); }
        catch (MembershipRateLimitException error) {
            Check(!error.Message.Contains("server-secret") && !error.ErrorCode.Contains("server-secret"), "rate-limit errors never expose response text or unknown codes");
            return error;
        }
    }
    private static async Task RateLimits()
    {
        var clock = new Clock(); var handler = new FakeHandler();
        using (var client = new MembershipClient(DirectoryForTest(), false, Config("https://test.supabase.co"), handler, clock.Read)) {
            handler.Add(request => Task.FromResult(Limited("{\"error_code\":\"over_email_send_rate_limit\",\"msg\":\"server-secret\"}", 429, 45)));
            var error = await RateFailure(() => client.SendCodeAsync(Address));
            Check(error.ErrorCode == "over_email_send_rate_limit" && error.RetryAfterSeconds == 45 && client.EmailSendRetrySeconds == 45, "allowlisted code and Retry-After seconds are preserved");
            Check(client.CodeVerificationRetrySeconds == 0, "send limit does not block verification");
            await RateFailure(() => client.SendCodeAsync("different@example.com"));
            Check(handler.Calls == 1, "changing email cannot bypass this client's send countdown");
            clock.Milliseconds = 44001;
            Check(client.EmailSendRetrySeconds == 1, "countdown rounds up remaining fractional seconds");
            clock.Milliseconds = 45000;
            handler.Reply(new Dictionary<string, object>()); await client.SendCodeAsync(Address);
            Check(client.EmailSendRetrySeconds == 60, "successful send retains the 60-second cooldown");
            handler.Add(request => Task.FromResult(Limited("{\"code\":\"over_request_rate_limit\"}", 429, 120)));
            error = await RateFailure(() => client.VerifyCodeAsync(Address, "123456"));
            Check(error.ErrorCode == "over_request_rate_limit" && client.CodeVerificationRetrySeconds == 120 && client.EmailSendRetrySeconds == 60, "verification limit has its own deadline and supports Supabase code field");
            clock.Milliseconds += 60000;
            Check(client.EmailSendRetrySeconds == 0 && client.CodeVerificationRetrySeconds == 60, "send countdown can finish while verification remains limited");
            handler.Reply(new Dictionary<string, object>()); await client.SendCodeAsync(Address);
            Check(client.CodeVerificationRetrySeconds == 60, "a newly requested code does not reset verification backoff");
            await RateFailure(() => client.VerifyCodeAsync(Address, "123456"));
            Check(handler.Calls == 4 && !client.IsSignedIn, "verification during cooldown never reaches transport or creates a session");
        }
        foreach (string body in new[] { "{\"error_code\":\"server-secret\",\"message\":\"server-secret\"}", "<html>server-secret</html>", new string('x', 262145) }) {
            handler = new FakeHandler(); clock = new Clock();
            handler.Add(request => Task.FromResult(Limited(body)));
            using (var client = new MembershipClient(DirectoryForTest(), false, Config("https://test.supabase.co"), handler, clock.Read)) {
                var error = await RateFailure(() => client.VerifyCodeAsync(Address, "123456"));
                Check(error.ErrorCode == "rate_limited" && error.RetryAfterSeconds == 60 && client.CodeVerificationRetrySeconds == 60, "unknown, malformed and oversized 429 bodies retain safe 60-second fallback");
            }
        }
        foreach (bool past in new[] { false, true }) {
            handler = new FakeHandler(); clock = new Clock();
            handler.Add(request => {
                var response = Limited("{}"); var date = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
                response.Headers.Date = date;
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date.AddSeconds(past ? -10 : 125));
                return Task.FromResult(response);
            });
            using (var client = new MembershipClient(DirectoryForTest(), false, Config("https://test.supabase.co"), handler, clock.Read)) {
                var error = await RateFailure(() => client.SendCodeAsync(Address));
                Check(error.RetryAfterSeconds == (past ? 60 : 125), "HTTP-date Retry-After uses server Date despite local clock skew; stale date falls back safely");
            }
        }
        handler = new FakeHandler(); clock = new Clock();
        handler.Add(request => Task.FromResult(Limited("{\"error_code\":\"over_email_send_rate_limit\"}", 400, 75)));
        using (var client = new MembershipClient(DirectoryForTest(), false, Config("https://test.supabase.co"), handler, clock.Read)) {
            Check((await RateFailure(() => client.SendCodeAsync(Address))).RetryAfterSeconds == 75, "known rate-limit code maps safely even on another auth error status");
        }
        foreach (bool verifying in new[] { false, true }) {
            handler = new FakeHandler(); clock = new Clock(); var deferred = new TaskCompletionSource<HttpResponseMessage>();
            handler.Add(request => deferred.Task);
            using (var client = new MembershipClient(DirectoryForTest(), false, Config("https://test.supabase.co"), handler, clock.Read)) {
                Task first = verifying ? client.VerifyCodeAsync(Address, "123456") : client.SendCodeAsync(Address);
                await ExpectSafeFailure(() => client.SendCodeAsync(Address));
                await ExpectSafeFailure(() => client.VerifyCodeAsync(Address, "123456"));
                Check(handler.Calls == 1, "overlapping email actions are rejected rather than queued or replacing the pending login");
                deferred.SetResult(verifying ? Limited("{\"error_code\":\"otp_expired\",\"msg\":\"server-secret\"}", 403) : Json(new Dictionary<string, object>()));
                if (verifying) await ExpectSafeFailure(() => first); else await first;
                Check(client.CodeVerificationRetrySeconds == 0, "an ordinary invalid code does not create a local account lockout");
            }
        }
    }
    private static async Task VerificationRecoveryHints()
    {
        var handler=new FakeHandler(); var clock=new Clock();
        using(var client=new MembershipClient(DirectoryForTest(),false,Config("https://test.supabase.co"),handler,clock.Read)) {
            LoginReplies(handler,"pro","active","2026-10-12T00:00:00Z"); await client.VerifyCodeAsync(Address,"123456");
            Check(!client.VerificationRetrySoon && client.VerificationRetrySeconds==0,"Verified membership has no recovery hint or wait");
            handler.Fail(); await client.RefreshAsync();
            Check(client.VerificationRetrySoon && client.HasPro,"Transport failure advertises recoverable verification without discarding a bounded server grant");
            handler.Add(request=>Task.FromResult(Limited("{\"msg\":\"server-secret\"}",429,125))); await client.RefreshAsync();
            int limitedCalls=handler.Calls;
            Check(client.VerificationRetrySeconds==125 && !client.VerificationRetrySoon,"Verification 429 uses the server deadline and disables fast recovery hint");
            await client.RefreshAsync(); clock.Milliseconds=124001; await client.RefreshAsync();
            Check(handler.Calls==limitedCalls && client.VerificationRetrySeconds==1,"Direct refresh calls and fractional last second cannot bypass verification Retry-After");
            clock.Milliseconds=125000; handler.Reply(Membership("pro","active","2026-10-12T00:00:00Z",Id)); await client.RefreshAsync();
            Check(handler.Calls==limitedCalls+1 && client.VerificationRetrySeconds==0 && !client.VerificationRetrySoon,"Fresh server confirmation clears recovery scheduling state");
            handler.Status(HttpStatusCode.InternalServerError); await client.RefreshAsync();
            Check(client.VerificationRetrySoon,"Server 5xx is recoverable without inventing entitlement");
            handler.Reply(new Dictionary<string,object>()); await client.RefreshAsync();
            Check(!client.VerificationRetrySoon && !client.HasPro,"Malformed entitlement is not treated as a network recovery and cannot keep Pro");
            handler.Status(HttpStatusCode.Forbidden); await client.RefreshAsync();
            Check(!client.IsSignedIn && !client.VerificationRetrySoon && client.VerificationRetrySeconds==0,"Rejected identity clears all recovery hints and deadlines");
        }
    }
    private sealed class Clock { internal long Milliseconds; internal long Read() { return Milliseconds; } }
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> replies = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
        internal readonly List<string> Paths = new List<string>();
        internal int Calls;
        internal void Add(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) { replies.Enqueue(reply); }
        internal void Reply(object value) { Add(request => Task.FromResult(Json(value))); }
        internal void Status(HttpStatusCode status) { Add(request => Task.FromResult(new HttpResponseMessage(status))); }
        internal void Fail() { Add(request => { throw new HttpRequestException("secret provider body must never be shown"); }); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Paths.Add(request.RequestUri.PathAndQuery);
            if (replies.Count == 0) throw new Exception("Unexpected request: " + request.RequestUri.AbsolutePath);
            return replies.Dequeue()(request);
        }
    }
}
