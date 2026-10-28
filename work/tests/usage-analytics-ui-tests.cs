using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageWidget;

internal static class UsageAnalyticsUiTests
{
    private static int count;
    private static string artifactDirectory;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message) { ++count; if (!value) throw new Exception(message); }
    private static T Field<T>(object item,string name) { return (T)item.GetType().GetField(name,Private).GetValue(item); }
    private static void Set(object item,string name,object value) { item.GetType().GetField(name,Private).SetValue(item,value); }
    private static object Call(object item,string name,params object[] values)
    { try { return item.GetType().GetMethod(name,Private).Invoke(item,values); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        yield return parent;
        foreach (object child in LogicalTreeHelper.GetChildren(parent)) {
            var dependency = child as DependencyObject; if (dependency == null) continue;
            foreach (var item in Walk(dependency)) yield return item;
        }
    }
    private static string Text(DependencyObject parent) { return String.Join("\n",Walk(parent).OfType<TextBlock>().Select(item => item.Text)); }
    private static T Named<T>(DependencyObject parent,string name) where T : DependencyObject
    { return Walk(parent).OfType<T>().Single(item => AutomationProperties.GetName(item) == L.Text(name)); }
    private static string Lines(UsageAnalysis analysis) { return String.Join("\n",WidgetWindow.AnalysisDisplayLines(analysis)); }
    private static string SupportLines(UsageAnalysis analysis) { return String.Join("\n",WidgetWindow.AnalysisSupportLines(analysis)); }
    private static void RejectThreshold(int low,int critical)
    { bool rejected = false; try { WidgetWindow.ValidateUsageAlertThresholds(low,critical); } catch (ArgumentException) { rejected = true; } Check(rejected,"Invalid threshold cannot be saved: " + low + "/" + critical); }

    [STAThread]
    public static int Main(string[] arguments)
    {
        if (arguments.Length > 0) { artifactDirectory = Path.GetFullPath(arguments[0]); Directory.CreateDirectory(artifactDirectory); }
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-analysis-ui-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\analysis-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow window = null;
        try {
            L.Language = "ko";
            CheckDisplayMeaning(); CheckThresholdValidation();
            window = new WidgetWindow(true);
            Field<DispatcherTimer>(window,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(window,"tray").Visible = false;
            Call(window,"ShowDemo");
            Check(!window.IsVisible && !window.IsLoaded,"Analytics tests use an unloaded, hidden preview without provider calls");
            CheckHistorySelection(window);
            CheckCompactSelection(window);
            CheckCurrentAnalysis(window);
            CheckMetricTitles(window);
            CheckInlineInsights(window);
            CheckSourcesAndGates(window);
            CheckSettings(window);
            Check(!Directory.Exists(isolated),"Preview analytics and settings never create account or history files");
            Console.WriteLine("Usage analytics UI tests passed: " + count + " assertions. Isolated demo data; no live accounts, network calls or visible windows.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (window != null) {
                Set(window,"demo",true);
                Field<DispatcherTimer>(window,"timer").Stop(); window.Close();
                Field<CodexService>(window,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(window,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null; ClaudeUsageService.ConfigDirectoryOverride = null;
            L.Language = "ko";
        }
    }

    private static void CheckDisplayMeaning()
    {
        var now = DateTimeOffset.Now;
        var forecast = new UsageAnalysis { Status = "분석 가능",Unit = "%p",AveragePerDay = 3.25,SampleDays = 3,ObservedHours = 25.5,
            ForecastSampleDays = 3,ForecastObservedHours = 24,CollectionHours = 80,RecommendedPerDay = 2,
            PredictedEmptyAt = now.AddDays(2),EarliestEmptyAt = now.AddDays(1.75),LatestEmptyAt = now.AddDays(2.25),ResetAt = now.AddDays(4),BeforeReset = true,RiskLevel = 2 };
        string text = Lines(forecast);
        Check(text.Contains("3.25 퍼센트포인트") && !text.Contains("3.25%"),"Percentage consumption is shown as percentage points, not a remaining percentage");
        Check(SupportLines(forecast).Contains("기록 3일") && SupportLines(forecast).Contains("25.5시간"),"Recorded days and actually observed hours stay distinct in secondary details");
        Check(text.Contains("소진 참고 범위") && text.Contains("초기화 예정") && text.Contains("범위 전체"),"A valid forecast presents its observed range and the reset deadline");
        Check(!text.Contains(forecast.PredictedEmptyAt.Value.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture)),"A model center never appears as a minute-precise depletion promise");
        Check(SupportLines(forecast).Contains("통계적 신뢰구간이 아닙니다") && SupportLines(forecast).Contains("사용 패턴"),"Reference ranges are explicitly distinguished from statistical confidence intervals");
        Check(text.IndexOf("소진 참고 범위",StringComparison.Ordinal) < text.IndexOf("하루 평균",StringComparison.Ordinal)
            && text.Contains("권장 소모량을 넘고"),"The risk and suggested consumption precede historical averages, with a clear comparison");
        forecast.BeforeReset = false; forecast.RiskLevel = 0;
        Check(Lines(forecast).Contains("초기화가 먼저") && !Lines(forecast).Contains("소진 참고 범위 ·"),"A reset-first result cannot display a misleading post-reset depletion date");
        forecast.RiskLevel = 1; forecast.BeforeReset = true;
        Check(Lines(forecast).Contains("범위 일부"),"A range partly before reset does not claim every scenario depletes before reset");
        forecast.LatestEmptyAt = null;
        Check(Lines(forecast).Contains("상한 미정"),"A zero-consumption lower bound never becomes an invented finite upper date");
        forecast.RiskLevel = 3; forecast.EarliestEmptyAt = now.AddHours(4); forecast.LatestEmptyAt = now.AddHours(8); forecast.TodaySpike = true;
        Check(Lines(forecast).Contains("24시간 안에") && Lines(forecast).Contains("오늘 관측된"),"Near-term depletion and observed same-day increases have explicit explanations");
        forecast.AveragePerDay = 1;
        Check(Lines(forecast).Contains("권장 소모량 이내"),"An average within the suggested pace does not produce an over-budget warning");
        forecast.TrendPerDay = 4;
        Check(Lines(forecast).Contains("현재 소모 추세 · 4 퍼센트포인트/일") && Lines(forecast).Contains("현재 소모 추세가 권장 소모량을 넘고")
            && !Lines(forecast).Contains("최근 평균은 권장 소모량 이내"),"A same-day increase uses the current trend for the allowance comparison instead of a reassuring old average");
        forecast.AveragePerDay = 3.25;
        foreach (string unit in new[] { "credits","USD" }) {
            forecast.Unit = unit;
            Check(Lines(forecast).Contains(unit == "credits" ? "3.25 크레딧" : "3.25 USD"),"Average keeps its original amount unit: " + unit);
        }
        text = Lines(new UsageAnalysis { Status = "분석 가능",Unit = "credits",AveragePerDay = 7160.25,SampleDays = 3,ObservedHours = 40 });
        Check(text.Contains("7,160.25 크레딧"),"Larger consumption values use grouping while preserving meaningful decimals");
        var messages = new Dictionary<string,string> {
            { "이력 기록 꺼짐","이력 기록을 켜면" }, { "데이터 수집 중","기록을 모으고" }, { "기록 부족","기록을 모으고" },
            { "수동 기록","수동 기록" }, { "짧은 한도","짧게 초기화" }, { "오래된 기록","최근 확인값이 오래" },
            { "지원하지 않는 항목","지원하지 않습니다" }, { "초기화 시각 없음","초기화 일정이 없어" }, { "소비 변화 없음","소비 변화가 없어" },
            { "초기화 확인 대기","새 잔량을 확인한 뒤" }
        };
        foreach (var status in messages) {
            text = Lines(new UsageAnalysis { Status = status.Key,Unit = "%p",SampleDays = 1,ObservedHours = 2 });
            Check(text.Contains(status.Value) && !text.Contains("예상 소진 ·"),"Unavailable forecasts explain their actual reason without fabricating a date: " + status.Key);
        }
        foreach (string status in new[] { "이력 기록 꺼짐","수동 기록","짧은 한도","지원하지 않는 항목" }) {
            text = String.Join("\n",WidgetWindow.AnalysisDisplayLines(new UsageAnalysis { Status = status },false));
            Check(text.Contains(messages[status]) && !text.Contains("새 잔량을 확인하면 예측"),
                "A provider error preserves the independent unavailable-forecast explanation: " + status);
        }
        text = Lines(new UsageAnalysis { Status = "수동 기록",AveragePerDay = 4,Unit = "credits",SampleDays = 3,ObservedHours = 26 });
        Check(text.Contains("하루 평균 소모량") && text.Contains("소진 시점은 예측하지"),"Manual changes may have an average without implying an automatic forecast");
        var collecting = new UsageAnalysis { Status = "기록 부족",Unit = "%p",RecommendedPerDay = 40,ResetAt = now.AddHours(6),
            ForecastSampleDays = 1,ForecastObservedHours = 4.5,SampleDays = 2,ObservedHours = 7,CollectionHours = 25 };
        Check(Lines(collecting).Contains("40 퍼센트포인트") && Lines(collecting).Contains("24시간 환산")
            && Lines(collecting).Contains("현재 잔량까지만"),"A six-hour allowance is labeled as a daily-equivalent pace, not permission to exceed the remaining balance");
        Check(!Lines(collecting).Contains("소진 참고 범위 ·") && SupportLines(collecting).Contains("1/3일")
            && SupportLines(collecting).Contains("4.5/12시간") && SupportLines(collecting).Contains("25시간 · 최소 72시간")
            && SupportLines(collecting).Contains("하루 4시간"),"Collecting states expose each actual observation requirement without fabricating a forecast");
        Check(SupportLines(collecting).Contains("관측 범위 제한적") && SupportLines(collecting).Contains("통계적 신뢰도나 예측 정확도가 아닙니다"),
            "Coverage is a factual observation label, not statistical confidence");
        L.Language = "en";
        text = Lines(forecast) + SupportLines(forecast) + Lines(collecting) + SupportLines(collecting);
        Check(!text.Any(c => c >= '\uAC00' && c <= '\uD7AF'),"English analysis translates labels, units and the estimate explanation: " + text);
        L.Language = "ko";
    }

    private static void CheckThresholdValidation()
    {
        WidgetWindow.ValidateUsageAlertThresholds(20,10); WidgetWindow.ValidateUsageAlertThresholds(20,0);
        WidgetWindow.ValidateUsageAlertThresholds(1,0); Check(true,"Default and disabled critical thresholds are valid");
        RejectThreshold(20,20); RejectThreshold(20,21); RejectThreshold(20,-1); RejectThreshold(0,0); RejectThreshold(100,10);
    }

    private static void CheckMetricTitles(WidgetWindow window)
    {
        string language = L.Language;
        var liveRow = Field<ProviderView>(window,"codexView").Snapshot.Rows[0];
        string originalLabel = liveRow.Label;
        new WindowInteropHelper(window).EnsureHandle(); Call(window,"BuildDashboardWindow");
        try {
            foreach (string locale in new[] { "ko","en" }) {
                L.Language = locale;
                string label = "Codex · 주간";
                var panel = (FrameworkElement)Call(window,"BuildUsageAnalysisPanel","codex",label,"api",DateTimeOffset.Now,false);
                string text = Text(panel);
                Check(!text.Contains("Codex · Codex") && text.Contains(WidgetWindow.UsageMetricTitle("codex",label,false)),
                    "Analysis displays an actual prefixed provider label once: " + locale);
                Check(label == "Codex · 주간","Formatting does not rewrite the stored metric identity");
                Check(WidgetWindow.UsageMetricTitle("codex","주간 한도",false) == "Codex · " + L.Text("주간 한도"),"Unprefixed example labels keep their service name");
                Check(WidgetWindow.UsageMetricTitle("codex","Codex custom",false) == "Codex · Codex custom","Similar words without a service separator are not stripped");
                Check(WidgetWindow.UsageMetricTitle("codex",label,true) == "Codex · " + label,"Manual user labels are preserved verbatim");
                liveRow.Label = label;
                Call(window,"SetDashboardHistoryService","codex"); Call(window,"NavigateDashboard","analysis");
                var cards = Field<System.Collections.IDictionary>(window,"dashboardAnalysisCards");
                var titles = cards.Values.Cast<object>().Select(card => Field<TextBlock>(card,"Identity").Text).ToList();
                Check(titles.Contains(L.Text(label)) && titles.All(title => !title.Contains("Codex · Codex")),
                    "The dedicated dashboard card renders the real provider prefix once: " + locale);
                Call(window,"RefreshDashboardHistoryIfOpen");
                Check(cards.Values.Cast<object>().All(card => !Field<TextBlock>(card,"Identity").Text.Contains("Codex · Codex")),
                    "Polling does not reintroduce duplicate dashboard titles: " + locale);
            }
        } finally { liveRow.Label = originalLabel; Call(window,"CloseDashboard"); L.Language = language; }
    }

    private static void CheckCurrentAnalysis(WidgetWindow window)
    {
        var now = DateTimeOffset.Now;
        var view = Field<ProviderView>(window,"elevenView");
        var original = view.Snapshot;
        var row = original.Rows.Single(item => item.Label == "기본 제공 크레딧");
        var journal = Field<UsageJournal>(window,"usageJournal");
        var analysis = journal.GetAnalysis("eleven",row.Label,"api",now);
        Check(analysis.AveragePerDay.HasValue && analysis.RecommendedPerDay.HasValue && analysis.EarliestEmptyAt.HasValue,
            "Failure regression starts with a mature real analyzer result");
        var compact = (FrameworkElement)Call(window,"BuildCompactAnalysis","eleven",now);
        var history = (FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,false);
        var disclosure = Walk(compact).OfType<Expander>().Single(); disclosure.IsExpanded = true;
        Check(Text(compact).Contains("하루 권장 소모량") && Text(compact).Contains("소진 참고 범위"),
            "Healthy minibar details still present the current recommendation and forecast");
        RenderAnalysisPanel(window,compact,"minibar-analysis-healthy.png");
        string error = view.Error, source = view.Source; var last = view.LastSuccess; var reset = row.ResetsAt;
        try {
            view.Error = "조회 실패";
            Call(window,"RefreshCompactBar");
            CheckHistoricalOnly(compact,"An already open minibar analysis after a failed refresh");
            RenderAnalysisPanel(window,compact,"minibar-analysis-error.png");
            CheckHistoricalOnly(history,"An already open legacy history analysis after a failed refresh");
            Check(Object.ReferenceEquals(disclosure,Walk(compact).OfType<Expander>().Single()) && disclosure.IsExpanded,
                "Polling preserves the existing disclosure control and its expanded state");
            var failed = (FrameworkElement)Call(window,"BuildCompactDetails","eleven",now);
            Check(Text(failed).Contains("조회 실패") && !Text(failed).Contains("하루 권장 소모량") && !Text(failed).Contains("소진 참고 범위"),
                "Newly opened minibar details expose the failure without retained forecast advice");
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,false),
                "The legacy history analysis after a failed refresh");
            var retained = journal.GetAnalysis("eleven",row.Label,"api",now);
            Check(retained.RecommendedPerDay == analysis.RecommendedPerDay && retained.EarliestEmptyAt == analysis.EarliestEmptyAt
                && Object.ReferenceEquals(original,view.Snapshot),"Display validation does not mutate the journal analysis or the retained provider snapshot");
            view.Error = error; view.LastSuccess = now;
            Call(window,"RefreshCompactBar");
            Check(Text(compact).Contains("하루 권장 소모량") && !Text(compact).Contains("최근 잔량 확인 필요") && disclosure.IsExpanded,
                "A recovered provider restores advice in the existing open panel");
            Check(Text(history).Contains("하루 권장 소모량") && !Text(history).Contains("최근 잔량 확인 필요"),
                "A recovered provider restores advice in the retained legacy history panel");
            view.LastSuccess = now.AddMinutes(-11);
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"An aged provider snapshot");
            view.LastSuccess = now.AddMinutes(1);
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"A future provider timestamp");
            view.LastSuccess = now; row.ResetsAt = now.AddMinutes(-1);
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"An expired reset window");
            row.ResetsAt = reset.Value.AddDays(1);
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"A changed reset window not yet observed by the journal");
            row.ResetsAt = reset; original.Rows.Add(new UsageRow { Label = row.Label,RemainingPercent = 10,ResetsAt = reset });
            try { CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"Duplicate metric labels"); }
            finally { original.Rows.RemoveAt(original.Rows.Count-1); }
            view.Snapshot = new UsageSnapshot { Rows = new List<UsageRow>() };
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"A removed quota row");
            view.Snapshot = original; view.Source = "manual";
            CheckHistoricalOnly((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true),"An automatic analysis after switching to manual input");
            view.Source = source; view.Error = "조회 실패"; L.Language = "en";
            string english = Text((FrameworkElement)Call(window,"BuildUsageAnalysisPanel","eleven",row.Label,"api",now,true));
            Check(!english.Any(c => c >= '\uAC00' && c <= '\uD7AF') && english.Contains(L.Text("최근 잔량 확인 필요")),
                "The withheld-forecast explanation and historical average are fully translated");
        }
        finally { L.Language = "ko"; view.Error = error; view.Source = source; view.LastSuccess = last; row.ResetsAt = reset; view.Snapshot = original; }

        var codex = (FrameworkElement)Call(window,"BuildCompactAnalysis","codex",DateTimeOffset.Now);
        var selector = Named<ComboBox>(codex,"분석 항목");
        selector.SelectedItem = selector.Items.Cast<object>().Single(choice => choice.ToString() == "5시간 한도");
        var selected = selector.SelectedItem;
        Call(window,"RefreshCompactBar");
        Check(Object.ReferenceEquals(selector,Named<ComboBox>(codex,"분석 항목")) && Object.ReferenceEquals(selected,selector.SelectedItem)
            && Text(codex).Contains("짧게 초기화"),"Polling preserves the compact selector control, metric and short-window explanation");
        selector.SelectedItem = selector.Items.Cast<object>().Single(choice => choice.ToString() == "주간 한도");
        selected = selector.SelectedItem;
        var codexView = Field<ProviderView>(window,"codexView"); string codexError = codexView.Error;
        try {
            codexView.Error = "조회 실패"; Call(window,"RefreshCompactBar");
            CheckHistoricalOnly(codex,"An already open multi-metric minibar analysis after failure");
            Check(Object.ReferenceEquals(selected,selector.SelectedItem),"A failed poll preserves the selected analysis metric");
        }
        finally { codexView.Error = codexError; }
        Call(window,"RefreshCompactBar");
        Check(Text(codex).Contains("하루 권장 소모량") && !Text(codex).Contains("최근 잔량 확인 필요"),
            "A recovered multi-metric analysis restores advice for the retained selection");
        var tracked = Field<List<WeakReference>>(window,"usageAnalysisPanels");
        compact.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent,compact));
        Check(!tracked.Any(reference => Object.ReferenceEquals(reference.Target,compact)),"Unloaded details leave no tracked panel entry");
        view.Error = "조회 실패";
        try {
            compact.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent,compact));
            compact.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent,compact));
            Check(tracked.Count(reference => Object.ReferenceEquals(reference.Target,compact)) == 1,
                "Reopening a retained context menu re-registers exactly one refresh entry");
            CheckHistoricalOnly(compact,"A reopened retained panel immediately re-checks current provider failure");
        }
        finally { view.Error = error; }
        Set(window,"demo",false);
        try {
            Call(window,"RefreshCompactBar");
            Check(compact.Visibility == Visibility.Visible && codex.Visibility == Visibility.Visible,
                "Accountless mode keeps single- and multi-metric analysis available");
        }
        finally { Set(window,"demo",true); }
        Call(window,"RefreshCompactBar");
        Check(compact.Visibility == Visibility.Visible && codex.Visibility == Visibility.Visible,
            "Restored demo access re-evaluates already retained analysis panels");
    }

    private static void CheckHistoricalOnly(FrameworkElement panel,string scenario)
    {
        string text = Text(panel);
        Check(text.Contains("최근 잔량 확인 필요") && text.Contains("평균만 표시") && text.Contains("하루 평균 소모량"),
            scenario + " explains why only the historical average is available");
        Check(!text.Contains("하루 권장 소모량") && !text.Contains("소진 참고 범위") && !text.Contains("초기화 예정")
            && !text.Contains("현재 소모 추세") && !text.Contains("권장량은 현재 잔량"),scenario + " suppresses all current-balance claims and their support text");
    }

    private static void RenderAnalysisPanel(WidgetWindow window,FrameworkElement panel,string name)
    {
        if (String.IsNullOrEmpty(artifactDirectory)) return;
        // Product-owned component rendering only: no desktop capture or visible window.
        var frame = new Border { Background = WidgetWindow.Back,Padding = new Thickness(16),Child = panel,
            Resources = window.Resources,Width = 342 };
        try {
            frame.Measure(new Size(342,Double.PositiveInfinity));
            frame.Arrange(new Rect(0,0,342,frame.DesiredSize.Height)); frame.UpdateLayout();
            var bitmap = new RenderTargetBitmap(342,(int)Math.Ceiling(frame.DesiredSize.Height),96,96,PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(artifactDirectory,name))) encoder.Save(stream);
        }
        finally { frame.Child = null; }
    }

    private static void CheckInlineInsights(WidgetWindow window)
    {
        var now = DateTimeOffset.Now;
        var view = Field<ProviderView>(window,"codexView");
        var row = view.Snapshot.Rows.Single(item => item.Label == "주간 한도");
        var shortRow = view.Snapshot.Rows.Single(item => item.Label == "5시간 한도");
        var journal = Field<UsageJournal>(window,"usageJournal");
        var button = (Button)Call(window,"BuildInlineUsageInsight",view,row,now,true);
        Check(button != null && Text(button).Contains("현 추세라면 초기화까지 여유"),"A mature weekly forecast gives the normal card a qualified reset-first explanation: " + (button == null ? "null" : Text(button))
            + "; id=" + view.Id + "; source=" + Call(window,"CurrentAnalysisSource",view.Id) + "; status=" + journal.GetAnalysis("codex",row.Label,"api",now).Status
            + "; reset=" + row.ResetsAt + "; analysis-reset=" + journal.GetAnalysis("codex",row.Label,"api",now).ResetAt);
        Check(button.Focusable && button.IsTabStop && AutomationProperties.GetName(button).Contains("주간 한도")
            && Convert.ToString(button.ToolTip).Contains("사용 패턴에 따라"),"Inline insights provide a keyboard-accessible exact-metric action and explain estimate uncertainty");
        Check(Call(window,"BuildInlineUsageInsight",view,shortRow,now,true) == null,"A short quota never inherits the adjacent weekly forecast");
        Check(Call(window,"BuildInlineUsageInsight",view,new UsageRow { Label = row.Label,RemainingPercent = 86,ResetsAt = row.ResetsAt },now,true) == null,
            "An old or unrelated row object cannot borrow a current forecast with the same label");
        view.Snapshot.Rows.Add(new UsageRow { Label = row.Label,RemainingPercent = 20,ResetsAt = row.ResetsAt });
        Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"Duplicate labels suppress an ambiguous per-row forecast");
        view.Snapshot.Rows.RemoveAt(view.Snapshot.Rows.Count-1);
        string source = view.Source, error = view.Error; var last = view.LastSuccess; var reset = row.ResetsAt; var percent = row.RemainingPercent;
        try {
            view.Source = "manual";
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"Manual records never get an automatic inline forecast");
            view.Source = source; view.Error = "offline";
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"A failed refresh cannot leave a reassuring forecast under the previous balance");
            view.Error = error; view.LastSuccess = now.AddMinutes(-11);
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"Old provider observations hide the inline forecast even if the journal is still recent");
            view.LastSuccess = now.AddMinutes(1);
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"Future observations never generate an inline forecast");
            view.LastSuccess = last; row.ResetsAt = now.AddMinutes(-1);
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"Expired quotas do not present a post-reset forecast");
            row.ResetsAt = reset.Value.AddDays(1);
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"An updated reset cycle cannot reuse the preceding cycle's result before observation");
            row.ResetsAt = reset; row.RemainingPercent = 0;
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"An empty balance keeps its actual quantity without an unnecessary forecast claim");
        }
        finally { view.Source = source; view.Error = error; view.LastSuccess = last; row.ResetsAt = reset; row.RemainingPercent = percent; }
        var options = journal.Options;
        try {
            var disabled = journal.Options; disabled.HistoryEnabled = false; journal.SaveOptions(disabled);
            Check(Call(window,"BuildInlineUsageInsight",view,row,now,true) == null,"History-disabled Pro accounts do not display old derived insights");
        }
        finally { journal.SaveOptions(options); journal.Observe("codex",view.Snapshot,now,"api",now); }
        Set(window,"demo",false);
        try {
            button = (Button)Call(window,"BuildInlineUsageInsight",view,row,now,true);
            Check(button != null,"Accountless mode exposes retained inline forecasts");
            Check(button.IsEnabled,"The retained insight action stays enabled without an account");
        }
        finally { Set(window,"demo",true); }
        Call(window,"ClearInlineUsageInsights");
        Check(button.Visibility == Visibility.Collapsed,"Clearing history or changing access can immediately hide an existing insight before the next refresh");

        var analysis = journal.GetAnalysis("codex",row.Label,"api",now);
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) != null,"The current long-period analyzer result is eligible for the one-line summary");
        analysis.RiskLevel = 1;
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) == "추세상 초기화 전 소진 가능","Partial observed risk uses possibility rather than a certain depletion claim");
        analysis.RiskLevel = 2;
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) == "추세상 초기화 전 소진 예상","Whole-range depletion stays framed as an estimate");
        analysis.RiskLevel = 3;
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) == "추세상 24시간 내 소진 예상","The highest risk communicates its time horizon without a precise-minute promise");
        L.Language = "en";
        Check(!L.Text(WidgetWindow.InlineUsageInsightText(analysis,now)).Any(c => c >= '\uAC00' && c <= '\uD7AF'),"Inline forecast messages translate in English");
        L.Language = "ko";
        foreach (string status in new[] { "데이터 수집 중","기록 부족","수동 기록","짧은 한도","오래된 기록","이력 기록 꺼짐","소비 변화 없음","초기화 시각 없음","지원하지 않는 항목" }) {
            analysis.Status = status;
            Check(WidgetWindow.InlineUsageInsightText(analysis,now) == null,"Unavailable observations omit the extra line instead of presenting an unjustified conclusion: " + status);
        }
        analysis.Status = "분석 가능"; analysis.ForecastSampleDays = 2;
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) == null,"The inline summary independently requires enough completed observation days");
        analysis.ForecastSampleDays = 3; analysis.ForecastObservedHours = 11;
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) == null,"The inline summary requires the actual observed-hour threshold");
        analysis.ForecastObservedHours = 12; analysis.CollectionHours = 71;
        Check(WidgetWindow.InlineUsageInsightText(analysis,now) == null,"The inline summary requires the elapsed collection period");
        CheckInlineHistoryTarget(window);
    }

    private static void CheckInlineHistoryTarget(WidgetWindow window)
    {
        var services = new ComboBox(); var metrics = new ComboBox();
        services.Items.Add(new ComboBoxItem { Tag = "eleven" }); services.Items.Add(new ComboBoxItem { Tag = "codex" });
        var metricType = typeof(WidgetWindow).GetNestedType("HistoryMetricChoice",BindingFlags.NonPublic);
        foreach (string pair in new[] { "eleven|주간 한도","codex|5시간 한도","codex|주간 한도" }) {
            var values = pair.Split('|'); var choice = Activator.CreateInstance(metricType,true);
            metricType.GetField("ServiceId").SetValue(choice,values[0]); metricType.GetField("Label").SetValue(choice,values[1]); metrics.Items.Add(choice);
        }
        Set(window,"historyService",services); Set(window,"historyMetric",metrics);
        try {
            Call(window,"SelectUsageHistoryMetric","codex","주간 한도");
            Check((string)((ComboBoxItem)services.SelectedItem).Tag == "codex" && Object.ReferenceEquals(metrics.SelectedItem,metrics.Items[2]),
                "The inline action targets the exact service and metric rather than another service's identically named quota");
        }
        finally { Set(window,"historyService",null); Set(window,"historyMetric",null); }
    }

    private static void CheckSourcesAndGates(WidgetWindow window)
    {
        var settings = Field<WidgetSettings>(window,"settings");
        settings.CodexWslDistribution = "Fixture-WSL";
        Check((string)Call(window,"CurrentAnalysisSource","codex") == UsageSourceIdentity.ForWsl("api","Fixture-WSL"),"Current Codex analysis uses the active installation identity");
        settings.ClaudeWslDistribution = "Fixture-WSL";
        Field<ProviderView>(window,"claudeView").Source = "code";
        Check((string)Call(window,"CurrentAnalysisSource","claude") == UsageSourceIdentity.ForWsl("code","Fixture-WSL"),"Current Claude analysis uses the active receipt installation");
        Field<ProviderView>(window,"claudeView").Source = "manual";
        Check((string)Call(window,"CurrentAnalysisSource","claude") == "manual","Manual Claude records cannot accidentally reuse automatic history");
        var now = DateTimeOffset.Now;
        var panel = (FrameworkElement)Call(window,"BuildUsageAnalysisPanel",null,null,null,now,false);
        Check(Text(panel).Contains("하나 선택"),"All-items history asks for a single metric instead of combining unrelated units");
        L.Language = "en";
        panel = (FrameworkElement)Call(window,"BuildUsageAnalysisPanel","claude","사용 이력","manual",now,false);
        Check(Text(panel).Contains("Claude · 사용 이력"),"English analytics preserves a user-entered manual metric label verbatim");
        L.Language = "ko";
        panel = (FrameworkElement)Call(window,"BuildCompactDetails","codex",now);
        Check(Text(panel).Contains("최근 7일 분석"),"Pro compact service details include the selected metric analysis");
        Set(window,"demo",false);
        try {
            Check(Call(window,"BuildUsageAnalysisPanel","codex","주간 한도","api",now,true) != null,"Accountless users can obtain analytics from retained history");
            Check(Call(window,"BuildCompactAnalysis","codex",now) != null,"Accountless users can obtain compact analytics");
            panel = (FrameworkElement)Call(window,"BuildCompactDetails","codex",now);
            Check(Text(panel).Contains("최근 7일 분석") && !Text(panel).Contains("업그레이드"),"Accountless service details show analysis without an upgrade advertisement");
        }
        finally { Set(window,"demo",true); }
    }

    private static void CheckCompactSelection(WidgetWindow window)
    {
        var view = Field<ProviderView>(window,"codexView");
        var now = DateTimeOffset.Now;
        var original = view.Snapshot;
        var journal = Field<UsageJournal>(window,"usageJournal"); int historyCount = journal.GetHistory().Count;
        var representative = CompactUsageSummary.From(view,now);
        var panel = (FrameworkElement)Call(window,"BuildCompactAnalysis","codex",now);
        var selector = Named<ComboBox>(panel,"분석 항목");
        Check(representative.Metric == "5시간" && selector.SelectedItem.ToString() == "주간 한도",
            "Codex keeps its five-hour minibar value while its analytics starts with the weekly quota");
        Check(selector.Items.Count == 2 && Text(panel).Contains("하루 평균 소모량"),"Both numeric quotas remain selectable and weekly demo data is immediately useful");
        Check(!Walk(panel).OfType<Expander>().Single().IsExpanded,"Ready observations keep calculation details secondary and collapsed");
        selector.SelectedItem = selector.Items.Cast<object>().Single(choice => choice.ToString() == "5시간 한도");
        Check(Text(panel).Contains("짧게 초기화") && !Text(panel).Contains("소진 참고 범위 ·"),"Switching to a short quota explains its limitation instead of recycling the weekly forecast");
        selector.SelectedItem = selector.Items.Cast<object>().Single(choice => choice.ToString() == "주간 한도");
        Check(Text(panel).Contains("하루 평균 소모량") && !Text(panel).Contains("짧게 초기화"),"Switching back replaces the short-quota explanation with the correct analysis");
        Check(Object.ReferenceEquals(view.Snapshot,original) && Object.ReferenceEquals(CompactUsageSummary.From(view,now).Row,representative.Row)
            && journal.GetHistory().Count == historyCount,"Analysis selection never changes the minibar representative, snapshots or collected history");
        var candidates = WidgetWindow.AnalysisCandidates("codex",new[] {
            new UsageRow { Label = "5시간 한도",RemainingPercent = 10 },new UsageRow { Label = "설명",DisplayValue = "연결됨" },
            new UsageRow { Label = "월간 한도",RemainingPercent = 90 },new UsageRow { Label = "주간 한도",RemainingPercent = 70 }
        },false);
        Check(candidates.Count == 3 && candidates[0].Label == "주간 한도" && candidates[1].Label == "월간 한도",
            "Long automatic quota windows precede short quotas and descriptive rows are excluded");
        candidates = WidgetWindow.AnalysisCandidates("openai",new[] {
            new UsageRow { Label = "이번 달 비용",DisplayValue = "$12",RemainingPercent = 50 },
            new UsageRow { Label = "설정 예산 잔여",Remaining = 38,Limit = 50,RemainingPercent = 76 }
        },false);
        Check(candidates.Count == 1 && candidates[0].Label == "설정 예산 잔여","Cost rows never enter the remaining-balance analysis selector");
        var claude = Field<ProviderView>(window,"claudeView"); var previous = claude.Snapshot; string previousSource = claude.Source;
        try {
            claude.Source = "manual";
            claude.Snapshot = new UsageSnapshot { Rows = new List<UsageRow> {
                new UsageRow { Label = "사용 이력",RemainingPercent = 20 },new UsageRow { Label = "주간 한도",RemainingPercent = 30 }
            } };
            L.Language = "en";
            panel = (FrameworkElement)Call(window,"BuildCompactAnalysis","claude",now);
            selector = Named<ComboBox>(panel,"분석 항목");
            Check(selector.SelectedItem.ToString() == "사용 이력" && selector.Items.Cast<object>().Last().ToString() == "주간 한도",
                "Manual labels remain verbatim and preserve their original order in English");
        }
        finally { L.Language = "ko"; claude.Snapshot = previous; claude.Source = previousSource; }
    }

    private static void CheckHistorySelection(WidgetWindow window)
    {
        var now = DateTimeOffset.Now;
        var panel = (FrameworkElement)Call(window,"BuildUsageAnalysisPanel","codex","주간 한도","api",now,false);
        Check(Text(panel).Contains("하루 평균 소모량") && Text(panel).Contains("초기화 예정"),
            "Seeded demo observations flow through the real analyzer into the history summary");
        Check(Text(panel).Contains("초기화가 먼저") && !Text(panel).Contains("예상 소진 ·"),
            "The demo's reset-first calculation renders no misleading post-reset depletion date");
        var metricType = typeof(WidgetWindow).GetNestedType("HistoryMetricChoice",BindingFlags.NonPublic);
        var metric = Activator.CreateInstance(metricType,true);
        metricType.GetField("ServiceId").SetValue(metric,"codex"); metricType.GetField("Label").SetValue(metric,"주간 한도");
        var host = new StackPanel(); Set(window,"historyAnalysis",host);
        var points = new List<UsageHistoryPoint> {
            new UsageHistoryPoint { ServiceId = "codex",Label = "주간 한도",Source = "api",ObservedAt = now.AddDays(-1) },
            new UsageHistoryPoint { ServiceId = "codex",Label = "주간 한도",Source = "manual",ObservedAt = now.AddMinutes(-3) },
            new UsageHistoryPoint { ServiceId = "claude",Label = "주간 한도",Source = "code",ObservedAt = now.AddMinutes(-2) },
            new UsageHistoryPoint { ServiceId = "codex",Label = "다른 항목",Source = "api",ObservedAt = now.AddMinutes(-1) },
            new UsageHistoryPoint { ServiceId = "codex",Label = "주간 한도",Source = "api",ObservedAt = now.AddDays(1) }
        };
        Call(window,"RefreshHistoryAnalysis",metric,points,now);
        Check(Text(host).Contains("수동 기록") && !Text(host).Contains("하루 평균 소모량"),
            "A metric's latest source is selected independently of other metrics and future timestamps, without reusing previous automatic analytics");
        Call(window,"RefreshHistoryAnalysis",null,points,now);
        Check(Text(host).Contains("하나 선택") && !Text(host).Contains("하루 평균 소모량"),
            "Returning to all metrics clears the previous metric summary");
        Set(window,"historyAnalysis",null);
    }

    private static void CheckSettings(WidgetWindow window)
    {
        var journal = Field<UsageJournal>(window,"usageJournal");
        journal.SaveOptions(new UsageTrackingOptions { LowEnabled = true,ResetEnabled = false,HistoryEnabled = true,
            ForecastEnabled = true,LowThresholdPercent = 17,CriticalThresholdPercent = 7,HiggsfieldCreditsThreshold = 13,HiggsfieldApiUsdThreshold = 2.5 });
        new WindowInteropHelper(window).EnsureHandle();
        var dialog = (Window)Call(window,"BuildNotificationSettingsDialog");
        try {
            Check(!dialog.IsLoaded && !dialog.IsVisible && !window.IsVisible,"Settings fixture creates no visible window");
            var low = Named<ComboBox>(dialog,"잔량 부족 알림 기준");
            var critical = Named<ComboBox>(dialog,"긴급 잔량 알림 기준");
            var history = Named<CheckBox>(dialog,"사용 이력 기록");
            var forecast = Named<CheckBox>(dialog,"소진 예상 알림");
            Check((int)((ComboBoxItem)low.SelectedItem).Tag == 17 && (int)((ComboBoxItem)critical.SelectedItem).Tag == 7,
                "Opening settings preserves custom low and critical percentages");
            low.SelectedItem = low.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == 10);
            Check(critical.Items.Cast<ComboBoxItem>().All(item => (int)item.Tag < 10) && (int)((ComboBoxItem)critical.SelectedItem).Tag == 7,
                "Changing the low threshold keeps valid selections and removes invalid critical choices");
            critical.SelectedItem = critical.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == 0);
            low.SelectedItem = low.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == 20);
            Check((int)((ComboBoxItem)critical.SelectedItem).Tag == 0,"A disabled critical stage stays disabled when another threshold changes");
            history.IsChecked = false;
            Check(!forecast.IsEnabled && forecast.IsChecked == true,"Turning off history disables forecasts without discarding their preference");
            history.IsChecked = true;
            Check(forecast.IsEnabled && forecast.IsChecked == true,"Turning history back on restores the retained forecast preference");
            history.IsChecked = false;
            var pending = Field<List<UsageNotice>>(window,"pendingUsageNotices");
            pending.Add(new UsageNotice { Kind = "forecast",ServiceId = "codex" });
            pending.Add(new UsageNotice { Kind = "critical",ServiceId = "codex" });
            pending.Add(new UsageNotice { Kind = "reset",ServiceId = "codex" });
            pending.Add(new UsageNotice { Kind = "low",ServiceId = "codex" });
            var save = Walk(dialog).OfType<Button>().Single(button => Convert.ToString(button.Content,CultureInfo.InvariantCulture) == L.Text("저장"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,save));
            var options = journal.Options;
            Check(options.LowThresholdPercent == 20 && options.CriticalThresholdPercent == 0 && options.LowEnabled,
                "Saving applies both percentage thresholds and the existing alert switch");
            Check(!options.HistoryEnabled && options.ForecastEnabled && !options.ResetEnabled,
                "Saving preserves a disabled forecast control's value and the independent reset preference");
            Check(options.HiggsfieldCreditsThreshold == 13 && options.HiggsfieldApiUsdThreshold == 2.5,
                "Adding forecast settings does not overwrite existing absolute thresholds");
            Check(pending.Count == 1 && pending[0].Kind == "low","Saving cancels stale forecasts and disabled alert stages while retaining an enabled low notice");
            pending.Add(new UsageNotice { Kind = "forecast",ServiceId = "codex" });
            Call(window,"ClearUsageHistory");
            Check(pending.All(notice => notice.Kind != "forecast") && journal.GetHistory().Count == 0,
                "Clearing usage history removes pending forecasts derived from the deleted data");
            Check(journal.Options.ForecastEnabled && !journal.Options.HistoryEnabled && journal.Options.LowThresholdPercent == 20,
                "Clearing usage history retains alert preferences");
        }
        finally { dialog.Close(); }
    }
}
