using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace UsageWidget
{
    // A projection of already-read data. It never refreshes providers or changes their snapshots.
    internal sealed class CompactUsageSummary
    {
        internal UsageRow Row;
        internal string Metric, Value, Reset;
        internal bool Warning;
        internal string StateLabel, StateMessage;
        internal DateTimeOffset? ObservedAt;

        internal static CompactUsageSummary From(ProviderView view, DateTimeOffset now)
        {
            var rows = view.Snapshot == null ? new List<UsageRow>() : view.Snapshot.Rows.Where(r => r != null).ToList();
            bool quotaService = (view.Id == "codex" || view.Id == "claude") && UsageFreshness.Source(view) != "manual";
            var quotaRows = quotaService ? rows.Where(r => Period(r.Label).Length != 0).ToList() : new List<UsageRow>();
            var row = quotaRows.Where(r => ValidPercent(r.RemainingPercent)).OrderBy(r => r.RemainingPercent.Value).FirstOrDefault()
                ?? quotaRows.FirstOrDefault() ?? rows.FirstOrDefault();
            var state = UsagePresentation.Describe(view,now);
            var result = new CompactUsageSummary { Row = row, Value = "—", Reset = "—", Metric = state.Label, Warning = state.Warning || state.Stale,
                StateLabel = state.Label, StateMessage = state.Message, ObservedAt = state.ObservedAt };
            if (row == null) {
                if (view.Id == "claude" && UsageFreshness.IsCode(view)) {
                    result.Value = view.ReceiptState == ProviderReceiptState.QuotaUnavailable ? "한도 미제공" : "수신 대기";
                    result.Metric = "";
                }
                return result;
            }
            result.Value = ValueText(row);
            string source = UsageFreshness.Source(view);
            result.Metric = source == "manual" ? "수동" : quotaService ? Period(row.Label) : "";
            if (view.Id == "openai") result.Metric = row.Label == "설정 예산 잔여" ? "예산" : "비용";
            if (view.Id == "higgsfield" || (row.Label ?? "").Contains("크레딧") && !row.RemainingPercent.HasValue)
                result.Value += result.Value == "—" || result.Value == "무제한" ? "" : " cr";
            if (row.ResetsAt.HasValue) {
                result.Reset = ResetText(row.ResetsAt.Value,now);
                result.Warning |= row.ResetsAt.Value <= now;
            }
            // A valid representative window must not hide another window awaiting a reset update.
            result.Warning |= quotaRows.Any(r => (r.Label ?? "").Contains("수신 대기") || r.ResetsAt.HasValue && r.ResetsAt.Value <= now);
            string note = view.Snapshot.Note ?? "";
            result.Warning |= note.Contains("이용 제한") || note.Contains("지출 한도 도달");
            return result;
        }

        internal static string Period(string label)
        {
            string name = (label ?? "").Replace(" · 수신 대기","");
            var match = Regex.Match(name,@"(?:^| · )(?<n>\d+)(?<unit>시간|일|분)(?: 한도)?$",RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups["n"].Value + match.Groups["unit"].Value;
            if (Regex.IsMatch(name,@"(?:^| · )주간(?: 한도)?$",RegexOptions.CultureInvariant)) return "주간";
            if (Regex.IsMatch(name,@"(?:^| · )(기본 한도|추가 한도)$",RegexOptions.CultureInvariant)) return "한도";
            return "";
        }

        internal static bool ValidPercent(double? value)
        { return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0 && value.Value <= 100; }

        internal static string ValueText(UsageRow row)
        {
            if (!String.IsNullOrWhiteSpace(row.DisplayValue)) return row.DisplayValue;
            if (ValidPercent(row.RemainingPercent)) return row.RemainingPercent.Value.ToString("0.#",CultureInfo.InvariantCulture) + "%";
            if (row.Remaining.HasValue && !Double.IsNaN(row.Remaining.Value) && !Double.IsInfinity(row.Remaining.Value)) return row.Remaining.Value.ToString("N0",CultureInfo.CurrentCulture);
            if ((row.Detail ?? "").Contains("무제한")) return "무제한";
            return "—";
        }

        internal static string ResetText(DateTimeOffset reset, DateTimeOffset now)
        {
            if (reset <= now) return "확인 중";
            var local = reset.ToLocalTime();
            return local.ToString(local.Date == now.ToLocalTime().Date ? "HH:mm" : local.Year == now.ToLocalTime().Year ? "MM/dd" : "yy/MM/dd",CultureInfo.InvariantCulture);
        }

        internal static string NarrowValue(string serviceId, UsageRow row, string fullValue)
        {
            if (row == null || ValidPercent(row.RemainingPercent) && String.IsNullOrWhiteSpace(row.DisplayValue)) return fullValue;
            bool dollar = (serviceId == "openai" || serviceId == "higgsfield-api") && (row.DisplayValue ?? "").StartsWith("$",StringComparison.Ordinal);
            bool credits = serviceId == "higgsfield" || (row.Label ?? "").Contains("크레딧");
            double amount;
            if (row.Remaining.HasValue && !Double.IsNaN(row.Remaining.Value) && !Double.IsInfinity(row.Remaining.Value)) amount = row.Remaining.Value;
            else if (dollar && serviceId == "openai" && (row.Label == "이번 달 비용" || row.Label == "이번 달 순비용")
                && Regex.IsMatch(row.DisplayValue,@"^\$-?\d[\d,]*(?:\.\d+)?$")
                && Double.TryParse(row.DisplayValue.Substring(1),NumberStyles.Number,CultureInfo.InvariantCulture,out amount)) { }
            else return fullValue;
            // A custom provider display string is not silently replaced by a parsed or unrelated number.
            if (!dollar && !credits && !String.IsNullOrEmpty(row.DisplayValue)) return fullValue;
            double magnitude = Math.Abs(amount), divisor = magnitude >= 1000000000 ? 1000000000 : magnitude >= 1000000 ? 1000000 : magnitude >= 1000 ? 1000 : 1;
            if (divisor == 1) return fullValue;
            string suffix = divisor == 1000000000 ? "B" : divisor == 1000000 ? "M" : "K";
            return (dollar ? "$" : "") + (amount / divisor).ToString("0.#",CultureInfo.InvariantCulture) + suffix + (credits ? " cr" : "");
        }

        // Prefix allocation keeps the user's service order and always reserves a reachable overflow control.
        internal static int VisibleCount(IList<double> widths, double available, double overflowWidth)
        {
            if (widths.Count == 0) return 0;
            if (widths.Sum() <= available) return widths.Count;
            double used = 0; int count = 0;
            while (count < widths.Count && used + widths[count] + overflowWidth <= available) used += widths[count++];
            return count > 0 ? count : available >= (widths.Count > 1 ? overflowWidth : 0) + 64 ? 1 : 0;
        }
    }

    public partial class WidgetWindow
    {
        private Grid compactBarRoot;
        private StackPanel compactServiceStrip;
        private StackPanel compactControls;
        private Grid compactControlGroup;
        private Button compactRefreshButton, compactRestoreButton, compactOverflowButton, compactEmptyButton;
        private readonly Dictionary<string,CompactServiceCell> compactCells = new Dictionary<string,CompactServiceCell>();
        private readonly List<string> compactOverflowIds = new List<string>();
        private bool arrangingCompact;
        private double compactPreferredWidth = 240;
        private string compactWidthContentKey;
        private double CompactPreferredWidth { get { return compactPreferredWidth; } }

        private sealed class CompactServiceCell
        {
            internal Button Button;
            internal TextBlock Metric, Value, Reset, Warning;
            internal Border WarningMark;
            internal bool Low;
            internal Image Icon;
            internal Border ResetChip;
            internal CompactUsageSummary Summary;
            internal string Id;
            internal double Width;
        }

        private FrameworkElement BuildCompactBar()
        {
            compactBarRoot = new LocalizedGrid { Height = 44, Margin = new Thickness(7,0,7,0), Background = Brushes.Transparent, ClipToBounds = true };
            compactBarRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            compactBarRoot.ColumnDefinitions.Add(new ColumnDefinition());
            compactBarRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var grip = new LocalizedBorder { Width = 12, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "드래그하여 위젯 이동" };
            var dots = new Grid { Width = 6, Height = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.55, IsHitTestVisible = false };
            for (int row = 0; row < 3; row++) for (int column = 0; column < 2; column++) {
                var dot = new Border { Width = 2, Height = 2, CornerRadius = new CornerRadius(1), Background = Muted,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(column * 4,row * 5,0,0) };
                dots.Children.Add(dot);
            }
            grip.Child = dots;
            grip.MouseLeftButtonDown += (s,e) => { if (e.ClickCount == 1) { try { DragMove(); } catch (InvalidOperationException) { } e.Handled = true; } };
            compactBarRoot.Children.Add(grip);
            compactServiceStrip = new LocalizedStackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(compactServiceStrip,1); compactBarRoot.Children.Add(compactServiceStrip);
            compactOverflowButton = CompactButton("추가 서비스 보기"); compactOverflowButton.Width = 36; compactOverflowButton.Padding = new Thickness(2,0,2,0);
            compactOverflowButton.Click += (s,e) => OpenCompactOverflow();
            compactEmptyButton = CompactButton("서비스 연결"); compactEmptyButton.Content = L.Text("서비스 연결");
            compactEmptyButton.Click += (s,e) => OpenServices(CurrentServiceSelection());
            compactControlGroup = new Grid { VerticalAlignment = VerticalAlignment.Center };
            compactControls = new LocalizedStackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            compactRefreshButton = ToolButton("\uE72C","새로고침",async (s,e) => {
                CloseCompactTip(compactRefreshButton);
                if (String.Equals(compactRefreshButton.Content as string,"\uE7BA",StringComparison.Ordinal)) ShowRefreshDetails(compactRefreshButton);
                else if (!demo) await RefreshAsync();
            });
            AttachKeyboardStatusTooltip(compactRefreshButton);
            ToolTipService.SetShowOnDisabled(compactRefreshButton,true);
            compactRestoreButton = ToolButton("\uE740","일반 보기로 전환",(s,e) => SetCompactView(false));
            compactRestoreButton.Content = CompactRestoreIcon();
            var more = ToolButton("\uE712","더 보기",null); more.Click += (s,e) => OpenMoreMenu(more);
            foreach (var button in new[] { compactRefreshButton,compactRestoreButton,more }) {
                button.Style = (Style)Resources["WidgetCompactAction"];
                button.Width = 24;
                compactControls.Children.Add(button);
            }
            compactControlGroup.Children.Add(compactControls);
            compactControlGroup.Children.Add(new Border { Width = 1, Height = 16, Background = BrushOf("#C8CECF"), Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            Grid.SetColumn(compactControlGroup,2); compactBarRoot.Children.Add(compactControlGroup);
            compactBarRoot.SizeChanged += (s,e) => ArrangeCompactCells();
            compactBarRoot.IsVisibleChanged += (s,e) => {
                if (compactBarRoot.IsVisible) return;
                foreach (var cell in compactCells.Values) {
                    CloseCompactTip(cell.Button);
                    if (cell.Button.ContextMenu != null) cell.Button.ContextMenu.IsOpen = false;
                }
                if (compactOverflowButton.ContextMenu != null) compactOverflowButton.ContextMenu.IsOpen = false;
                CloseCompactTip(compactRefreshButton);
            };
            RefreshCompactBar();
            return compactBarRoot;
        }

        private FrameworkElement CompactRestoreIcon()
        {
            // A window outline makes the return to normal view clearer than a lone diagonal arrow.
            return new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2,4 L 2,13 L 13,13 L 13,10 M 6,1 L 15,1 L 15,10 M 15,1 L 7,9"),
                Stroke = Muted, StrokeThickness = 1.15, Width = 16, Height = 16, Stretch = Stretch.None,
                SnapsToDevicePixels = true, IsHitTestVisible = false };
        }

        private double CompactControlWidth(double barWidth, IList<double> serviceWidths)
        {
            int denseCount = CompactUsageSummary.VisibleCount(serviceWidths,barWidth - 84,36);
            double required = serviceWidths.Take(denseCount).Sum() + (denseCount < serviceWidths.Count ? 36 : 0);
            // Extra control spacing must never hide a service or its reset date when the bar grows.
            bool roomy = barWidth >= 330 && required <= barWidth - 104;
            foreach (Button button in compactControls.Children) button.Width = roomy ? 28 : 24;
            compactControls.Margin = new Thickness(roomy ? 8 : 0,0,0,0);
            compactControlGroup.Measure(new Size(Double.PositiveInfinity,32));
            return compactControlGroup.DesiredSize.Width;
        }

        private static TextBlock CompactText(string value, double size, bool muted)
        {
            return new LocalizedTextBlock { Text = value, FontSize = size, Foreground = muted ? Muted : Fore, FontWeight = muted ? FontWeights.Normal : FontWeights.Medium,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        }

        private Button CompactButton(string accessibleName)
        {
            var button = new LocalizedButton { Height = 32, Style = (Style)Resources["WidgetCompactService"], Padding = new Thickness(7,0,7,0), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button,L.Text(accessibleName));
            ToolTipService.SetInitialShowDelay(button,450); ToolTipService.SetShowDuration(button,20000);
            button.GotKeyboardFocus += (s,e) => { var tip = button.ToolTip as ToolTip; if (tip != null) { tip.PlacementTarget = button; tip.IsOpen = true; } };
            button.LostKeyboardFocus += (s,e) => CloseCompactTip(button);
            button.Unloaded += (s,e) => CloseCompactTip(button);
            button.Click += (s,e) => CloseCompactTip(button);
            return button;
        }

        private static void CloseCompactTip(Button button)
        { var tip = button.ToolTip as ToolTip; if (tip != null) tip.IsOpen = false; }

        private CompactServiceCell CreateCompactCell(string id)
        {
            var cell = new CompactServiceCell { Id = id, Button = CompactButton(UsageServiceName(id)) };
            var line = new LocalizedStackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            cell.Icon = new Image { Source = ServiceIcons.Get(id), Width = 18, Height = 18, Margin = new Thickness(0,0,7,0), Stretch = Stretch.Uniform }; line.Children.Add(cell.Icon);
            cell.Value = CompactText("—",15,false); cell.Value.FontWeight = FontWeights.SemiBold; line.Children.Add(cell.Value);
            cell.Metric = CompactText("",12,true); cell.Metric.Margin = new Thickness(5,0,0,0); line.Children.Add(cell.Metric);
            cell.Reset = CompactText("—",12,true);
            cell.ResetChip = new Border { Child = cell.Reset, Background = BrushOf("#F2F3EF"), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5,2,5,2), Margin = new Thickness(9,0,0,0), VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(cell.ResetChip);
            // Same inverse attention style as the widget and dashboard marks. The
            // circle follows the "!" text's visibility, which the fit logic toggles.
            cell.Warning = CompactText("!",12,false); cell.Warning.Foreground = BrushOf("#FFFFFF"); cell.Warning.FontWeight = FontWeights.Bold;
            cell.Warning.HorizontalAlignment = HorizontalAlignment.Center; cell.Warning.VerticalAlignment = VerticalAlignment.Center;
            cell.WarningMark = new Border { Child = cell.Warning, Background = BrushOf("#252827"), CornerRadius = new CornerRadius(8),
                Width = 16, Height = 16, Margin = new Thickness(6,0,0,0), VerticalAlignment = VerticalAlignment.Center };
            cell.WarningMark.SetBinding(UIElement.VisibilityProperty,new System.Windows.Data.Binding("Visibility") { Source = cell.Warning });
            line.Children.Add(cell.WarningMark);
            cell.Button.Content = line;
            cell.Button.Click += (s,e) => OpenCompactDetails(id,cell.Button);
            return cell;
        }

        private string[] CompactServiceIds()
        { return settings.ServiceOrder.Where(id => serviceCards.ContainsKey(id) && serviceCards[id].Visibility == Visibility.Visible).ToArray(); }

        private void RefreshCompactBar()
        {
            // Open details keep their controls and selection while the advice
            // re-checks the latest provider result, including failed refreshes.
            RefreshUsageAnalysisPanels(DateTimeOffset.Now);
            if (compactBarRoot == null) return;
            var now = DateTimeOffset.Now;
            var ids = CompactServiceIds();
            foreach (string id in ids) {
                CompactServiceCell cell;
                if (!compactCells.TryGetValue(id,out cell)) compactCells[id] = cell = CreateCompactCell(id);
                var view = ViewFor(id); var summary = CompactUsageSummary.From(view,now);
                cell.Summary = summary;
                cell.Low = summary.Row != null && DashboardAssessment.IsLowRow(view,summary.Row,usageJournal == null ? null : usageJournal.Options,now);
                cell.Metric.Text = summary.Metric; cell.Metric.Visibility = String.IsNullOrEmpty(summary.Metric) ? Visibility.Collapsed : Visibility.Visible;
                cell.Value.Text = summary.Value; cell.Reset.Text = "↻ " + L.Text(summary.Reset);
                cell.Warning.Visibility = summary.Warning || cell.Low ? Visibility.Visible : Visibility.Collapsed;
                string details = CompactDetailsText(id,now);
                var tip = cell.Button.ToolTip as ToolTip;
                if (tip == null) cell.Button.ToolTip = tip = new ToolTip { Placement = PlacementMode.Bottom, MaxWidth = 430 };
                tip.Content = new TextBlock { Text = CompactTooltipText(id,now), TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(cell.Button,L.Text(UsageServiceName(id)) + " · " + L.Text(summary.Metric) + " " + L.Text(summary.Value) + " · " + L.Text("초기화") + " " + L.Text(summary.Reset) + (summary.Warning ? " · " + L.Text(summary.StateLabel) : "") + (cell.Low ? " · " + L.Text("잔량 부족") : ""));
                AutomationProperties.SetHelpText(cell.Button,details);
                RestoreCompactCell(cell);
                cell.Width = Math.Max(116,MeasureCompactCell(cell));
            }
            // Keep enough room for controls and reset dates. Ordinary changes to
            // the remaining number must not shrink/grow the bar on every poll.
            // A changed service set or language starts a fresh fit and may shrink.
            string widthKey = L.Language + "\n" + String.Join("\n",ids.OrderBy(id => id,StringComparer.Ordinal));
            double measuredWidth = Math.Max(240,118 + ids.Sum(id => compactCells[id].Width));
            compactPreferredWidth = compactWidthContentKey == widthKey && settings.CompactView
                ? Math.Max(compactPreferredWidth,measuredWidth) : measuredWidth;
            compactWidthContentKey = widthKey;
            compactRefreshButton.IsEnabled = !refreshing;
            string status = StatusTooltip();
            SetPresentationTooltip(compactRefreshButton,status);
            AutomationProperties.SetHelpText(compactRefreshButton,status);
            bool failed = ids.Any(id => compactCells[id].Summary.Warning) || ActivePresentationViews().Any(view => UsagePresentation.Describe(view,now).Warning) || !String.IsNullOrEmpty(planStorageError) || !String.IsNullOrEmpty(usageTrackingError)
                || footer != null && !String.IsNullOrWhiteSpace(footer.Text) && !(demo && UiL.Source(footer,TextBlock.TextProperty) == "예시 데이터 · 실제 API 호출 없음");
            compactRefreshButton.Content = !refreshing && failed ? "\uE7BA" : "\uE72C";
            compactRefreshButton.Foreground = failed ? BrushOf("#91501F") : Muted;
            AutomationProperties.SetName(compactRefreshButton,L.Text(failed ? "새로고침 · 확인 필요" : ShowRefreshStatus ? "사용량 갱신 중…" : "새로고침"));
            ArrangeCompactCells();
            QueueCompactAutoWidth();
        }

        private void ResetCompactWidthMeasurement() { compactWidthContentKey = null; }

        private void RestoreCompactCell(CompactServiceCell cell)
        {
            cell.Button.Width = Double.NaN; cell.Button.Padding = new Thickness(7,0,7,0);
            cell.Value.Text = CompactDisplayValue(cell.Summary); cell.Value.FontSize = cell.Summary.Row == null ? 12 : 15; cell.Value.MaxWidth = Double.PositiveInfinity;
            cell.Value.FontWeight = cell.Summary.Row == null ? FontWeights.Medium : FontWeights.SemiBold;
            cell.Icon.Width = cell.Icon.Height = 18; cell.Icon.Margin = new Thickness(0,0,7,0);
            cell.Metric.Visibility = String.IsNullOrEmpty(cell.Summary.Metric) ? Visibility.Collapsed : Visibility.Visible;
            cell.ResetChip.Visibility = cell.Summary.Reset == "—" ? Visibility.Collapsed : Visibility.Visible;
            cell.Warning.Visibility = cell.Summary.Warning || cell.Low ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string CompactDisplayValue(CompactUsageSummary summary)
        {
            if (summary.Row == null && L.English) {
                if (summary.Value == "한도 미제공") return "Unavailable";
                if (summary.Value == "수신 대기") return "Waiting";
            }
            return summary.Value;
        }

        private static double MeasureCompactCell(CompactServiceCell cell)
        {
            var content = (FrameworkElement)cell.Button.Content;
            // Visibility can change several times in one fitting pass. Measure each
            // child in its current state before asking the row for its natural width.
            cell.Reset.InvalidateMeasure();
            double width = 0;
            foreach (FrameworkElement child in ((Panel)content).Children) {
                child.InvalidateMeasure();
                child.Measure(new Size(Double.PositiveInfinity,32));
                if (child.Visibility != Visibility.Collapsed) width += child.DesiredSize.Width;
            }
            content.InvalidateMeasure();
            content.Measure(new Size(Double.PositiveInfinity,32));
            return Math.Ceiling(width + cell.Button.Padding.Left + cell.Button.Padding.Right
                + cell.Button.BorderThickness.Left + cell.Button.BorderThickness.Right);
        }

        private void FitCompactCell(CompactServiceCell cell, double width)
        {
            RestoreCompactCell(cell);
            if (MeasureCompactCell(cell) > width) cell.ResetChip.Visibility = Visibility.Collapsed;
            if (MeasureCompactCell(cell) > width && cell.Id != "openai") cell.Metric.Visibility = Visibility.Collapsed;
            if (MeasureCompactCell(cell) > width) {
                cell.Value.Text = cell.Summary.Row == null ? CompactDisplayValue(cell.Summary) : CompactUsageSummary.NarrowValue(cell.Id,cell.Summary.Row,cell.Summary.Value);
                cell.Button.Padding = new Thickness(1,0,1,0);
                cell.Icon.Width = cell.Icon.Height = 14; cell.Icon.Margin = new Thickness(0,0,4,0);
                // The shared status control still exposes this warning when the inline mark cannot fit.
                cell.Warning.Visibility = Visibility.Collapsed;
            }
            if (MeasureCompactCell(cell) > width) { cell.Value.FontSize = 12; cell.Value.FontWeight = FontWeights.Medium; }
            if (MeasureCompactCell(cell) > width) {
                double others = MeasureCompactCell(cell) - cell.Value.DesiredSize.Width;
                cell.Value.MaxWidth = Math.Max(26,width - others);
            }
            cell.Button.Width = Math.Max(1,width);
            cell.Button.Measure(new Size(Math.Max(1,width),32));
        }

        private void ArrangeCompactCells()
        {
            if (compactBarRoot == null || arrangingCompact) return;
            arrangingCompact = true;
            try {
                var ids = CompactServiceIds().Where(compactCells.ContainsKey).ToArray();
                double barWidth = compactBarRoot.ActualWidth > 0 ? compactBarRoot.ActualWidth : Math.Max(240,Width) - 14;
                var widths = ids.Select(id => compactCells[id].Width).ToArray();
                double available = Math.Max(0,barWidth - 12 - CompactControlWidth(barWidth,widths));
                int count = CompactUsageSummary.VisibleCount(widths,available,36);
                for (int i = 0; i < count; i++) {
                    double width = count == 1 ? Math.Min(compactCells[ids[i]].Width,Math.Max(1,available - (ids.Length > 1 ? 36 : 0))) : compactCells[ids[i]].Width;
                    FitCompactCell(compactCells[ids[i]],width);
                    compactCells[ids[i]].Button.Tag = i > 0 ? "divider" : null;
                }
                compactOverflowIds.Clear(); compactOverflowIds.AddRange(ids.Skip(count));
                var children = ids.Take(count).Select(id => (UIElement)compactCells[id].Button).ToList();
                if (ids.Length == 0) children.Add(compactEmptyButton);
                if (count < ids.Length) {
                    bool warning = compactOverflowIds.Any(id => compactCells[id].Summary.Warning);
                    compactOverflowButton.Content = "+" + (ids.Length - count).ToString(CultureInfo.InvariantCulture) + (warning ? " !" : "");
                    compactOverflowButton.Tag = count > 0 ? "divider" : null;
                    string hidden = L.Text("추가 서비스 보기") + "\n" + String.Join("\n",compactOverflowIds.Select(id => L.Text(UsageServiceName(id)) + (compactCells[id].Summary.Warning ? " · " + L.Text(compactCells[id].Summary.StateLabel) : "")));
                    SetPresentationTooltip(compactOverflowButton,hidden);
                    AutomationProperties.SetName(compactOverflowButton,L.Text("추가 서비스 보기") + " " + (ids.Length-count).ToString(CultureInfo.InvariantCulture) + (warning ? " · " + L.Text("확인 필요") : ""));
                    AutomationProperties.SetHelpText(compactOverflowButton,hidden);
                    children.Add(compactOverflowButton);
                }
                if (!compactServiceStrip.Children.Cast<UIElement>().SequenceEqual(children)) {
                    var focused = Keyboard.FocusedElement as DependencyObject;
                    bool lostFocus = focused != null && compactServiceStrip.IsKeyboardFocusWithin;
                    foreach (var cell in compactCells.Values) CloseCompactTip(cell.Button);
                    compactServiceStrip.Children.Clear(); foreach (var child in children) compactServiceStrip.Children.Add(child);
                    if (lostFocus && settings.CompactView) { if (children.Contains(focused as UIElement)) Keyboard.Focus((IInputElement)focused); else compactRestoreButton.Focus(); }
                }
            }
            finally { arrangingCompact = false; }
        }

        private string CompactDetailsText(string id, DateTimeOffset now)
        {
            var view = ViewFor(id); var lines = new List<string> { L.Text(UsageServiceName(id)) };
            var state = UsagePresentation.Describe(view,now);
            string source = UsageFreshness.Source(view);
            if (!String.IsNullOrEmpty(state.Label)) lines.Add(L.Text("연결 상태") + " · " + L.Text(state.Label));
            if (!String.IsNullOrEmpty(state.Message)) lines.Add(L.Text(state.Message));
            lines.Add(L.Text(source == "manual" ? "수동 기록 · 자동 갱신 안 됨" : source == "code" ? "Claude 응답 후 갱신" : "자동 조회 " + (settings.RefreshSeconds / 60.0).ToString("0.#",CultureInfo.InvariantCulture) + "분"));
            lines.Add(state.ObservedAt.HasValue ? L.Text(ObservationTitle(source)) + ": " + state.ObservedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture) : L.Text("아직 확인된 사용량이 없습니다."));
            if (view.Snapshot != null) {
                foreach (var row in view.Snapshot.Rows.Where(r => r != null)) {
                    lines.Add((source == "manual" ? row.Label : L.Text(row.Label ?? "사용 한도")) + " · " + L.Text(CompactUsageSummary.ValueText(row)));
                    lines.Add(L.Text("초기화") + ": " + (row.ResetsAt.HasValue ? row.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz",CultureInfo.InvariantCulture) + (row.ResetsAt.Value <= now ? " · " + L.Text("확인 중") : "") : "—"));
                    if (!String.IsNullOrWhiteSpace(row.DisplaySubtitle)) lines.Add(L.Text(row.DisplaySubtitle));
                    if (!String.IsNullOrWhiteSpace(row.Detail)) lines.Add(L.Text(row.Detail));
                }
                if (!String.IsNullOrWhiteSpace(view.Snapshot.Note)) lines.Add(L.Text(view.Snapshot.Note));
            }
            if (!String.IsNullOrEmpty(view.Error)) lines.Add(L.Text("갱신 실패") + " · " + L.Text(view.Error));
            if (view.RefreshSchedule.Failures > 0) lines.Add(L.Text("다음 재시도") + ": " + view.RefreshSchedule.NextAttempt.ToLocalTime().ToString("HH:mm:ss",CultureInfo.InvariantCulture));
            return String.Join("\n",lines);
        }

        private static string ObservationTitle(string source)
        { return source == "manual" ? "마지막 기록" : source == "code" ? "마지막 수신" : "마지막 확인"; }

        private string CompactTooltipText(string id, DateTimeOffset now)
        {
            var summary = CompactUsageSummary.From(ViewFor(id),now);
            var lines = new List<string> { L.Text(UsageServiceName(id)), L.Text(summary.StateLabel) };
            if (!String.IsNullOrEmpty(summary.StateMessage)) lines.Add(L.Text(summary.StateMessage));
            if (summary.Row != null) lines.Add(L.Text(summary.Metric) + " " + L.Text(summary.Value));
            if (summary.Reset != "—") lines.Add("↻ " + L.Text("초기화") + " " + L.Text(summary.Reset));
            if (summary.ObservedAt.HasValue) lines.Add(L.Text(ObservationTitle(UsageFreshness.Source(ViewFor(id)))) + " " + summary.ObservedAt.Value.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture));
            lines.Add(L.Text("클릭하여 자세히 보기"));
            return String.Join("\n",lines.Where(line => !String.IsNullOrWhiteSpace(line)));
        }

        private static TextBlock CompactDetailText(string text, bool heading = false)
        {
            return new TextBlock { Text = text, Foreground = heading ? Fore : Muted, FontSize = heading ? 14 : 13,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
                LineHeight = heading ? 21 : 19, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0,heading ? 18 : 6,0,0) };
        }

        private FrameworkElement BuildCompactDetails(string id, DateTimeOffset now)
        {
            var view = ViewFor(id); var state = UsagePresentation.Describe(view,now);
            string source = UsageFreshness.Source(view);
            var body = new StackPanel { Width = 310 };
            var name = CompactDetailText(L.Text(UsageServiceName(id)),true); name.FontSize = 16; name.LineHeight = 24; name.Margin = new Thickness(0,0,0,0); body.Children.Add(name);
            body.Children.Add(CompactDetailText(L.Text("연결 상태"),true));
            body.Children.Add(CompactDetailText(L.Text(state.Label)));
            if (!String.IsNullOrWhiteSpace(state.Message)) body.Children.Add(CompactDetailText(L.Text(state.Message)));
            body.Children.Add(CompactDetailText(L.Text("사용량"),true));
            var rows = view.Snapshot == null ? new List<UsageRow>() : view.Snapshot.Rows.Where(row => row != null).ToList();
            if (rows.Count == 0) body.Children.Add(CompactDetailText(L.Text("확인된 한도 정보가 없습니다.")));
            foreach (var row in rows) {
                var line = new Grid { Margin = new Thickness(0,10,0,0) };
                line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = CompactDetailText(source == "manual" ? row.Label ?? "" : L.Text(row.Label ?? "사용 한도")); label.Margin = new Thickness(0,0,10,0); line.Children.Add(label);
                var value = CompactDetailText(L.Text(CompactUsageSummary.ValueText(row)),true); value.Margin = new Thickness(0); value.MaxWidth = 145;
                value.FontSize = 18; value.LineHeight = 26;
                Grid.SetColumn(value,1); line.Children.Add(value); body.Children.Add(line);
                if (!String.IsNullOrEmpty(row.DisplaySubtitle)) body.Children.Add(CompactDetailText(L.Text(row.DisplaySubtitle)));
            }
            body.Children.Add(CompactDetailText("↻ " + L.Text("초기화"),true));
            var resets = rows.Where(row => row.ResetsAt.HasValue).ToList();
            if (resets.Count == 0) body.Children.Add(CompactDetailText(L.Text("초기화 일정이 제공되지 않았습니다.")));
            foreach (var row in resets) {
                string label = source == "manual" ? row.Label ?? "" : L.Text(row.Label ?? "사용 한도");
                body.Children.Add(CompactDetailText(label));
                var reset = CompactDetailText(row.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz",CultureInfo.InvariantCulture)
                    + (row.ResetsAt.Value <= now ? " · " + L.Text("확인 중") : ""),true);
                reset.FontSize = 15; reset.LineHeight = 22; reset.Margin = new Thickness(0,3,0,0);
                body.Children.Add(reset);
            }
            body.Children.Add(CompactDetailText(L.Text(ObservationTitle(source)),true));
            body.Children.Add(CompactDetailText(state.ObservedAt.HasValue ? state.ObservedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture) : L.Text("아직 확인된 사용량이 없습니다.")));
            body.Children.Add(CompactDetailText(L.Text(source == "manual" ? "수동 기록 · 자동 갱신 안 됨" : source == "code" ? "Claude 응답 후 갱신" : "자동 조회 " + (settings.RefreshSeconds / 60.0).ToString("0.#",CultureInfo.InvariantCulture) + "분")));
            if (ProFeaturesAvailable) {
                var analysis = BuildCompactAnalysis(id,now);
                if (analysis != null) body.Children.Add(analysis);
            }
            var notes = rows.Select(row => row.Detail).Where(value => !String.IsNullOrWhiteSpace(value)).ToList();
            if (view.Snapshot != null && !String.IsNullOrWhiteSpace(view.Snapshot.Note)) notes.Add(view.Snapshot.Note);
            if (!String.IsNullOrWhiteSpace(view.Error) && view.Error != state.Message) notes.Add(view.Error);
            if (view.RefreshSchedule.Failures > 0) notes.Add(L.Text("다음 재시도") + ": " + view.RefreshSchedule.NextAttempt.ToLocalTime().ToString("HH:mm:ss",CultureInfo.InvariantCulture));
            if (notes.Count > 0) {
                body.Children.Add(CompactDetailText(L.Text("연결 및 지원 안내"),true));
                foreach (string note in notes.Distinct()) body.Children.Add(CompactDetailText(L.Text(note)));
            }
            return body;
        }

        private ContextMenu CompactMenu(Button target)
        {
            return new ContextMenu { Style = (Style)Resources["WidgetContextMenu"], FontFamily = FontFamily,
                PlacementTarget = target, Placement = PlacementMode.Bottom, MaxWidth = 450 };
        }

        private void OpenCompactDetails(string id, Button target)
        {
            var menu = CompactMenu(target);
            var content = BuildCompactDetails(id,DateTimeOffset.Now);
            var scroll = new ScrollViewer { Content = content, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(4) };
            menu.Items.Add(new MenuItem { Header = scroll, StaysOpenOnClick = true, Style = (Style)Resources["WidgetMenuItem"] });
            menu.Items.Add(new Separator { Style = (Style)Resources["WidgetMenuSeparator"] });
            var manage = WidgetMenuItem("서비스 연결 · 관리","\uE713"); manage.Click += (s,e) => OpenServices(id); menu.Items.Add(manage);
            target.ContextMenu = menu; menu.IsOpen = true;
        }

        private void OpenCompactOverflow()
        {
            var menu = CompactMenu(compactOverflowButton);
            foreach (string id in compactOverflowIds.ToArray()) {
                string selected = id; var summary = CompactUsageSummary.From(ViewFor(id),DateTimeOffset.Now);
                var item = WidgetMenuItem(UsageServiceName(id),"");
                item.Icon = new Image { Source = ServiceIcons.Get(id), Width = 16, Height = 16 };
                item.Header = L.Text(UsageServiceName(id)) + " · " + L.Text(summary.Metric) + " " + L.Text(summary.Value) + (summary.Reset == "—" ? "" : " · ↻ " + L.Text(summary.Reset)) + (summary.Warning ? " !" : "");
                item.ToolTip = CompactTooltipText(id,DateTimeOffset.Now);
                item.Click += (s,e) => { menu.IsOpen = false; Dispatcher.BeginInvoke(new Action(() => OpenCompactDetails(selected,compactOverflowButton))); };
                menu.Items.Add(item);
            }
            compactOverflowButton.ContextMenu = menu; menu.IsOpen = true;
        }
    }
}
