using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Only this build-time public configuration is used. A file beside the app cannot
    // redirect stored login credentials to a different server or grant an entitlement.
    internal sealed class MembershipConfiguration
    {
        internal Uri Origin { get; private set; }
        internal string PublishableKey { get; private set; }
        internal bool GoogleEnabled { get; private set; }
        internal bool EmailEnabled { get; private set; }
        internal bool IsConfigured { get { return Origin != null && PublishableKey != null; } }
        internal static MembershipConfiguration FromValues(string url, string key, bool googleEnabled = false, bool emailEnabled = true)
        {
            var result = new MembershipConfiguration();
            Uri origin;
            url = (url ?? "").Trim(); key = (key ?? "").Trim();
            if (url.Length == 0 || key.Length == 0) return result;
            if (!Uri.TryCreate(url, UriKind.Absolute, out origin) || origin.Scheme != "https" || origin.IsLoopback ||
                !String.IsNullOrEmpty(origin.UserInfo) || !String.IsNullOrEmpty(origin.Query) ||
                !String.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/" || key.Length > 12000 ||
                key.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) >= 0) return result;
            bool publicKey = Regex.IsMatch(key, @"\Asb_publishable_[A-Za-z0-9_-]{8,}\z");
            if (!publicKey && !key.StartsWith("sb_", StringComparison.Ordinal)) {
                try {
                    string[] parts = key.Split('.');
                    if (parts.Length == 3) {
                        string part = parts[1].Replace('-', '+').Replace('_', '/');
                        part = part.PadRight((part.Length + 3) / 4 * 4, '=');
                        var payload = MembershipClient.ParseJson(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
                        publicKey = MembershipClient.StringValue(payload, "role") == "anon";
                    }
                } catch { publicKey = false; }
            }
            if (!publicKey) return result;
            result.Origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
            result.PublishableKey = key;
            result.GoogleEnabled = googleEnabled;
            result.EmailEnabled = emailEnabled;
            return result;
        }
        internal static MembershipConfiguration LoadEmbedded()
        {
            try {
                using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsageWidget.MembershipConfig.json")) {
                    if (source == null || source.Length > 16384) return new MembershipConfiguration();
                    using (var reader = new StreamReader(source, Encoding.UTF8)) {
                        var data = MembershipClient.ParseJson(reader.ReadToEnd());
                        object google, email;
                        return FromValues(MembershipClient.StringValue(data, "SupabaseUrl"), MembershipClient.StringValue(data, "PublishableKey"),
                            data.TryGetValue("GoogleEnabled", out google) && google is bool && (bool)google,
                            !data.TryGetValue("EmailEnabled", out email) || (email is bool && (bool)email));
                    }
                }
            } catch { return new MembershipConfiguration(); }
        }
    }

    public sealed class MembershipRateLimitException : InvalidOperationException
    {
        public string ErrorCode { get; private set; }
        public int RetryAfterSeconds { get; private set; }
        internal MembershipRateLimitException(string code, int seconds, bool sending)
            : base(sending ? "인증번호 요청이 일시 제한되었습니다. 표시된 시간이 지난 뒤 다시 보내 주세요." :
                "인증번호 확인이 일시 제한되었습니다. 표시된 시간이 지난 뒤 다시 인증해 주세요.")
        { ErrorCode = code; RetryAfterSeconds = seconds; }
    }

    public sealed class MembershipClient : IDisposable
    {
        private const int ResponseLimit = 262144;
        private const int SessionLimit = 65536;
        private const long GraceMilliseconds = 15L * 60 * 1000;
        private readonly object sync = new object();
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly HttpClient http;
        private readonly MembershipConfiguration configuration;
        private readonly Func<long> now;
        private readonly bool demo;
        private readonly string sessionPath;
        private readonly string reauthenticationPath;
        private CancellationTokenSource epochCancellation = new CancellationTokenSource();
        private CancellationTokenSource googleCancellation;
        private int epoch;
        private bool disposed;
        private string accessToken, refreshToken, userId, email;
        private long accessDeadline, entitlementDeadline, subscriptionDeadline;
        private bool verifiedPro, verifiedAdmin;
        private bool sessionStorageFailed;
        private bool sessionRestoreFailed;
        private bool reauthenticationRequired;
        internal const string SessionRestoreFailureMessage = "저장된 로그인 정보를 불러오지 못했습니다. 연결 설정은 유지됩니다. 계정에서 다시 로그인하면 이용 권한을 확인합니다.";
        internal const string ReauthenticationRequiredMessage = "로그인이 만료되었거나 서버에서 종료되었습니다. 연결 설정과 이력은 유지됩니다. 계정에서 다시 로그인해 주세요.";
        private string status = "guest", lastError;
        private DateTimeOffset? expiresAt;
        private DateTimeOffset? lastVerifiedAt;
        private bool verificationDelayed;
        private bool verificationRetrySoon;
        private long verificationRetryDeadline;
        // UX backoff only. Authentication abuse limits must be enforced by the server.
        private bool emailOperationPending;
        private long emailSendDeadline, codeVerificationDeadline;

        public event EventHandler Changed;
        public bool IsConfigured { get { return !demo && configuration.IsConfigured; } }
        public bool IsGoogleConfigured { get { return IsConfigured && configuration.GoogleEnabled; } }
        public bool IsEmailConfigured { get { return IsConfigured && configuration.EmailEnabled; } }
        public bool IsGoogleSignInPending { get { lock (sync) return !disposed && googleCancellation != null; } }
        public int EmailSendRetrySeconds { get { lock (sync) return RemainingEmailWait(emailSendDeadline); } }
        public int CodeVerificationRetrySeconds { get { lock (sync) return RemainingEmailWait(codeVerificationDeadline); } }
        public bool IsSignedIn { get { lock (sync) return !disposed && userId != null && refreshToken != null; } }
        public string Email { get { lock (sync) return email ?? ""; } }
        // A non-reversible local analysis owner marker. Never store an email,
        // access token or refresh token in the usage journal.
        internal string AnalysisOwner { get { lock (sync) {
            if (disposed || String.IsNullOrEmpty(userId)) return "";
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes("nephylo-analysis-owner:" + userId))).Replace("-","").ToLowerInvariant();
        } } }
        public bool HasPro { get { lock (sync) return IsProCurrent(); } }
        public bool IsAdmin { get { lock (sync) return !disposed && userId != null && verifiedAdmin && now() < entitlementDeadline; } }
        public DateTimeOffset? ExpiresAt { get { lock (sync) return expiresAt; } }
        public string LastError { get { lock (sync) return lastError ?? ""; } }
        public bool SessionRestoreFailed { get { lock (sync) return !disposed && sessionRestoreFailed; } }
        public bool ReauthenticationRequired { get { lock (sync) return !disposed && reauthenticationRequired; } }
        public DateTimeOffset? LastVerifiedAt { get { lock (sync) return lastVerifiedAt; } }
        public bool VerificationDelayed { get { lock (sync) return !disposed && userId != null && (verificationDelayed || now() >= entitlementDeadline); } }
        // Scheduling hints only; neither a restored network nor a timer grants entitlement.
        internal bool VerificationRetrySoon { get { lock (sync) return !disposed && userId != null && verificationRetrySoon; } }
        internal int VerificationRetrySeconds { get { lock (sync) return disposed || userId == null ? 0 : RemainingEmailWait(verificationRetryDeadline); } }
        public string Status {
            get { lock (sync) {
                if (disposed || userId == null) return "guest";
                if (verifiedPro && now() >= subscriptionDeadline) return "expired";
                if (verifiedPro && now() >= entitlementDeadline) return "unverified";
                return status;
            } }
        }

        public MembershipClient(string directory, bool demo)
            : this(directory, demo, MembershipConfiguration.LoadEmbedded(),
                  new HttpClientHandler { AllowAutoRedirect = false }, CreateMonotonicClock()) { }

        internal MembershipClient(string directory, bool preview, MembershipConfiguration settings,
            HttpMessageHandler handler, Func<long> monotonicMilliseconds)
        {
            demo = preview;
            configuration = settings ?? new MembershipConfiguration();
            sessionPath = Path.Combine(directory, "membership-session.dat");
            reauthenticationPath = Path.Combine(directory, "membership-reauth-required.json");
            now = monotonicMilliseconds ?? CreateMonotonicClock();
            http = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(25) };
        }

        private static Func<long> CreateMonotonicClock()
        {
            var clock = Stopwatch.StartNew();
            return () => clock.ElapsedMilliseconds;
        }
        private bool IsProCurrent()
        {
            return !disposed && IsConfigured && userId != null && verifiedPro && now() < entitlementDeadline && now() < subscriptionDeadline;
        }

        public async Task SendCodeAsync(string address)
        {
            RequireEmailConfigured();
            address = NormalizeEmail(address);
            int generation; CancellationToken cancellation;
            Capture(out generation, out cancellation);
            EnterEmailOperation(true);
            bool entered = false;
            try {
                await operation.WaitAsync(cancellation); entered = true;
                EnsureCurrent(generation);
                // Legacy sign-in reaches existing accounts only; it never registers a new one.
                await RequestEmailAsync("auth/v1/otp", new Dictionary<string, object> { { "email", address }, { "create_user", false } }, generation, true, cancellation);
                lock (sync) { EnsureCurrent(generation); emailSendDeadline = now() + 60000; lastError = null; }
            } catch (OperationCanceledException) { if (!IsCurrent(generation)) return; SetError(generation, "인증 서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요."); throw new InvalidOperationException(LastError); }
            catch (MembershipRateLimitException error) { if (!IsCurrent(generation)) return; SetError(generation, error.Message); throw; }
            catch (Exception error) { if (!IsCurrent(generation)) return; SetError(generation, SafeMessage(error, true)); throw new InvalidOperationException(LastError); }
            finally { if (entered) operation.Release(); lock (sync) emailOperationPending = false; Notify(); }
        }

        public async Task VerifyCodeAsync(string address, string code)
        {
            RequireEmailConfigured();
            address = NormalizeEmail(address);
            code = (code ?? "").Trim();
            if (code.Length != 6 || !AllDigits(code)) throw new InvalidOperationException("이메일로 받은 6자리 숫자 인증 코드를 입력해 주세요.");
            EnterEmailOperation(false);
            int generation; CancellationToken cancellation;
            BeginNewEpoch(out generation, out cancellation);
            // Account changes lose the former account's cached privileges immediately.
            EraseSession(generation); Notify();
            bool entered = false;
            try {
                await operation.WaitAsync(cancellation); entered = true;
                EnsureCurrent(generation);
                var response = await RequestEmailAsync("auth/v1/verify", new Dictionary<string, object> { { "email", address }, { "token", code }, { "type", "email" } }, generation, false, cancellation);
                ApplySession(response, generation, null, address);
                PersistSession(generation);
                await FetchMembershipAsync(generation, cancellation);
            } catch (MembershipRateLimitException error) {
                if (!IsCurrent(generation)) return;
                SetError(generation, error.Message); throw;
            } catch (Exception error) {
                if (!IsCurrent(generation)) return;
                HandleFailure(generation, error);
                throw new InvalidOperationException(LastError);
            } finally { if (entered) operation.Release(); lock (sync) emailOperationPending = false; Notify(); }
        }

        private int RemainingEmailWait(long deadline)
        { return (int)Math.Max(0, Math.Min(86400, Math.Ceiling((deadline - now()) / 1000.0))); }

        private void EnterEmailOperation(bool sending)
        {
            lock (sync) {
                EnsureNotDisposed();
                if (emailOperationPending) throw new InvalidOperationException("인증 요청을 처리하고 있습니다. 완료될 때까지 기다려 주세요.");
                int wait = RemainingEmailWait(sending ? emailSendDeadline : codeVerificationDeadline);
                if (wait > 0) throw new MembershipRateLimitException("rate_limited", wait, sending);
                emailOperationPending = true;
            }
        }

        private async Task<Dictionary<string, object>> RequestEmailAsync(string relative, object body, int generation, bool sending, CancellationToken cancellation)
        {
            try { return await RequestAsync(relative, body, null, cancellation); }
            catch (MembershipHttpException error) {
                if (error.Code != 429 && error.ErrorCode == null) throw;
                int seconds = error.RetryAfterSeconds;
                lock (sync) {
                    EnsureCurrent(generation);
                    if (sending) emailSendDeadline = now() + seconds * 1000L;
                    else codeVerificationDeadline = now() + seconds * 1000L;
                }
                throw new MembershipRateLimitException(error.ErrorCode ?? "rate_limited", seconds, sending);
            }
        }

        public Task SignInWithGoogleAsync()
        {
            return SignInWithGoogleAsync(uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), TimeSpan.FromMinutes(3));
        }

        // Browser and deadline injection are internal so tests never open an external account.
        internal async Task SignInWithGoogleAsync(Action<Uri> openBrowser, TimeSpan timeout)
        {
            EnsureNotDisposed();
            if (!IsGoogleConfigured) throw new InvalidOperationException("Google 로그인은 인증 서버와 Google 연결 설정 후 사용할 수 있습니다.");
            if (openBrowser == null || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(3)) throw new ArgumentException();
            int generation; CancellationToken cancellation;
            BeginNewEpoch(out generation, out cancellation);
            EraseSession(generation);
            using (var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                lock (sync) { EnsureCurrent(generation); googleCancellation = pending; }
                pending.CancelAfter(timeout);
                Notify();
                bool entered = false;
                try {
                    await operation.WaitAsync(pending.Token); entered = true;
                    EnsureCurrent(generation);
                    using (var flow = new MembershipGoogleFlow(configuration.Origin)) {
                        string code = await flow.ReceiveCodeAsync(openBrowser, pending.Token);
                        EnsureCurrent(generation);
                        var response = await RequestAsync("auth/v1/token?grant_type=pkce", new Dictionary<string, object> {
                            { "auth_code", code }, { "code_verifier", flow.Verifier }
                        }, null, pending.Token);
                        // A callback or decoded JWT alone cannot establish identity or grant Pro.
                        string access = StringValue(response, "access_token");
                        if (!ValidToken(access)) throw new InvalidDataException();
                        var identity = await RequestAsync("auth/v1/user", null, access, pending.Token, HttpMethod.Get);
                        var supplied = ObjectValue(response, "user");
                        DateTimeOffset confirmed;
                        string verifiedEmail = StringValue(identity, "email");
                        if (!SameId(StringValue(identity, "id"), StringValue(supplied, "id")) ||
                            !TryTimestamp(StringValue(identity, "email_confirmed_at"), out confirmed) ||
                            String.IsNullOrEmpty(verifiedEmail) ||
                            !String.Equals(verifiedEmail, StringValue(supplied, "email"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        pending.Token.ThrowIfCancellationRequested();
                        ApplySession(response, generation, StringValue(identity, "id"), verifiedEmail);
                        PersistSession(generation);
                        await FetchMembershipAsync(generation, pending.Token);
                    }
                } catch (Exception error) {
                    if (!IsCurrent(generation)) return;
                    HandleFailure(generation, error);
                    SetError(generation, error is OperationCanceledException ? "Google 로그인 시간이 만료되었습니다. 다시 시도해 주세요." :
                        error is MembershipGoogleException ? ((MembershipGoogleException)error).SafeMessage :
                        IsEmailConfigured ? "Google 로그인을 완료하지 못했습니다. 다시 시도하거나 이메일 인증을 이용해 주세요." : "Google 로그인을 완료하지 못했습니다. 다시 시도해 주세요.");
                    throw new InvalidOperationException(LastError);
                } finally {
                    lock (sync) { if (Object.ReferenceEquals(googleCancellation, pending)) googleCancellation = null; }
                    if (entered) operation.Release();
                    Notify();
                }
            }
        }

        public void CancelGoogleSignIn()
        {
            int generation;
            lock (sync) {
                if (disposed || googleCancellation == null) return;
                // Invalidate before closing sockets; a callback already being exchanged must not log in later.
                epoch++; generation = epoch; epochCancellation.Cancel();
                epochCancellation = new CancellationTokenSource();
                googleCancellation.Cancel(); googleCancellation = null;
                ClearSessionMemory(); lastError = null;
            }
            EraseSession(generation); Notify();
        }

        public async Task RestoreAsync()
        {
            if (!IsConfigured || disposed) return;
            int generation; CancellationToken cancellation;
            Capture(out generation, out cancellation);
            await operation.WaitAsync();
            try {
                EnsureCurrent(generation);
                lock (sync) { if (userId != null) return; }
                bool restoreFailed;
                var stored = ReadSession(out restoreFailed);
                if (stored == null) {
                    lock (sync) {
                        EnsureCurrent(generation);
                        sessionRestoreFailed = restoreFailed;
                        reauthenticationRequired = !restoreFailed && ReadReauthenticationNotice();
                        lastError = restoreFailed ? SessionRestoreFailureMessage : reauthenticationRequired ? ReauthenticationRequiredMessage : null;
                    }
                    return;
                }
                lock (sync) {
                    EnsureCurrent(generation);
                    sessionRestoreFailed = false;
                    userId = StringValue(stored, "user_id"); email = StringValue(stored, "email"); refreshToken = StringValue(stored, "refresh_token"); status = "unverified";
                }
                Notify(); // Explain the startup verification wait without granting cached Pro.
                await RenewSessionAsync(generation, cancellation);
                await FetchMembershipAsync(generation, cancellation);
            } catch (Exception error) { if (IsCurrent(generation)) HandleFailure(generation, error); }
            finally { operation.Release(); Notify(); }
        }

        public async Task RefreshAsync()
        {
            if (!IsConfigured || !IsSignedIn) return;
            int generation; CancellationToken cancellation;
            Capture(out generation, out cancellation);
            await operation.WaitAsync();
            try {
                EnsureCurrent(generation);
                bool renew;
                lock (sync) { if (userId == null || now() < verificationRetryDeadline) return; renew = accessToken == null || now() + 60000 >= accessDeadline; }
                if (renew) await RenewSessionAsync(generation, cancellation);
                bool retryUnauthorized = false;
                try { await FetchMembershipAsync(generation, cancellation); }
                catch (MembershipHttpException error) {
                    if (error.Code != 401) throw;
                    // Drop privileges on the first rejected access token, even if renewal fails.
                    lock (sync) { EnsureCurrent(generation); ClearEntitlement(); }
                    Notify();
                    retryUnauthorized = true;
                }
                if (retryUnauthorized) {
                    await RenewSessionAsync(generation, cancellation);
                    await FetchMembershipAsync(generation, cancellation);
                }
            } catch (Exception error) { if (IsCurrent(generation)) HandleFailure(generation, error); }
            finally { operation.Release(); Notify(); }
        }

        public async Task SignOutAsync()
        {
            string token;
            lock (sync) { token = accessToken; }
            int generation; CancellationToken cancellation;
            BeginNewEpoch(out generation, out cancellation);
            lock (sync) { if (IsCurrent(generation)) { sessionRestoreFailed = false; SetReauthenticationNotice(false); } }
            bool erased = EraseSession(generation);
            if (!erased) SetError(generation, "이 PC의 로그인 정보를 지우지 못했습니다. 파일 권한을 확인해 주세요.");
            Notify();
            if (!IsConfigured || String.IsNullOrEmpty(token)) return;
            try { await RequestAsync("auth/v1/logout?scope=local", null, token, cancellation); }
            catch { /* Local credentials and privileges have already been removed. */ }
        }

        private async Task RenewSessionAsync(int generation, CancellationToken cancellation)
        {
            string refresh, expected;
            lock (sync) { EnsureCurrent(generation); refresh = refreshToken; expected = userId; }
            if (String.IsNullOrEmpty(refresh)) throw new MembershipHttpException(401);
            var response = await RequestAsync("auth/v1/token?grant_type=refresh_token", new Dictionary<string, object> { { "refresh_token", refresh } }, null, cancellation);
            ApplySession(response, generation, expected, null);
            PersistSession(generation);
        }

        private void ApplySession(Dictionary<string, object> response, int generation, string expectedId, string expectedEmail)
        {
            var user = ObjectValue(response, "user");
            string id = StringValue(user, "id"), nextEmail = StringValue(user, "email");
            string access = StringValue(response, "access_token"), refresh = StringValue(response, "refresh_token");
            Guid parsed;
            double seconds = NumberValue(response, "expires_in");
            if (!Guid.TryParse(id, out parsed) || String.IsNullOrEmpty(nextEmail) || nextEmail.Length > 254 ||
                !ValidToken(access) || !ValidToken(refresh) || Double.IsNaN(seconds) || seconds < 1 || seconds > 86400 ||
                (expectedId != null && !SameId(expectedId, id)) ||
                (expectedEmail != null && !String.Equals(expectedEmail, nextEmail, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException();
            lock (sync) {
                EnsureCurrent(generation);
                accessToken = access; refreshToken = refresh; userId = id; email = nextEmail;
                sessionRestoreFailed = false;
                SetReauthenticationNotice(false);
                accessDeadline = now() + (long)(seconds * 1000); status = verifiedPro ? status : "unverified";
            }
        }

        private async Task FetchMembershipAsync(int generation, CancellationToken cancellation)
        {
            string token, expectedId;
            lock (sync) { EnsureCurrent(generation); token = accessToken; expectedId = userId; }
            long requestStarted = now();
            var data = await RequestAsync("rest/v1/rpc/get_my_membership", new Dictionary<string, object>(), token, cancellation);
            string plan = StringValue(data, "plan"), state = StringValue(data, "status");
            DateTimeOffset serverTime, end;
            object adminValue;
            bool validState = state == "free" || state == "active" || state == "canceled" || state == "expired" || state == "revoked";
            if (!SameId(expectedId, StringValue(data, "user_id")) || !validState || (plan != "free" && plan != "pro") ||
                !TryTimestamp(StringValue(data, "server_time"), out serverTime) ||
                !data.TryGetValue("is_admin", out adminValue) || !(adminValue is bool)) throw new InvalidDataException();
            string expiration = StringValue(data, "expires_at");
            DateTimeOffset? parsedExpiration = null;
            if (expiration != null) { if (!TryTimestamp(expiration, out end)) throw new InvalidDataException(); parsedExpiration = end; }
            bool pro = plan == "pro" && (state == "active" || state == "canceled") && parsedExpiration.HasValue && parsedExpiration.Value > serverTime;
            if (plan == "pro" && !pro) throw new InvalidDataException();
            long remaining = pro ? Math.Max(0, (long)Math.Min((parsedExpiration.Value - serverTime).TotalMilliseconds, Int64.MaxValue / 2) - Math.Max(0, now() - requestStarted)) : 0;
            lock (sync) {
                EnsureCurrent(generation);
                verifiedPro = pro; verifiedAdmin = (bool)adminValue; status = state; expiresAt = parsedExpiration;
                long current = now(); entitlementDeadline = Math.Min(requestStarted + GraceMilliseconds, current + (pro ? remaining : GraceMilliseconds));
                subscriptionDeadline = current + remaining;
                lastVerifiedAt = serverTime; verificationDelayed = false;
                verificationRetrySoon = false; verificationRetryDeadline = 0;
                lastError = sessionStorageFailed ? "로그인 정보를 이 PC에 저장하지 못했습니다. 다음 실행 시 다시 로그인해 주세요." : null;
            }
        }

        private async Task<Dictionary<string, object>> RequestAsync(string relative, object body, string bearer, CancellationToken cancellation, HttpMethod method = null)
        {
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                bounded.CancelAfter(TimeSpan.FromSeconds(20));
                using (var request = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(configuration.Origin, relative))) {
                    request.Headers.TryAddWithoutValidation("apikey", configuration.PublishableKey);
                    if (!String.IsNullOrEmpty(bearer)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (method != HttpMethod.Get) request.Content = new StringContent(new JavaScriptSerializer().Serialize(body ?? new Dictionary<string, object>()), Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token)) {
                        if (!response.IsSuccessStatusCode) throw await ReadHttpErrorAsync(response, bounded.Token);
                        if (response.Content == null) return new Dictionary<string, object>();
                        if (response.Content.Headers.ContentLength > ResponseLimit) throw new InvalidDataException();
                        using (var source = await response.Content.ReadAsStreamAsync())
                        using (var buffer = new MemoryStream()) {
                            byte[] bytes = new byte[8192]; int count;
                            while ((count = await source.ReadAsync(bytes, 0, bytes.Length, bounded.Token)) > 0) {
                                if (buffer.Length + count > ResponseLimit) throw new InvalidDataException();
                                buffer.Write(bytes, 0, count);
                            }
                            EnsureNotDisposed();
                            if (buffer.Length == 0) return new Dictionary<string, object>();
                            return ParseJson(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
                        }
                    }
                }
            }
        }

        private static async Task<MembershipHttpException> ReadHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellation)
        {
            string code = null;
            try {
                if (response.Content != null && !(response.Content.Headers.ContentLength > ResponseLimit)) {
                    using (var source = await response.Content.ReadAsStreamAsync())
                    using (var buffer = new MemoryStream()) {
                        byte[] bytes = new byte[4096]; int count;
                        while ((count = await source.ReadAsync(bytes, 0, bytes.Length, cancellation)) > 0) {
                            if (buffer.Length + count > ResponseLimit) throw new InvalidDataException();
                            buffer.Write(bytes, 0, count);
                        }
                        var body = ParseJson(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
                        string candidate = StringValue(body, "error_code") ?? StringValue(body, "code");
                        // Never reflect arbitrary provider text, identifiers, or URLs in an exception.
                        if (candidate == "over_email_send_rate_limit" || candidate == "over_request_rate_limit") code = candidate;
                    }
                }
            } catch { /* HTTP status and safe fallback remain usable for malformed error bodies. */ }
            int seconds = 60;
            try {
                var retry = response.Headers.RetryAfter;
                if (retry != null) {
                    double value = retry.Delta.HasValue ? retry.Delta.Value.TotalSeconds : retry.Date.HasValue ?
                        (retry.Date.Value - (response.Headers.Date ?? DateTimeOffset.UtcNow)).TotalSeconds : 0;
                    if (value >= 0) seconds = (int)Math.Max(1, Math.Min(86400, Math.Ceiling(value)));
                }
            } catch { }
            return new MembershipHttpException((int)response.StatusCode, code, seconds);
        }

        private void HandleFailure(int generation, Exception error)
        {
            var rejected = error as MembershipHttpException;
            bool unauthorized = rejected != null && (rejected.Code == 401 || rejected.Code == 403 || rejected.Code == 400);
            bool transient = error is HttpRequestException || error is OperationCanceledException ||
                (error is IOException && !(error is InvalidDataException)) || (rejected != null && (rejected.Code == 429 || rejected.Code >= 500));
            lock (sync) {
                if (!IsCurrent(generation)) return;
                if (unauthorized) {
                    // An invalid OTP for a guest is not an expired restored session.
                    // Only rejection of an actual identity requires persistent reauthentication guidance.
                    if (userId != null && refreshToken != null) SetReauthenticationNotice(true);
                    ClearSessionMemory();
                }
                else if (!transient) ClearEntitlement();
                if (userId != null) verificationDelayed = true;
                verificationRetrySoon = userId != null && transient && (rejected == null || rejected.Code >= 500);
                if (userId != null && rejected != null && rejected.Code == 429)
                    verificationRetryDeadline = Math.Max(verificationRetryDeadline, now() + Math.Max(1, Math.Min(86400, rejected.RetryAfterSeconds)) * 1000L);
                lastError = unauthorized && reauthenticationRequired ? ReauthenticationRequiredMessage : SafeMessage(error, false);
            }
            if (unauthorized) EraseSession(generation);
        }

        private void PersistSession(int generation)
        {
            lock (sync) {
                EnsureCurrent(generation);
                var values = new Dictionary<string, object> { { "version", 1 }, { "issuer", configuration.Origin.AbsoluteUri },
                    { "user_id", userId }, { "email", email }, { "refresh_token", refreshToken } };
                try {
                    byte[] plaintext = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(values));
                    byte[] protectedBytes;
                    try { protectedBytes = ProtectedData.Protect(plaintext, Entropy(), DataProtectionScope.CurrentUser); }
                    finally { Array.Clear(plaintext, 0, plaintext.Length); }
                    AtomicWrite(protectedBytes);
                    sessionStorageFailed = false;
                    sessionRestoreFailed = false;
                } catch { sessionStorageFailed = true; lastError = "로그인 정보를 이 PC에 저장하지 못했습니다. 다음 실행 시 다시 로그인해 주세요."; }
            }
        }

        private Dictionary<string, object> ReadSession(out bool restoreFailed)
        {
            restoreFailed = false;
            try {
                using (var source = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    // EraseSession may leave an empty file if deletion is denied.
                    // This is an intentional local logout tombstone, not a broken login.
                    if (source.Length == 0) return null;
                    if (source.Length > SessionLimit) throw new InvalidDataException();
                    byte[] encrypted = new byte[(int)source.Length];
                    int read = 0, count;
                    while (read < encrypted.Length && (count = source.Read(encrypted, read, encrypted.Length - read)) > 0) read += count;
                    if (read != encrypted.Length) throw new InvalidDataException();
                    byte[] plaintext = ProtectedData.Unprotect(encrypted, Entropy(), DataProtectionScope.CurrentUser);
                    try {
                        var values = ParseJson(Encoding.UTF8.GetString(plaintext));
                        Guid parsed;
                        if (NumberValue(values, "version") != 1 || StringValue(values, "issuer") != configuration.Origin.AbsoluteUri ||
                            !Guid.TryParse(StringValue(values, "user_id"), out parsed) || !ValidToken(StringValue(values, "refresh_token")) ||
                            String.IsNullOrEmpty(StringValue(values, "email"))) throw new InvalidDataException();
                        return values;
                    } finally { Array.Clear(plaintext, 0, plaintext.Length); }
                }
            } catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch { restoreFailed = true; return null; }
        }

        private byte[] Entropy() { return Encoding.UTF8.GetBytes("UsageWidget.Membership.v1|" + configuration.Origin.AbsoluteUri); }
        private void AtomicWrite(byte[] bytes)
        { AtomicWrite(sessionPath, bytes); }
        private static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { target.Write(bytes, 0, bytes.Length); target.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // This origin-bound marker is presentation-only: it contains no identity, token or privileges.
        // It survives deletion of a rejected session so the next launch can still explain how to recover.
        private bool ReadReauthenticationNotice()
        {
            try {
                using (var source = new FileStream(reauthenticationPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (source.Length == 0 || source.Length > 4096) return false;
                    using (var reader = new StreamReader(source, Encoding.UTF8)) {
                        var data = ParseJson(reader.ReadToEnd());
                        return NumberValue(data, "version") == 1 && StringValue(data, "issuer") == configuration.Origin.AbsoluteUri;
                    }
                }
            } catch { return false; }
        }
        private void SetReauthenticationNotice(bool required)
        {
            reauthenticationRequired = required;
            if (demo || !configuration.IsConfigured) return;
            try {
                if (required) AtomicWrite(reauthenticationPath, Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(
                    new Dictionary<string, object> { { "version", 1 }, { "issuer", configuration.Origin.AbsoluteUri } })));
                else if (File.Exists(reauthenticationPath)) {
                    try { File.Delete(reauthenticationPath); }
                    catch { AtomicWrite(reauthenticationPath, new byte[0]); }
                }
            } catch { /* An advisory notice must never prevent clearing rejected tokens or completing sign-in. */ }
        }
        private bool EraseSession(int generation)
        {
            if (demo || !configuration.IsConfigured) return true;
            lock (sync) {
                if (!IsCurrent(generation)) return true;
                try { if (File.Exists(sessionPath)) File.Delete(sessionPath); return true; }
                catch { try { AtomicWrite(new byte[0]); return true; } catch { return false; } }
            }
        }

        private void Capture(out int generation, out CancellationToken cancellation)
        { lock (sync) { EnsureNotDisposed(); generation = epoch; cancellation = epochCancellation.Token; } }
        private void BeginNewEpoch(out int generation, out CancellationToken cancellation)
        {
            lock (sync) {
                EnsureNotDisposed(); epoch++; epochCancellation.Cancel(); epochCancellation = new CancellationTokenSource(); ClearSessionMemory(); lastError = null;
                generation = epoch; cancellation = epochCancellation.Token;
            }
        }
        private bool IsCurrent(int generation) { lock (sync) return !disposed && epoch == generation; }
        private void EnsureCurrent(int generation) { if (!IsCurrent(generation)) throw new OperationCanceledException(); }
        private void EnsureNotDisposed() { if (disposed) throw new ObjectDisposedException("MembershipClient"); }
        private void RequireEmailConfigured()
        {
            EnsureNotDisposed();
            if (!IsConfigured) throw new InvalidOperationException("이메일 로그인은 인증 서버 연결 후 사용할 수 있습니다. 무료 기능은 로그인 없이 이용할 수 있습니다.");
            if (!IsEmailConfigured) throw new InvalidOperationException("이메일 로그인 준비 중");
        }
        private void SetError(int generation, string value) { lock (sync) { if (IsCurrent(generation)) lastError = value; } }
        private void ClearEntitlement() { verifiedPro = false; verifiedAdmin = false; entitlementDeadline = 0; subscriptionDeadline = 0; expiresAt = null; status = userId == null ? "guest" : "unverified"; }
        private void ClearSessionMemory() { accessToken = null; refreshToken = null; userId = null; email = null; accessDeadline = 0; sessionStorageFailed = false; lastVerifiedAt = null; verificationDelayed = false; verificationRetrySoon = false; verificationRetryDeadline = 0; ClearEntitlement(); }
        private void Notify() { var handler = Changed; if (handler != null && !disposed) handler(this, EventArgs.Empty); }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; epoch++; epochCancellation.Cancel(); ClearSessionMemory(); }
            http.Dispose();
        }

        internal static Dictionary<string, object> ParseJson(string value)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = ResponseLimit, RecursionLimit = 12 };
            object parsed = serializer.DeserializeObject(value);
            var single = parsed as Dictionary<string, object>;
            if (single != null) return single;
            var array = parsed as object[];
            if (array != null && array.Length == 1 && array[0] is Dictionary<string, object>) return (Dictionary<string, object>)array[0];
            throw new InvalidDataException();
        }
        internal static string StringValue(Dictionary<string, object> data, string key)
        { object value; return data != null && data.TryGetValue(key, out value) ? value as string : null; }
        private static Dictionary<string, object> ObjectValue(Dictionary<string, object> data, string key)
        { object value; return data != null && data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        private static double NumberValue(Dictionary<string, object> data, string key)
        {
            object value; double number;
            return data != null && data.TryGetValue(key, out value) && Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) ? number : Double.NaN;
        }
        private static bool ValidToken(string value) { return value != null && value.Length >= 8 && value.Length <= 12000 && value.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) < 0; }
        private static bool SameId(string first, string second) { Guid a, b; return Guid.TryParse(first, out a) && Guid.TryParse(second, out b) && a == b; }
        private static bool AllDigits(string value) { foreach (char c in value) if (c < '0' || c > '9') return false; return true; }
        private static string NormalizeEmail(string value)
        {
            value = (value ?? "").Trim();
            try { var address = new MailAddress(value); if (value.Length > 254 || address.Address != value || value.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) >= 0) throw new FormatException(); return address.Address; }
            catch { throw new InvalidOperationException("올바른 이메일 주소를 입력해 주세요."); }
        }
        private static bool TryTimestamp(string value, out DateTimeOffset parsed)
        {
            parsed = default(DateTimeOffset);
            // A timezone is required; never interpret server timestamps in the PC's zone.
            return !String.IsNullOrEmpty(value) && (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || value.LastIndexOf('+') > 9 || value.LastIndexOf('-') > 9) &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
        }
        private static string SafeMessage(Exception error, bool sending)
        {
            var response = error as MembershipHttpException;
            if (response != null && response.Code == 429) return "요청이 많습니다. 잠시 기다린 뒤 다시 시도해 주세요.";
            if (response != null && (response.Code == 400 || response.Code == 401 || response.Code == 403 || response.Code == 422))
                return sending ? "인증 메일을 보내지 못했습니다. 이메일 주소와 서버 인증 설정을 확인해 주세요." : "인증 코드가 올바르지 않거나 로그인이 만료되었습니다. 다시 로그인해 주세요.";
            if (error is InvalidDataException || error is ArgumentException || error is FormatException) return "인증 서버의 응답을 확인할 수 없습니다. 관리자에게 문의해 주세요.";
            if (response != null && response.Code >= 300 && response.Code < 500) return "회원 서버가 아직 준비되지 않았습니다. 관리자에게 문의해 주세요.";
            return "회원 서버에 연결할 수 없습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.";
        }
        private sealed class MembershipHttpException : Exception
        {
            internal readonly int Code;
            internal readonly string ErrorCode;
            internal readonly int RetryAfterSeconds;
            internal MembershipHttpException(int code, string errorCode = null, int retryAfterSeconds = 60)
            { Code = code; ErrorCode = errorCode; RetryAfterSeconds = retryAfterSeconds; }
        }
    }

    internal sealed class MembershipGoogleException : Exception
    {
        internal readonly string SafeMessage;
        internal MembershipGoogleException(string message) { SafeMessage = message; }
    }

    // RFC 8252 loopback redirect. Only 127.0.0.1 is bound, for one short-lived flow.
    // The auth code, state, and verifier are never logged or written to disk.
    internal sealed class MembershipGoogleFlow : IDisposable
    {
        private const int HeaderLimit = 8192;
        private readonly TcpListener listener;
        private readonly string callbackPath, state, authority;
        private string verifier;
        internal string Verifier { get { return verifier; } }
        internal Uri AuthorizationUri { get; private set; }

        internal MembershipGoogleFlow(Uri origin)
        {
            verifier = RandomValue(48); state = RandomValue(32);
            callbackPath = "/auth/google/" + RandomValue(24);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(4); }
            catch { listener.Stop(); throw new MembershipGoogleException("로그인 응답을 받을 수 없습니다. 보안 프로그램의 로컬 연결 설정을 확인하거나 이메일 인증을 이용해 주세요."); }
            authority = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            string redirect = "http://" + authority + callbackPath + "?state=" + Uri.EscapeDataString(state);
            string challenge;
            using (var hash = SHA256.Create()) challenge = Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            AuthorizationUri = new Uri(origin, "auth/v1/authorize?provider=google&redirect_to=" + Uri.EscapeDataString(redirect) +
                "&code_challenge=" + challenge + "&code_challenge_method=s256&scopes=" + Uri.EscapeDataString("openid email profile"));
        }

        internal async Task<string> ReceiveCodeAsync(Action<Uri> openBrowser, CancellationToken cancellation)
        {
            using (cancellation.Register(() => listener.Stop())) {
                cancellation.ThrowIfCancellationRequested();
                try { openBrowser(AuthorizationUri); }
                catch { cancellation.ThrowIfCancellationRequested(); throw new MembershipGoogleException("기본 브라우저를 열지 못했습니다. Windows 기본 브라우저 설정을 확인하거나 이메일 인증을 이용해 주세요."); }
                for (;;) {
                    cancellation.ThrowIfCancellationRequested();
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { cancellation.ThrowIfCancellationRequested(); throw; }
                    using (client)
                    using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                        bounded.CancelAfter(TimeSpan.FromSeconds(5));
                        using (bounded.Token.Register(() => client.Close())) {
                            try {
                                var stream = client.GetStream();
                                string headers = await ReadHeadersAsync(stream, bounded.Token);
                                string code; bool denied;
                                if (!TryCallback(headers, out code, out denied)) {
                                    await ReplyAsync(stream, 400, "이 로그인 요청은 사용할 수 없습니다.", bounded.Token);
                                    continue;
                                }
                                await ReplyAsync(stream, 200, denied ? "Google 로그인이 완료되지 않았습니다. 위젯으로 돌아가 다시 시도해 주세요." :
                                    "로그인 응답을 받았습니다. 위젯으로 돌아가 결과를 확인해 주세요.", bounded.Token);
                                listener.Stop();
                                cancellation.ThrowIfCancellationRequested();
                                if (denied) throw new MembershipGoogleException("Google 로그인이 취소되었거나 인증되지 않았습니다. 다시 시도하거나 이메일 인증을 이용해 주세요.");
                                return code;
                            } catch (MembershipGoogleException) { throw; }
                            catch (Exception error) {
                                cancellation.ThrowIfCancellationRequested();
                                if (!(error is IOException) && !(error is SocketException) && !(error is ObjectDisposedException) &&
                                    !(error is InvalidDataException) && !(error is OperationCanceledException)) throw;
                                // Invalid local connections never consume the pending valid callback.
                            }
                        }
                    }
                }
            }
        }

        private bool TryCallback(string headers, out string code, out bool denied)
        {
            code = null; denied = false;
            string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] request = lines[0].Split(' ');
            if (request.Length != 3 || request[0] != "GET" || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0")) return false;
            string target = request[1]; int queryStart = target.IndexOf('?');
            if (queryStart < 0 || target.Substring(0, queryStart) != callbackPath || target.IndexOf('#') >= 0) return false;
            string host = null;
            for (int i = 1; i < lines.Length && lines[i].Length > 0; i++) {
                int colon = lines[i].IndexOf(':');
                if (colon < 1 || Char.IsWhiteSpace(lines[i][0])) return false;
                if (String.Equals(lines[i].Substring(0, colon), "Host", StringComparison.OrdinalIgnoreCase)) {
                    if (host != null) return false;
                    host = lines[i].Substring(colon + 1).Trim();
                }
            }
            if (host != authority) return false;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try {
                foreach (string pair in target.Substring(queryStart + 1).Split('&')) {
                    int separator = pair.IndexOf('=');
                    if (separator < 1) return false;
                    string name = Uri.UnescapeDataString(pair.Substring(0, separator).Replace('+', ' '));
                    string value = Uri.UnescapeDataString(pair.Substring(separator + 1).Replace('+', ' '));
                    if (values.ContainsKey(name)) return false;
                    values.Add(name, value);
                }
            } catch (UriFormatException) { return false; }
            string returnedState;
            if (!values.TryGetValue("state", out returnedState) || !EqualState(state, returnedState)) return false;
            Guid parsed;
            if (values.ContainsKey("error") || !values.TryGetValue("code", out code)) { denied = true; return true; }
            if (values.ContainsKey("access_token") || values.ContainsKey("refresh_token") || code.Length != 36 || !Guid.TryParseExact(code, "D", out parsed)) return false;
            return true;
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellation)
        {
            byte[] bytes = new byte[HeaderLimit]; int length = 0;
            while (length < bytes.Length) {
                int count = await stream.ReadAsync(bytes, length, bytes.Length - length, cancellation);
                if (count == 0) throw new InvalidDataException();
                for (int i = length; i < length + count; i++) if (bytes[i] > 126 || (bytes[i] < 32 && bytes[i] != 13 && bytes[i] != 10 && bytes[i] != 9)) throw new InvalidDataException();
                length += count;
                string value = Encoding.ASCII.GetString(bytes, 0, length);
                int end = value.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return value.Substring(0, end + 4);
            }
            throw new InvalidDataException();
        }

        private static async Task ReplyAsync(NetworkStream stream, int status, string message, CancellationToken cancellation)
        {
            string nonce = RandomValue(18);
            // Static content only. Clear callback code from this browser history entry.
            byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"" + L.Language + "\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Nephylo " + (L.English ? "sign-in" : "로그인") + "</title>" +
                "<body><h1>Nephylo</h1><p>" + System.Net.WebUtility.HtmlEncode(L.Text(message)) + "</p><script nonce=\"" + nonce + "\">history.replaceState(null,'','/');</script></body></html>");
            string headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + (status == 200 ? " OK" : " Bad Request") +
                "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) +
                "\r\nCache-Control: no-store\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; script-src 'nonce-" + nonce +
                "'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'\r\nConnection: close\r\n\r\n";
            byte[] prefix = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(prefix, 0, prefix.Length, cancellation);
            await stream.WriteAsync(body, 0, body.Length, cancellation);
        }

        private static bool EqualState(string expected, string actual)
        {
            if (actual == null || actual.Length != expected.Length) return false;
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ actual[i];
            return difference == 0;
        }
        private static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        private static string RandomValue(int count)
        {
            byte[] bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Base64Url(bytes);
        }
        public void Dispose() { listener.Stop(); verifier = null; AuthorizationUri = null; }
    }
}
