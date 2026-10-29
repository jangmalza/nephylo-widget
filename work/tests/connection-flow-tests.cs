using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// Tests never access user settings, real credentials, networks, browser or visible UI.
// Sentinel strings, in-memory delegates and isolated Claude fixtures exercise behavior.
internal static class ConnectionFlowTests
{
    private static int assertions;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static async Task<Exception> Failure(Task task, string message)
    {
        try { await task; }
        catch (Exception ex) { return ex; }
        throw new Exception(message);
    }

    private static async Task CheckInvalidInput()
    {
        var invalid = new[] { null, "", "   ", "\t\r\n", "\nsentinel", "sentinel\r", "sentinel\rnext", "sentinel\nnext", "sentinel\tother", "sentinel other", "sentinel\u00A0other", "sentinel\0other", "sentinel\u001Fother", "sentinel\u007Fother", new string('x', 8193) };
        foreach (string value in invalid)
        {
            int checkedCount = 0, savedCount = 0;
            await Failure(ApiConnectionFlow.CheckAndSaveAsync(value,
                candidate => { checkedCount++; return Task.FromResult(new UsageSnapshot()); },
                candidate => savedCount++), "Invalid input unexpectedly succeeded");
            Check(checkedCount == 0 && savedCount == 0, "Invalid input is rejected before any query or persistence");
        }
    }

    private static async Task CheckFailurePreservesStoredKey()
    {
        const string prior = "existing-sentinel";
        string stored = prior;
        int writes = 0;
        var failure = new InvalidOperationException("synthetic service failure");
        var response = new TaskCompletionSource<UsageSnapshot>();
        var pending = ApiConnectionFlow.CheckAndSaveAsync("replacement-sentinel", candidate => response.Task,
            candidate => { writes++; stored = candidate; });
        Check(!pending.IsCompleted && stored == prior && writes == 0, "A pending verification never replaces the stored key");
        response.SetException(failure);
        var actual = await Failure(pending, "Failed verification unexpectedly succeeded");
        Check(Object.ReferenceEquals(actual, failure), "Verification errors reach the caller unchanged");
        Check(stored == prior && writes == 0, "Failed verification leaves the previous key intact");

        actual = await Failure(ApiConnectionFlow.CheckAndSaveAsync("replacement-sentinel",
            candidate => { throw failure; }, candidate => { writes++; stored = candidate; }),
            "Synchronous verification failure unexpectedly succeeded");
        Check(Object.ReferenceEquals(actual, failure) && stored == prior && writes == 0,
            "A synchronously throwing checker also leaves the previous key intact");

        var canceled = new TaskCompletionSource<UsageSnapshot>();
        canceled.SetCanceled();
        await Failure(ApiConnectionFlow.CheckAndSaveAsync("replacement-sentinel", candidate => canceled.Task,
            candidate => { writes++; stored = candidate; }), "Canceled verification unexpectedly succeeded");
        Check(stored == prior && writes == 0, "A canceled verification never saves the candidate");

        await Failure(ApiConnectionFlow.CheckAndSaveAsync("replacement-sentinel", candidate => Task.FromResult<UsageSnapshot>(null),
            candidate => { writes++; stored = candidate; }), "Missing verification response unexpectedly succeeded");
        Check(stored == prior && writes == 0, "A null response cannot be mistaken for successful authentication");
    }

    private static async Task CheckSuccessOrdering()
    {
        var events = new List<string>();
        var response = new TaskCompletionSource<UsageSnapshot>();
        string checkedCandidate = null, savedCandidate = null;
        var verified = new UsageSnapshot { Plan = "verified", Rows = new List<UsageRow> {
            new UsageRow { Label = "synthetic quota", RemainingPercent = 37, Remaining = 37, Limit = 100 }
        } };
        var pending = ApiConnectionFlow.CheckAndSaveAsync("  candidate-sentinel  ", candidate => {
            events.Add("check"); checkedCandidate = candidate; return response.Task;
        }, candidate => { events.Add("save"); savedCandidate = candidate; });
        Check(events.SequenceEqual(new[] { "check" }) && savedCandidate == null && !pending.IsCompleted,
            "The UI can await authentication while persistence remains untouched");
        response.SetResult(verified);
        var actual = await pending;
        Check(events.SequenceEqual(new[] { "check", "save" }), "Successful authentication saves exactly once and only after checking");
        Check(checkedCandidate == "candidate-sentinel" && savedCandidate == checkedCandidate,
            "The verified and stored key are the same trimmed candidate");
        Check(Object.ReferenceEquals(actual, verified), "The verified usage snapshot is returned for immediate display without a second query");
        Check(actual.Rows[0].RemainingPercent == 37, "The verified result retains the actual usage data");

        string largest = new string('x', 8192);
        int saves = 0;
        await ApiConnectionFlow.CheckAndSaveAsync(largest, candidate => Task.FromResult(verified), candidate => saves++);
        Check(saves == 1, "The documented maximum key length remains usable");
    }

    private static async Task CheckStorageFailure()
    {
        int queries = 0, writes = 0;
        var failure = new IOException("synthetic persistence failure");
        var actual = await Failure(ApiConnectionFlow.CheckAndSaveAsync("candidate-sentinel", candidate => {
            queries++; return Task.FromResult(new UsageSnapshot());
        }, candidate => { writes++; throw failure; }), "Failed persistence unexpectedly reported success");
        Check(Object.ReferenceEquals(actual, failure), "Persistence failure is surfaced instead of claiming successful connection");
        Check(queries == 1 && writes == 1, "Persistence failure does not repeat authentication or silently retry writes");
    }

    private static async Task CheckApiFlow()
    {
        await CheckInvalidInput();
        await CheckFailurePreservesStoredKey();
        await CheckSuccessOrdering();
        await CheckStorageFailure();
        await CheckSelectionOrdering();
    }

    private static async Task CheckSelectionOrdering()
    {
        var plan = new ServicePlan(null,true,null,null);
        var response = new TaskCompletionSource<UsageSnapshot>();
        var pending = FreeServiceSelectionFlow.VerifyThenSelectAsync(() => response.Task,() => plan.Select("eleven"));
        Check(plan.IsActive("codex") && !pending.IsCompleted,"Pending candidate verification preserves the active Free service");
        response.SetException(new InvalidOperationException("synthetic rejected connection"));
        await Failure(pending,"Rejected candidate unexpectedly selected");
        Check(plan.IsActive("codex"),"Rejected candidate never replaces the active service");
        await Failure(FreeServiceSelectionFlow.VerifyThenSelectAsync(() => Task.FromResult<UsageSnapshot>(null),() => plan.Select("eleven")),"Missing usage unexpectedly selected");
        Check(plan.IsActive("codex"),"Missing configuration keeps the working Free connection");
        var canceled = new TaskCompletionSource<UsageSnapshot>(); canceled.SetCanceled();
        await Failure(FreeServiceSelectionFlow.VerifyThenSelectAsync(() => canceled.Task,() => plan.Select("eleven")),"Canceled check unexpectedly selected");
        Check(plan.IsActive("codex"),"Canceled verification leaves prior selection unchanged");
        var verified = new UsageSnapshot { Plan = "verified" };
        var result = await FreeServiceSelectionFlow.VerifyThenSelectAsync(() => Task.FromResult(verified),() => plan.Select("eleven"));
        Check(plan.IsActive("eleven") && Object.ReferenceEquals(result,verified),"Successful verification commits one service and reuses its checked snapshot");
        await Failure(FreeServiceSelectionFlow.VerifyThenSelectAsync(() => Task.FromResult(verified),() => { throw new IOException("synthetic selection storage failure"); }),"Storage failure silently accepted");
        Check(plan.IsActive("eleven"),"Selection storage failure never alters the previous in-memory service");
    }

    private static T Field<T>(WidgetWindow window, string name)
    {
        return (T)typeof(WidgetWindow).GetField(name, PrivateInstance).GetValue(window);
    }

    private static void SetField(WidgetWindow window, string name, object value)
    {
        typeof(WidgetWindow).GetField(name, PrivateInstance).SetValue(window, value);
    }

    private static object Call(WidgetWindow window, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, PrivateInstance).Invoke(window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var match = parent as T;
        if (match != null) yield return match;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            var item = child as DependencyObject;
            if (item == null) continue;
            foreach (var found in Descendants<T>(item)) yield return found;
        }
    }

    private static IEnumerable<Button> HelpButtons(DependencyObject parent)
    {
        return Descendants<Button>(parent).Where(button => Convert.ToString(button.Content) == "해결 방법");
    }

    private static void CheckErrorHelp(WidgetWindow window)
    {
        foreach (string name in new[] { "codexView", "elevenView", "openAiView" })
        {
            var view = Field<ProviderView>(window, name);
            bool isCodex = name == "codexView";
            view.Snapshot = null;
            view.LastSuccess = null;
            view.Error = "synthetic connection error";
            Call(window, "Render", view, isCodex);
            Check(HelpButtons(view.Body).Count() == 1, name + " exposes a direct recovery action when no usage has ever loaded");
            Check(view.Badge.Text == "연결 확인", name + " shows that a failed service needs attention");

            view.Snapshot = new UsageSnapshot { Plan = "verified", Rows = new List<UsageRow> {
                new UsageRow { Label = "synthetic remaining", RemainingPercent = 42 }
            } };
            view.LastSuccess = DateTimeOffset.Now.AddMinutes(-5);
            Call(window, "Render", view, isCodex);
            Check(HelpButtons(view.Body).Count() == 1 && view.Badge.Text == "이전 값",
                name + " retains an accessible recovery action when displaying stale usage");
            Check(Descendants<TextBlock>(view.Body).Any(text => text.Text == "42%"),
                name + " keeps the last known usage visible while explaining a connection failure");

            view.Error = null;
            Call(window, "Render", view, isCodex);
            Check(!HelpButtons(view.Body).Any() && view.Badge.Text != "이전 값",
                name + " removes obsolete recovery UI after a successful refresh");
        }
    }

    private static void CheckApiEditors(WidgetWindow window)
    {
        foreach (string id in new[] { "eleven", "openai" })
        {
            string field = id == "eleven" ? "secret" : "openAiKey";
            SetField(window, field, "");
            var editor = new StackPanel();
            Call(window, "BuildApiKeySettings", editor, id);
            var input = Descendants<PasswordBox>(editor).Single();
            var save = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "연결 확인 후 저장");
            Check(input.Password.Length == 0 && save.IsEnabled, id + " has a discoverable connection action and an empty protected input");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "API 키가 필요합니다"),
                id + " immediately explains what is missing when connecting without a key");
            Check(!Field<bool>(window, "refreshing") && Field<Button>(window, "refreshButton").IsEnabled
                && Field<string>(window, field) == "", id + " empty input does not start a refresh or alter saved credentials");

            input.Password = "ui-candidate-sentinel";
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "미리보기") && save.IsEnabled && input.IsEnabled,
                id + " safely previews connection feedback and restores controls without external I/O");
            Check(Field<string>(window, field) == "" && !Field<bool>(window, "refreshing"),
                id + " preview does not save the candidate or leave a refresh lock behind");

            input.Clear();
            SetField(window, field, "stored-sentinel-should-never-be-displayed");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "미리보기"),
                id + " an existing editor uses the current saved key instead of a captured stale value");
            var reopened = new StackPanel();
            Call(window, "BuildApiKeySettings", reopened, id);
            Check(Descendants<PasswordBox>(reopened).Single().Password.Length == 0
                && !Descendants<TextBlock>(reopened).Any(text => text.Text.Contains("stored-sentinel")),
                id + " reopening connection settings never pre-fills or reveals the saved secret");
            Check(Descendants<Expander>(reopened).Where(expander => Convert.ToString(expander.Header) != "연결 방법").All(expander => !expander.IsExpanded),
                id + " optional budget and disconnection details stay out of the initial setup flow");
            SetField(window, field, "");
        }
    }

    private static void CheckCodexEditor(WidgetWindow window)
    {
        var editor = new StackPanel();
        Call(window, "BuildCodexSettings", editor);
        var advanced = Descendants<Expander>(editor).Single(expander => Convert.ToString(expander.Header).Contains("고급 설정"));
        Check(!advanced.IsExpanded && Descendants<TextBox>(advanced).Count() == 1,
            "Codex execution path stays inside collapsed advanced settings for users who need it");
        Check(!Descendants<PasswordBox>(editor).Any(), "Codex setup uses an existing login instead of asking for an API key");
        var check = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "연결 확인");
        var login = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "브라우저로 로그인");
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Descendants<TextBlock>(editor).Any(text => text.Text == "미리보기") && check.IsEnabled,
            "Codex connection check can be previewed without running a real CLI");
        login.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Descendants<TextBlock>(editor).Any(text => text.Text == "실제 로그인 페이지는 열지 않습니다.")
            && !Field<bool>(window, "loginPending"), "Codex login preview never launches a browser or changes login state");
    }

    private static void CheckCachedEditorRefresh(WidgetWindow window)
    {
        foreach (string id in new[] { "eleven", "openai" })
        {
            var view = Field<ProviderView>(window, id == "eleven" ? "elevenView" : "openAiView");
            view.Issue = null; view.Error = null; view.LastSuccess = DateTimeOffset.Now;
            var editor = new StackPanel();
            Call(window, "BuildApiKeySettings", editor, id);
            var input = Descendants<PasswordBox>(editor).Single();
            input.Password = "unsaved-candidate-sentinel";
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "연결됨"),
                id + " cached editor starts with the previously successful connection state");

            var issue = ConnectionDiagnostics.Http(id, 403, "{\"detail\":{\"status\":\"missing_permissions\"}}");
            view.Issue = issue; view.Error = issue.Title;
            Call(window, "RefreshConnectionEditor", id);
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == issue.Title)
                && Descendants<TextBlock>(editor).Any(text => text.Text == issue.Message),
                id + " reopening a cached editor presents the current issue and recovery steps");
            Check(input.Password == "unsaved-candidate-sentinel",
                id + " updating cached feedback preserves a candidate the user is still editing");
            var action = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == issue.ActionLabel + " ↗");
            Check(action.Visibility == Visibility.Visible,
                id + " the current issue exposes its official recovery destination");

            view.Issue = null; view.Error = null;
            Call(window, "RefreshConnectionEditor", id);
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "연결됨") && action.Visibility == Visibility.Collapsed,
                id + " a recovered service clears the obsolete issue and recovery action");
            Check(input.Password == "unsaved-candidate-sentinel",
                id + " successful feedback refresh also preserves unsaved input");
        }
    }

    private static void CheckProviderRejectedCandidates(WidgetWindow window)
    {
        // The shared flow accepts up to 8,192 characters; each provider rejects over
        // 4,096 before constructing/sending a request. These sentinels deliberately
        // exercise the real event-handler catch path with no network possible.
        foreach (string id in new[] { "eleven", "openai" })
        {
            string field = id == "eleven" ? "secret" : "openAiKey";
            Field<ServicePlan>(window,"servicePlan").Select(id);
            string old = new string('x', 4097);
            SetField(window, field, old);
            var view = Field<ProviderView>(window, id == "eleven" ? "elevenView" : "openAiView");
            var snapshot = new UsageSnapshot { Plan = "previous", Rows = new List<UsageRow> {
                new UsageRow { Label = "synthetic remaining", RemainingPercent = 51 }
            } };
            view.Snapshot = snapshot; view.LastSuccess = DateTimeOffset.Now; view.Error = null; view.Issue = null;
            var editor = new StackPanel();
            Call(window, "BuildApiKeySettings", editor, id);
            var input = Descendants<PasswordBox>(editor).Single();
            var save = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "연결 확인 후 저장");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Field<bool>(window, "refreshing") && save.IsEnabled,
                id + " provider-side format rejection completes synchronously without a request");
            Check(view.Issue != null && !String.IsNullOrEmpty(view.Error) && view.Badge.Text == "이전 값",
                id + " a failed recheck of the saved key marks its main card as stale");
            Check(Object.ReferenceEquals(view.Snapshot, snapshot) && Field<string>(window, field) == old,
                id + " a failed saved-key recheck retains the key and last known usage");

            view.Error = null; view.Issue = null;
            input.Password = new string('y', 4097);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.Issue == null && view.Error == null && Object.ReferenceEquals(view.Snapshot, snapshot),
                id + " a failed replacement candidate does not mark the prior working connection failed");
            Check(Field<string>(window, field) == old && input.Password.Length == 4097,
                id + " a rejected replacement remains editable and never replaces the saved key");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text.Contains("기존에 저장된 키는 유지했습니다.")),
                id + " rejected replacement feedback explicitly confirms the previous key was preserved");
            SetField(window, field, "");
        }
    }

    private static void CheckIsolatedNonDemo(string isolated)
    {
        WidgetWindow window = null;
        try
        {
            // All settings/config overrides are already installed by CheckWpfFlow.
            // Never Show or raise Loaded; stop its timer before the dispatcher pumps.
            window = new WidgetWindow(false);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            Check(!window.IsLoaded && !window.IsVisible && SettingsStore.DataDirectory == isolated,
                "Non-demo handler fixture is hidden and reads only its isolated settings directory");
            var widgetSettings = Field<WidgetSettings>(window,"settings");
            widgetSettings.CodexPath = Path.Combine(isolated,"does-not-exist.exe");
            var codexEditor = new StackPanel(); Call(window,"BuildCodexSettings",codexEditor);
            var missingCheck = Descendants<Button>(codexEditor).Single(button => Convert.ToString(button.Content) == "설치 다시 확인");
            var browserLogin = Descendants<Button>(codexEditor).Single(button => Convert.ToString(button.Content) == "브라우저로 로그인");
            Check(browserLogin.Visibility == Visibility.Collapsed && Descendants<Button>(codexEditor).Any(button => Convert.ToString(button.Content) == "Codex 설치 방법 보기" && button.Visibility == Visibility.Visible),"Missing Codex offers install instructions instead of an unusable browser login");
            missingCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Field<bool>(window,"refreshing") && !Field<bool>(window,"loginPending"),"Missing executable check starts neither a process nor a browser wait");
            var codexPath = Descendants<TextBox>(codexEditor).Single();
            codexPath.Text = typeof(ConnectionFlowTests).Assembly.Location;
            Check(browserLogin.Visibility == Visibility.Visible && Convert.ToString(missingCheck.Content) == "연결 확인","A newly selected executable immediately enables the login and usage check steps without being launched");
            widgetSettings.CodexPath = "";
            CheckProviderRejectedCandidates(window);
            Field<ServicePlan>(window,"servicePlan").Select("codex");
            SetField(window,"secret","preserved-inactive-key");
            var candidateEditor = new StackPanel(); Call(window,"BuildApiKeySettings",candidateEditor,"eleven");
            Descendants<PasswordBox>(candidateEditor).Single().Password = new string('z',4097);
            Descendants<Button>(candidateEditor).Single(button => Convert.ToString(button.Content) == "연결 확인 후 저장").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<ServicePlan>(window,"servicePlan").IsActive("codex") && Field<string>(window,"secret") == "preserved-inactive-key", "A failed inactive API candidate keeps the current service and previously saved connection");
            Check(!Field<bool>(window,"refreshing") && Descendants<TextBlock>(candidateEditor).Any(text => text.Text.Contains("기존에 저장된 키는 유지했습니다.")),"Inactive candidate failure is explained without requiring an early service switch");
            SetField(window,"secret","");
            Check(Directory.GetFiles(isolated).All(path => Path.GetFileName(path) == "plan-settings.json" || Path.GetFileName(path) == "usage-journal.json"), "Rejected candidates create no stored key or provider settings files; only explicit Free selection persists");
            Field<ServicePlan>(window,"servicePlan").Select("claude");

            var editor = new StackPanel();
            Call(window, "BuildClaudeSettings", editor);
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "연결 전"),
                "An isolated Claude installation starts with an honest disconnected state");
            var connect = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "Claude Code 연결");
            ClaudeUsageService.BridgeProbeOverride = (info, token) => Task.FromResult("launch");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!File.Exists(Path.Combine(ClaudeUsageService.ConfigDirectoryOverride, "settings.json")), "Failed bridge preflight does not register a command");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "Claude 연결 명령 실행을 확인하지 못했습니다"), "Execution failure is not presented as normal receipt waiting");
            ClaudeUsageService.BridgeProbeOverride = (info, token) => Task.FromResult("ok");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "연결 설정됨 · 조회 대기"),
                "Installing the Claude hook does not claim that quota has already arrived");
            Check(File.Exists(Path.Combine(ClaudeUsageService.ConfigDirectoryOverride, "settings.json")),
                "Claude connection writes only the explicitly redirected temporary config");

            ClaudeUsageService.CaptureJson("{\"rate_limits\":{}}", DateTimeOffset.UtcNow);
            Call(window, "RefreshConnectionEditor", "claude");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "수신됨 · 한도 미제공"),
                "An empty quota receipt explains unavailable limits instead of pretending the bridge has not run");
            ClaudeUsageService.CaptureJson("{\"model\":{\"id\":\"fixture\"}}",DateTimeOffset.UtcNow);
            Call(window,"RefreshConnectionEditor","claude");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "수신됨 · 한도 미제공"),"A Free/API-shaped event confirms receipt without inferring the actual plan");
            string quota = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":25,\"resets_at\":253402300000}}}";
            ClaudeUsageService.CaptureJson(quota, DateTimeOffset.UtcNow);
            Call(window, "RefreshConnectionEditor", "claude");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "사용량 수신 완료"),
                "A real quota-shaped local fixture changes Claude feedback to received");
            ClaudeUsageService.CaptureJson(quota, DateTimeOffset.UtcNow.AddMinutes(-31));
            Call(window, "RefreshConnectionEditor", "claude");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text == "이전 값 · 새로고침 필요"),
                "A stale local Claude record asks for a fresh query rather than claiming current usage");
            Call(window,"RefreshLocalExtras");
            Check((bool)Call(window,"IsExtraReady","claude"),"Installed Claude hook is connected even with stale cache");
            string configFile = Path.Combine(ClaudeUsageService.ConfigDirectoryOverride,"settings.json");
            string originalHook = File.ReadAllText(configFile);
            var config = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(originalHook);
            var hook = (Dictionary<string,object>)config["statusLine"];
            hook["command"] = (string)hook["command"] + " ; echo custom-display";
            string customHook = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config);
            File.WriteAllText(configFile,customHook);
            ClaudeUsageService.CaptureJson(quota,DateTimeOffset.UtcNow);
            Call(window,"RefreshLocalExtras");
            Check(!(bool)Call(window,"IsExtraReady","claude"),"Custom forwarding requires explicit user choice despite a cached receipt");
            var customChoice = Descendants<CheckBox>(editor).Single(box => Convert.ToString(box.Content) == "기존 상태 표시줄에서 수신");
            customChoice.IsChecked = true;
            var receiptCheck = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "잔여량 조회");
            receiptCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(widgetSettings.ClaudeUseExistingStatusLine && (bool)Call(window,"IsExtraReady","claude"),"Explicit custom bridge receipt verification persists acceptance and displays data");
            Check(Field<ProviderView>(window,"claudeView").Snapshot.Rows[0].RemainingPercent == 75,"Custom bridge renders actual fresh quota");
            Check(File.ReadAllText(configFile) == customHook,"Custom bridge verification never rewrites the user's script");
            var topSwitch = (Task<UsageSnapshot>)Call(window,"VerifyServiceForSelection","claude");
            Check(topSwitch.GetAwaiter().GetResult().Rows[0].RemainingPercent == 75,"Top-level Free service switching accepts a verified custom-forwarded receipt");
            Field<ServicePlan>(window,"servicePlan").Select("codex");
            ClaudeUsageService.CaptureJson("{\"model\":{\"id\":\"fixture\"}}",DateTimeOffset.UtcNow);
            receiptCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<ServicePlan>(window,"servicePlan").IsActive("codex"),"A received event without subscription quota never switches the current Free service");
            Check(((Task<UsageSnapshot>)Call(window,"VerifyServiceForSelection","claude")).GetAwaiter().GetResult() == null,"Top-level service verification does not accept unavailable quota");
            Check(Descendants<TextBlock>(editor).Any(text => text.Text.Contains("현재 Claude 계정은 구독 한도를 제공하지 않습니다")),"A query without subscription quota explains the support boundary and manual alternative");
            Field<ServicePlan>(window,"servicePlan").Select("claude"); Call(window,"RefreshLocalExtras");
            string cachePath = Path.Combine(isolated,"claude-usage.json");
            string cacheBeforeDisconnect = File.ReadAllText(cachePath);
            var disconnect = Descendants<Button>(editor).Single(button => Convert.ToString(button.Content) == "위젯 연결 해제");
            disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!widgetSettings.ClaudeUseExistingStatusLine && File.ReadAllText(configFile) == customHook,"Custom disconnect revokes receipt acceptance without changing the user's status line");
            Check(customChoice.IsChecked == false,"Custom disconnect also clears the unsaved editor choice");
            Check(!(bool)Call(window,"IsExtraReady","claude"),"Claude disconnect is not masked by retained cache");
            Check(Field<ProviderView>(window,"claudeView").Snapshot == null,"Disconnected Claude no longer renders cached quota as an active card");
            Check(File.ReadAllText(cachePath) == cacheBeforeDisconnect,"Disconnect preserves local cached record for later reconnect");
            Field<ExtraSettings>(window,"extras").ClaudeManual = new ManualUsageRecord { Label = "manual fixture", RemainingPercent = 41, ObservedAt = DateTimeOffset.Now };
            Call(window,"RefreshLocalExtras");
            Check((bool)Call(window,"IsExtraReady","claude") && Field<ProviderView>(window,"claudeView").Snapshot.Plan == "수동","Independent user-entered Claude record remains available after bridge disconnect");
            Check(!window.IsLoaded && !window.IsVisible, "Non-demo handler tests never load or show the widget");
        }
        finally
        {
            if (window != null)
            {
                Field<DispatcherTimer>(window, "timer").Stop();
                Field<DispatcherTimer>(window, "noticeTimer").Stop();
                // Drain the real background journal before deleting the isolated
                // fixture; Close() otherwise returns while async persistence runs.
                ((Task)Call(window, "FlushUsageTrackingAsync")).GetAwaiter().GetResult();
                SetField(window, "shutdownFlushed", true);
                window.Close();
                Field<CodexService>(window, "codex").Dispose();
                Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
        }
    }

    private static void CheckWpfFlow()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(ConnectionFlowTests).Assembly.Location));
        string isolated = Path.Combine(parent, "connection-flow-test-data-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\connection-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated, "claude-config");
        WidgetWindow window = null;
        try
        {
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window, "timer").Stop();
            Field<Forms.NotifyIcon>(window, "tray").Visible = false;
            Check(!window.IsLoaded && !window.IsVisible, "The WPF fixture remains hidden and never starts a live refresh");
            CheckErrorHelp(window);
            CheckApiEditors(window);
            CheckCodexEditor(window);
            CheckCachedEditorRefresh(window);
            Check(!Directory.Exists(isolated), "UI connection checks never persist data or credentials");
            CheckIsolatedNonDemo(isolated);
        }
        finally
        {
            if (window != null)
            {
                Field<DispatcherTimer>(window, "timer").Stop();
                window.Close();
                Field<CodexService>(window, "codex").Dispose();
                Field<Forms.NotifyIcon>(window, "tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null;
            SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null;
            string full = Path.GetFullPath(isolated);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("connection-flow-test-data-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }

    [STAThread]
    public static int Main()
    {
        ClaudeUsageService.BridgeProbeOverride = (info, token) => System.Threading.Tasks.Task.FromResult("ok");
        ClaudeQuotaUiFixture.Install();
        try
        {
            Check(SynchronizationContext.Current == null, "Async flow tests start without a UI synchronization context");
            CheckApiFlow().GetAwaiter().GetResult();
            CheckWpfFlow();
            assertions += SupportDiagnosticsTests.Run();
            Console.WriteLine("Connection flow tests passed: " + assertions + " assertions. No live accounts, network requests, user settings or visible windows were used; isolated fixtures were removed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Connection flow test failed: " + ex);
            return 1;
        }
        finally { ClaudeQuotaService.ReadOverride = null; }
    }
}
