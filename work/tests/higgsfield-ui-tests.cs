using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

// WPF behavior fixtures only. Management windows are opened and closed against
// an isolated profile; no real CLI, browser login, or account is accessed.
// Child processes run only this test assembly, with fixed workspace responses.
// Storage stays inside this isolated run; a fake membership endpoint must stay unused.
internal static class HiggsfieldUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string FirstWorkspace = "108927c1-3159-453d-b9b2-63dafcfd3224";
    private const string SecondWorkspace = "7061167d-ad58-4bdf-9949-8296e8135dba";
    private const string FixtureMode = "USAGE_WIDGET_HIGGSFIELD_UI_MODE";
    private const string FixtureLog = "USAGE_WIDGET_HIGGSFIELD_UI_LOG";
    private const string FixtureGate = "USAGE_WIDGET_HIGGSFIELD_UI_GATE";
    private static int checks;
    private static T Field<T>(WidgetWindow w, string name) { return (T)typeof(WidgetWindow).GetField(name, Hidden).GetValue(w); }
    private static void Set(WidgetWindow w, string name, object value) { typeof(WidgetWindow).GetField(name, Hidden).SetValue(w, value); }
    private static object Call(WidgetWindow w, string name, params object[] args)
    {
        try { return typeof(WidgetWindow).GetMethod(name, Hidden).Invoke(w, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }
    private static IEnumerable<TextBlock> Texts(DependencyObject parent)
    {
        var text = parent as TextBlock;
        if (text != null) yield return text;
        foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
            var item = child as DependencyObject;
            if (item != null) foreach (var found in Texts(item)) yield return found;
        }
    }
    private static IEnumerable<T> Controls<T>(DependencyObject parent) where T : DependencyObject
    {
        var control = parent as T;
        if (control != null) yield return control;
        foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
            var item = child as DependencyObject;
            if (item != null) foreach (var found in Controls<T>(item)) yield return found;
        }
    }
    private static Button ButtonNamed(DependencyObject body, string title)
    {
        return Controls<Button>(body).Single(button => Object.Equals(button.Content, title));
    }
    private static T Accessible<T>(DependencyObject body, params string[] names) where T : DependencyObject
    {
        return Controls<T>(body).Single(control => names.Contains(System.Windows.Automation.AutomationProperties.GetName(control)));
    }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void PumpUntil(Func<bool> complete, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (!complete() && timeout.Elapsed < TimeSpan.FromSeconds(12)) {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
        Check(complete(), message);
    }
    private static void WaitForConnection(WidgetWindow window)
    {
        PumpUntil(() => Field<CancellationTokenSource>(window, "higgsfieldOperation") == null, "Fixture connection completes within its bound");
        StopBackground(window);
    }
    private static string[] CliCalls(string path) { return File.Exists(path) ? File.ReadAllLines(path) : new string[0]; }
    private static string WorkspaceList(bool multiple)
    {
        return "[{\"id\":\"" + FirstWorkspace + "\",\"name\":\"Personal fixture\",\"plan_type\":\"pro\",\"is_selected\":false}" +
            (multiple ? ",{\"id\":\"" + SecondWorkspace + "\",\"name\":\"Team fixture\",\"plan_type\":\"team\",\"is_selected\":false}" : "") + "]";
    }
    private static int RunCliFixture(string[] args)
    {
        string mode = Environment.GetEnvironmentVariable(FixtureMode);
        string log = Environment.GetEnvironmentVariable(FixtureLog);
        if (String.IsNullOrEmpty(mode) || String.IsNullOrEmpty(log)) return 25;
        File.AppendAllText(log, args[0] + Environment.NewLine);
        if (mode.StartsWith("gate-",StringComparison.Ordinal)) {
            string gate = Environment.GetEnvironmentVariable(FixtureGate);
            if (String.IsNullOrEmpty(gate)) return 27;
            var bound = Stopwatch.StartNew();
            while (!File.Exists(gate) && bound.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(20);
            if (!File.Exists(gate)) return 28;
            mode = mode.Substring(5);
        }
        if (mode == "wait") { Thread.Sleep(20000); return 0; }
        if (mode == "fail") { Console.Error.WriteLine("not authenticated; fixture only"); return 1; }
        if (args[0] == "auth") return 0;
        if (args[0] == "workspace") { Console.WriteLine(WorkspaceList(mode == "multiple")); return 0; }
        string selected = Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID");
        if (String.IsNullOrEmpty(selected)) { Console.Error.WriteLine("Error: No workspace selected. Hint: Run: hf workspace set <workspace_id>"); return 4; }
        if (selected != FirstWorkspace && selected != SecondWorkspace) return 26;
        Console.WriteLine("{\"credits\":" + (selected == FirstWorkspace ? "120.5" : "62.75") + ",\"subscription_plan_type\":\"pro\"}"); return 0;
    }
    private static void StopBackground(WidgetWindow window)
    {
        Field<DispatcherTimer>(window, "timer").Stop();
        Field<DispatcherTimer>(window, "noticeTimer").Stop();
        Field<Forms.NotifyIcon>(window, "tray").Visible = false;
    }
    // The accountless app must never call the retired membership server.
    private sealed class FakeMembership : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            throw new Exception("Unexpected membership fixture request");
        }
    }
    private static string Shown(Dictionary<string, StackPanel> cards)
    {
        return String.Join(",", cards.Where(p => p.Value.Visibility == Visibility.Visible).Select(p => p.Key).OrderBy(id => id, StringComparer.Ordinal));
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0) return args[0] == "account" || args[0] == "auth" || args[0] == "workspace" ? RunCliFixture(args) : 24;
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(HiggsfieldUiTests).Assembly.Location));
        string root = Path.Combine(parent, "higgsfield-ui-data-" + Guid.NewGuid().ToString("N"));
        string previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string previousWorkspace = Environment.GetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID");
        string previousMode = Environment.GetEnvironmentVariable(FixtureMode);
        string previousLog = Environment.GetEnvironmentVariable(FixtureLog);
        string previousGate = Environment.GetEnvironmentVariable(FixtureGate);
        WidgetWindow window = null, preview = null;
        try {
            string data = Path.Combine(root, "data");
            SettingsStore.DataDirectoryOverride = data;
            ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(root, "claude");
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, "codex"));
            Environment.SetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID", null);
            Environment.SetEnvironmentVariable(FixtureMode, "single");
            string cliLog = Path.Combine(root, "fixture-cli.log");
            Environment.SetEnvironmentVariable(FixtureLog, cliLog);
            CheckOrderMigration();
            CheckHistory(Path.Combine(root, "history"));
            var expectedTime = DateTimeOffset.Now.AddMinutes(-15);
            var initial = new HiggsfieldSettings { WebConnected = true, CliPath = Path.Combine(root, "never-run.exe"), WorkspaceId = "36e6bf3b-4e13-44de-a0b2-23b713ed8c90", ApiBalanceUsd = 12.5, ApiObservedAt = expectedTime };
            HiggsfieldSettingsStore.Save(initial);
            SettingsStore.SaveApiKey("isolated-eleven-fixture");
            string foreignKeyPath = Path.Combine(data, "elevenlabs-key.dat");
            byte[] foreignKey = File.ReadAllBytes(foreignKeyPath);
            new ServicePlan(data, false, null, null).Select("codex");
            var server = new FakeMembership();
            var member = new MembershipClient(data, false, MembershipConfiguration.FromValues("https://test.supabase.co", "sb_publishable_test_key"), server, () => 0);
            window = new WidgetWindow(false, member); StopBackground(window);
            Check(!window.IsLoaded && !window.IsVisible && server.Requests == 0, "Construction never loads UI or starts membership requests");
            var cards = Field<Dictionary<string, StackPanel>>(window, "serviceCards");
            var web = Field<ProviderView>(window, "higgsfieldView");
            var api = Field<ProviderView>(window, "higgsfieldApiView");
            Check(cards.Count == 8 && cards.ContainsKey("higgsfield") && cards.ContainsKey("higgsfield-api"), "Both Higgsfield services register their own cards");
            Check(Shown(cards) == "eleven,higgsfield,higgsfield-api", "Every stored connection shows its card regardless of the legacy single-service selection");
            Check(Object.ReferenceEquals(Call(window, "ViewFor", "higgsfield"), web) && Object.ReferenceEquals(Call(window, "ViewFor", "higgsfield-api"), api), "Fold and layout route to each Higgsfield view");
            Check(ServiceIcons.Get("higgsfield") != null && ServiceIcons.Get("higgsfield-api") != null, "Both service choices have icons");
            var missing = (Task)Call(window, "ReadHiggsfield");
            Check(missing.IsCompleted && web.Snapshot == null && !String.IsNullOrEmpty(web.Error) && CliCalls(cliLog).Length == 0, "A stored web connection is read without a Free selection, and a missing configured CLI fails without discovering another executable"); missing.GetAwaiter().GetResult();
            Reject(() => Call(window, "SaveHiggsfieldApiBalance", (object)(double?)Double.NaN), "An invalid API balance is rejected before saving");
            Check(HiggsfieldSettingsStore.Load().ApiBalanceUsd == 12.5, "A rejected edit preserves the saved API amount");
            using (var operation = new CancellationTokenSource()) {
                Set(window, "higgsfieldOperation", operation);
                Call(window, "RefreshLocalExtras");
                Check(api.Snapshot != null && api.Snapshot.Rows.Single().Remaining == 12.5 && cards["higgsfield-api"].Visibility == Visibility.Visible, "Pending Higgsfield web work does not block the independent API balance");
                web.Error = null;
                var busyRead = (Task)Call(window, "ReadHiggsfield");
                Check(busyRead.IsCompleted && web.Error == null, "An active Higgsfield operation suppresses overlapping background CLI reads"); busyRead.GetAwaiter().GetResult();
                Call(window, "CancelHiggsfieldOperation");
                Check(operation.IsCancellationRequested, "Cancel reaches the pending connection operation");
                Check(HiggsfieldSettingsStore.Load().WebConnected && HiggsfieldSettingsStore.Load().ApiBalanceUsd == 12.5, "Cancel does not alter saved connection or API settings");
                Set(window, "higgsfieldOperation", null);
            }
            Call(window, "RefreshLocalExtras");
            Check(Shown(cards) == "eleven,higgsfield,higgsfield-api", "The API balance card stays alongside the other connected services");
            Check(api.Snapshot != null && api.Snapshot.Rows.Single().Remaining == 12.5 && api.Snapshot.Rows.Single().DisplayValue == "$12.50", "Saved API balance displays in USD");
            Check(api.Snapshot.Rows.All(row => !row.RemainingPercent.HasValue && !row.ResetsAt.HasValue), "Absolute API balance has no invented ratio or reset deadline");
            Check(api.LastSuccess.HasValue && Math.Abs((api.LastSuccess.Value - expectedTime).TotalSeconds) < 1, "Rendering uses the recorded time rather than refresh time");
            Check(api.Badge.Text.Contains("수동") && String.Join(" ", Texts(api.Body).Select(t => t.Text)).Contains("자동"), "Manual API balance clearly explains its lack of automatic refresh");
            Call(window, "SaveHiggsfieldApiBalance", (object)(double?)0);
            Check(api.Snapshot.Rows.Single().DisplayValue == "$0.00" && HiggsfieldSettingsStore.Load().ApiBalanceUsd == 0, "A zero USD balance remains a valid connected record");
            var pending = Field<List<UsageNotice>>(window, "pendingUsageNotices");
            Check(Field<UsageJournal>(window, "usageJournal").GetHistory().Any(p => p.ServiceId == "higgsfield-api" && p.Source == "manual" && p.Remaining == 0), "Manual API edits record their amount and source in history");
            Check(pending.Count == 1 && pending[0].ServiceId == "higgsfield-api" && pending[0].Kind == "empty" && pending[0].Message.Contains(" USD") && !pending[0].Message.Contains("%"), "An empty manual API balance alerts against the USD threshold without a fabricated percentage");
            Call(window, "SaveHiggsfieldApiBalance", new object[] { null });
            var cleared = HiggsfieldSettingsStore.Load();
            Check(!cleared.ApiBalanceUsd.HasValue && !cleared.ApiObservedAt.HasValue && cards["higgsfield-api"].Visibility == Visibility.Collapsed && pending.Count == 0, "API clear removes its value, timestamp, card and pending alert");
            Check(cleared.WebConnected && cleared.CliPath == initial.CliPath && cleared.WorkspaceId == initial.WorkspaceId, "API clear preserves the independent web connection, CLI path and workspace");
            Check(File.ReadAllBytes(foreignKeyPath).SequenceEqual(foreignKey), "Higgsfield changes preserve another service's encrypted credentials");
            Call(window,"SaveHiggsfieldApiBalance",(object)(double?)3.25);
            Check(Shown(cards) == "eleven,higgsfield,higgsfield-api" && ServicePlan.ReadActiveServiceId(data) == "codex","Saving an API balance restores its card without rewriting the legacy single-service selection");
            Call(window,"SaveHiggsfieldApiBalance",new object[] { null });

            Check(cards["higgsfield"].Visibility == Visibility.Visible && cards["higgsfield-api"].Visibility == Visibility.Collapsed, "Connected web credits stay visible while an unrecorded API balance is hidden");
            var tracked = ((IEnumerable<string>)Call(window, "ActiveUsageServices")).ToList();
            Check(tracked.Contains("higgsfield") && !tracked.Contains("higgsfield-api"), "Only configured Higgsfield services enter tracking");
            Call(window, "SaveHiggsfieldApiBalance", (object)(double?)7.25); StopBackground(window);
            Check(cards["higgsfield-api"].Visibility == Visibility.Visible && cards["higgsfield"].Visibility == Visibility.Visible, "Both independently connected Higgsfield services display together");
            Check(Field<UsageJournal>(window, "usageJournal").GetHistory().Any(p => p.ServiceId == "higgsfield-api" && p.Source == "manual" && p.Remaining == 7.25), "A manual API save records amount and source in history");
            Check(pending.Count == 0, "A USD balance above its threshold queues no alert");
            var webSettings = Field<HiggsfieldSettings>(window, "higgsfieldSettings"); webSettings.WebConnected = false;
            Call(window, "UpdateServiceVisibility");
            Check(cards["higgsfield"].Visibility == Visibility.Collapsed && cards["higgsfield-api"].Visibility == Visibility.Visible, "Disconnecting web credits does not hide the independent API balance");
            Check(HiggsfieldSettingsStore.Load().ApiBalanceUsd == 7.25 && ServicePlan.ReadActiveServiceId(data) == "codex", "Higgsfield changes keep the API balance and never rewrite the legacy selection");
            var orderBody = new StackPanel(); Call(window, "BuildOrderControls", orderBody, "higgsfield-api");
            Check(Texts(orderBody).Any(t => t.Text.Contains("8 / 8")), "Service order controls include all eight entries");
            CheckWorkspaceConnectionUi(window, cliLog);
            CheckManagementWindowLifetime(window,cliLog,root);
            Check(server.Requests == 0, "Higgsfield connections, balances and management windows never contact the retired membership server");

            SettingsStore.DataDirectoryOverride = Path.Combine(root, "preview");
            preview = new WidgetWindow(true); StopBackground(preview); Call(preview, "ShowDemo");
            var demoWeb = Field<ProviderView>(preview, "higgsfieldView"); var demoApi = Field<ProviderView>(preview, "higgsfieldApiView");
            Check(demoWeb.Snapshot != null && demoWeb.Snapshot.Rows.Any(row => row.Remaining == 120), "Preview includes 120 sample web credits");
            Check(demoApi.Snapshot != null && demoApi.Snapshot.Rows.Any(row => row.DisplayValue == "$12.50"), "Preview includes a clearly separate sample API dollar balance");
            Check(!Directory.Exists(SettingsStore.DataDirectoryOverride), "Preview never persists connection or balance samples");
            CheckWorkspacePreviewUi(preview, cliLog);
            Console.WriteLine("Higgsfield UI tests passed: " + checks + " checks. No real CLI, network, login or live account changes."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (preview != null) { StopBackground(preview); ((Task)Call(preview, "FlushUsageTrackingAsync")).GetAwaiter().GetResult(); preview.Close(); }
            if (window != null) { StopBackground(window); SettingsStore.DataDirectoryOverride = Path.Combine(root, "data"); ((Task)Call(window, "FlushUsageTrackingAsync")).GetAwaiter().GetResult(); window.Close(); }
            SettingsStore.DataDirectoryOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
            Environment.SetEnvironmentVariable("HIGGSFIELD_WORKSPACE_ID", previousWorkspace);
            Environment.SetEnvironmentVariable(FixtureMode, previousMode);
            Environment.SetEnvironmentVariable(FixtureLog, previousLog);
            Environment.SetEnvironmentVariable(FixtureGate, previousGate);
            string full = Path.GetFullPath(root);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("higgsfield-ui-data-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private static void CheckOrderMigration()
    {
        var legacy = new[] { "cursor", "eleven", "codex", "openai", "gemini", "claude" };
        SettingsStore.Save(new WidgetSettings { ServiceOrder = legacy, CollapsedServices = new[] { "cursor", "higgsfield-api" } });
        var loaded = SettingsStore.Load();
        Check(loaded.ServiceOrder.Length == 8 && loaded.ServiceOrder.Take(6).SequenceEqual(legacy), "Legacy ordering remains unchanged while two new services are appended");
        Check(loaded.ServiceOrder.Skip(6).SequenceEqual(new[] { "higgsfield", "higgsfield-api" }), "New web and API IDs migrate in stable order");
        Check(loaded.CollapsedServices.Contains("higgsfield-api"), "Collapsed-state validation accepts the new API ID");
        Check(ServicePlan.IsSupported("higgsfield") && ServicePlan.IsSupported("higgsfield-api"), "Both new IDs pass Free plan validation");
    }

    private static bool DisplayedWithin(DependencyObject control, DependencyObject body)
    {
        while (control != null) {
            var element = control as FrameworkElement;
            if (element != null && element.Visibility != Visibility.Visible) return false;
            if (Object.ReferenceEquals(control, body)) return true;
            control = LogicalTreeHelper.GetParent(control);
        }
        return false;
    }

    private static StackPanel ConnectionEditor(WidgetWindow window)
    {
        var body = new StackPanel(); Call(window, "BuildHiggsfieldSettings", body); return body;
    }

    private static void CheckWorkspaceConnectionUi(WidgetWindow window, string cliLog)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try {
            var initial = new HiggsfieldSettings { CliPath = typeof(HiggsfieldUiTests).Assembly.Location, ApiBalanceUsd = 9.25, ApiObservedAt = DateTimeOffset.Now.AddMinutes(-5) };
            Call(window, "SaveHiggsfieldSettings", initial);
            var cards = Field<Dictionary<string, StackPanel>>(window, "serviceCards");
            var body = ConnectionEditor(window);
            var workspace = Accessible<TextBox>(body, "Higgsfield 워크스페이스 ID");
            var choices = Accessible<ComboBox>(body, "Higgsfield 작업 공간 선택");
            Check(!DisplayedWithin(choices, body) && choices.Items.Count == 0, "Workspace choices start hidden rather than requiring users to enter an ID");
            Check(ButtonNamed(body, "작업 공간 불러오기") != null && ButtonNamed(body, "선택한 작업 공간 연결") != null, "Workspace discovery and selected-workspace verification are distinct actions");
            Check(Accessible<TextBox>(body, "Higgsfield 실행 파일 경로").Text == initial.CliPath, "An isolated test executable replaces all CLI discovery in connection fixtures");
            Environment.SetEnvironmentVariable(FixtureMode, "single");
            Click(ButtonNamed(body, "연결 확인")); WaitForConnection(window);
            var saved = HiggsfieldSettingsStore.Load();
            Check(saved.WebConnected && saved.WorkspaceId == FirstWorkspace && saved.CliPath == initial.CliPath, "A single workspace is recovered and its resolved ID is persisted after verified credits");
            Check(saved.ApiBalanceUsd == initial.ApiBalanceUsd && saved.ApiObservedAt == initial.ApiObservedAt, "Automatic web connection preserves the separate manual API balance and recorded time");
            Check(cards["higgsfield"].Visibility == Visibility.Visible && Field<ProviderView>(window, "higgsfieldView").Snapshot.Rows.Single().Remaining == 120.5, "Recovered connection shows its card with the verified credits");
            Check(CliCalls(cliLog).SequenceEqual(new[] { "account", "workspace", "account" }), "Recovery uses only account status and workspace list, with no login or mutation command");

            // The next normal poll must retain a recovered ID as well, otherwise
            // every restart would repeat the missing-default failure.
            saved.WorkspaceId = String.Empty; Call(window, "SaveHiggsfieldSettings", saved);
            int readStart = CliCalls(cliLog).Length;
            var refresh = (Task)Call(window, "ReadHiggsfield");
            PumpUntil(() => refresh.IsCompleted, "Background workspace recovery completes"); refresh.GetAwaiter().GetResult(); StopBackground(window);
            Check(HiggsfieldSettingsStore.Load().WorkspaceId == FirstWorkspace, "A background credit refresh also retains the recovered workspace ID");
            Check(CliCalls(cliLog).Skip(readStart).SequenceEqual(new[] { "account", "workspace", "account" }), "Background recovery never starts authentication or changes CLI defaults");

            initial.WebConnected = false; initial.WorkspaceId = String.Empty;
            Call(window, "SaveHiggsfieldSettings", initial);
            body = ConnectionEditor(window); workspace = Accessible<TextBox>(body, "Higgsfield 워크스페이스 ID");
            choices = Accessible<ComboBox>(body, "Higgsfield 작업 공간 선택");
            int unverifiedStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "작업 공간 불러오기")); WaitForConnection(window);
            Check(choices.Items.Count == 1 && choices.SelectedIndex == 0 && workspace.Text == FirstWorkspace, "An explicit one-workspace lookup supplies a convenient selection without ID copying");
            Check(CliCalls(cliLog).Skip(unverifiedStart).SequenceEqual(new[] { "workspace" }) && !HiggsfieldSettingsStore.Load().WebConnected && HiggsfieldSettingsStore.Load().WorkspaceId.Length == 0, "An unverified lookup never persists a connection or workspace preference");
            Check(cards["higgsfield"].Visibility == Visibility.Collapsed, "A workspace lookup alone does not show an unverified web credit card");
            workspace.Clear();
            int loginStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "힉스필드 로그인")); WaitForConnection(window);
            Check(CliCalls(cliLog).Skip(loginStart).SequenceEqual(new[] { "auth", "account", "workspace", "account" }), "Browser-login callback recovers a missing default before verifying account credits");
            Check(HiggsfieldSettingsStore.Load().WebConnected && HiggsfieldSettingsStore.Load().WorkspaceId == FirstWorkspace, "A successful login saves the recovered workspace instead of the pre-login blank ID");

            initial.WebConnected = false; initial.WorkspaceId = String.Empty;
            Call(window, "SaveHiggsfieldSettings", initial);
            Environment.SetEnvironmentVariable(FixtureMode, "multiple");
            body = ConnectionEditor(window); workspace = Accessible<TextBox>(body, "Higgsfield 워크스페이스 ID");
            choices = Accessible<ComboBox>(body, "Higgsfield 작업 공간 선택");
            int selectionStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "연결 확인")); WaitForConnection(window);
            Check(DisplayedWithin(choices, body) && choices.Items.Count == 2, "Multiple unselected workspaces reveal the selector in the normal connection screen");
            Check(choices.SelectedIndex == -1 && workspace.Text.Length == 0, "An ambiguous list never silently selects the first workspace");
            Check(Texts(body).Any(t => t.Text.Contains("작업 공간 선택")), "The connection feedback explains the next workspace-selection step");
            Check(!HiggsfieldSettingsStore.Load().WebConnected && cards["higgsfield"].Visibility == Visibility.Collapsed, "A selection prerequisite never persists an unverified connection or shows its card");
            Check(CliCalls(cliLog).Skip(selectionStart).SequenceEqual(new[] { "account", "workspace" }), "Ambiguous workspaces stop before an arbitrary account read");
            var first = choices.Items[0] as ComboBoxItem; var second = choices.Items[1] as ComboBoxItem;
            Check(first != null && second != null && Object.Equals(first.Tag, FirstWorkspace) && Object.Equals(second.Tag, SecondWorkspace), "Workspace choices retain their validated UUIDs separately from display labels");
            Check(Object.Equals(first.Content, "Personal fixture · pro") && Object.Equals(second.Content, "Team fixture · team"), "Workspace names and plan labels distinguish personal and team choices");
            choices.SelectedIndex = 1;
            Check(workspace.Text == SecondWorkspace, "Choosing a workspace immediately populates its exact ID without manual copying");
            int verificationStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "선택한 작업 공간 연결")); WaitForConnection(window);
            saved = HiggsfieldSettingsStore.Load();
            Check(saved.WebConnected && saved.WorkspaceId == SecondWorkspace, "Selected-workspace verification saves the actual chosen ID");
            Check(Field<ProviderView>(window, "higgsfieldView").Snapshot.Rows.Single().Remaining == 62.75, "The saved connection displays the chosen workspace's own credits");
            Check(saved.ApiBalanceUsd == initial.ApiBalanceUsd && saved.ApiObservedAt == initial.ApiObservedAt, "Workspace selection does not overwrite independent API balance settings");
            Check(CliCalls(cliLog).Skip(verificationStart).SequenceEqual(new[] { "account" }), "Selecting an already listed workspace verifies only that account instead of logging in again");

            int listStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "작업 공간 불러오기")); WaitForConnection(window);
            Check(CliCalls(cliLog).Skip(listStart).SequenceEqual(new[] { "workspace" }), "Explicit workspace discovery does not invoke login or request account credits");
            Check(choices.SelectedIndex == 1 && workspace.Text == SecondWorkspace, "Refreshing choices preserves the matching saved selection");
            Check(HiggsfieldSettingsStore.Load().WorkspaceId == SecondWorkspace && HiggsfieldSettingsStore.Load().ApiBalanceUsd == 9.25, "Read-only discovery does not save or replace independent settings");

            Environment.SetEnvironmentVariable(FixtureMode, "fail");
            int failureStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "작업 공간 불러오기")); WaitForConnection(window);
            Check(CliCalls(cliLog).Skip(failureStart).SequenceEqual(new[] { "workspace" }), "Failed discovery remains a read-only request");
            saved = HiggsfieldSettingsStore.Load();
            Check(saved.WebConnected && saved.WorkspaceId == SecondWorkspace && saved.ApiBalanceUsd == 9.25, "An expired-login discovery error preserves the verified connection and API settings");
            Check(Texts(body).Any(t => t.Text.Contains("로그인")), "Failed workspace discovery presents an actionable login explanation");
            Check(ButtonNamed(body, "연결 확인").IsEnabled && ButtonNamed(body, "작업 공간 불러오기").IsEnabled && workspace.IsEnabled, "Connection controls recover after a failed lookup");

            Environment.SetEnvironmentVariable(FixtureMode, "wait");
            int cancelStart = CliCalls(cliLog).Length;
            Click(ButtonNamed(body, "작업 공간 불러오기"));
            PumpUntil(() => CliCalls(cliLog).Length > cancelStart, "Cancellation fixture starts only its hidden test process");
            Check(!ButtonNamed(body, "연결 확인").IsEnabled && !ButtonNamed(body, "작업 공간 불러오기").IsEnabled && !workspace.IsEnabled, "Workspace lookup suppresses competing actions while its process is pending");
            Click(ButtonNamed(body, "취소")); WaitForConnection(window);
            saved = HiggsfieldSettingsStore.Load();
            Check(saved.WebConnected && saved.WorkspaceId == SecondWorkspace && saved.ApiBalanceUsd == 9.25, "Cancelling workspace discovery preserves both verified web and independent API settings");
            Check(ButtonNamed(body, "연결 확인").IsEnabled && ButtonNamed(body, "작업 공간 불러오기").IsEnabled && workspace.IsEnabled, "Cancelled workspace discovery restores all connection controls");

            Environment.SetEnvironmentVariable(FixtureMode, "single");
            int invalidStart = CliCalls(cliLog).Length;
            workspace.Text = "not-a-uuid";
            Click(ButtonNamed(body, "연결 확인")); WaitForConnection(window);
            Check(CliCalls(cliLog).Length == invalidStart && HiggsfieldSettingsStore.Load().WorkspaceId == SecondWorkspace, "Invalid manually typed IDs cannot launch a subprocess or replace verified settings");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static StackPanel ManagedEditor(WidgetWindow window, string id)
    {
        Call(window,"OpenServices",id);
        var dialog = Field<Window>(window,"servicesWindow");
        dialog.ShowInTaskbar = false;
        return Controls<StackPanel>(dialog).Single(panel => Object.Equals(panel.Tag,id));
    }

    private static void CloseServices(WidgetWindow window)
    {
        var dialog = Field<Window>(window,"servicesWindow");
        if (dialog != null) dialog.Close();
        Check(Field<Window>(window,"servicesWindow") == null,"Closing management actually releases the old window");
    }

    private static void CheckManagementWindowLifetime(WidgetWindow window, string cliLog, string root)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try {
            // Creating the owner HWND does not invoke Loaded/membership startup.
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Call(window,"SaveHiggsfieldSettings",new HiggsfieldSettings { CliPath = typeof(HiggsfieldUiTests).Assembly.Location });
            string gate = Path.Combine(root,"lifetime-gate-1");
            Environment.SetEnvironmentVariable(FixtureGate,gate);
            Environment.SetEnvironmentVariable(FixtureMode,"gate-single");
            var body = ManagedEditor(window,"higgsfield");
            int before = CliCalls(cliLog).Length;
            Click(ButtonNamed(body,"힉스필드 로그인"));
            PumpUntil(() => CliCalls(cliLog).Length > before,"Hidden fixture enters the pending authentication phase");
            var operation = Field<CancellationTokenSource>(window,"higgsfieldOperation");
            Check(operation != null && !operation.IsCancellationRequested,"Login has a live widget-owned cancellation source");
            var apiBody = ManagedEditor(window,"eleven");
            var input = Controls<PasswordBox>(apiBody).Single(); input.Password = "fixture-never-send";
            CloseServices(window);
            Check(input.Password.Length == 0,"Management close clears API password fields while other login continues");
            Check(Object.ReferenceEquals(operation,Field<CancellationTokenSource>(window,"higgsfieldOperation")) && !operation.IsCancellationRequested,"Window close does not cancel or replace pending login");
            var restored = ManagedEditor(window,"higgsfield");
            Check(Object.ReferenceEquals(body,restored),"Reopening reattaches the same pending login editor");
            Check(!ButtonNamed(restored,"힉스필드 로그인").IsEnabled && ButtonNamed(restored,"취소").Visibility == Visibility.Visible,"Reopened pending view retains its busy state and explicit cancel action");
            CloseServices(window);
            File.WriteAllText(gate,"release"); WaitForConnection(window);
            restored = ManagedEditor(window,"higgsfield");
            Check(Object.ReferenceEquals(body,restored) && Texts(restored).Any(t => t.Text == "연결 완료"),"Login completed while closed preserves the success feedback on reopen");
            Check(HiggsfieldSettingsStore.Load().WebConnected && HiggsfieldSettingsStore.Load().WorkspaceId == FirstWorkspace,"Closed-window login commits only verified connection settings");

            var workspace = Accessible<TextBox>(body,"Higgsfield 워크스페이스 ID"); workspace.Clear();
            gate = Path.Combine(root,"lifetime-gate-2"); Environment.SetEnvironmentVariable(FixtureGate,gate); Environment.SetEnvironmentVariable(FixtureMode,"gate-multiple");
            before = CliCalls(cliLog).Length; Click(ButtonNamed(body,"힉스필드 로그인"));
            PumpUntil(() => CliCalls(cliLog).Length > before,"Multiple-workspace fixture enters authentication");
            CloseServices(window); File.WriteAllText(gate,"release"); WaitForConnection(window);
            restored = ManagedEditor(window,"higgsfield");
            var choices = Accessible<ComboBox>(restored,"Higgsfield 작업 공간 선택");
            Check(choices.Items.Count == 2 && choices.SelectedIndex == -1,"Workspace selection received while closed is restored without an arbitrary default");
            Check(Texts(restored).Any(t => t.Text == "로그인 완료 · 작업 공간 선택"),"Reopening preserves the required next step after browser login");
            choices.SelectedIndex = 1; CloseServices(window); restored = ManagedEditor(window,"higgsfield");
            Check(choices.SelectedIndex == 1 && workspace.Text == SecondWorkspace,"Closing again preserves the uncommitted workspace selection");
            Check(HiggsfieldSettingsStore.Load().WorkspaceId == FirstWorkspace,"Closing an unverified workspace choice does not silently save it");
            Click(ButtonNamed(restored,"선택한 작업 공간 연결")); WaitForConnection(window);
            Check(HiggsfieldSettingsStore.Load().WorkspaceId == SecondWorkspace,"Explicit confirmation after reopen verifies then saves the chosen workspace");

            gate = Path.Combine(root,"lifetime-gate-3"); Environment.SetEnvironmentVariable(FixtureGate,gate); Environment.SetEnvironmentVariable(FixtureMode,"gate-fail");
            before = CliCalls(cliLog).Length; Click(ButtonNamed(body,"힉스필드 로그인"));
            PumpUntil(() => CliCalls(cliLog).Length > before,"Failure fixture starts authentication");
            CloseServices(window); File.WriteAllText(gate,"release"); WaitForConnection(window);
            restored = ManagedEditor(window,"higgsfield");
            Check(Texts(restored).Any(t => t.Text == "연결하지 못했습니다"),"A closed-window login error is retained instead of replaced by generic connected text");
            Check(HiggsfieldSettingsStore.Load().WorkspaceId == SecondWorkspace,"Closed-window authentication failure retains the previous verified workspace");

            gate = Path.Combine(root,"lifetime-gate-4"); Environment.SetEnvironmentVariable(FixtureGate,gate); Environment.SetEnvironmentVariable(FixtureMode,"gate-single");
            before = CliCalls(cliLog).Length; Click(ButtonNamed(body,"힉스필드 로그인"));
            PumpUntil(() => CliCalls(cliLog).Length > before,"Cancelable fixture starts authentication");
            CloseServices(window); restored = ManagedEditor(window,"higgsfield");
            Click(ButtonNamed(restored,"취소")); WaitForConnection(window);
            Check(Texts(restored).Any(t => t.Text == "연결 취소됨"),"Explicit cancel works after closing and reopening management");
            Check(HiggsfieldSettingsStore.Load().WorkspaceId == SecondWorkspace,"Explicit cancellation leaves the existing saved connection intact");
            CloseServices(window);
        }
        finally { CloseServices(window); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void CheckWorkspacePreviewUi(WidgetWindow preview, string cliLog)
    {
        var body = ConnectionEditor(preview);
        int before = CliCalls(cliLog).Length;
        Click(ButtonNamed(body, "작업 공간 불러오기"));
        Click(ButtonNamed(body, "힉스필드 로그인"));
        Click(ButtonNamed(body, "연결 확인"));
        Check(CliCalls(cliLog).Length == before && Field<CancellationTokenSource>(preview, "higgsfieldOperation") == null, "All preview connection actions avoid starting even the isolated fixture subprocess");
        Check(!Directory.Exists(SettingsStore.DataDirectoryOverride), "Preview login, discovery and verification never persist samples or settings");
        Check(Texts(body).Any(t => t.Text.Contains("미리보기")), "Preview actions explain why no real account request is sent");
    }

    private static void CheckHistory(string directory)
    {
        var journal = new UsageJournal(directory, false); var now = DateTimeOffset.Now;
        var web = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "웹 크레딧", Remaining = 120, DisplayValue = "120" } } };
        var api = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = "API 잔액", Remaining = 12.5, DisplayValue = "$12.50" } } };
        Check(journal.Observe("higgsfield", web, now, "cli", now).Count == 0, "CLI absolute credits create no fabricated low-percentage alert");
        Check(journal.Observe("higgsfield-api", api, now, "manual", now).Count == 0, "Manual dollar amount creates no fabricated low-percentage alert");
        var points = new UsageJournal(directory, false).GetHistory();
        Check(points.Count == 2 && points.Any(p => p.ServiceId == "higgsfield" && p.Source == "cli" && p.Remaining == 120), "CLI Higgsfield history persists and reloads");
        Check(points.Any(p => p.ServiceId == "higgsfield-api" && p.Source == "manual" && p.DisplayValue == "$12.50" && !p.RemainingPercent.HasValue), "Manual API amount and source survive history reload without a fake ratio");
        Check(journal.CheckResetReminders(now.AddMinutes(1), new[] { "higgsfield", "higgsfield-api" }).Count == 0, "Balances without provider deadlines never invent reset alerts");
        journal.ForgetService("higgsfield-api");
        Check(new UsageJournal(directory, false).GetHistory().Count == 2, "Disconnecting a Higgsfield service retains its historical readings");
    }
}
