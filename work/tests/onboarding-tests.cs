using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class OnboardingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int assertions;
    private static T Field<T>(WidgetWindow w,string name) { return (T)typeof(WidgetWindow).GetField(name,Hidden).GetValue(w); }
    private static void Set(WidgetWindow w,string name,object value) { typeof(WidgetWindow).GetField(name,Hidden).SetValue(w,value); }
    private static object Call(WidgetWindow w,string name,params object[] args) { try { return typeof(WidgetWindow).GetMethod(name,Hidden).Invoke(w,args); } catch (TargetInvocationException error) { throw error.InnerException; } }
    private static void Check(bool value,string message) { assertions++; if (!value) throw new Exception(message); }
    private static IEnumerable<T> Elements<T>(DependencyObject parent) where T: DependencyObject
    {
        var typed = parent as T; if (typed != null) yield return typed;
        foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
            var next = child as DependencyObject;
            if (next != null) foreach (var item in Elements<T>(next)) yield return item;
        }
    }
    private static void Press(DependencyObject parent,string text) { Elements<Button>(parent).Single(button => Convert.ToString(button.Content) == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Storage(string root)
    {
        Check(OnboardingStore.ShouldOffer(root),"A clean profile offers onboarding without a membership login");
        foreach (string filename in new[] { "settings.json", "extra-settings.json", "plan-settings.json", "membership-session.dat", "elevenlabs-key.dat", "openai-admin-key.dat", "higgsfield-settings.json", "usage-journal.json", "usage-history.json" }) {
            string path = Path.Combine(root,filename); Directory.CreateDirectory(root); File.WriteAllText(path,"");
            Check(!OnboardingStore.ShouldOffer(root),"An existing/tombstone " + filename + " suppresses upgrade onboarding"); File.Delete(path);
        }
        var state = new OnboardingState { Shown = true, ServiceId = "eleven" }; OnboardingStore.Save(root,state);
        var loaded = OnboardingStore.Load(root);
        Check(!OnboardingStore.ShouldOffer(root) && loaded.Shown && !loaded.Completed && loaded.ServiceId == "eleven","Later and close remain dismissed across restarts");
        loaded.Completed = true; OnboardingStore.Save(root,loaded);
        Check(OnboardingStore.Load(root).Completed,"Verified completion survives restart");
        var json = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"onboarding.json")));
        Check(json.Count == 4 && json.Keys.All(key => new[] { "Version", "Shown", "Completed", "ServiceId" }.Contains(key)),"Only four non-secret fields are stored");
        File.WriteAllText(Path.Combine(root,"onboarding.json"),"{\"ServiceId\":\"../private\",\"Completed\":\"yes\"}");
        Check(OnboardingStore.Load(root).ServiceId == "codex" && !OnboardingStore.Load(root).Completed,"Untrusted ids and mistyped fields cannot select arbitrary services");
        foreach (string value in new[] { "", "{", "null", "[]", new string('x',5000) }) {
            File.WriteAllText(Path.Combine(root,"onboarding.json"),value);
            Check(OnboardingStore.Load(root).Shown && !OnboardingStore.ShouldOffer(root),"Malformed optional guide state never forces another setup");
        }
        Check(!Directory.GetFiles(root,".onboarding-*.tmp").Any(),"Atomic state saves leave no temporary files");
    }

    private static void Verification()
    {
        var now = DateTimeOffset.Now;
        var view = new ProviderView { Id = "codex", LastSuccess = now, Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { RemainingPercent = 0, ResetsAt = now.AddDays(1) } } } };
        Check(OnboardingStore.HasVerifiedUsage(view,true,true,now),"A real zero balance is a successful connection");
        Check(!OnboardingStore.HasVerifiedUsage(view,false,true,now),"A stored inactive service is not silently selected in Free");
        Check(!OnboardingStore.HasVerifiedUsage(view,true,false,now),"A snapshot alone does not prove saved connection");
        view.Busy = true; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"In-flight checks cannot report completion"); view.Busy = false;
        view.Error = "fixture"; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Old usage with a current error is not completion"); view.Error = null;
        view.LastSuccess = now.AddHours(-1); Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Stale automatic readings are not completion");
        view.LastSuccess = now.AddHours(1); Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Future readings are not completion"); view.LastSuccess = now;
        view.Snapshot.Rows[0].ResetsAt = now.AddMinutes(-1); Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Expired quota requires a current reading"); view.Snapshot.Rows[0].ResetsAt = null;
        view.Snapshot.Rows[0].RemainingPercent = null; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Unsupported/empty quotas do not look connected");
        view.Snapshot.Rows[0].RemainingPercent = Double.NaN; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Invalid numbers are not usage");
        view.Snapshot.Rows[0].DisplayValue = "—"; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Placeholder display text is not a measurement");
        foreach (double invalid in new[] { -1.0,101.0,Double.PositiveInfinity }) { view.Snapshot.Rows[0].RemainingPercent = invalid; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Out-of-range percentage is not completion"); }
        view.Snapshot.Rows[0].RemainingPercent = null; view.Snapshot.Rows[0].Remaining = -1; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Negative remaining balance is not completion"); view.Snapshot.Rows[0].Remaining = null;
        view.Id = "openai"; view.Snapshot.Rows[0].Label = "이번 달 비용"; view.Snapshot.Rows[0].ResetsAt = now.AddDays(1);
        foreach (string value in new[] { "$0.00", "-$1.00", "<$0.01", ">-$0.01" }) { view.Snapshot.Rows[0].DisplayValue = value; Check(OnboardingStore.HasVerifiedUsage(view,true,true,now),"OpenAI cost/refund without a budget is supported: " + value); }
        view.Id = "claude"; view.ReceiptState = ProviderReceiptState.QuotaUnavailable; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Claude Free/API missing quota is never treated as supported auto usage");
        view.ReceiptState = ProviderReceiptState.Waiting; Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"Installed Claude hook without a receipt stays pending");
        view.ReceiptState = ProviderReceiptState.ResetWaiting; view.Snapshot.Rows[0].Label = "5시간 한도 · 수신 대기"; view.Snapshot.Rows[0].RemainingPercent = 20; view.Snapshot.Rows[0].ResetsAt = now.AddMinutes(-1);
        Check(!OnboardingStore.HasVerifiedUsage(view,true,true,now),"All expired Claude windows remain incomplete");
        view.Snapshot.Rows.Add(new UsageRow { Label = "주간 한도", RemainingPercent = 60, ResetsAt = now.AddDays(2) });
        Check(OnboardingStore.HasVerifiedUsage(view,true,true,now),"A current weekly quota remains usable while the short window awaits reset");
        view.Snapshot.Rows.RemoveAt(1); view.Snapshot.Rows[0].Label = "manual"; view.Snapshot.Rows[0].ResetsAt = null;
        view.ReceiptState = ProviderReceiptState.None; view.Source = "manual"; view.LastSuccess = now.AddDays(-1); view.Snapshot.Rows[0].RemainingPercent = 38;
        Check(OnboardingStore.HasVerifiedUsage(view,true,true,now),"Valid manual records can complete with their explicit manual label");
    }

    private static void HiddenUi(string root)
    {
        SettingsStore.DataDirectoryOverride = root;
        WidgetWindow window = null;
        try {
            window = new WidgetWindow(false); Field<DispatcherTimer>(window,"timer").Stop(); Field<Forms.NotifyIcon>(window,"tray").Visible = false;
            Check(Field<bool>(window,"initialOnboardingPending"),"New-user eligibility is captured before plan/settings initialization creates files");
            Call(window,"ShowInitialOnboarding");
            Check(Field<Window>(window,"onboardingWindow") == null && Field<bool>(window,"initialOnboardingPending"),"Hidden fixtures and tray-only startup never steal focus for onboarding");
            new WindowInteropHelper(window).EnsureHandle();
            Window guide = (Window)Call(window,"CreateOnboardingDialog");
            Check(!guide.IsVisible && !window.IsLoaded,"The production guide can be tested without starting a live refresh");
            var selector = Elements<ListBox>(guide).Single();
            Check(selector.Items.Count == 8,"All supported automatic and manual services are available");
            string prior = Field<ServicePlan>(window,"servicePlan").ActiveServiceId;
            selector.SelectedIndex = 6; Press(guide,"다음");
            Check(Elements<TextBlock>(guide).Any(text => text.Text.Contains("‘힉스필드 로그인’")),"Higgsfield guidance names the exact sign-in action in the connection editor");
            Press(guide,"이전"); selector = Elements<ListBox>(guide).Single();
            selector.SelectedIndex = 3; Press(guide,"다음");
            Check(Elements<TextBlock>(guide).Any(text => text.Text.Contains("‘잔여량 조회’") && text.Text.Contains("대화 없이") && text.Text.Contains("2.1.304")),"Claude onboarding directs a new user to the supported query without requiring a model response");
            Check(!Elements<TextBlock>(guide).Any(text => text.Text.Contains("수신 확인") || text.Text.Contains("다음 응답을 받은 후")),"The new-user guide does not send the user to the retired receipt action");
            Press(guide,"이전"); selector = Elements<ListBox>(guide).Single();
            selector.SelectedIndex = 4;
            Check(Field<ServicePlan>(window,"servicePlan").ActiveServiceId == prior,"Selecting Gemini leaves the current Free service unchanged");
            Press(guide,"다음");
            Check(Field<int>(window,"onboardingStep") == 1 && Elements<TextBlock>(guide).Any(text => text.Text.Contains("자동 조회하지 않습니다")),"Manual services clearly explain that no API key or automatic query is available");
            Set(window,"onboardingStep",2); Call(window,"RenderOnboarding");
            Check(Field<Button>(window,"onboardingFinish").Visibility == Visibility.Collapsed,"A guide cannot finish simply by opening service settings");
            var editor = new StackPanel(); Call(window,"BuildManualEditor",editor,"gemini");
            var boxes = Elements<TextBox>(editor).ToArray(); boxes[0].Text = "daily fixture"; boxes[1].Text = "36"; Press(editor,"지금 확인한 값으로 저장");
            Call(window,"RefreshOnboardingProgress");
            Check(Field<ServicePlan>(window,"servicePlan").ActiveServiceId == prior && (bool)Call(window,"IsServiceActive","gemini"),"A validated save makes Gemini usable without replacing the legacy preference");
            Check(Field<OnboardingState>(window,"onboardingState").Completed && OnboardingStore.Load(root).Completed,"The guide completes only after the actual editor produced a saved reading");
            Check(Field<TextBlock>(window,"onboardingFeedback").Text.Contains("수동") && Field<TextBlock>(window,"onboardingFeedback").Text.Contains("36%"),"Completion shows the real reading and explicitly identifies manual data");
            Check(Field<Button>(window,"onboardingFinish").Visibility == Visibility.Visible,"Verified reading exposes the widget action");
            object[] leaseArguments = { "gemini",null };
            Check((bool)Call(window,"TryReserveServiceConnection",leaseArguments),"A pending reconnection can reserve the existing service");
            Call(window,"RefreshOnboardingProgress");
            Check(Field<Button>(window,"onboardingFinish").Visibility == Visibility.Collapsed && !Field<OnboardingState>(window,"onboardingState").Completed,"A pending service lease hides completion even while the previous saved reading is recent");
            ((IDisposable)leaseArguments[1]).Dispose(); Call(window,"RefreshOnboardingProgress");
            Check(Field<Button>(window,"onboardingFinish").Visibility == Visibility.Visible,"Completion resumes only after the service operation has released its lease");
            var pending = new CancellationTokenSource(); Set(window,"codexLoginOperation",pending);
            Press(guide,"나중에"); Pump();
            Check(!pending.IsCancellationRequested,"Dismissing the guide does not cancel an existing browser sign-in");
            Check(Field<Window>(window,"onboardingWindow") == null && !Field<DispatcherTimer>(window,"onboardingTimer").IsEnabled,"Guide close clears the view and stops polling");
            Set(window,"codexLoginOperation",null); pending.Dispose();
            Check(!OnboardingStore.ShouldOffer(root),"Closed guide is not forced again on the next run");
            guide = (Window)Call(window,"CreateOnboardingDialog");
            Check(Elements<ListBox>(guide).Single().SelectedIndex == 4,"Menu re-entry resumes the last service without reconnecting others");
            L.Language = "en"; Set(window,"onboardingStep",1); Call(window,"RefreshOnboardingProgress");
            Check(Elements<TextBlock>(guide).Any(text => text.Text.Contains("No API key is needed")),"English setup instructions distinguish manual readings");
            Check(guide.Title == "Nephylo · Connection guide" && Elements<TextBlock>(guide).Any(text => text.Text == "Connect your first service"),"Changing language refreshes an already-open guide header and title");
            Field<OnboardingState>(window,"onboardingState").ServiceId = "claude"; Call(window,"RenderOnboarding");
            Check(Elements<TextBlock>(guide).Any(text => text.Text.Contains("Free, API-key and web users")),"English Claude onboarding explains the Free/API limitation before connecting");
            Check(Elements<TextBlock>(guide).Any(text => text.Text.Contains("Check remaining usage") && text.Text.Contains("without sending a conversation")),"English guidance names the current query action and does not require a conversation");
            guide.Width = 390; guide.Height = 440; guide.Measure(new Size(390,440)); guide.Arrange(new Rect(0,0,390,440));
            Check(Elements<ScrollViewer>(guide).Any(view => view.VerticalScrollBarVisibility == ScrollBarVisibility.Auto),"Short/high-DPI work areas keep instructions scrollable and footer separate");
            guide.Close(); L.Language = "ko";
            Check(!window.IsLoaded && !window.IsVisible,"No real account requests or visible windows were started by the hidden WPF fixture");
            ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult();
            Check(File.Exists(Path.Combine(root,"usage-journal.json")),"The synthetic reading is durably saved before fixture teardown");
            Check(!Directory.GetFiles(root,".usage-journal.*.tmp").Any(),"The journal writer has released its temporary file before cleanup");
        } finally {
            if (window != null) {
                // This fixture bypasses the normal asynchronous Closing flush.
                // Join its background journal writer before deleting its data.
                ((Task)Call(window,"FlushUsageTrackingAsync")).GetAwaiter().GetResult();
                Set(window,"shutdownFlushed",true); Field<DispatcherTimer>(window,"timer").Stop(); window.Close();
                Field<CodexService>(window,"codex").Dispose(); Field<Forms.NotifyIcon>(window,"tray").Dispose();
            }
        }
    }

    [STAThread]
    public static int Main()
    {
        string parent = Path.GetFullPath(Path.GetDirectoryName(typeof(OnboardingTests).Assembly.Location));
        string root = Path.Combine(parent,"onboarding-test-data-" + Guid.NewGuid().ToString("N"));
        string priorCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        SynchronizationContext priorContext = SynchronizationContext.Current;
        try {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Environment.SetEnvironmentVariable("CODEX_HOME",Path.Combine(root,"codex"));
            ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(root,"claude");
            Storage(Path.Combine(root,"storage")); Verification(); HiddenUi(Path.Combine(root,"ui"));
            Console.WriteLine("Onboarding tests passed: " + assertions + " assertions. Isolated profile and hidden WPF; no live authentication, network, credential reads or visible windows."); return 0;
        } catch (Exception error) { Console.Error.WriteLine("Onboarding tests failed: " + error); return 1; }
        finally {
            SettingsStore.DataDirectoryOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null; Environment.SetEnvironmentVariable("CODEX_HOME",priorCodexHome); L.Language = "ko"; SynchronizationContext.SetSynchronizationContext(priorContext);
            string full = Path.GetFullPath(root);
            if (full.StartsWith(parent + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("onboarding-test-data-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
