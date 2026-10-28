using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private StackPanel historyAnalysis;
        private readonly Dictionary<string,WeakReference> inlineUsageInsights = new Dictionary<string,WeakReference>();
        private readonly List<WeakReference> usageAnalysisPanels = new List<WeakReference>();

        private sealed class AnalysisMetricChoice
        {
            internal string Label;
            internal bool Manual;
            public override string ToString() { return Manual ? Label : L.Text(Label); }
        }

        internal static List<UsageRow> AnalysisCandidates(string serviceId, IEnumerable<UsageRow> rows, bool manual)
        {
            return (rows ?? Enumerable.Empty<UsageRow>()).Where(row => row != null && !String.IsNullOrWhiteSpace(row.Label)
                && UsageJournal.IsAnalysisCandidate(serviceId,row)).GroupBy(row => row.Label).Select(group => group.First())
                .OrderBy(row => manual ? 0 : AnalysisPeriodPriority(row.Label)).ToList();
        }

        private static int AnalysisPeriodPriority(string label)
        {
            string value = (label ?? "").ToLowerInvariant();
            if (value.Contains("주간") || value.Contains("weekly") || value.Contains("week") || value.Contains("7일")) return 0;
            if (value.Contains("월간") || value.Contains("월별") || value.Contains("monthly") || value.Contains("month") || value.Contains("30일")) return 1;
            return UsageJournal.IsShortAnalysisLimit(label) ? 3 : 2;
        }

        private FrameworkElement BuildCompactAnalysis(string serviceId, DateTimeOffset now)
        {
            if (!ProFeaturesAvailable || usageJournal == null) return null;
            var view = ViewFor(serviceId);
            string source = CurrentAnalysisSource(serviceId);
            var rows = AnalysisCandidates(serviceId,view.Snapshot == null ? null : view.Snapshot.Rows,source == "manual");
            if (rows.Count == 0) return null;
            if (rows.Count == 1) return BuildUsageAnalysisPanel(serviceId,rows[0].Label,source,now,true);
            var panel = new StackPanel();
            AddAnalysisText(panel,"최근 7일 분석",true,true);
            var selector = new LocalizedComboBox { FontSize = 14,MinHeight = 38,HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0,7,0,4),Style = (Style)Resources[typeof(ComboBox)],ItemContainerStyle = (Style)Resources[typeof(ComboBoxItem)] };
            AutomationProperties.SetName(selector,"분석 항목");
            foreach (var row in rows) selector.Items.Add(new AnalysisMetricChoice { Label = row.Label,Manual = source == "manual" });
            selector.SelectedIndex = 0; panel.Children.Add(selector);
            var content = new StackPanel(); panel.Children.Add(content);
            string displayedLabel = null, displayedSource = null;
            FrameworkElement displayed = null;
            Action<DateTimeOffset> update = at => {
                if (!ProFeaturesAvailable) { panel.Visibility = Visibility.Collapsed; return; }
                panel.Visibility = Visibility.Visible;
                var selected = selector.SelectedItem as AnalysisMetricChoice;
                if (selected == null) return;
                string currentSource = CurrentAnalysisSource(serviceId);
                if (displayed == null || displayedLabel != selected.Label || displayedSource != currentSource) {
                    displayed = BuildUsageAnalysisPanelCore(serviceId,selected.Label,currentSource,at,true,false,false);
                    displayedLabel = selected.Label; displayedSource = currentSource;
                    content.Children.Clear(); if (displayed != null) content.Children.Add(displayed);
                }
                else RefreshUsageAnalysisPanel(displayed,at);
            };
            selector.SelectionChanged += (s,e) => update(DateTimeOffset.Now); update(now);
            panel.Tag = update;
            return TrackUsageAnalysisPanel(panel);
        }

        private FrameworkElement TrackUsageAnalysisPanel(FrameworkElement panel)
        {
            if (panel == null) return null;
            var reference = new WeakReference(panel);
            usageAnalysisPanels.Add(reference);
            panel.Loaded += (s,e) => {
                if (!usageAnalysisPanels.Contains(reference)) usageAnalysisPanels.Add(reference);
                RefreshUsageAnalysisPanel(panel,DateTimeOffset.Now);
            };
            panel.Unloaded += (s,e) => usageAnalysisPanels.Remove(reference);
            return panel;
        }

        private static void RefreshUsageAnalysisPanel(FrameworkElement panel, DateTimeOffset now)
        {
            var refresh = panel == null ? null : panel.Tag as Action<DateTimeOffset>;
            if (refresh != null) refresh(now);
        }

        private void RefreshUsageAnalysisPanels(DateTimeOffset now)
        {
            foreach (var reference in usageAnalysisPanels.ToArray()) {
                var panel = reference.Target as FrameworkElement;
                if (panel == null) usageAnalysisPanels.Remove(reference);
                else RefreshUsageAnalysisPanel(panel,now);
            }
        }

        private string CurrentAnalysisSource(string id)
        {
            string source = UsageFreshness.Source(ViewFor(id));
            if (source == "manual") return source;
            if (id == "codex") return CodexUsageSource();
            if (id == "claude" && source == "code") return ClaudeUsageSource();
            return source;
        }

        private UsageAnalysis InlineUsageAnalysis(ProviderView view, UsageRow row, DateTimeOffset now)
        {
            if (!ProFeaturesAvailable || usageJournal == null || !usageJournal.Options.HistoryEnabled || view == null || row == null
                || String.IsNullOrEmpty(view.Id) || view.Snapshot == null || view.Snapshot.Rows == null || !view.Snapshot.Rows.Contains(row)
                || view.Snapshot.Rows.Count(item => item != null && item.Label == row.Label) != 1
                || !String.IsNullOrEmpty(view.Error) || !UsageJournal.IsAnalysisCandidate(view.Id,row)
                || UsageJournal.IsShortAnalysisLimit(row.Label) || !row.ResetsAt.HasValue || row.ResetsAt.Value <= now) return null;
            string source = CurrentAnalysisSource(view.Id);
            if (source == "manual") return null;
            var observed = UsageFreshness.Source(view) == "code" ? view.ReceiptObservedAt ?? view.LastSuccess : view.LastSuccess;
            if (!observed.HasValue || observed.Value > now || now-observed.Value > TimeSpan.FromMinutes(source == "code" || source.StartsWith("code:",StringComparison.Ordinal) ? 30 : 10)) return null;
            if (row.Remaining.HasValue && row.Remaining.Value <= 0 || row.RemainingPercent.HasValue && row.RemainingPercent.Value <= 0) return null;
            UsageAnalysis analysis;
            try { analysis = usageJournal.GetAnalysis(view.Id,row.Label,source,now); }
            catch { return null; }
            if (analysis.ResetAt != row.ResetsAt) return null;
            return String.IsNullOrEmpty(InlineUsageInsightText(analysis,now)) ? null : analysis;
        }

        // A row already marked low keeps its forecast as plain text, so one row never shows two marks.
        private FrameworkElement BuildInlineUsageInsight(ProviderView view, UsageRow row, DateTimeOffset now, bool emphasizeRisk)
        {
            var analysis = InlineUsageAnalysis(view,row,now);
            if (analysis == null) return null;
            string insight = InlineUsageInsightText(analysis,now);
            var text = T(insight,10,analysis.RiskLevel > 0 ? "#252827" : "#647070",FontWeights.Normal);
            text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
            // Expected depletion before reset (risk 2) or within 24 hours (risk 3) needs attention.
            object content = emphasizeRisk && analysis.RiskLevel >= 2 ? (object)new AttentionMark(text) : text;
            var button = new LocalizedButton { Content = content,Style = (Style)Resources["WidgetServiceToggle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,Margin = new Thickness(-1,3,-1,0),Padding = new Thickness(0,2,0,2),
                ToolTip = L.Text(insight) + "\n" + L.Text("관측한 사용 추세에 따른 참고값입니다. 사용 패턴에 따라 달라질 수 있습니다.") + "\n" + L.Text("잔량 분석 자세히 보기") };
            string label = row.Label;
            AutomationProperties.SetName(button,UsageMetricTitle(view.Id,label,UsageFreshness.Source(view) == "manual") + " · " + L.Text("잔량 분석 자세히 보기"));
            AutomationProperties.SetHelpText(button,L.Text(insight));
            button.Click += (s,e) => {
                // Re-check access and current observations when an old control is
                // activated after an account/source change or a collection pause.
                if (InlineUsageAnalysis(view,row,DateTimeOffset.Now) == null) return;
                OpenUsageHistory();
                SelectUsageHistoryMetric(view.Id,label);
            };
            inlineUsageInsights[view.Id + "\n" + label] = new WeakReference(button);
            return button;
        }

        private void ClearInlineUsageInsights()
        {
            foreach (var reference in inlineUsageInsights.Values) {
                var element = reference.Target as FrameworkElement;
                if (element != null) element.Visibility = Visibility.Collapsed;
            }
            inlineUsageInsights.Clear();
        }

        internal static string InlineUsageInsightText(UsageAnalysis analysis, DateTimeOffset now)
        {
            if (analysis == null || analysis.Status != "분석 가능" || analysis.Source == "manual" || UsageJournal.IsShortAnalysisLimit(analysis.Label)
                || analysis.ForecastSampleDays < analysis.RequiredSampleDays || analysis.ForecastObservedHours < analysis.RequiredObservedHours
                || analysis.CollectionHours < analysis.RequiredCollectionHours || !analysis.ResetAt.HasValue || analysis.ResetAt.Value <= now
                || !analysis.PredictedEmptyAt.HasValue || !analysis.EarliestEmptyAt.HasValue || !analysis.TrendPerDay.HasValue || analysis.TrendPerDay.Value <= 0) return null;
            if (analysis.RiskLevel == 3) return "추세상 24시간 내 소진 예상";
            if (analysis.RiskLevel == 2) return "추세상 초기화 전 소진 예상";
            if (analysis.RiskLevel == 1) return "추세상 초기화 전 소진 가능";
            return analysis.RiskLevel == 0 && analysis.EarliestEmptyAt.Value >= analysis.ResetAt.Value ? "현 추세라면 초기화까지 여유" : null;
        }

        private void SelectUsageHistoryMetric(string serviceId, string label)
        {
            if (!ProFeaturesAvailable || historyService == null || historyMetric == null) return;
            var service = historyService.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == serviceId);
            if (service == null) return;
            historyService.SelectedItem = service;
            var metric = historyMetric.Items.OfType<HistoryMetricChoice>().FirstOrDefault(item => item.ServiceId == serviceId && item.Label == label);
            if (metric != null) historyMetric.SelectedItem = metric;
            if (historyAnalysis != null) historyAnalysis.BringIntoView();
        }

        private FrameworkElement BuildUsageAnalysisPanel(string serviceId, string label, string source, DateTimeOffset now, bool compact)
        { return BuildUsageAnalysisPanelCore(serviceId,label,source,now,compact,true,true); }

        private FrameworkElement BuildUsageAnalysisPanelCore(string serviceId, string label, string source, DateTimeOffset now, bool compact, bool heading, bool identity)
        {
            if (!ProFeaturesAvailable || usageJournal == null) return null;
            var panel = new StackPanel();
            if (heading) AddAnalysisText(panel,"최근 7일 분석",true,compact);
            if (String.IsNullOrEmpty(serviceId) || String.IsNullOrEmpty(label)) {
                AddAnalysisText(panel,"서비스와 항목을 하나 선택하면 분석을 표시합니다.",false,compact);
                return panel;
            }
            if (identity) AddAnalysisText(panel,UsageMetricTitle(serviceId,label,source == "manual"),false,compact,source == "manual");
            var display = new StackPanel(); panel.Children.Add(display);
            var support = new StackPanel();
            var disclosure = new LocalizedExpander { Header = "관측·계산 안내",Content = support,FontSize = 13,
                Foreground = Muted,Margin = new Thickness(0,14,0,0) };
            panel.Children.Add(disclosure);
            bool initialized = false;
            Action<DateTimeOffset> update = at => {
                if (!ProFeaturesAvailable) { panel.Visibility = Visibility.Collapsed; return; }
                panel.Visibility = Visibility.Visible;
                UsageAnalysis analysis;
                try { analysis = usageJournal.GetAnalysis(serviceId,label,source,at); }
                catch {
                    UpdateAnalysisTexts(display,new[] { "분석을 불러오지 못했습니다. 잠시 뒤 다시 확인해 주세요." },compact,true);
                    disclosure.Visibility = Visibility.Collapsed;
                    return;
                }
                // History may still have a recent successful observation after a
                // failed poll. Current-balance advice must also match the live view.
                bool current = source == CurrentAnalysisSource(serviceId)
                    && DashboardAnalysisCurrent(ViewFor(serviceId),label,analysis,at);
                UpdateAnalysisTexts(display,AnalysisDisplayLines(analysis,current),compact,true);
                UpdateAnalysisTexts(support,new[] { L.Text(HistorySourceName(source)) }.Concat(AnalysisSupportLines(analysis,current)),compact);
                disclosure.Visibility = Visibility.Visible;
                if (!initialized) { disclosure.IsExpanded = AnalysisCollecting(analysis); initialized = true; }
            };
            panel.Tag = update; update(now);
            return heading ? TrackUsageAnalysisPanel(panel) : panel;
        }

        private static void UpdateAnalysisTexts(StackPanel panel,IEnumerable<string> lines,bool compact,bool emphasize = false)
        {
            int index = 0;
            foreach (string line in lines) {
                if (index == panel.Children.Count) AddAnalysisText(panel,line,false,compact);
                else ((TextBlock)panel.Children[index]).Text = L.Text(line);
                var text = (TextBlock)panel.Children[index];
                bool reading = emphasize && AnalysisReadingLine(line);
                text.FontSize = reading ? (compact ? 15 : 16) : emphasize ? 14 : 13;
                text.FontWeight = reading ? FontWeights.SemiBold : FontWeights.Normal;
                text.Foreground = reading ? Fore : Muted;
                text.Margin = new Thickness(0, reading ? 12 : 7, 0, 0);
                text.LineHeight = Math.Ceiling(text.FontSize * 1.45);
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                index++;
            }
            while (panel.Children.Count > index) panel.Children.RemoveAt(panel.Children.Count-1);
        }

        private static bool AnalysisReadingLine(string line)
        {
            // Emphasize only numeric readings, never promote explanatory copy
            // based on its position when live observations change.
            foreach (string format in new[] { "하루 평균 소모량 · {0} {1}", "하루 권장 소모량 · {0} {1} 이하 (24시간 환산)",
                "현재 소모 추세 · {0} {1}/일", "소진 참고 범위 · {0} 이후 (상한 미정)", "소진 참고 범위 · {0} 무렵", "소진 참고 범위 · {0} ~ {1}" }) {
                string prefix = L.Text(format).Split('{')[0];
                if (line.StartsWith(prefix,StringComparison.Ordinal)) return true;
            }
            return line.StartsWith(L.Text("초기화 예정") + " · ",StringComparison.Ordinal);
        }

        private static void AddAnalysisText(StackPanel panel, string text, bool heading, bool compact, bool raw = false)
        {
            if (compact) panel.Children.Add(CompactDetailText(raw ? text : L.Text(text),heading));
            else {
                var element = HistoryText(raw ? text : L.Text(text),heading ? 16 : 14,heading ? "#252827" : "#647070",
                    heading ? FontWeights.SemiBold : FontWeights.Normal,new Thickness(0,heading ? 0 : 8,0,0));
                if (raw) UiL.Raw(element);
                panel.Children.Add(element);
            }
        }

        internal static IEnumerable<string> AnalysisDisplayLines(UsageAnalysis analysis, bool current = true)
        {
            if (analysis == null) { yield return L.Text("데이터 수집 중"); yield break; }
            string unit = AnalysisUnit(analysis.Unit);
            if (!current) {
                yield return L.Text("최근 잔량 확인 필요");
                if (analysis.AveragePerDay.HasValue)
                    yield return String.Format(CultureInfo.CurrentCulture,L.Text("하루 평균 소모량 · {0} {1}"),AnalysisNumber(analysis.AveragePerDay.Value),unit);
                // A new poll cannot enable a disabled history feature or make
                // manual/short/unsupported metrics eligible for forecasting.
                bool unavailable = analysis.Status == "이력 기록 꺼짐" || analysis.Status == "수동 기록"
                    || analysis.Status == "짧은 한도" || analysis.Status == "지원하지 않는 항목";
                yield return L.Text(unavailable ? AnalysisStatusExplanation(analysis.Status)
                    : "마지막 확인 기록으로 평균만 표시합니다. 새 잔량을 확인하면 예측을 다시 표시합니다.");
                yield break;
            }
            if (analysis.EarliestEmptyAt.HasValue) {
                if (!analysis.ResetAt.HasValue || analysis.RiskLevel > 0) {
                    string earliest = AnalysisRangeDate(analysis.EarliestEmptyAt.Value);
                    if (!analysis.LatestEmptyAt.HasValue)
                        yield return String.Format(CultureInfo.CurrentCulture,L.Text("소진 참고 범위 · {0} 이후 (상한 미정)"),earliest);
                    else if (analysis.EarliestEmptyAt.Value.ToLocalTime().Date == analysis.LatestEmptyAt.Value.ToLocalTime().Date)
                        yield return String.Format(CultureInfo.CurrentCulture,L.Text("소진 참고 범위 · {0} 무렵"),earliest);
                    else yield return String.Format(CultureInfo.CurrentCulture,L.Text("소진 참고 범위 · {0} ~ {1}"),earliest,AnalysisRangeDate(analysis.LatestEmptyAt.Value));
                }
                if (analysis.ResetAt.HasValue) yield return L.Text(analysis.RiskLevel >= 3 ? "관측 범위에서는 24시간 안에 소진이 예상됩니다."
                    : analysis.RiskLevel == 2 ? "관측 범위 전체에서 초기화 전 소진이 예상됩니다."
                    : analysis.RiskLevel == 1 ? "관측 범위 일부에서 초기화 전 소진 가능성이 있습니다."
                    : "현재 추세에서는 소진 예상보다 초기화가 먼저입니다.");
            }
            if (analysis.RecommendedPerDay.HasValue) {
                yield return String.Format(CultureInfo.CurrentCulture,L.Text("하루 권장 소모량 · {0} {1} 이하 (24시간 환산)"),AnalysisNumber(analysis.RecommendedPerDay.Value),unit);
                if (AnalysisResetWithinDay(analysis)) yield return L.Text("초기화 전에는 현재 잔량까지만 사용할 수 있습니다.");
            }
            if (analysis.AveragePerDay.HasValue) {
                yield return String.Format(CultureInfo.CurrentCulture,L.Text("하루 평균 소모량 · {0} {1}"),
                    AnalysisNumber(analysis.AveragePerDay.Value),unit);
            }
            if (analysis.TrendPerDay.HasValue && (!analysis.AveragePerDay.HasValue || Math.Abs(analysis.TrendPerDay.Value-analysis.AveragePerDay.Value) >= 0.01))
                yield return String.Format(CultureInfo.CurrentCulture,L.Text("현재 소모 추세 · {0} {1}/일"),AnalysisNumber(analysis.TrendPerDay.Value),unit);
            if (analysis.RecommendedPerDay.HasValue) {
                if (analysis.TrendPerDay.HasValue) yield return L.Text(analysis.TrendPerDay.Value > analysis.RecommendedPerDay.Value
                    ? "현재 소모 추세가 권장 소모량을 넘고 있습니다." : "현재 소모 추세는 권장 소모량 이내입니다.");
                else if (analysis.AveragePerDay.HasValue) yield return L.Text(analysis.AveragePerDay.Value > analysis.RecommendedPerDay.Value
                    ? "최근 평균이 권장 소모량을 넘고 있습니다." : "최근 평균은 권장 소모량 이내입니다.");
            }
            if (analysis.TodaySpike) yield return L.Text("오늘 관측된 소모 증가를 참고 범위에 반영했습니다.");
            if (analysis.ResetAt.HasValue) yield return L.Text("초기화 예정") + " · " + AnalysisDate(analysis.ResetAt.Value);
            string explanation = AnalysisStatusExplanation(analysis.Status);
            if (!String.IsNullOrEmpty(explanation)) yield return L.Text(explanation);
        }

        internal static IEnumerable<string> AnalysisSupportLines(UsageAnalysis analysis, bool current = true)
        {
            if (analysis == null) yield break;
            yield return String.Format(CultureInfo.CurrentCulture,L.Text("평균 기록 {0}일 · 유효 관측 {1}시간 (오늘 포함)"),analysis.SampleDays,analysis.ObservedHours.ToString("0.#",CultureInfo.CurrentCulture));
            if (analysis.Status != "수동 기록" && analysis.Status != "짧은 한도" && analysis.Status != "지원하지 않는 항목" && analysis.Status != "이력 기록 꺼짐") {
                bool enough = analysis.ForecastSampleDays >= analysis.RequiredSampleDays && analysis.ForecastObservedHours >= analysis.RequiredObservedHours
                    && analysis.CollectionHours >= analysis.RequiredCollectionHours;
                yield return L.Text(enough ? "관측 충분도 · 기록 조건 충족" : "관측 충분도 · 관측 범위 제한적");
                yield return String.Format(CultureInfo.CurrentCulture,L.Text("예측용 기록 {0}/{1}일 · 관측 {2}/{3}시간"),analysis.ForecastSampleDays,analysis.RequiredSampleDays,
                    analysis.ForecastObservedHours.ToString("0.#",CultureInfo.CurrentCulture),AnalysisNumber(analysis.RequiredObservedHours));
                if (!enough) {
                    yield return String.Format(CultureInfo.CurrentCulture,L.Text("수집 경과 {0}시간 · 최소 {1}시간 필요"),analysis.CollectionHours.ToString("0.#",CultureInfo.CurrentCulture),AnalysisNumber(analysis.RequiredCollectionHours));
                    yield return String.Format(CultureInfo.CurrentCulture,L.Text("하루 {0}시간 이상 관측한 완료일을 예측에 사용합니다."),AnalysisNumber(analysis.RequiredDailyHours));
                }
                yield return L.Text("관측 충분도는 기록 조건의 충족 여부이며, 통계적 신뢰도나 예측 정확도가 아닙니다.");
            }
            if (analysis.AveragePerDay.HasValue)
                yield return L.Text("하루 평균은 오늘을 제외한 최근 7일 중 1시간 이상 관측한 날의 실제 소모량 평균입니다. 하루 전체로 환산하지 않습니다.");
            if (current && analysis.RecommendedPerDay.HasValue) {
                yield return L.Text("권장량은 현재 잔량을 초기화까지 남은 기간에 고르게 나눈 참고값입니다.");
            }
            if (current && analysis.EarliestEmptyAt.HasValue)
                yield return L.Text("소진 참고 범위는 관측일별 변동을 반영하며, 통계적 신뢰구간이 아닙니다. 사용 패턴에 따라 달라질 수 있습니다.");
        }

        private static bool AnalysisCollecting(UsageAnalysis analysis)
        { return analysis != null && (analysis.Status == "데이터 수집 중" || analysis.Status == "기록 부족"); }

        private static bool AnalysisResetWithinDay(UsageAnalysis analysis)
        { var now = DateTimeOffset.Now; return analysis.ResetAt.HasValue && analysis.ResetAt.Value > now && analysis.ResetAt.Value - now < TimeSpan.FromDays(1); }

        private static string AnalysisUnit(string unit)
        { return unit == "%p" ? L.Text("퍼센트포인트") : unit == "credits" ? L.Text("크레딧") : unit ?? ""; }

        private static string AnalysisNumber(double value)
        { return value.ToString("#,0.##",CultureInfo.CurrentCulture); }

        private static string AnalysisRangeDate(DateTimeOffset value)
        { return value.ToLocalTime().ToString(value.ToLocalTime().Year == DateTimeOffset.Now.ToLocalTime().Year ? "MM/dd" : "yyyy/MM/dd",CultureInfo.InvariantCulture); }

        private static string AnalysisDate(DateTimeOffset value)
        { return value.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture); }

        private static string AnalysisStatusExplanation(string status)
        {
            switch (status) {
                case "분석 가능": return "";
                case "이력 기록 꺼짐": return "이력 기록을 켜면 새 기록을 바탕으로 분석합니다.";
                case "기록 부족": case "데이터 수집 중": return "분석에 필요한 기록을 모으고 있습니다. 관측 시간이 충분해지면 표시합니다.";
                case "수동 기록": return "수동 기록의 변화만 요약합니다. 소진 시점은 예측하지 않습니다.";
                case "짧은 한도": return "짧게 초기화되는 한도는 소진 시점을 예측하지 않습니다. 주간 한도를 확인하세요.";
                case "오래된 기록": return "최근 확인값이 오래되어 예측을 표시하지 않습니다. 새로고침하거나 새 사용량을 수신하세요.";
                case "지원하지 않는 항목": return "이 항목은 잔량 분석을 지원하지 않습니다.";
                case "초기화 시각 없음": return "초기화 일정이 없어 소진 시점을 예측하지 않습니다.";
                case "초기화 확인 대기": return "초기화 예정 시각이 지났습니다. 새 잔량을 확인한 뒤 예측을 다시 표시합니다.";
                case "소비 변화 없음": return "관측한 구간에 소비 변화가 없어 소진 시점을 계산하지 않습니다.";
                default: return status ?? "";
            }
        }

        private void RefreshHistoryAnalysis(HistoryMetricChoice metric, List<UsageHistoryPoint> points, DateTimeOffset now)
        {
            if (historyAnalysis == null) return;
            historyAnalysis.Children.Clear();
            if (!ProFeaturesAvailable) return;
            var latest = metric == null || metric.Label == null ? null : points.Where(point => point.ServiceId == metric.ServiceId
                && point.Label == metric.Label && point.ObservedAt <= now).OrderByDescending(point => point.ObservedAt).FirstOrDefault();
            var panel = BuildUsageAnalysisPanel(metric == null ? null : metric.ServiceId,metric == null ? null : metric.Label,
                latest == null ? null : latest.Source,now,false);
            if (panel != null) historyAnalysis.Children.Add(panel);
        }

        internal static void ValidateUsageAlertThresholds(int low, int critical)
        {
            if (low < 1 || low > 99 || critical < 0 || critical >= low)
                throw new ArgumentException("긴급 기준은 0 이상이며 잔량 부족 기준보다 작아야 합니다.");
        }
    }
}
