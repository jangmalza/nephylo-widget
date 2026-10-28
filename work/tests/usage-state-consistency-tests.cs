using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

internal static class UsageStateConsistencyTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026,10,13,12,0,0,TimeSpan.FromHours(9));
    private static void Check(bool condition,string message) { if (!condition) throw new Exception(message); checks++; }
    private static object Get(object target,string name) { return target.GetType().GetField(name,Flags).GetValue(target); }
    private static void Set(object target,string name,object value) { target.GetType().GetField(name,Flags).SetValue(target,value); }
    private static object Call(object target,string method,params object[] args)
    { return target.GetType().GetMethod(method,Flags,null,args.Select(arg=>arg.GetType()).ToArray(),null).Invoke(target,args); }
    private static ProviderView View(string id,string source)
    {
        return new ProviderView { Id=id,Source=source,LastSuccess=Now,ReceiptState=ProviderReceiptState.Received,
            Snapshot=new UsageSnapshot { Plan="PRO",Rows=new List<UsageRow> { new UsageRow { Label="주간 한도",RemainingPercent=10,ResetsAt=Now.AddDays(1) } } } };
    }
    private static void CheckReading(ProviderView view,bool stale,string label,string scenario)
    {
        var state=UsagePresentation.Describe(view,Now);
        var compact=CompactUsageSummary.From(view,Now);
        var dashboard=DashboardAssessment.Assess(view,new UsageTrackingOptions(),Now);
        Check(state.Stale==stale && state.Warning==stale && state.Label==label,scenario+": shared reading state");
        Check(compact.Warning==stale && compact.StateLabel==label && compact.Value=="10%",scenario+": minibar retains the last amount and its warning");
        Check(dashboard.Stale==stale && dashboard.HasUnknown==stale && dashboard.HasLow!=stale && dashboard.StatusLabel==label,
            scenario+": dashboard uses the same freshness boundary");
        Check(view.Snapshot.Rows[0].RemainingPercent==10 && view.Snapshot.Rows[0].ResetsAt==Now.AddDays(1),scenario+": presentation never invents fresh provider data");
    }
    private static void CheckBoundaries()
    {
        foreach (string source in new[] { "api","cli" }) {
            var view=View("codex",source);
            view.LastSuccess=Now.AddMinutes(-10); CheckReading(view,false,"PRO",source+" exactly ten minutes");
            view.LastSuccess=Now.AddMinutes(-10).AddTicks(-1); CheckReading(view,true,"이전 값",source+" beyond ten minutes");
            view.LastSuccess=Now.AddMinutes(-20); CheckReading(view,true,"이전 값",source+" twenty-minute review reproduction");
            view.LastSuccess=Now.AddTicks(1); CheckReading(view,true,"이전 값",source+" future observation");
            view.LastSuccess=null; CheckReading(view,false,"PRO",source+" undated legacy caller remains compatible");
            view.LastSuccess=Now; CheckReading(view,false,"PRO",source+" fresh response recovers");
        }
        foreach (string source in new[] { "code","code:win:fixture","code:wsl:fixture" }) {
            var view=View("claude",source); view.ReceiptObservedAt=Now.AddMinutes(-30);
            CheckReading(view,false,"CODE 연결",source+" exactly thirty minutes");
            view.ReceiptObservedAt=Now.AddMinutes(-30).AddTicks(-1); CheckReading(view,true,"이전 수신",source+" beyond thirty minutes");
            view.ReceiptObservedAt=Now.AddTicks(1); CheckReading(view,true,"이전 수신",source+" future receipt");
            view.ReceiptObservedAt=null; CheckReading(view,false,"CODE 연결",source+" last-success fallback");
            view.LastSuccess=null; CheckReading(view,true,"이전 수신",source+" no confirmed receipt time");
        }
        var manual=View("claude","manual"); manual.LastSuccess=Now.AddDays(-10);
        CheckReading(manual,false,"수동","Manual readings are not automatic freshness estimates");
        manual.Snapshot.Rows[0].ResetsAt=Now.AddSeconds(-1);
        Check(UsagePresentation.Describe(manual,Now).Label=="수동 · 기한 경과" && CompactUsageSummary.From(manual,Now).Warning,
            "Expired manual dates retain their specific warning");
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        { throw new InvalidOperationException("Usage state consistency tests forbid network."); }
    }
    [STAThread]
    public static int Main()
    {
        string parent=Path.GetFullPath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        string root=Path.Combine(parent,"usage-state-fixture-"+Guid.NewGuid().ToString("N"));
        WidgetWindow window=null; MembershipClient member=null;
        try {
            CheckBoundaries();
            SettingsStore.DataDirectoryOverride=root; ClaudeUsageService.ConfigDirectoryOverride=Path.Combine(root,"claude");
            member=new MembershipClient(root,false,MembershipConfiguration.FromValues("https://fixture.supabase.co","sb_publishable_fixture_only",true),new NoNetwork(),()=>0L);
            window=new WidgetWindow(true,member);
            ((DispatcherTimer)Get(window,"timer")).Stop(); ((Forms.NotifyIcon)Get(window,"tray")).Visible=false;
            Set(window,"demo",false);
            Set(member,"userId","fixture-user"); Set(member,"verifiedPro",true); Set(member,"entitlementDeadline",100000L); Set(member,"subscriptionDeadline",100000L);
            var settings=(WidgetSettings)Get(window,"settings"); settings.AnimationsEnabled=false; settings.CollapsedServices=new[] { "codex","claude" };
            var cards=(Dictionary<string,StackPanel>)Get(window,"serviceCards");
            foreach(var pair in cards) pair.Value.Visibility=new[] { "codex","claude" }.Contains(pair.Key)?Visibility.Visible:Visibility.Collapsed;
            Check(!window.IsVisible && !window.IsLoaded,"The integration fixture stays hidden without startup side effects");
            CheckRenderedAutomatic(window);
            CheckAttentionMarks(window);
            CheckClaudeReceiptFlow(window);
            Console.WriteLine("Usage state consistency tests passed: "+checks+" assertions. Hidden fixtures only; no network or user settings accessed.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if(window!=null) { Set(window,"demo",true); ((DispatcherTimer)Get(window,"timer")).Stop(); ((DispatcherTimer)Get(window,"noticeTimer")).Stop(); window.Close(); }
            if(member!=null) member.Dispose();
            SettingsStore.DataDirectoryOverride=null; ClaudeUsageService.ConfigDirectoryOverride=null; L.Language="ko";
            string full=Path.GetFullPath(root);
            if(full.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("usage-state-fixture-",StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
    private static void CheckRenderedAutomatic(WidgetWindow window)
    {
        var view=(ProviderView)Get(window,"codexView"); view.Source="cli"; view.Error=null; view.Issue=null; view.Busy=false;
        view.Snapshot=new UsageSnapshot { Plan="PRO",Rows=new List<UsageRow> { new UsageRow { Label="Codex · 주간",RemainingPercent=33,ResetsAt=DateTimeOffset.Now.AddDays(2) } } };
        view.LastSuccess=DateTimeOffset.Now.AddMinutes(-20);
        Call(window,"Render",view,true); Call(window,"RefreshCompactBar");
        Check(view.Badge.Visibility==Visibility.Visible && view.Badge.Text=="이전 값","A real normal-card render visibly marks a twenty-minute cached value");
        Check(view.Summary.Visibility==Visibility.Visible && view.Summary.Text.Contains("33%"),"Folding preserves the cached amount without replacing it by zero");
        var cells=(IDictionary)Get(window,"compactCells"); var cell=cells["codex"];
        Check(((TextBlock)Get(cell,"Warning")).Visibility==Visibility.Visible,"The rendered minibar shows its stale warning glyph");
        Check(AutomationProperties.GetName((Button)Get(cell,"Button")).Contains("이전 값")
            && AutomationProperties.GetHelpText((Button)Get(cell,"Button")).Contains("최근 조회값이 아닙니다"),"Minibar accessibility and details explain stale data");
        Check(DashboardAssessment.Assess(view,new UsageTrackingOptions(),DateTimeOffset.Now).StatusLabel=="이전 값", "Dashboard and both rendered widget modes agree");
        view.Busy=true; Call(window,"Render",view,true); Call(window,"RefreshCompactBar");
        Check(view.Badge.Text=="갱신 중" && ((TextBlock)Get(cell,"Warning")).Visibility==Visibility.Visible,"Refreshing does not clear the old-data warning before a new response arrives");
        view.Busy=false; view.LastSuccess=DateTimeOffset.Now; Call(window,"Render",view,true); Call(window,"RefreshCompactBar");
        Check(view.Badge.Visibility==Visibility.Collapsed && ((TextBlock)Get(cell,"Warning")).Visibility==Visibility.Collapsed,
            "A successful fresh response clears both actual UI warnings");
        Check(!DashboardAssessment.Assess(view,new UsageTrackingOptions(),DateTimeOffset.Now).HasUnknown,"The same fresh response restores dashboard usability");
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var item in Tree(child)) yield return item;
    }
    private static void CheckAttentionMarks(WidgetWindow window)
    {
        var view=(ProviderView)Get(window,"codexView"); view.Source="cli"; view.Error=null; view.Issue=null; view.Busy=false; view.LastSuccess=DateTimeOffset.Now;
        view.Snapshot=new UsageSnapshot { Plan="PRO",Rows=new List<UsageRow> { new UsageRow { Label="Codex · 주간",RemainingPercent=12,ResetsAt=DateTimeOffset.Now.AddDays(2) } } };
        Call(window,"Render",view,true); Call(window,"RefreshCompactBar");
        var rowMarks=Tree(view.Body).OfType<AttentionMark>().ToList();
        Check(rowMarks.Count==1 && rowMarks[0].Active && rowMarks[0].Text.Text=="잔량 부족" && !view.BadgeMark.Active,"A fresh low row is marked in the widget while the connection badge stays calm");
        var cell=((IDictionary)Get(window,"compactCells"))["codex"];
        Check(((TextBlock)Get(cell,"Warning")).Visibility==Visibility.Visible && ((Border)Get(cell,"WarningMark")).Visibility==Visibility.Visible
            && AutomationProperties.GetName((Button)Get(cell,"Button")).Contains("잔량 부족"),"The minibar marks a low balance and names it for screen readers");
        var card=(Button)Call(window,"BuildDashboardOverviewCard",view); var surface=(Border)card.Content;
        Check(surface.BorderThickness.Left==2 && Tree(card).OfType<AttentionMark>().Any(mark=>mark.Text.Text=="잔량 부족"),"The dashboard card shows the same low mark with a heavier border");

        view.Snapshot.Rows[0].RemainingPercent=72; view.Error="연결을 확인해 주세요.";
        Call(window,"Render",view,true); Call(window,"RefreshCompactBar");
        Check(view.BadgeMark.Active && view.Badge.Text=="이전 값" && !Tree(view.Body).OfType<AttentionMark>().Any(),"A failed read marks the card header, not a balance row");
        card=(Button)Call(window,"BuildDashboardOverviewCard",view); surface=(Border)card.Content;
        Check(surface.BorderThickness.Left==2 && Tree(card).OfType<TextBlock>().Any(text=>text.Text=="연결을 확인해 주세요."),"The dashboard card states why the read failed");

        view.Error=null; Call(window,"Render",view,true); Call(window,"RefreshCompactBar");
        card=(Button)Call(window,"BuildDashboardOverviewCard",view); surface=(Border)card.Content;
        Check(!view.BadgeMark.Active && !Tree(view.Body).OfType<AttentionMark>().Any() && ((TextBlock)Get(cell,"Warning")).Visibility==Visibility.Collapsed
            && surface.BorderThickness.Left==1 && !Tree(card).OfType<AttentionMark>().Any(),"A healthy fresh reading shows no attention mark anywhere");
    }
    private static void CheckClaudeReceiptFlow(WidgetWindow window)
    {
        var view=(ProviderView)Get(window,"claudeView"); var extras=(ExtraSettings)Get(window,"extras"); extras.ClaudeManual=new ManualUsageRecord();
        Set(window,"claudeConfigured",true); Call(window,"RenderClaude");
        Check(view.ReceiptState==ProviderReceiptState.Waiting && view.Summary.Text=="연결 대기","No receipt still displays connection waiting in the folded summary");
        var now=DateTimeOffset.Now; ClaudeUsageService.CaptureJson("{}",now); Call(window,"RenderClaude"); Call(window,"RefreshCompactBar");
        Check(view.Snapshot==null && view.Error==null && view.ReceiptState==ProviderReceiptState.QuotaUnavailable,"The real receipt flow reproduces received-without-quota rather than a connection failure");
        Check(view.Summary.Text=="수신됨 · 한도 미제공" && view.Summary.Visibility==Visibility.Visible && view.Badge.Text==view.Summary.Text,
            "The folded card and expanded badge agree that receipt succeeded without quota");
        Check(CompactUsageSummary.From(view,now).StateLabel==view.Summary.Text && DashboardAssessment.Assess(view,new UsageTrackingOptions(),now).StatusLabel==view.Summary.Text,
            "The minibar and dashboard share the received-without-quota state");
        ((WidgetSettings)Get(window,"settings")).CollapsedServices=new string[0]; Call(window,"UpdateProviderFold",view,false);
        Check(view.Summary.Visibility==Visibility.Collapsed && view.Body.Visibility==Visibility.Visible && view.Badge.Text=="수신됨 · 한도 미제공",
            "Expanding an unavailable quota preserves its truthful receipt state");
        extras.ClaudeManual=new ManualUsageRecord { Label="수동 기록",RemainingPercent=60,ObservedAt=now.AddHours(-4),ResetsAt=now.AddHours(2) };
        Call(window,"RenderClaude");
        Check(view.Source=="manual" && !UsagePresentation.Describe(view,now).Warning && view.Summary.Text.Contains("60%"),"Manual fallback replaces the receipt warning with the user's explicit record");
    }
}
