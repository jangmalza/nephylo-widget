using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private ToolTip refreshStatusTooltip;
        private bool refreshStatusKeyboardAttached;
        private Popup refreshDetailsPopup;

        private FrameworkElement CreateWidgetPictogram()
        {
            // The same half-filled vessel as the app icon, without its tile.
            // A fixed brand shape, not a live balance; use theme-aware ink.
            var vessel = new EllipseGeometry(new Point(32,32),22,22);
            var opening = Geometry.Parse("M18,32 A14,14 0 0 1 46,32 Z");
            var shape = Geometry.Combine(vessel,opening,GeometryCombineMode.Exclude,null);
            shape.Freeze();
            var icon = new Image { Source = new DrawingImage(new GeometryDrawing(Fore,null,shape)),
                Width = 18, Height = 18, Margin = new Thickness(0,0,8,0), Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false };
            AutomationProperties.SetName(icon,"Nephylo");
            return icon;
        }

        private void UpdateProviderHeaderDetails(ProviderView view, string info)
        {
            if (view.FoldButton == null) return;
            string title = L.Text(UsageServiceName(view.Id));
            if (view.Snapshot != null && !String.IsNullOrWhiteSpace(view.Snapshot.Plan)) title += " · " + L.Text(view.Snapshot.Plan);
            string details = title + "\n" + L.Text(info);
            SetPresentationTooltip(view.FoldButton,details);
            AutomationProperties.SetHelpText(view.FoldButton,details);
        }

        private TextBlock CreatePlanLabel()
        {
            var label = new TextBlock {
                FontFamily = FontFamily, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                Focusable = true, Cursor = Cursors.Hand
            };
            bool listening = false;
            EventHandler themeChanged = (s,e) => UpdatePlanLabel(label);
            EventHandler ownerClosed = null;
            Action unsubscribe = () => {
                if (listening) { WidgetPalette.Changed -= themeChanged; listening = false; }
                if (ownerClosed != null) Closed -= ownerClosed;
            };
            ownerClosed = (s,e) => unsubscribe();
            label.Loaded += (s,e) => {
                if (!listening) { WidgetPalette.Changed += themeChanged; Closed += ownerClosed; listening = true; }
                UpdatePlanLabel(label);
            };
            label.Unloaded += (s,e) => unsubscribe();
            label.MouseLeftButtonUp += (s,e) => { OpenPlan(); e.Handled = true; };
            label.KeyDown += (s,e) => {
                if (e.Key == Key.Enter || e.Key == Key.Space) { OpenPlan(); e.Handled = true; }
            };
            AttachKeyboardStatusTooltip(label);
            UpdatePlanLabel(label);
            return label;
        }

        private void UpdatePlanLabel(TextBlock label)
        {
            if (label == null) return;
            label.Inlines.Clear(); label.Foreground = Muted;
            label.Inlines.Add(new Run(L.Text("모든 기능 무료")) { Foreground = Muted });
            var details = new List<string> { "Nephylo · " + L.Text("무료 이용 안내"), L.Text(demo ? "전체 서비스 미리보기" : "Nephylo 회원가입이나 로그인 없이 이용할 수 있습니다.") };
            SetPresentationTooltip(label, String.Join("\n", details));
            AutomationProperties.SetName(label, "Nephylo · " + L.Text("모든 기능 무료"));
            AutomationProperties.SetHelpText(label, L.Text("무료 이용 안내"));
        }

        private FrameworkElement BuildWidgetStatusFooter()
        {
            var area = new LocalizedGrid { Margin = new Thickness(0,8,0,0), Visibility = Visibility.Collapsed };
            var warning = new WidgetWarningTextBlock {
                FontFamily = FontFamily, FontSize = 13, LineHeight = 20, Foreground = BrushOf("#91501F"),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            footer = warning;
            warning.WarningChanged = () => {
                area.Visibility = warning.Visibility;
                UpdateRefreshIndicator(); RefreshCompactBar(); RequestFitHeight();
            };
            area.Children.Add(warning);
            widgetFooter = area;
            area.SizeChanged += (s,e) => RequestFitHeight();
            return area;
        }

        // Legacy save and launch failures still write footer.Text directly. Their
        // visibility must not depend on a later periodic UpdateFooter call.
        private sealed class WidgetWarningTextBlock : LocalizedTextBlock
        {
            internal Action WarningChanged;
            static WidgetWarningTextBlock()
            {
                TextProperty.OverrideMetadata(typeof(WidgetWarningTextBlock),new FrameworkPropertyMetadata("",TextChanged));
            }
            private static void TextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
            {
                var warning = (WidgetWarningTextBlock)owner;
                warning.Visibility = String.IsNullOrWhiteSpace(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
                warning.ToolTip = warning.Text;
                AutomationProperties.SetName(warning, warning.Text ?? "");
                if (warning.WarningChanged != null) warning.WarningChanged();
            }
        }

        private List<ProviderView> ActivePresentationViews()
        {
            var active = new List<ProviderView>();
            foreach (string id in new[] { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" }) {
                if (!IsServiceActive(id)) continue;
                var view = ViewFor(id);
                StackPanel card;
                bool shown = serviceCards.TryGetValue(id,out card) && card.Visibility == Visibility.Visible;
                // Do not let a temporarily hidden card suppress an active error.
                if (shown || !String.IsNullOrEmpty(view.Error)) active.Add(view);
            }
            return active;
        }

        private bool ShowRefreshStatus { get { return refreshing && (!automaticRefreshInProgress || ActivePresentationViews().Any(UsagePresentation.ShowBusy)); } }

        private string StatusTooltip()
        {
            var lines = new List<string> { L.Text(ShowRefreshStatus ? "사용량 갱신 중…" : "새로고침") };
            if (demo) lines.Add(L.Text("예시 데이터 · 실제 API 호출 없음"));
            if (loginPending) lines.Add(L.Text("Codex 로그인 대기"));
            if (higgsfieldOperation != null) lines.Add(L.Text("Higgsfield 연결 확인 중"));
            var active = ActivePresentationViews();
            if (active.Count == 0) lines.Add(L.Text("서비스 연결 필요"));
            foreach (var view in active) {
                string source = UsageFreshness.Source(view), mode;
                if (source == "manual") mode = L.Text("수동 기록 · 자동 갱신 안 됨");
                else if (source == "code") mode = L.Text("Claude 응답 시 갱신");
                else {
                    string transport = source == "cli" || view.Id == "codex" || view.Id == "higgsfield" ? "CLI" : "API";
                    mode = transport + " · " + L.Text("자동 조회 " + (settings.RefreshSeconds / 60.0).ToString("0.#",CultureInfo.InvariantCulture) + "분");
                }
                lines.Add(L.Text(UsageServiceName(view.Id)) + " · " + mode);
                var state = UsagePresentation.Describe(view,DateTimeOffset.Now);
                DateTimeOffset? observed = state.ObservedAt;
                if (observed.HasValue) {
                    string prefix = source == "manual" ? "마지막 기록 " : source == "code" ? "마지막 수신 " : "마지막 확인: ";
                    lines.Add("  " + L.Text(prefix) + UsageFreshness.Stamp(observed.Value,DateTimeOffset.Now));
                } else lines.Add("  " + L.Text("아직 확인된 사용량이 없습니다."));
                if (state.Warning && String.IsNullOrEmpty(view.Error)) {
                    lines.Add("  " + L.Text(state.Label));
                    if (!String.IsNullOrEmpty(state.Message)) lines.Add("  " + L.Text(state.Message));
                }
                if (!String.IsNullOrEmpty(view.Error)) lines.Add("  " + L.Text(view.Error));
                if (view.Issue != null && !String.IsNullOrEmpty(view.Issue.Message) && view.Issue.Message != view.Error) lines.Add("  " + L.Text(view.Issue.Message));
                if (UsageFreshness.Automatic(view) && view.RefreshSchedule.Failures > 0) {
                    string retry = view.RefreshSchedule.NextAttempt > DateTimeOffset.Now
                        ? "다음 재시도 " + view.RefreshSchedule.NextAttempt.ToLocalTime().ToString("HH:mm:ss",CultureInfo.InvariantCulture)
                        : "연결 재시도 대기";
                    lines.Add("  " + L.Text(retry));
                }
            }
            foreach (string error in new[] { planStorageError, usageTrackingError })
                if (!String.IsNullOrEmpty(error)) lines.Add(L.Text(error));
            if (footer != null && !String.IsNullOrWhiteSpace(footer.Text) && !lines.Contains(footer.Text)) lines.Add(footer.Text);
            return String.Join("\n",lines);
        }

        private void UpdateRefreshIndicator()
        {
            if (refreshButton == null) return;
            bool warning = ActivePresentationViews().Any(view => UsagePresentation.Describe(view,DateTimeOffset.Now).Warning)
                || !String.IsNullOrEmpty(planStorageError) || !String.IsNullOrEmpty(usageTrackingError)
                || footer != null && !String.IsNullOrWhiteSpace(footer.Text) && !(demo && UiL.Source(footer,TextBlock.TextProperty) == "예시 데이터 · 실제 API 호출 없음");
            refreshButton.Content = !refreshing && warning ? "\uE7BA" : "\uE72C";
            refreshButton.Foreground = warning ? BrushOf("#91501F") : Muted;
            string detail = StatusTooltip();
            if (refreshStatusTooltip == null) {
                refreshStatusTooltip = NewPresentationTooltip();
                refreshButton.ToolTip = refreshStatusTooltip;
            }
            refreshStatusTooltip.Content = detail;
            ToolTipService.SetShowOnDisabled(refreshButton,true);
            AutomationProperties.SetName(refreshButton,L.Text(warning ? "연결 확인 필요" : "새로고침"));
            AutomationProperties.SetHelpText(refreshButton,detail);
            if (!refreshStatusKeyboardAttached) { AttachKeyboardStatusTooltip(refreshButton); refreshStatusKeyboardAttached = true; }
            UpdateRefreshMotion();
        }

        private void ShowRefreshDetails(Button anchor)
        {
            if (anchor == null || exiting) return;
            if (refreshStatusTooltip != null) refreshStatusTooltip.IsOpen = false;
            if (refreshDetailsPopup != null && refreshDetailsPopup.IsOpen) { refreshDetailsPopup.IsOpen = false; return; }
            var popup = CreateRefreshDetailsPopup(anchor);
            refreshDetailsPopup = popup;
            popup.IsOpen = true;
        }

        private Popup CreateRefreshDetailsPopup(Button anchor)
        {
            var content = new LocalizedStackPanel { MaxWidth = 330 };
            var text = new LocalizedTextBlock { Text = StatusTooltip(), FontFamily = FontFamily, FontSize = 13, LineHeight = 20, Foreground = Fore, TextWrapping = TextWrapping.Wrap };
            content.Children.Add(new ScrollViewer { Content = text, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var refresh = new LocalizedButton { Content = "새로고침", ContentTemplate = ActionTextTemplate(), FontFamily = FontFamily, FontSize = 14, MinHeight = 38, Margin = new Thickness(0,10,0,0), IsEnabled = !refreshing };
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = false };
            popup.Child = new LocalizedBorder { Child = content, Background = BrushOf("#FBFBF8"), BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(12), Resources = Resources };
            content.Children.Add(refresh);
            refresh.Click += async (s,e) => { popup.IsOpen = false; if (!demo) await RefreshAsync(); };
            content.KeyDown += (s,e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; anchor.Focus(); e.Handled = true; } };
            popup.Opened += (s,e) => refresh.Focus();
            EventHandler closed = null;
            closed = (s,e) => { popup.IsOpen = false; Closed -= closed; };
            Closed += closed;
            popup.Closed += (s,e) => { Closed -= closed; if (Object.ReferenceEquals(refreshDetailsPopup,popup)) refreshDetailsPopup = null; };
            return popup;
        }

        private static ToolTip NewPresentationTooltip()
        {
            return new ToolTip { Placement = PlacementMode.Bottom, MaxWidth = 380 };
        }

        private static void SetPresentationTooltip(FrameworkElement element, string text)
        {
            var tip = element.ToolTip as ToolTip;
            if (tip == null) { tip = NewPresentationTooltip(); element.ToolTip = tip; }
            tip.Content = text;
        }

        private static void AttachKeyboardStatusTooltip(FrameworkElement element)
        {
            // WPF distinguishes keyboard navigation from mouse-acquired focus,
            // owns the hover delay and dismisses the tooltip on pointer input.
            // Opening IsOpen on every focus event bypasses that lifecycle and
            // leaves a clicked service header's tooltip pinned over its balance.
            ToolTipService.SetShowsToolTipOnKeyboardFocus(element,true);
            element.PreviewKeyDown += (s,e) => {
                var tip = element.ToolTip as ToolTip;
                if (tip == null || !tip.IsOpen) return;
                if (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Space) {
                    tip.IsOpen = false;
                    if (e.Key == Key.Escape) e.Handled = true;
                }
            };
            element.LostKeyboardFocus += (s,e) => {
                var tip = element.ToolTip as ToolTip;
                if (tip != null) tip.IsOpen = false;
            };
            element.Unloaded += (s,e) => {
                var tip = element.ToolTip as ToolTip;
                if (tip != null) tip.IsOpen = false;
            };
        }
    }

    // One monochrome "needs attention" mark shared by widget cards, their rows and
    // the dashboard. Active, it inverts (dark fill with light text in the light
    // theme, the reverse in dark) and adds a warning glyph, so a warning stands out
    // without relying on color. Palette brushes follow later theme changes.
    internal sealed class AttentionMark : Border
    {
        internal readonly TextBlock Text, Glyph;
        private readonly Brush normalForeground;
        private readonly FontWeight normalWeight;

        internal AttentionMark(TextBlock text) : this(text,true) { }

        internal AttentionMark(TextBlock text, bool active)
        {
            Text = text; normalForeground = text.Foreground; normalWeight = text.FontWeight;
            Glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = Math.Max(9,text.FontSize - 2),
                Foreground = WidgetPalette.Brush("#FFFFFF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,4,0) };
            // A dock layout lets long translated text trim instead of overflowing.
            var line = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(Glyph,Dock.Left); line.Children.Add(Glyph);
            text.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(text);
            Child = line; CornerRadius = new CornerRadius(3);
            HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Center;
            Active = active;
        }

        internal bool Active
        {
            get { return Glyph.Visibility == Visibility.Visible; }
            set {
                Background = value ? WidgetPalette.Brush("#252827") : Brushes.Transparent;
                Padding = value ? new Thickness(5,1,6,2) : new Thickness(0);
                Glyph.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                Text.Foreground = value ? WidgetPalette.Brush("#FFFFFF") : normalForeground;
                Text.FontWeight = value ? FontWeights.SemiBold : normalWeight;
            }
        }
    }
}
