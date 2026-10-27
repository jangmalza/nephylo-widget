using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class StartupRefreshTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026,10,13,12,0,0,TimeSpan.Zero);
    private static void Check(bool condition,string message) { if (!condition) throw new Exception(message); checks++; }
    private static object Get(object target,string name) { return target.GetType().GetField(name,Flags).GetValue(target); }
    private static void Set(object target,string name,object value) { target.GetType().GetField(name,Flags).SetValue(target,value); }
    private static object Call(object target,string name,params object[] args)
    {
        try { return target.GetType().GetMethod(name,Flags,null,args.Select(arg=>arg.GetType()).ToArray(),null).Invoke(target,args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void Pump()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> done,string message)
    {
        var elapsed=Stopwatch.StartNew();
        while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(1); }
        Check(done(),message);
    }
    private static void Await(Task task) { Until(()=>task.IsCompleted,"Synthetic read completes without a polling tick"); task.GetAwaiter().GetResult(); Pump(); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { throw new InvalidOperationException("Membership network is forbidden in the startup fixture."); }
    }
    private sealed class ControlledHttp : HttpMessageHandler
    {
        internal readonly string Host,Body;
        internal bool Block;
        internal HttpStatusCode Status=HttpStatusCode.OK;
        internal int Calls,Active,MaximumActive;
        private TaskCompletionSource<HttpResponseMessage> pending;
        internal ControlledHttp(string host,string body) { Host=host; Body=body; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if (request.RequestUri.Host != Host) throw new InvalidOperationException("Unexpected provider origin in fixture.");
            Calls++; Active++; MaximumActive=Math.Max(MaximumActive,Active);
            try {
                if (!Block) return Response();
                if (pending != null) throw new InvalidOperationException("Concurrent request unexpectedly started.");
                pending=new TaskCompletionSource<HttpResponseMessage>();
                return await pending.Task.ConfigureAwait(false);
            } finally { Active--; }
        }
        private HttpResponseMessage Response()
        {
            var response=new HttpResponseMessage(Status) { Content=new StringContent(Body) };
            if ((int)Status==429) response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        }
        internal void Release()
        {
            if (pending==null) throw new InvalidOperationException("No blocked fixture response.");
            var completion=pending; pending=null; Block=false; completion.SetResult(Response());
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly WidgetWindow Window;
        internal readonly MembershipClient Member;
        internal readonly ProviderView Eleven,OpenAi;
        internal readonly ControlledHttp ElevenHttp,OpenAiHttp;
        private readonly HttpClient elevenClient,openAiClient,previousElevenClient;
        private readonly FieldInfo elevenClientField;
        internal Fixture(string parent,bool loaded=true)
        {
            string root=Path.Combine(parent,Guid.NewGuid().ToString("N"));
            SettingsStore.DataDirectoryOverride=root; ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude");
            ElevenHttp=new ControlledHttp("api.elevenlabs.io","{\"tier\":\"starter\",\"character_count\":72,\"character_limit\":1000}");
            OpenAiHttp=new ControlledHttp("api.openai.com","{\"object\":\"page\",\"data\":[],\"has_more\":false,\"next_page\":null}");
            elevenClient=new HttpClient(ElevenHttp); openAiClient=new HttpClient(OpenAiHttp);
            elevenClientField=typeof(ElevenLabsService).GetField("Client",BindingFlags.Static|BindingFlags.NonPublic);
            previousElevenClient=(HttpClient)elevenClientField.GetValue(null); elevenClientField.SetValue(null,elevenClient);
            Member=new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            // A legacy membership object is deliberately supplied but never used;
            // accountless startup must not depend on restoring or verifying it.
            Set(Member,"userId","synthetic-user");
            Window=new WidgetWindow(true,Member) { ShowActivated=false,ShowInTaskbar=false,Opacity=0 };
            ((DispatcherTimer)Get(Window,"timer")).Stop(); ((Forms.NotifyIcon)Get(Window,"tray")).Visible=false;
            ((WidgetSettings)Get(Window,"settings")).AnimationsEnabled=false;
            if (loaded) { Window.Show(); Until(()=>Window.IsLoaded,"Transparent fixture reaches the actual Loaded state"); Pump(); }
            foreach (string name in new[] { "codexView","elevenView","openAiView","claudeView","geminiView","cursorView","higgsfieldView","higgsfieldApiView" }) {
                var view=(ProviderView)Get(Window,name); view.Snapshot=null; view.LastSuccess=null; view.Error=null; view.Issue=null; view.Busy=false;
            }
            Set(Window,"demo",false); Set(Window,"showingExtraDemo",false); Set(Window,"loginPending",true);
            Set(Window,"extras",new ExtraSettings()); Set(Window,"higgsfieldSettings",new HiggsfieldSettings());
            var plan=new ServicePlan(root,true,null,null); plan.Select("openai"); Set(Window,"servicePlan",plan);
            Set(Window,"openAi",new OpenAiUsageService(openAiClient,()=>Now,10000));
            Call(Window,"ApplyMembershipState"); Pump();
            Set(Window,"secret","synthetic-eleven-key"); Set(Window,"openAiKey","synthetic-admin-key");
            Eleven=(ProviderView)Get(Window,"elevenView"); OpenAi=(ProviderView)Get(Window,"openAiView");
            Render();
            Check(!Member.HasPro && ElevenHttp.Calls==0 && OpenAiHttp.Calls==0,"Fixture starts accountless with saved keys and no provider traffic");
        }
        internal Task Refresh(bool manual=false) { return (Task)Call(Window,"RefreshDueAsync",manual); }
        internal void Request() { Call(Window,"RequestAutomaticRefresh"); }
        internal void Render() { Call(Window,"Render",Eleven??(ProviderView)Get(Window,"elevenView"),false); Call(Window,"RenderOpenAi"); Call(Window,"UpdateServiceVisibility"); Call(Window,"RefreshCompactBar"); }
        internal void Idle() { Until(()=>!(bool)Get(Window,"refreshing") && !(bool)Get(Window,"automaticRefreshQueued"),"The queued automatic refresh drains"); Pump(); }
        public void Dispose()
        {
            Set(Window,"demo",true); ((DispatcherTimer)Get(Window,"timer")).Stop(); Window.Close();
            ((Forms.NotifyIcon)Get(Window,"tray")).Dispose(); Member.Dispose();
            elevenClientField.SetValue(null,previousElevenClient); elevenClient.Dispose(); openAiClient.Dispose(); Pump();
        }
    }
    private static void CheckAccountlessStartup(string root)
    {
        using (var f=new Fixture(root)) {
            Await(f.Refresh()); f.Idle();
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1,"Initial accountless startup reads every configured provider exactly once");
            Check(Math.Abs(f.Eleven.Snapshot.Rows[0].RemainingPercent.Value-92.8)<0.000001,"The first provider response supplies the actual remaining percentage");
        }
        using (var f=new Fixture(root)) {
            Set(f.Window,"secret",""); f.Render();
            f.OpenAiHttp.Block=true; Task initial=f.Refresh();
            Until(()=>f.OpenAiHttp.Calls==1,"The initial connected provider starts");
            Check(f.ElevenHttp.Calls==0,"A provider without a key cannot start a background API read");
            Set(f.Window,"secret","synthetic-eleven-key"); f.Render(); f.Request(); f.Request(); Pump();
            Check((bool)Get(f.Window,"automaticRefreshPending") && f.ElevenHttp.Calls==0,"Adding a connection during an active read preserves a pending request without overlap");
            f.ElevenHttp.Block=true; f.OpenAiHttp.Release(); Await(initial);
            Until(()=>f.ElevenHttp.Calls==1,"Newly connected provider starts immediately after the current read without waiting for the timer");
            Check(f.OpenAiHttp.Calls==1,"Automatic follow-up does not reread a recently successful provider");
            f.Request(); f.Request(); Pump();
            Check(f.ElevenHttp.Calls==1 && f.ElevenHttp.MaximumActive==1,"Repeated refresh requests cannot overlap an in-flight provider read");
            f.ElevenHttp.Release(); f.Idle();
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1 && !(bool)Get(f.Window,"automaticRefreshPending"),"Requests coalesce and the final automatic pass honors both successful schedules");
        }
        using (var f=new Fixture(root)) {
            f.Request(); f.Request(); f.Idle();
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1,"Queued startup refresh needs no separate membership confirmation");
        }
    }
    private static void CheckGuards(string root)
    {
        using (var f=new Fixture(root)) {
            f.Request(); f.Request(); f.Idle();
            Check(f.OpenAiHttp.Calls==1 && f.ElevenHttp.Calls==1,"Queued accountless refresh reads all configured services");
            Set(f.Member,"verifiedPro",true); Call(f.Window,"ApplyMembershipState");
            Set(f.Member,"verifiedPro",false); Call(f.Window,"ApplyMembershipState"); f.Request(); f.Idle();
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1,"Legacy entitlement changes neither suppress services nor force duplicate reads");
        }
        using (var f=new Fixture(root)) {
            Set(f.Window,"claudeConnectionBusy",true); f.Request(); Pump(); f.Idle();
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1,"Claude connection work does not block unrelated providers from refreshing");
            Check((bool)Get(f.Window,"claudeConnectionBusy") && !(bool)Get(f.Window,"automaticRefreshPending"),"Unrelated reads do not clear Claude's pending state or retain a needless global refresh");
            Await(f.Refresh());
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1,"A repeat automatic pass honors successful schedules while Claude is busy");
            Set(f.Window,"claudeConnectionBusy",false);
        }
        using (var f=new Fixture(root,false)) {
            f.Request(); Pump();
            Check(f.ElevenHttp.Calls==0 && (bool)Get(f.Window,"automaticRefreshPending"),"An unloaded window cannot launch background provider work");
        }
        using (var f=new Fixture(root)) {
            Set(f.Window,"demo",true); f.Request(); Pump();
            Check(f.ElevenHttp.Calls==0 && f.OpenAiHttp.Calls==0,"Demo mode never schedules real provider work");
            Set(f.Window,"demo",false); f.Request(); Set(f.Window,"exiting",true); Pump();
            Check(f.ElevenHttp.Calls==0 && f.OpenAiHttp.Calls==0,"An exit between queuing and execution prevents provider calls");
        }
    }
    private static void CheckBackoff(string root)
    {
        foreach (HttpStatusCode status in new[] { (HttpStatusCode)429,HttpStatusCode.ServiceUnavailable }) {
            using (var f=new Fixture(root)) {
                f.ElevenHttp.Status=status; f.Request(); f.Idle();
                Check(f.ElevenHttp.Calls==1 && f.Eleven.RefreshSchedule.Failures==1 && f.Eleven.Error!=null,"Failed first response records real provider failure and schedule");
                DateTimeOffset deadline=f.Eleven.RefreshSchedule.NextAttempt;
                if ((int)status==429) Check(f.Eleven.RefreshSchedule.ServerNotBefore>DateTimeOffset.Now.AddSeconds(90),"First-response Retry-After remains enforced");
                Set(f.Member,"verifiedPro",false); Call(f.Window,"ApplyMembershipState"); f.Request(); f.Idle();
                Check(f.ElevenHttp.Calls==1 && f.Eleven.RefreshSchedule.NextAttempt==deadline,"Accountless automatic follow-ups cannot bypass "+(int)status+" backoff");
                Check(f.OpenAiHttp.Calls==1,"A failing provider does not cause a healthy provider to be queried again");
            }
        }
    }
    private static void CheckLateReadBoundaries(string root)
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.OK,HttpStatusCode.ServiceUnavailable }) {
            using (var f=new Fixture(root,false)) {
                f.OpenAiHttp.Block=true; f.OpenAiHttp.Status=status;
                Task reading=f.Refresh(); Until(()=>f.OpenAiHttp.Calls==1,"A connected accountless provider starts a delayed fixture read");
                ((ServicePlan)Get(f.Window,"servicePlan")).Select("codex");
                f.OpenAiHttp.Release(); Await(reading);
                Check(status==HttpStatusCode.OK ? f.OpenAi.Snapshot!=null && f.OpenAi.Error==null : f.OpenAi.Snapshot==null && f.OpenAi.Error!=null,
                    "A legacy single-service selection cannot discard the current provider's "+(int)status+" result");
                Check(!f.OpenAi.Busy && !(bool)Get(f.Window,"refreshing"),"Successful or failed accountless completion releases read state");
            }
            using (var f=new Fixture(root,false)) {
                f.ElevenHttp.Block=true; f.ElevenHttp.Status=status;
                Task reading=f.Refresh(); Until(()=>f.ElevenHttp.Calls==1,"A provider starts a delayed fixture read under its local lifecycle");
                Set(f.Window,"localReadingEpoch",Guid.NewGuid().ToString("N"));
                f.ElevenHttp.Release(); Await(reading);
                Check(f.Eleven.Snapshot==null && f.Eleven.Error==null && f.Eleven.RefreshSchedule.Failures==0,"A late "+(int)status+" response from a previous local lifecycle cannot update current state");
                Check(!((UsageJournal)Get(f.Window,"usageJournal")).GetHistory().Any(point=>point.ServiceId=="eleven"),"A stale lifecycle response adds no history");
            }
        }
        using (var f=new Fixture(root,false)) {
            f.ElevenHttp.Block=true;
            Task reading=f.Refresh(); Until(()=>f.ElevenHttp.Calls==1,"Legacy entitlement fixture has an outstanding accountless read");
            Set(f.Member,"userId","replacement-legacy-user"); Set(f.Member,"verifiedPro",false); Call(f.Window,"ApplyMembershipState");
            f.ElevenHttp.Release(); Await(reading);
            Check(f.Eleven.Snapshot!=null && f.Eleven.Error==null && !f.Member.HasPro,"Legacy account expiry cannot discard a valid provider response");
        }
        using (var f=new Fixture(root,false)) {
            f.OpenAiHttp.Block=true;
            Task reading=f.Refresh(); Until(()=>f.OpenAiHttp.Calls==1,"The exit fixture starts a delayed read");
            Set(f.Window,"exiting",true); f.OpenAiHttp.Release(); Await(reading);
            Check(f.OpenAi.Snapshot==null && f.OpenAi.Error==null,"A read completing after app exit cannot publish a late snapshot or error");
        }
    }
    private static void CheckQuietAutomaticRefresh(string root)
    {
        using (var f = new Fixture(root,false)) {
            f.ElevenHttp.Block = f.OpenAiHttp.Block = true;
            Task initial = f.Refresh();
            Until(() => f.ElevenHttp.Calls == 1 && f.OpenAiHttp.Calls == 1,"Both initial provider reads are pending");
            Check(Convert.ToString(((ToolTip)Get(f.Window,"refreshStatusTooltip")).Content).Contains("사용량 갱신 중…")
                && AutomationProperties.GetHelpText((Button)Get(f.Window,"compactRefreshButton")).Contains("사용량 갱신 중…"),
                "First-read feedback is stored when readers start, before a separate UI render");
            f.Render();
            foreach (var view in new[] { f.Eleven,f.OpenAi }) CheckState(f,view,"갱신 중","사용량을 조회하고 있습니다.",false);
            f.ElevenHttp.Release(); f.OpenAiHttp.Release(); Await(initial);
            var oldEleven = f.Eleven.Snapshot; var oldOpenAi = f.OpenAi.Snapshot;
            string[] oldValues = new[] { CompactUsageSummary.From(f.Eleven,DateTimeOffset.Now).Value,CompactUsageSummary.From(f.OpenAi,DateTimeOffset.Now).Value };
            foreach (var view in new[] { f.Eleven,f.OpenAi }) view.RefreshSchedule.Succeeded(DateTimeOffset.Now.AddMinutes(-2),60);
            f.ElevenHttp.Block = f.OpenAiHttp.Block = true;
            Task automatic = f.Refresh();
            Until(() => f.ElevenHttp.Calls == 2 && f.OpenAiHttp.Calls == 2,"The scheduled refresh reaches both delayed providers");
            foreach (string language in new[] { "ko","en" }) {
                L.Language = language; f.Render();
                int index = 0;
                foreach (var view in new[] { f.Eleven,f.OpenAi }) {
                    var compact = CompactUsageSummary.From(view,DateTimeOffset.Now);
                    Check(view.Busy && view.BackgroundRefresh && !UsagePresentation.ShowBusy(view),"Automatic presentation does not clear the actual in-flight state");
                    Check(view.Badge.Text != L.Text("갱신 중") && compact.StateLabel != "갱신 중" && compact.Value == oldValues[index++],"Cached normal and minibar values stay visible without an updating label");
                    Check(!TextOf((DependencyObject)Call(f.Window,"DashboardProviderStatus",view)).Contains(L.Text("갱신 중"))
                        && !TextOf((DependencyObject)Call(f.Window,"BuildDashboardOverviewCard",view)).Contains(L.Text("갱신 중")),"Dashboard overview and details stay quiet during cached automatic reads");
                }
                Check(!((string)Call(f.Window,"StatusTooltip")).Contains(L.Text("사용량 갱신 중…"))
                    && !AutomationProperties.GetName((Button)Get(f.Window,"compactRefreshButton")).Contains(L.Text("사용량 갱신 중…")),"Automatic tooltip and compact accessible name do not announce updating");
            }
            L.Language = "ko";
            Check(Object.ReferenceEquals(oldEleven,f.Eleven.Snapshot) && Object.ReferenceEquals(oldOpenAi,f.OpenAi.Snapshot),"Waiting never removes or fabricates an existing response");
            Check(!((Button)Get(f.Window,"refreshButton")).IsEnabled,"Concurrent refresh requests remain disabled");
            f.ElevenHttp.Release(); f.OpenAiHttp.Release(); Await(automatic);
            Check(!f.Eleven.Busy && !f.OpenAi.Busy && !f.Eleven.BackgroundRefresh && !f.OpenAi.BackgroundRefresh,"Completion clears provider loading and presentation flags");

            f.ElevenHttp.Block = f.OpenAiHttp.Block = true;
            Task manual = f.Refresh(true);
            Until(() => f.ElevenHttp.Calls == 3 && f.OpenAiHttp.Calls == 3,"Manual refresh starts immediately without changing the automatic interval"); f.Render();
            foreach (var view in new[] { f.Eleven,f.OpenAi }) Check(UsagePresentation.ShowBusy(view) && view.Badge.Text == "갱신 중","A direct user refresh keeps its progress feedback");
            Check(((string)Call(f.Window,"StatusTooltip")).Contains("사용량 갱신 중…"),"Manual refresh keeps tooltip feedback");
            f.ElevenHttp.Release(); f.OpenAiHttp.Release(); Await(manual);

            foreach (var view in new[] { f.Eleven,f.OpenAi }) view.RefreshSchedule.Succeeded(DateTimeOffset.Now.AddMinutes(-2),60);
            f.ElevenHttp.Status = f.OpenAiHttp.Status = HttpStatusCode.ServiceUnavailable;
            Await(f.Refresh()); f.Render();
            foreach (var view in new[] { f.Eleven,f.OpenAi }) Check(view.Snapshot != null && !String.IsNullOrEmpty(view.Error)
                && UsagePresentation.Describe(view,DateTimeOffset.Now).Warning && view.Badge.Visibility == Visibility.Visible,
                "A failed silent refresh retains values and exposes the error instead of hiding it");
        }
    }

    private static void CheckRefreshRecovery(string root)
    {
        using (var f=new Fixture(root)) {
            var button=(Button)Get(f.Window,"refreshButton");
            Set(f.Window,"refreshButton",null);
            Task failed=f.Refresh(true);
            Check(failed.IsFaulted && failed.Exception!=null,"A UI failure while starting a refresh surfaces as a faulted task");
            Check(!(bool)Get(f.Window,"refreshing") && !(bool)Get(f.Window,"automaticRefreshInProgress"),"A UI failure while starting a refresh releases the refresh guard");
            Set(f.Window,"refreshButton",button);
            Await(f.Refresh(true)); f.Idle();
            Check(f.ElevenHttp.Calls==1 && f.OpenAiHttp.Calls==1 && button.IsEnabled,"The next refresh reads providers and re-enables the button");
        }
    }
    private static void GeometryMessage(WidgetWindow window,int message)
    {
        typeof(WidgetWindow).GetMethod("WidgetGeometryMessage",Flags).Invoke(window,new object[] { IntPtr.Zero,message,IntPtr.Zero,IntPtr.Zero,false });
    }
    private static void CheckShutdownPersistence(string root)
    {
        using (var f=new Fixture(root)) {
            GeometryMessage(f.Window,0x0231);
            f.Window.Left-=37; f.Window.Top+=23;
            GeometryMessage(f.Window,0x0232);
            var saved=SettingsStore.Load();
            Check(saved.Left==f.Window.Left && saved.Top==f.Window.Top,"A finished drag saves the widget position without waiting for exit");
            f.Window.Left-=11;
            Call(f.Window,"SaveForSessionEnd");
            Check(SettingsStore.Load().Left==f.Window.Left,"Logoff or shutdown saves the current position synchronously");
            Check((bool)Get(f.Window,"shutdownFlushed") && (bool)Get(f.Window,"exiting"),"Session end lets the forced close skip its asynchronous flush");
        }
    }

    private static string TextOf(DependencyObject node)
    {
        var text=node as TextBlock; if(text!=null) return text.Text;
        return String.Join("\n",LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().Select(TextOf));
    }
    private static void CheckState(Fixture f,ProviderView view,string label,string message,bool warning)
    {
        var state=UsagePresentation.Describe(view,DateTimeOffset.Now);
        var compact=CompactUsageSummary.From(view,DateTimeOffset.Now);
        var dashboard=DashboardAssessment.Assess(view,new UsageTrackingOptions(),DateTimeOffset.Now);
        Check(state.Label==label && state.Message==message && state.Warning==warning,"Shared state distinguishes "+label);
        Check(view.Badge.Text==L.Text(label) && TextOf(view.Body).Contains(L.Text(message)),"The normal widget shows the correct "+label+" message: "+view.Id+" / "+L.Language+" / "+view.Badge.Text+" / "+TextOf(view.Body));
        Check(compact.StateLabel==label && compact.StateMessage==message && compact.Value=="—" && compact.Warning==warning,"Minibar has the same state and never fabricates zero usage");
        Check(dashboard.StatusLabel==label && dashboard.UsableCount==0 && dashboard.HasUnknown,"Dashboard preserves the waiting/error status without inventing a measurement");
        string dashboardText=TextOf((DependencyObject)Call(f.Window,"DashboardProviderStatus",view));
        Check(dashboardText.Contains(L.Text(label)) && dashboardText.Contains(L.Text(message)),"The rendered dashboard agrees with the normal widget");
        Call(f.Window,"RefreshCompactBar");
        var cells=(IDictionary)Get(f.Window,"compactCells"); var cell=cells[view.Id];
        Check(AutomationProperties.GetHelpText((Button)Get(cell,"Button")).Contains(L.Text(message)),"Rendered minibar accessibility explains "+label);
    }
    private static void CheckPresentation(string root)
    {
        using (var f=new Fixture(root)) {
            Set(f.Window,"claudeConnectionBusy",true); f.Render();
            foreach (string language in new[] { "ko","en" }) {
                L.Language=language;
                foreach (var view in new[] { f.Eleven,f.OpenAi }) {
                    view.Busy=false; view.Error=null; view.Issue=null; f.Render();
                    CheckState(f,view,"조회 대기","첫 사용량 조회를 기다리고 있습니다.",false);
                    Check(!TextOf(view.Body).Contains(L.Text("설정에서 API 키를 연결해 주세요.")),"Saved keys never display a request to re-enter them");
                    view.Busy=true; f.Render(); CheckState(f,view,"갱신 중","사용량을 조회하고 있습니다.",false);
                    view.Busy=false; view.Error="연결 실패"; f.Render();
                    CheckState(f,view,"연결 확인","연결 실패",true);
                    view.Error=null;
                }
                Set(f.Window,"secret",""); f.Render();
                Check(!f.Eleven.AwaitingInitialRead && f.Eleven.Badge.Text==L.Text("연결 전") && TextOf(f.Eleven.Body).Contains(L.Text("설정에서 API 키를 연결해 주세요.")),"An actually missing key still receives setup guidance in "+language);
                Set(f.Window,"secret","synthetic-eleven-key");
            }
            L.Language="en";
            Check(L.Text("조회 대기")!="조회 대기" && L.Text("첫 사용량 조회를 기다리고 있습니다.")!="첫 사용량 조회를 기다리고 있습니다." && L.Text("사용량을 조회하고 있습니다.")!="사용량을 조회하고 있습니다.","All new startup messages have English translations");
            L.Language="ko";
        }
    }
    [STAThread]
    public static int Main()
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root=Path.Combine(parent,"startup-refresh-fixture-"+Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(root); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            CheckAccountlessStartup(root); CheckGuards(root); CheckBackoff(root); CheckLateReadBoundaries(root); CheckPresentation(root); CheckQuietAutomaticRefresh(root); CheckRefreshRecovery(root); CheckShutdownPersistence(root);
            Console.WriteLine("Startup refresh tests passed: "+checks+" assertions. Synthetic responses only; isolated settings; no polling-delay waits.");
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null; L.Language="ko";
            string full=Path.GetFullPath(root);
            if (full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("startup-refresh-fixture-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
}
