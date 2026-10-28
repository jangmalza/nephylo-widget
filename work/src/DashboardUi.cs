using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private Window dashboardWindow;
        private string dashboardPage = "overview", dashboardSelectedService;
        private Grid dashboardRoot;
        private StackPanel dashboardNavigation;
        private ContentControl dashboardContent;
        private ScrollViewer dashboardScroll;
        private TextBlock dashboardBreadcrumb, dashboardUpdate, dashboardPlanLabel;
        private Button dashboardRefresh;
        private DispatcherOperation dashboardRefreshOperation;
        private string dashboardSignature, dashboardLanguage, dashboardNavigationKey;
        private bool dashboardCollapsed;
        private string dashboardFilter = "all";
        private bool dashboardFavoritesOnly;
        private string dashboardReturnPage = "services", dashboardReturnFocus;
        private double dashboardReturnOffset;
        private UniformGrid dashboardOverviewGrid, dashboardSummaryGrid;

        private void OpenDashboard()
        {
            if (exiting) return;
            if (dashboardWindow != null) {
                if (dashboardWindow.WindowState == WindowState.Minimized) dashboardWindow.WindowState = WindowState.Normal;
                dashboardWindow.Show(); dashboardWindow.Activate(); return;
            }
            BuildDashboardWindow().Show();
        }

        // Construction is separate from Show so the shell can be tested without
        // loading the widget or starting any provider/identity requests.
        private Window BuildDashboardWindow()
        {
            if (dashboardWindow != null) return dashboardWindow;
            var window = CreateManagementDialog("대시보드",1100,760,620);
            window.Topmost = false; window.ShowInTaskbar = true;
            dashboardWindow = window;
            RestoreDashboardGeometry(window);
            dashboardRoot = new Grid { Background = Back };
            TextOptions.SetTextFormattingMode(dashboardRoot,TextFormattingMode.Display);
            dashboardRoot.ColumnDefinitions.Add(new ColumnDefinition());
            dashboardRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
            var sidebar = new Border { Background = BrushOf("#F2F3EF"), BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(0,0,1,0) };
            var sideLayout = new DockPanel { Margin = new Thickness(12,18,12,16) };
            var brand = DashboardText("NEPHYLO",18,"#252827",FontWeights.SemiBold,new Thickness(8,0,0,22));
            brand.TextWrapping = TextWrapping.NoWrap; DockPanel.SetDock(brand,Dock.Top); sideLayout.Children.Add(brand);
            var footer = new StackPanel { Margin = new Thickness(8,20,0,0) };
            dashboardPlanLabel = CreatePlanLabel(); dashboardPlanLabel.FontSize = 13; footer.Children.Add(dashboardPlanLabel);
            footer.Children.Add(DashboardText("이 PC의 사용량",11,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)));
            DockPanel.SetDock(footer,Dock.Bottom); sideLayout.Children.Add(footer);
            dashboardNavigation = new StackPanel();
            sideLayout.Children.Add(new ScrollViewer { Content = dashboardNavigation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            sidebar.Child = sideLayout; dashboardRoot.Children.Add(sidebar);

            var workspace = new Grid(); Grid.SetColumn(workspace,1); dashboardRoot.Children.Add(workspace);
            workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1,GridUnitType.Star) });
            var toolbar = new DockPanel { Margin = new Thickness(20,12,20,12), LastChildFill = true };
            var toggle = DashboardIconButton("\uE700","사이드바 접기 · 펼치기",() => {
                if (window.ActualWidth < 850) { ShowDashboardPageMenu(); return; }
                settings.DashboardSidebarCollapsed = !dashboardCollapsed;
                UpdateDashboardSidebar(); if (!demo) TrySaveSettings();
            });
            AutomationProperties.SetAutomationId(toggle,"dashboard-toggle-sidebar");
            DockPanel.SetDock(toggle,Dock.Left); toolbar.Children.Add(toggle);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var add = DashboardAction("서비스 추가",() => OpenServices(CurrentServiceSelection()));
            dashboardRefresh = DashboardIconButton("\uE72C","새로고침",async () => { if (!demo) await RefreshAsync(); else RefreshDashboardIfOpen(); });
            actions.Children.Add(dashboardRefresh); actions.Children.Add(add);
            DockPanel.SetDock(actions,Dock.Right); toolbar.Children.Add(actions);
            dashboardBreadcrumb = DashboardText("대시보드",14,"#647070",FontWeights.Normal,new Thickness(10,0,10,0));
            AutomationProperties.SetAutomationId(dashboardBreadcrumb,"dashboard-page-menu");
            dashboardBreadcrumb.VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(dashboardBreadcrumb);
            workspace.Children.Add(new Border { BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(0,0,0,1), Child = toolbar });
            dashboardUpdate = DashboardText("",14,"#475256",FontWeights.Normal,new Thickness(24,12,24,0));
            Grid.SetRow(dashboardUpdate,1); workspace.Children.Add(dashboardUpdate);
            dashboardContent = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(28,26,28,28) };
            dashboardScroll = new ScrollViewer { Content = dashboardContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(dashboardScroll,2); workspace.Children.Add(dashboardScroll);
            window.Content = dashboardRoot;
            window.SizeChanged += (s,e) => { if (e.WidthChanged) { UpdateDashboardSidebar(); UpdateDashboardCardColumns(); } };
            window.Loaded += (s,e) => AnimateDashboardPage();
            EventHandler paletteChanged = (s,e) => { ApplyDialogTheme(window); dashboardSignature = null; RefreshDashboardIfOpen(); };
            WidgetPalette.Changed += paletteChanged;
            window.Closed += (s,e) => {
                SaveDashboardGeometry(window); WidgetPalette.Changed -= paletteChanged;
                if (dashboardRefreshOperation != null) { dashboardRefreshOperation.Abort(); dashboardRefreshOperation = null; }
                dashboardWindow = null; dashboardRoot = null; dashboardNavigation = null; dashboardContent = null;
                dashboardScroll = null; dashboardBreadcrumb = null; dashboardUpdate = null; dashboardRefresh = null; dashboardPlanLabel = null;
                dashboardSignature = dashboardLanguage = dashboardNavigationKey = null; dashboardOverviewGrid = dashboardSummaryGrid = null; ResetDashboardHistoryState(); ResetDashboardPreferenceState(); ResetWeeklySummaryState();
                dashboardReturnPage = "services"; dashboardReturnFocus = null; dashboardReturnOffset = 0;
            };
            UpdateDashboardSidebar(); RenderDashboard(true); return window;
        }

        private Button DashboardIconButton(string glyph,string label,Action action)
        {
            var button = new LocalizedButton { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
                Width = 36, Height = 36, Padding = new Thickness(0), ToolTip = label };
            AutomationProperties.SetName(button,label); button.Click += (s,e) => action(); return button;
        }

        private void UpdateDashboardSidebar()
        {
            if (dashboardRoot == null) return;
            double width = dashboardWindow.ActualWidth > 0 ? dashboardWindow.ActualWidth : dashboardWindow.Width;
            dashboardCollapsed = settings.DashboardSidebarCollapsed || width < 850;
            dashboardRoot.ColumnDefinitions[0].Width = new GridLength(dashboardCollapsed ? 0 : DashboardSidebarWidth);
            dashboardRoot.Children[0].Visibility = dashboardCollapsed ? Visibility.Collapsed : Visibility.Visible;
            UpdateDashboardCardColumns();
            // The top breadcrumb becomes a compact page menu on small screens.
            if (dashboardBreadcrumb != null) {
                dashboardBreadcrumb.Cursor = Cursors.Hand;
                dashboardBreadcrumb.ToolTip = L.Text("페이지 선택");
                dashboardBreadcrumb.MouseLeftButtonUp -= DashboardPageMenu;
                dashboardBreadcrumb.MouseLeftButtonUp += DashboardPageMenu;
                dashboardBreadcrumb.Focusable = true;
                dashboardBreadcrumb.KeyDown -= DashboardPageMenuKey;
                dashboardBreadcrumb.KeyDown += DashboardPageMenuKey;
            }
        }

        private void DashboardPageMenuKey(object sender,KeyEventArgs args)
        { if (args.Key == Key.Enter || args.Key == Key.Space) { ShowDashboardPageMenu(); args.Handled = true; } }
        private void DashboardPageMenu(object sender,MouseButtonEventArgs args) { ShowDashboardPageMenu(); args.Handled = true; }
        private void ShowDashboardPageMenu()
        {
            if (dashboardBreadcrumb == null) return;
            var focused = Keyboard.FocusedElement as DependencyObject;
            string menuOrigin = focused == null ? "" : AutomationProperties.GetAutomationId(focused);
            if (menuOrigin != "dashboard-toggle-sidebar") menuOrigin = "dashboard-page-menu";
            var menu = new ContextMenu { PlacementTarget = dashboardBreadcrumb, Resources = MakeResources() };
            foreach (var choice in DashboardPages()) {
                string page = choice.Key;
                var item = new MenuItem { Header = L.Text(choice.Value), IsCheckable = true, IsChecked = dashboardPage == page };
                item.Click += (s,e) => { NavigateDashboard(page); RestoreDashboardFocus(menuOrigin); }; menu.Items.Add(item);
            }
            var views = DashboardViews();
            if (views.Count > 0) menu.Items.Add(new Separator());
            foreach (var view in views.OrderByDescending(v => DashboardFavorite(v.Id))) {
                string id = view.Id;
                var item = new MenuItem { Header = L.Text(UsageServiceName(id)), IsCheckable = true,
                    IsChecked = dashboardPage == "detail" && dashboardSelectedService == id };
                item.Click += (s,e) => OpenDashboardServiceFromMenu(id,menuOrigin); menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private static KeyValuePair<string,string>[] DashboardPages()
        {
            return new[] { new KeyValuePair<string,string>("overview","개요"), new KeyValuePair<string,string>("services","서비스"),
                new KeyValuePair<string,string>("timeline","초기화 일정"),
                new KeyValuePair<string,string>("history","사용 이력"), new KeyValuePair<string,string>("analysis","잔량 분석"),
                new KeyValuePair<string,string>("weekly","주간 요약"),
                new KeyValuePair<string,string>("settings","설정") };
        }

        private void NavigateDashboard(string page)
        {
            dashboardPage = page; RenderDashboard(true);
            if (dashboardScroll != null) dashboardScroll.ScrollToTop();
            AnimateDashboardPage();
        }

        private void OpenDashboardService(string id)
        {
            if (!DashboardViews().Any(view => view.Id == id)) { RefreshDashboardIfOpen(); return; }
            if (dashboardPage != "detail") {
                dashboardReturnPage = DashboardPages().Any(page => page.Key == dashboardPage) ? dashboardPage : "services";
                dashboardReturnOffset = dashboardScroll == null ? 0 : dashboardScroll.VerticalOffset;
                var focused = Keyboard.FocusedElement as DependencyObject;
                dashboardReturnFocus = focused == null ? null : AutomationProperties.GetAutomationId(focused);
            }
            dashboardSelectedService = id; NavigateDashboard("detail");
            RestoreDashboardFocus("dashboard-back:services");
        }

        private void OpenDashboardServiceFromMenu(string id,string origin)
        {
            bool entering = dashboardPage != "detail";
            OpenDashboardService(id);
            if (entering && dashboardPage == "detail" && dashboardSelectedService == id)
                dashboardReturnFocus = origin == "dashboard-toggle-sidebar" ? origin : "dashboard-page-menu";
        }

        private void ReturnFromDashboardService()
        {
            string page = DashboardPages().Any(choice => choice.Key == dashboardReturnPage) ? dashboardReturnPage : "services";
            double offset = dashboardReturnOffset;
            string focusId = dashboardReturnFocus;
            NavigateDashboard(page);
            var content = dashboardContent == null ? null : dashboardContent.Content;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() => {
                if (dashboardPage != page || dashboardContent == null || dashboardContent.Content != content || dashboardScroll == null) return;
                dashboardScroll.ScrollToVerticalOffset(offset); RestoreDashboardFocus(focusId);
            }));
        }

        private string DashboardReturnLabel()
        {
            switch (dashboardReturnPage) {
                case "overview": return "← 개요";
                case "timeline": return "← 초기화 일정";
                case "history": return "← 사용 이력";
                case "analysis": return "← 잔량 분석";
                case "weekly": return "← 주간 요약";
                case "settings": return "← 설정 · 계정";
                default: return "← 서비스 목록";
            }
        }

        private void RefreshDashboardIfOpen()
        {
            if (dashboardWindow == null || exiting || dashboardRefreshOperation != null || Dispatcher.HasShutdownStarted) return;
            var target = dashboardWindow;
            dashboardRefreshOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => {
                dashboardRefreshOperation = null;
                if (dashboardWindow == target && !exiting) RenderDashboard(false);
            }));
        }

        private void RefreshDashboardAccess()
        {
            // Rights can change before the journal has been replaced. Clear now,
            // and only read the new journal after the membership handler finishes.
            LockDashboardHistory();
            LockWeeklySummary();
            if (dashboardContent != null) dashboardContent.Content = null;
            if (dashboardNavigation != null) dashboardNavigation.Children.Clear();
            dashboardSelectedService = null; dashboardFilter = "all"; dashboardFavoritesOnly = false; dashboardSignature = dashboardNavigationKey = null; ResetDashboardHistoryState(); ResetDashboardPreferenceState();
            dashboardReturnPage = "services"; dashboardReturnFocus = null; dashboardReturnOffset = 0;
            if (dashboardPage == "detail") dashboardPage = "services";
            RefreshDashboardIfOpen();
        }

        private void CloseDashboard() { if (dashboardWindow != null) dashboardWindow.Close(); }

        private List<ProviderView> DashboardViews()
        {
            var result = new List<ProviderView>();
            foreach (string id in new[] { "codex","eleven","openai","claude","gemini","cursor","higgsfield","higgsfield-api" }) {
                if (!IsServiceActive(id)) continue;
                var view = ViewFor(id);
                bool configured = demo ? view.Snapshot != null : id == "codex" ? HasCodexConnection()
                    : id == "eleven" ? !String.IsNullOrWhiteSpace(secret) : IsExtraReady(id);
                if (configured) result.Add(view);
            }
            return result;
        }

        private string DashboardStateKey()
        {
            var key = new StringBuilder();
            key.Append(dashboardPage).Append('|').Append(dashboardSelectedService).Append('|').Append(dashboardFilter).Append('|').Append(ProFeaturesAvailable)
                .Append('|').Append(L.Language).Append('|').Append(WidgetPalette.IsDark).Append('|').Append(DateTimeOffset.Now.ToUnixTimeSeconds() / 60);
            foreach (var view in DashboardViews()) {
                key.Append('|').Append(view.Id).Append(view.Source).Append(view.Error).Append(view.LastSuccess).Append(view.ReceiptState)
                    .Append(view.ReceiptObservedAt).Append(view.ReceiptMessage).Append(UsagePresentation.ShowBusy(view)).Append(view.AwaitingInitialRead);
                if (view.Snapshot == null) continue;
                key.Append(view.Snapshot.Plan).Append(view.Snapshot.Note);
                foreach (var row in view.Snapshot.Rows.Where(r => r != null)) key.Append('|').Append(row.Label).Append(row.RemainingPercent)
                    .Append(row.Remaining).Append(row.Limit).Append(row.ResetsAt).Append(row.DisplayValue).Append(row.Detail).Append(row.DisplaySubtitle);
            }
            if (usageJournal != null) key.Append('|').Append(usageJournal.Options.LowThresholdPercent).Append('|')
                .Append(usageJournal.Options.HiggsfieldCreditsThreshold).Append('|').Append(usageJournal.Options.HiggsfieldApiUsdThreshold);
            key.Append('|').Append(settings.AnimationsEnabled).Append('|').Append(dashboardFavoritesOnly).Append('|').Append(DashboardPreferencesKey());
            return key.ToString();
        }

        private string DashboardPreferencesKey()
        {
            var key = new StringBuilder();
            foreach (var view in DashboardViews()) {
                string preferred = DashboardPreferredMetric(view.Id) ?? "";
                key.Append(view.Id).Append(':').Append(DashboardFavorite(view.Id)).Append(':').Append(preferred.Length).Append(':').Append(preferred).Append('|');
            }
            return key.ToString();
        }

        private void RenderDashboard(bool force)
        {
            if (dashboardContent == null) return;
            UpdatePlanLabel(dashboardPlanLabel);
            dashboardRefresh.IsEnabled = !refreshing;
            dashboardRefresh.ToolTip = StatusTooltip();
            dashboardUpdate.Text = preparedUpdate == null ? "" : settings.AutoUpdateEnabled
                ? "업데이트가 준비되었습니다. 적용하려면 대시보드와 다른 관리 화면을 닫아 주세요."
                : "업데이트가 준비되었습니다. 설정 · 계정의 버전 · 설치 · 업데이트에서 설치할 수 있습니다.";
            dashboardUpdate.Visibility = preparedUpdate == null ? Visibility.Collapsed : Visibility.Visible;
            if (!force && dashboardPage == "detail" && dashboardLanguage == L.Language
                && DashboardViews().Any(v => v.Id == dashboardSelectedService) && DashboardPreferencePopupOpen()) return;
            string signature = DashboardStateKey();
            if (!force && dashboardSignature == signature) {
                if (ProFeaturesAvailable && (dashboardPage == "history" || dashboardPage == "analysis")) RefreshDashboardHistoryIfOpen();
                if (dashboardPage == "weekly") RefreshWeeklySummary();
                return;
            }
            bool sameHistory = !force && dashboardSignature != null && dashboardLanguage == L.Language && ProFeaturesAvailable && (dashboardPage == "history" || dashboardPage == "analysis");
            bool sameWeekly = !force && dashboardSignature != null && dashboardLanguage == L.Language && dashboardPage == "weekly" && weeklyPageRoot != null;
            dashboardSignature = signature; dashboardLanguage = L.Language;
            BuildDashboardNavigation();
            dashboardBreadcrumb.Text = L.Text(dashboardPage == "detail" ? UsageServiceName(dashboardSelectedService ?? "codex")
                : DashboardPages().FirstOrDefault(p => p.Key == dashboardPage).Value ?? "개요") + "  ▾";
            if (sameHistory) { RefreshDashboardHistoryIfOpen(); return; }
            if (sameWeekly) { RefreshWeeklySummary(); return; }
            double offset = dashboardScroll.VerticalOffset;
            var focused = Keyboard.FocusedElement as DependencyObject;
            string focusId = focused == null ? null : AutomationProperties.GetAutomationId(focused);
            dashboardOverviewGrid = null;
            ResetDashboardPreferenceState();
            switch (dashboardPage) {
                case "services": dashboardContent.Content = BuildDashboardServices(); break;
                case "detail": dashboardContent.Content = BuildDashboardServiceDetail(dashboardSelectedService); break;
                case "history": dashboardContent.Content = BuildDashboardHistoryPage(false); break;
                case "analysis": dashboardContent.Content = BuildDashboardHistoryPage(true); break;
                case "timeline": dashboardContent.Content = BuildDashboardTimelinePage(); break;
                case "weekly": dashboardContent.Content = BuildWeeklySummaryPage(); break;
                case "settings": dashboardContent.Content = BuildDashboardSettings(); break;
                default: dashboardContent.Content = BuildDashboardOverview(); break;
            }
            if (!force) dashboardScroll.ScrollToVerticalOffset(offset);
            if (!force) RestoreDashboardFocus(focusId);
        }

        private void BuildDashboardNavigation()
        {
            string key = dashboardPage + "|" + dashboardSelectedService + "|" + L.Language + "|" + DashboardPreferencesKey();
            if (dashboardNavigationKey == key) return;
            dashboardNavigationKey = key;
            dashboardNavigation.Children.Clear();
            string[] glyphs = { "\uE80F","\uE8A9","\uE787","\uE81C","\uE9D9","\uE8A5","\uE713" };
            int index = 0;
            foreach (var choice in DashboardPages()) {
                string page = choice.Key;
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.Children.Add(new TextBlock { Text = glyphs[index++], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Width = 16, Margin = new Thickness(0,0,10,0), VerticalAlignment = VerticalAlignment.Center });
                var label = DashboardText(choice.Value,14,"#252827",page == dashboardPage ? FontWeights.SemiBold : FontWeights.Normal);
                label.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(label,1); line.Children.Add(label);
                var button = new Button { Content = line, Style = (Style)Resources["WidgetServiceToggle"], HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12,11,8,11), Margin = new Thickness(0,2,0,2),
                    Background = BrushOf(page == dashboardPage ? "#E9EDFF" : "#F2F3EF"), BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(button,L.Text(choice.Value));
                AutomationProperties.SetAutomationId(button,"dashboard-nav:" + page);
                AutomationProperties.SetItemStatus(button,page == dashboardPage ? L.Text("선택됨") : "");
                button.Click += (s,e) => { NavigateDashboard(page); RestoreDashboardFocus("dashboard-nav:" + page); }; dashboardNavigation.Children.Add(button);
            }
            dashboardNavigation.Children.Add(DashboardText("연결된 서비스",11,"#647070",FontWeights.Normal,new Thickness(12,24,0,8)));
            foreach (var view in DashboardViews().OrderByDescending(v => DashboardFavorite(v.Id))) {
                string id = view.Id;
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new Image { Source = ServiceIcons.Get(id), Width = 16, Height = 16, Margin = new Thickness(0,0,10,0) });
                var label = DashboardText(UsageServiceName(id),14,"#475256",FontWeights.Normal);
                label.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(label,1); line.Children.Add(label);
                if (DashboardFavorite(id)) {
                    var favorite = DashboardText("★",13,"#647070",FontWeights.Normal,new Thickness(7,0,0,0));
                    favorite.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(favorite,2); line.Children.Add(favorite);
                }
                bool selected = dashboardPage == "detail" && dashboardSelectedService == id;
                var button = new Button { Content = line, Style = (Style)Resources["WidgetServiceToggle"], HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12,10,6,10), BorderThickness = new Thickness(0), Background = selected ? BrushOf("#E9EDFF") : Brushes.Transparent };
                AutomationProperties.SetName(button,L.Text(UsageServiceName(id)));
                AutomationProperties.SetAutomationId(button,"dashboard-nav-service:" + id);
                AutomationProperties.SetItemStatus(button,selected ? L.Text("선택됨") : "");
                button.Click += (s,e) => OpenDashboardService(id); dashboardNavigation.Children.Add(button);
            }
        }

        private Border DashboardCard(UIElement content)
        {
            return new Border { Child = content, Padding = new Thickness(22), Margin = new Thickness(0,0,0,18),
                Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        }

        private StackPanel DashboardPageHeader(string title,string description)
        {
            var header = new StackPanel { Margin = new Thickness(0,0,0,24) };
            header.Children.Add(DashboardText(title,28,"#252827",FontWeights.SemiBold));
            header.Children.Add(DashboardText(description,14,"#647070",FontWeights.Normal,new Thickness(0,10,0,0))); return header;
        }

        private FrameworkElement BuildDashboardOverview()
        {
            var page = new StackPanel();
            var header = DashboardPageHeader("개요",demo ? "미리보기 · 예시 데이터입니다. 실제 사용량이 아니며 PC에 저장하지 않습니다." : "서비스별 잔량과 다음 초기화를 한눈에 확인하세요.");
            header.Margin = new Thickness(0,0,0,18); page.Children.Add(header);
            var views = DashboardViews();
            var now = DateTimeOffset.Now;
            var options = usageJournal == null ? new UsageTrackingOptions() : usageJournal.Options;
            var states = views.ToDictionary(v => v.Id,v => DashboardAssessment.Assess(v,options,now));
            var metrics = dashboardSummaryGrid = new UniformGrid { Columns = 4, Margin = new Thickness(0,0,-12,20) };
            Grid.SetIsSharedSizeScope(metrics,true);
            int usable = states.Values.Sum(s => s.UsableCount);
            metrics.Children.Add(DashboardSummary("등록 서비스",views.Count.ToString(CultureInfo.InvariantCulture),"all","연결된 지원 서비스"));
            metrics.Children.Add(DashboardSummary("잔량 부족",usable == 0 ? "—" : states.Values.Sum(s => s.LowCount).ToString(CultureInfo.InvariantCulture),"low","항목별 잔량 알림 기준 · 클릭하여 필터"));
            metrics.Children.Add(DashboardSummary("확인 필요",states.Values.Count(s => s.HasUnknown).ToString(CultureInfo.InvariantCulture),"attention","연결 오류 · 이전 값 · 수신 대기"));
            metrics.Children.Add(DashboardSummary("24시간 내 초기화",states.Values.Sum(s => s.ResetCount).ToString(CultureInfo.InvariantCulture),"reset","확인 가능한 초기화 일정 · 클릭하여 필터"));
            page.Children.Add(metrics);
            var selected = views.Where(v => dashboardFilter == "all" || dashboardFilter == "low" && states[v.Id].HasLow
                || dashboardFilter == "attention" && states[v.Id].HasUnknown || dashboardFilter == "reset" && states[v.Id].ResetCount > 0)
                .Where(v => !dashboardFavoritesOnly || DashboardFavorite(v.Id)).OrderByDescending(v => DashboardFavorite(v.Id))
                .ThenBy(v => states[v.Id].HasUnknown ? 0 : states[v.Id].HasLow ? 1 : states[v.Id].ResetCount > 0 ? 2 : 3)
                .ThenBy(v => states[v.Id].NextReset ?? DateTimeOffset.MaxValue).ToList();
            var heading = new DockPanel { Margin = new Thickness(0,0,0,12), LastChildFill = true };
            var favoriteFilter = new CheckBox { Content = L.Text("즐겨찾기만"), IsChecked = dashboardFavoritesOnly, FontSize = 14, MinHeight = 36,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14,0,0,0), ToolTip = L.Text("서비스 상세에서 즐겨찾기를 설정할 수 있습니다.") };
            AutomationProperties.SetAutomationId(favoriteFilter,"dashboard-favorites-only");
            favoriteFilter.Click += (s,e) => { dashboardFavoritesOnly = favoriteFilter.IsChecked == true; NavigateDashboard("overview"); RestoreDashboardFocus("dashboard-favorites-only"); };
            DockPanel.SetDock(favoriteFilter,Dock.Right); heading.Children.Add(favoriteFilter);
            var caption = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            caption.Children.Add(DashboardText("서비스별 잔량",18,"#252827",FontWeights.SemiBold,new Thickness(0,0,10,0)));
            var count = DashboardText(String.Format(L.Text("{0}/{1}개 서비스 표시"),selected.Count,views.Count),11,"#647070",FontWeights.Normal,new Thickness(0,3,0,0));
            UiL.Raw(count); caption.Children.Add(count); heading.Children.Add(caption); page.Children.Add(heading);
            if (dashboardFilter != "all" || dashboardFavoritesOnly) {
                var filters = new WrapPanel { Margin = new Thickness(0,0,0,12) };
                string label = dashboardFilter == "low" ? "잔량 부족" : dashboardFilter == "attention" ? "확인 필요" : dashboardFilter == "reset" ? "24시간 내 초기화" : "등록 서비스";
                var filterText = DashboardText(L.Text(label) + (dashboardFavoritesOnly ? " · " + L.Text("즐겨찾기만") : ""),11,"#475256",FontWeights.Normal,new Thickness(0,6,12,0)); UiL.Raw(filterText); filters.Children.Add(filterText);
                var clear = DashboardAction("필터 해제",() => { dashboardFilter = "all"; dashboardFavoritesOnly = false; NavigateDashboard("overview"); RestoreDashboardFocus("dashboard-filter:all"); });
                clear.Margin = new Thickness(0); clear.Padding = new Thickness(12,8,12,8); clear.MinHeight = 36;
                AutomationProperties.SetAutomationId(clear,"dashboard-clear-filters"); filters.Children.Add(clear); page.Children.Add(filters);
            }
            dashboardOverviewGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0,0,-12,6) };
            page.Children.Add(dashboardOverviewGrid); UpdateDashboardCardColumns();
            if (views.Count == 0) page.Children.Add(BuildDashboardEmpty());
            else if (selected.Count == 0) {
                var empty = new StackPanel(); empty.Children.Add(DashboardText("해당하는 항목이 없습니다.",14,"#252827",FontWeights.SemiBold));
                empty.Children.Add(DashboardText(dashboardFavoritesOnly ? "즐겨찾기만 보기를 해제하거나 서비스 상세에서 즐겨찾기를 추가하세요."
                    : dashboardFilter == "low" && usable == 0 ? "현재 잔량을 판단할 수 없습니다. 확인 필요 항목을 살펴보세요." : "다른 요약을 선택하면 해당 서비스가 표시됩니다.",12,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)));
                page.Children.Add(DashboardCard(empty));
            }
            foreach (var view in selected) dashboardOverviewGrid.Children.Add(BuildDashboardOverviewCard(view));
            if (views.Count > 0) {
                page.Children.Add(DashboardText("최근 확인값 기준 · 카드를 선택하면 모든 한도와 연결 상태를 확인합니다.",11,"#647070",FontWeights.Normal));
                page.Children.Add(BuildDashboardUpcomingResetPreview());
            }
            return page;
        }

        private Button DashboardSummary(string label,string value,string filter,string description)
        {
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = "DashboardSummaryLabel" });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = DashboardText(label,11,"#647070",FontWeights.Normal);
            title.TextWrapping = TextWrapping.Wrap; title.ToolTip = L.Text(label);
            body.Children.Add(title);
            var number = DashboardText(value,30,"#252827",FontWeights.SemiBold,new Thickness(0,8,0,0));
            var reading = new WrapPanel();
            reading.Children.Add(number);
            bool limits = filter == "low" || filter == "reset";
            var unit = DashboardText("",10,"#647070",FontWeights.Normal,new Thickness(6,0,0,3)); UiL.Raw(unit);
            unit.Text = L.Language == "en" ? (limits ? "limit" : "service") + (value == "1" ? "" : "s") : limits ? "한도" : "서비스";
            reading.Children.Add(unit);
            ((FrameworkElement)reading.Children[1]).VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(reading,1); body.Children.Add(reading);
            var card = DashboardCard(body); card.Padding = new Thickness(14,14,14,14); card.Margin = new Thickness(0);
            if (dashboardFilter == filter) card.BorderBrush = Fore;
            var button = DashboardSurfaceButton(card,() => {
                dashboardFilter = dashboardFilter == filter ? "all" : filter; NavigateDashboard("overview");
                RestoreDashboardFocus("dashboard-filter:" + filter);
            });
            button.Margin = new Thickness(0,0,12,12); button.ToolTip = L.Text(description);
            AutomationProperties.SetName(button,L.Text(label)); AutomationProperties.SetAutomationId(button,"dashboard-filter:" + filter);
            AutomationProperties.SetItemStatus(button,dashboardFilter == filter ? L.Text("선택됨") : ""); return button;
        }

        private void UpdateDashboardCardColumns()
        {
            if (dashboardOverviewGrid == null && dashboardSummaryGrid == null) return;
            double windowWidth = dashboardWindow == null ? 1100 : dashboardWindow.ActualWidth > 0 ? dashboardWindow.ActualWidth : dashboardWindow.Width;
            double contentWidth = windowWidth - (dashboardCollapsed ? 0 : DashboardSidebarWidth) - 72;
            if (dashboardOverviewGrid != null) dashboardOverviewGrid.Columns = contentWidth >= 700 ? 2 : 1;
            if (dashboardSummaryGrid != null) dashboardSummaryGrid.Columns = contentWidth >= 680 ? 4 : 2;
        }

        private Button DashboardSurfaceButton(FrameworkElement content,Action click)
        {
            var button = new Button { Content = content, Style = (Style)Resources["WidgetServiceToggle"],
                Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch };
            button.Click += (s,e) => click(); AttachDashboardHover(button,content); return button;
        }

        private FrameworkElement BuildDashboardOverviewCard(ProviderView view)
        {
            var now = DateTimeOffset.Now;
            var rows = view.Snapshot == null || view.Snapshot.Rows == null ? new List<UsageRow>() : view.Snapshot.Rows.Where(r => r != null).ToList();
            var row = DashboardOverviewRow(view);
            var state = DashboardAssessment.Assess(view,usageJournal == null ? new UsageTrackingOptions() : usageJournal.Options,now);
            var presentation = UsagePresentation.Describe(view,now);
            bool usable = row != null && DashboardAssessment.IsUsableRow(view,row,now);
            var body = new StackPanel();
            var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = true };
            var icon = new Image { Source = ServiceIcons.Get(view.Id), Width = 20, Height = 20, Margin = new Thickness(0,0,8,0) };
            DockPanel.SetDock(icon,Dock.Left); name.Children.Add(icon);
            if (DashboardFavorite(view.Id)) { var star = DashboardText("★",10,"#647070",FontWeights.Normal,new Thickness(5,0,0,0)); DockPanel.SetDock(star,Dock.Right); name.Children.Add(star); }
            var serviceName = DashboardText(UsageServiceName(view.Id),16,"#252827",FontWeights.SemiBold);
            serviceName.TextWrapping = TextWrapping.NoWrap; serviceName.TextTrimming = TextTrimming.CharacterEllipsis; serviceName.ToolTip = L.Text(UsageServiceName(view.Id)); name.Children.Add(serviceName); top.Children.Add(name);
            string reading = row == null ? "—" : CompactUsageSummary.ValueText(row);
            if (row != null && !row.RemainingPercent.HasValue && row.Remaining.HasValue && String.IsNullOrEmpty(row.DisplayValue))
                reading = view.Id == "higgsfield-api" || view.Id == "openai" ? "$" + row.Remaining.Value.ToString("0.##",CultureInfo.InvariantCulture) : reading + " cr";
            var value = DashboardText(reading,32,"#252827",FontWeights.SemiBold,new Thickness(0,12,0,0)); value.Opacity = usable ? 1 : 0.7;
            value.HorizontalAlignment = HorizontalAlignment.Left; value.TextWrapping = TextWrapping.Wrap;
            bool awaiting = view.Snapshot == null && !presentation.Warning && (view.Busy || view.AwaitingInitialRead);
            value.ToolTip = awaiting ? L.Text(presentation.Message) : usable ? reading : L.Text("이전 값 또는 확인 불가 · 현재 잔량으로 판단하지 마세요.") + " " + reading;
            body.Children.Add(top); body.Children.Add(value);
            string metric = row == null ? L.Text(awaiting ? presentation.Message : "현재 잔량 확인 불가") : !demo && UsageFreshness.Source(view) == "manual" ? row.Label : L.Text(row.Label ?? "사용 한도");
            if (rows.Count > 1) metric += " · " + String.Format(L.Text(rows.Count == 2 ? "외 1개 한도" : "외 {0}개 한도"),rows.Count-1);
            var metricText = DashboardText(metric,11,"#647070",FontWeights.Normal,new Thickness(0,5,0,0)); UiL.Raw(metricText);
            metricText.TextWrapping = TextWrapping.Wrap; metricText.ToolTip = metric; body.Children.Add(metricText);
            // A failed read says why on the card instead of only fading the last value.
            if (!String.IsNullOrEmpty(view.Error) && !String.IsNullOrEmpty(presentation.Message)) {
                var reason = DashboardText(L.Text(presentation.Message),12,"#475256",FontWeights.Normal,new Thickness(0,6,0,0)); UiL.Raw(reason);
                reason.TextWrapping = TextWrapping.Wrap; reason.TextTrimming = TextTrimming.CharacterEllipsis; reason.MaxHeight = 40;
                reason.ToolTip = L.Text(presentation.Message); body.Children.Add(reason);
            }
            var meta = new WrapPanel { Margin = new Thickness(0,14,0,0) };
            string resetText = row != null && row.ResetsAt.HasValue ? !usable ? L.Text("일정 확인 필요") : L.Text(ResetText(row.ResetsAt.Value)) : L.Text("초기화 일정 없음");
            var reset = DashboardText(resetText,13,"#647070",FontWeights.Normal); reset.TextWrapping = TextWrapping.Wrap;
            reset.ToolTip = row != null && row.ResetsAt.HasValue ? L.Text("초기화 예정") + " · " + row.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") : resetText;
            string status = UsagePresentation.ShowBusy(view) ? "갱신 중" : state.HasUnknown ? usable ? "일부 항목 확인 필요" : state.StatusLabel : UsageFreshness.Source(view) == "manual" ? "수동 기록" : state.HasLow ? "잔량 부족" : "";
            // A provider restriction on otherwise usable rows still names itself.
            if (String.IsNullOrEmpty(status) && presentation.Warning) status = presentation.Label;
            bool attention = !UsagePresentation.ShowBusy(view) && !String.IsNullOrEmpty(status) && status != "수동 기록"
                && (presentation.Warning || state.HasLow || state.Stale);
            string stamp = presentation.ObservedAt.HasValue ? presentation.ObservedAt.Value.ToLocalTime().ToString("MM/dd HH:mm") : "—";
            var statusText = DashboardText(String.IsNullOrEmpty(status) ? L.Text("최근 확인") + " " + stamp : L.Text(status),11,state.HasUnknown || state.HasLow ? "#475256" : "#647070",state.HasUnknown || state.HasLow ? FontWeights.SemiBold : FontWeights.Normal,new Thickness(0,0,10,0));
            UiL.Raw(statusText);
            statusText.TextWrapping = TextWrapping.Wrap;
            statusText.ToolTip = L.Text("최근 확인") + " · " + stamp + (String.IsNullOrEmpty(presentation.Message) ? "" : "\n" + L.Text(presentation.Message));
            if (attention) { statusText.Margin = new Thickness(0); meta.Children.Add(new AttentionMark(statusText) { Margin = new Thickness(0,0,10,0) }); }
            else meta.Children.Add(statusText);
            meta.Children.Add(reset); body.Children.Add(meta);
            var card = DashboardCard(body); card.Padding = new Thickness(18,18,18,18); card.Margin = new Thickness(0);
            // A heavier border keeps the card distinct at a glance; padding absorbs the extra pixel.
            if (attention) { card.BorderBrush = BrushOf("#252827"); card.BorderThickness = new Thickness(2); card.Padding = new Thickness(17); }
            else if (state.HasUnknown) card.BorderBrush = BrushOf("#647070");
            string id = view.Id;
            var button = DashboardSurfaceButton(card,() => OpenDashboardService(id));
            button.Margin = new Thickness(0,0,12,12);
            AutomationProperties.SetName(button,L.Text(UsageServiceName(id)) + " · " + reading + (String.IsNullOrEmpty(status) ? "" : " · " + L.Text(status)));
            AutomationProperties.SetAutomationId(button,"dashboard-service:" + id);
            AutomationProperties.SetHelpText(button,metric + " · " + resetText + " · " + L.Text("최근 확인") + " " + stamp + " · " + L.Text("자세히 보기"));
            return button;
        }

        internal static bool IsDashboardLowRow(ProviderView view,UsageRow row,DateTimeOffset now,int threshold)
        {
            return DashboardAssessment.IsLowRow(view,row,new UsageTrackingOptions { LowThresholdPercent = threshold },now);
        }

        private FrameworkElement BuildDashboardServices()
        {
            var page = new StackPanel(); page.Children.Add(DashboardPageHeader("서비스","서비스별 연결 상태와 모든 한도를 관리하세요."));
            var views = DashboardViews(); if (views.Count == 0) page.Children.Add(BuildDashboardEmpty());
            foreach (var view in views.OrderByDescending(v => DashboardFavorite(v.Id))) page.Children.Add(BuildDashboardServiceCard(view)); return page;
        }

        private FrameworkElement BuildDashboardEmpty()
        {
            var body = new StackPanel { Margin = new Thickness(0,20,0,20) };
            body.Children.Add(DashboardText("첫 서비스를 연결해 보세요",20,"#252827",FontWeights.SemiBold));
            body.Children.Add(DashboardText("연결된 서비스가 여기에 표시됩니다. 로그인이나 API 키가 없는 서비스는 숨겨집니다.",12,"#647070",FontWeights.Normal,new Thickness(0,12,0,18)));
            var add = DashboardAction("서비스 연결 · 관리",() => OpenServices(CurrentServiceSelection()),true); add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0); body.Children.Add(add); return DashboardCard(body);
        }

        private FrameworkElement BuildDashboardServiceCard(ProviderView view)
        {
            var body = new StackPanel(); var head = new DockPanel { LastChildFill = true };
            string id = view.Id;
            var open = DashboardAction("자세히 보기",() => OpenDashboardService(id));
            AutomationProperties.SetAutomationId(open,"dashboard-detail:" + id);
            DockPanel.SetDock(open,Dock.Right); head.Children.Add(open);
            var connect = DashboardAction(String.IsNullOrEmpty(view.Error) ? "연결 · 설정" : "연결 확인",() => OpenServices(id));
            AutomationProperties.SetAutomationId(connect,"dashboard-connect:" + id); DockPanel.SetDock(connect,Dock.Right); head.Children.Add(connect);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new Image { Source = ServiceIcons.Get(id), Width = 23, Height = 23, Margin = new Thickness(0,0,10,0) });
            title.Children.Add(DashboardText(UsageServiceName(id),17,"#252827",FontWeights.SemiBold)); head.Children.Add(title); body.Children.Add(head);
            body.Children.Add(DashboardProviderStatus(view));
            if (view.Snapshot != null) foreach (var row in view.Snapshot.Rows.Where(r => r != null)) body.Children.Add(DashboardUsageRow(view,row,false));
            var card = DashboardCard(body); AutomationProperties.SetName(card,"dashboard-service:" + id); return card;
        }

        private FrameworkElement DashboardProviderStatus(ProviderView view)
        {
            var now = DateTimeOffset.Now;
            var state = UsagePresentation.Describe(view,now);
            var assessment = DashboardAssessment.Assess(view,usageJournal == null ? new UsageTrackingOptions() : usageJournal.Options,now);
            var body = new StackPanel { Margin = new Thickness(0,12,0,0) };
            string source = UsageFreshness.Source(view);
            string mode = source == "manual" ? "수동 기록" : source == "code" ? "Claude Code 수신" : "자동 조회";
            string observed = state.ObservedAt.HasValue ? state.ObservedAt.Value.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture) : "—";
            string label = UsagePresentation.ShowBusy(view) ? "갱신 중" : assessment.HasUnknown ? assessment.UsableCount > 0 ? "일부 항목 확인 필요" : assessment.StatusLabel : state.Label;
            bool warning = state.Warning || assessment.HasUnknown;
            var summary = DashboardText(L.Text(label) + " · " + L.Text(mode) + " · " + observed,11,warning ? "#252827" : "#647070",warning ? FontWeights.SemiBold : FontWeights.Normal);
            UiL.Raw(summary); body.Children.Add(summary);
            string message = state.Message;
            if (String.IsNullOrEmpty(message) && assessment.HasUnknown) message = source == "manual"
                ? "서비스에서 잔량을 확인하고 수동 기록을 갱신하세요."
                : source == "code" || source.StartsWith("code:",StringComparison.Ordinal) ? "Claude Code에서 새 응답 후 갱신됩니다."
                : "새로고침으로 최신 잔량을 확인하세요. 문제가 계속되면 연결 · 설정을 확인하세요.";
            if (!String.IsNullOrEmpty(message)) body.Children.Add(DashboardText(message,14,"#475256",FontWeights.Normal,new Thickness(0,10,0,0)));
            return body;
        }

        private FrameworkElement DashboardUsageRow(ProviderView view,UsageRow row,bool detailed)
        {
            var body = new StackPanel { Margin = new Thickness(0,16,0,0) };
            var line = new Grid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = DashboardText(row.Label ?? "사용 한도",14,"#647070",FontWeights.Normal,new Thickness(0,0,14,0));
            if (!demo && UsageFreshness.Source(view) == "manual") UiL.Raw(label);
            label.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(label);
            var value = DashboardText(CompactUsageSummary.ValueText(row),32,"#252827",FontWeights.SemiBold); Grid.SetColumn(value,1); line.Children.Add(value); body.Children.Add(line);
            bool usable = DashboardAssessment.IsUsableRow(view,row,DateTimeOffset.Now);
            if (!usable) {
                value.Opacity = 0.5;
                body.Children.Add(DashboardText("이전 값 또는 확인 불가 · 현재 잔량으로 판단하지 마세요.",11,"#475256",FontWeights.Normal,new Thickness(0,5,0,0)));
            }
            if (CompactUsageSummary.ValidPercent(row.RemainingPercent)) {
                var meter = new Grid { Height = 5, Margin = new Thickness(0,10,0,0), Background = BrushOf("#C8CECF") };
                meter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(row.RemainingPercent.Value,GridUnitType.Star) });
                meter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100-row.RemainingPercent.Value,GridUnitType.Star) });
                meter.Children.Add(new Border { Background = Fore }); meter.Opacity = usable ? 1 : 0.35; body.Children.Add(meter);
            }
            if (!String.IsNullOrWhiteSpace(row.DisplaySubtitle)) body.Children.Add(DashboardText(row.DisplaySubtitle,11,"#647070",FontWeights.Normal,new Thickness(0,7,0,0)));
            else if (row.Remaining.HasValue && row.Limit.HasValue) body.Children.Add(DashboardText(row.Remaining.Value.ToString("N0") + " / " + row.Limit.Value.ToString("N0") + " " + L.Text("크레딧"),11,"#647070",FontWeights.Normal,new Thickness(0,7,0,0)));
            if (row.ResetsAt.HasValue) {
                string reset = L.Text("초기화 예정") + " · " + row.ResetsAt.Value.ToLocalTime().ToString("MM/dd HH:mm",CultureInfo.InvariantCulture);
                if (row.ResetsAt.Value <= DateTimeOffset.Now) reset += " · " + L.Text("확인 필요");
                body.Children.Add(DashboardText(reset,11,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)));
            }
            if (detailed && !String.IsNullOrWhiteSpace(row.Detail)) body.Children.Add(DashboardText(row.Detail,14,"#475256",FontWeights.Normal,new Thickness(0,12,0,0)));
            return body;
        }

        private FrameworkElement BuildDashboardServiceDetail(string id)
        {
            var view = DashboardViews().FirstOrDefault(v => v.Id == id);
            if (view == null) return BuildDashboardEmpty();
            var page = new StackPanel(); page.Children.Add(DashboardPageHeader(UsageServiceName(id),"잔량 · 초기화 · 연결 상태"));
            var actions = new WrapPanel { Margin = new Thickness(0,0,0,16) };
            var back = DashboardAction(DashboardReturnLabel(),ReturnFromDashboardService);
            AutomationProperties.SetAutomationId(back,"dashboard-back:services"); actions.Children.Add(back);
            var connect = DashboardAction("연결 · 설정",() => OpenServices(id));
            AutomationProperties.SetAutomationId(connect,"dashboard-connect:" + id); actions.Children.Add(connect);
            var history = DashboardAction("사용 이력",() => { SetDashboardHistoryService(id); NavigateDashboard("history"); });
            AutomationProperties.SetAutomationId(history,"dashboard-history:" + id); actions.Children.Add(history);
            var analysis = DashboardAction("잔량 분석",() => { SetDashboardHistoryService(id); NavigateDashboard("analysis"); });
            AutomationProperties.SetAutomationId(analysis,"dashboard-analysis:" + id); actions.Children.Add(analysis);
            page.Children.Add(actions);
            page.Children.Add(BuildDashboardPreferencesControls(view));
            var body = new StackPanel(); body.Children.Add(DashboardProviderStatus(view));
            if (view.Snapshot != null) {
                foreach (var row in view.Snapshot.Rows.Where(r => r != null)) body.Children.Add(DashboardUsageRow(view,row,true));
                if (!String.IsNullOrWhiteSpace(view.Snapshot.Note)) body.Children.Add(DashboardText(view.Snapshot.Note,14,"#647070",FontWeights.Normal,new Thickness(0,20,0,0)));
            }
            page.Children.Add(DashboardCard(body)); return page;
        }

        private FrameworkElement BuildDashboardProGate()
        {
            // This is only a fallback for invalidated local history views.
            var page = new StackPanel();
            page.Children.Add(DashboardPageHeader("사용 이력","사용량 기록과 서비스 연결 정보는 이 PC에서 관리합니다."));
            page.Children.Add(DashboardText("Nephylo 로그인 없이 모든 기능을 무료로 이용합니다.",14,"#647070",FontWeights.Normal));
            return page;
        }

        private FrameworkElement BuildDashboardSettings()
        {
            var page = new StackPanel(); page.Children.Add(DashboardPageHeader("설정","연결과 표시 방법을 관리하세요."));
            page.Children.Add(DashboardSettingsRow("무료 이용 안내","Nephylo 로그인 없이 모든 기능을 무료로 이용합니다.",OpenPlan));
            page.Children.Add(DashboardSettingsRow("위젯 설정","언어 · 표시 · 시작 프로그램",OpenSettings));
            page.Children.Add(DashboardSettingsRow("알림 · 이력 설정","잔량 부족 · 초기화 · 사용 이력",OpenNotificationSettings));
            page.Children.Add(DashboardSettingsRow("버전 · 설치 · 업데이트","업데이트와 설치 상태를 확인하세요.",OpenReleaseInfo));
            page.Children.Add(DashboardText("사용량 기록과 서비스 연결 정보는 이 PC에서 관리합니다.",11,"#647070",FontWeights.Normal)); return page;
        }

        private FrameworkElement DashboardSettingsRow(string title,string description,Action action)
        {
            var body = new DockPanel(); var button = DashboardAction("열기",action); DockPanel.SetDock(button,Dock.Right); body.Children.Add(button);
            AutomationProperties.SetAutomationId(button,"dashboard-settings:" + title);
            var text = new StackPanel { Margin = new Thickness(0,0,12,0) }; text.Children.Add(DashboardText(title,16,"#252827",FontWeights.SemiBold));
            text.Children.Add(DashboardText(description,14,"#647070",FontWeights.Normal,new Thickness(0,8,0,0))); body.Children.Add(text); return DashboardCard(body);
        }
    }
}
