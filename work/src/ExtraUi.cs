using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private ExtraSettings extras;
        private string openAiKey = "";
        private readonly OpenAiUsageService openAi = new OpenAiUsageService();
        private readonly ProviderView openAiView = new ProviderView { Accent = "#2448F5" };
        private readonly ProviderView claudeView = new ProviderView { Accent = "#2448F5" };
        private readonly ProviderView geminiView = new ProviderView { Accent = "#2448F5" };
        private readonly ProviderView cursorView = new ProviderView { Accent = "#2448F5" };
        private readonly Dictionary<string, FrameworkElement> extraCards = new Dictionary<string, FrameworkElement>();
        private Window servicesWindow;
        private System.Windows.Controls.Primitives.Selector servicesSelector;
        private Action refreshServiceNavigation;
        private bool showingExtraDemo;
        private bool claudeConfigured;
        private string claudeUnavailableSource;
        private DateTimeOffset? claudeUnavailableAt;
        private UsageJournal claudeUnavailableJournal;

        private void LoadExtraServices()
        {
            extras = demo ? new ExtraSettings() : ExtraSettingsStore.Load();
            higgsfieldSettings = demo ? new HiggsfieldSettings() : HiggsfieldSettingsStore.Load();
            if (!demo) openAiKey = ExtraSettingsStore.LoadOpenAiKey();
            if (!demo) claudeConfigured = IsSavedClaudeConfigured();
        }

        private void AddExtraCards(StackPanel providers)
        {
            AddExtraCard(providers, "openai", "OpenAI API", openAiView);
            AddExtraCard(providers, "claude", "Claude", claudeView);
            AddExtraCard(providers, "gemini", "Gemini API", geminiView);
            AddExtraCard(providers, "cursor", "Cursor", cursorView);
            AddExtraCard(providers, "higgsfield", "Higgsfield 웹", higgsfieldView);
            AddExtraCard(providers, "higgsfield-api", "Higgsfield API", higgsfieldApiView);
            RefreshLocalExtras();
        }

        private void AddExtraCard(StackPanel providers, string id, string name, ProviderView view)
        {
            var block = AddProviderCard(providers,id,name,view);
            var buttons = new LocalizedStackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,6,0,0) };
            buttons.Children.Add(SmallAction("사용량 페이지 ↗", () => OpenUsagePage(id)));
            buttons.Children.Add(SmallAction(id == "openai" || id == "claude" || id == "higgsfield" ? "연결 · 설정" : "수동 기록", () => OpenServices(id)));
            block.Children.Add(buttons); serviceActions[id] = buttons; extraCards[id] = block;
        }

        private Button SmallAction(string title, Action action)
        {
            var button = new LocalizedButton { Content = title, ContentTemplate = ActionTextTemplate(), FontSize = 13, MinHeight = 32, Padding = new Thickness(0,6,0,6), Background = System.Windows.Media.Brushes.Transparent, Foreground = BrushOf("#2448F5"), BorderThickness = new Thickness(0), Margin = new Thickness(0,0,14,0) };
            button.Click += (s,e) => action(); return button;
        }

        private static string UsageUrl(string id)
        {
            switch (id) {
                case "openai": return "https://platform.openai.com/usage";
                case "claude": return "https://claude.ai/settings/usage";
                case "gemini": return "https://aistudio.google.com/usage";
                case "cursor": return "https://cursor.com/settings";
                case "higgsfield": return "https://higgsfield.ai";
                case "higgsfield-api": return "https://open.higgsfield.ai";
                default: throw new ArgumentException("Unknown service");
            }
        }
        private void OpenUsagePage(string id) { OpenOfficialPage(UsageUrl(id)); }
        private void OpenOfficialPage(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { if (footer != null) footer.Text = "브라우저를 열지 못했습니다."; }
        }

        private bool IsExtraShown(string id)
        {
            if (!demo) return true; // Free selection determines activity; retain legacy visibility preferences on disk.
            if (id == "higgsfield" || id == "higgsfield-api") return true;
            return id == "openai" ? extras.ShowOpenAi : id == "claude" ? extras.ShowClaude : id == "gemini" ? extras.ShowGemini : extras.ShowCursor;
        }
        private static bool HasManualUsage(ManualUsageRecord record)
        {
            return record != null && record.RemainingPercent.HasValue && record.ObservedAt.HasValue;
        }
        private bool IsExtraReady(string id)
        {
            if (demo) return true;
            if (id == "openai") return !String.IsNullOrWhiteSpace(openAiKey);
            if (id == "claude") return claudeConfigured || HasManualUsage(extras.ClaudeManual);
            if (id == "gemini") return HasManualUsage(extras.GeminiManual);
            if (id == "cursor") return HasManualUsage(extras.CursorManual);
            if (id == "higgsfield") return higgsfieldSettings.WebConnected;
            if (id == "higgsfield-api") return higgsfieldSettings.ApiBalanceUsd.HasValue && higgsfieldSettings.ApiObservedAt.HasValue;
            return false;
        }
        private void UpdateServiceVisibility()
        {
            RefreshDashboardIfOpen();
            bool pro = !demo && ProFeaturesAvailable;
            bool anyConnected = HasCodexConnection() || !String.IsNullOrWhiteSpace(secret) || extraCards.Keys.Any(IsExtraReady);
            Func<string, bool> visible = id => IsServiceActive(id) && (!pro || !anyConnected && servicePlan.IsActive(id) || (id == "codex" ? HasCodexConnection() : id == "eleven" ? !String.IsNullOrWhiteSpace(secret) : IsExtraReady(id)));
            StackPanel codexCard;
            if (serviceCards.TryGetValue("codex",out codexCard)) codexCard.Visibility = visible("codex") ? Visibility.Visible : Visibility.Collapsed;
            if (elevenCard != null) elevenCard.Visibility = visible("eleven") ? Visibility.Visible : Visibility.Collapsed;
            foreach (var pair in extraCards)
                pair.Value.Visibility = visible(pair.Key) && (demo ? IsExtraShown(pair.Key) : true) ? Visibility.Visible : Visibility.Collapsed;
            ApplyServiceLayout();
            if (refreshServiceNavigation != null) refreshServiceNavigation();
        }
        private void SetExtraShown(string id, bool shown)
        {
            if (id == "openai") extras.ShowOpenAi = shown;
            else if (id == "claude") extras.ShowClaude = shown;
            else if (id == "gemini") extras.ShowGemini = shown;
            else if (id == "cursor") extras.ShowCursor = shown;
        }

        private async Task ReadOpenAi()
        {
            if (!IsServiceActive("openai") || !IsExtraShown("openai") || String.IsNullOrWhiteSpace(openAiKey)) { RenderOpenAi(); return; }
            openAiView.Busy = true; RenderOpenAi();
            string owner = ServiceReadingOwner;
            try {
                var snapshot = await openAi.ReadAsync(openAiKey, extras.OpenAiMonthlyBudgetUsd);
                if (!CanApplyServiceReading("openai",owner)) return;
                openAiView.Snapshot = snapshot;
                openAiView.LastSuccess = DateTimeOffset.Now; openAiView.Error = null; openAiView.Issue = null;
                RefreshSucceeded(openAiView); ObserveUsage(openAiView,"api");
            } catch (Exception ex) { if (CanApplyServiceReading("openai",owner)) { RefreshFailed(openAiView,ex); openAiView.Issue = ConnectionDiagnostics.For("openai",ex); openAiView.Error = openAiView.Issue.Title; } }
            finally { openAiView.Busy = false; openAiView.BackgroundRefresh = false; if (!exiting) RenderOpenAi(); }
        }

        private void EmptyExtra(ProviderView view, string badge, string message)
        {
            if (view.Body == null) return;
            view.Badge.Text = badge; view.BadgeMark.Active = false; view.Badge.ToolTip = message;
            view.Badge.Visibility = Visibility.Visible;
            UpdateProviderHeaderDetails(view,message);
            view.Body.ToolTip = message; view.Body.Children.Clear();
            PruneUsageVisuals(view,new HashSet<string>());
            view.Body.Children.Add(T(message,11,"#647070",FontWeights.Normal));
            UpdateProviderFold(view);
        }
        private void RenderOpenAi()
        {
            if (openAiView.Body == null) return;
            Render(openAiView, false);
            if (openAiView.Snapshot == null && String.IsNullOrWhiteSpace(openAiKey) && !showingExtraDemo)
                EmptyExtra(openAiView,"연결 전","Admin API 키 연결 후 월 사용 금액을 표시합니다.");
            else if (openAiView.Snapshot == null && showingExtraDemo)
                EmptyExtra(openAiView,"미리보기","실제 API를 호출하지 않습니다. 정식 실행에서 연결하세요.");
        }

        private void RefreshLocalExtras()
        {
            RenderHiggsfield();
            RenderHiggsfieldApi();
            if (showingExtraDemo) {
                RenderOpenAi();
                if (extras.ClaudeManual.ObservedAt.HasValue) RenderManual(claudeView,extras.ClaudeManual,"Claude 사용량을 수동 기록하세요."); else Render(claudeView,false);
                RenderManual(geminiView,extras.GeminiManual,"AI Studio에서 모델별 한도를 확인하고 기록하세요.");
                RenderManual(cursorView,extras.CursorManual,"개인 구독 사용량 페이지를 확인하고 기록하세요.");
                UpdateServiceVisibility();
                return;
            }
            RenderOpenAi();
            if (IsServiceActive("claude") && IsExtraShown("claude")) { if (!demo) claudeConfigured = IsSavedClaudeConfigured(); RenderClaude(); }
            if (IsServiceActive("gemini") && IsExtraShown("gemini")) RenderManual(geminiView,extras.GeminiManual,"AI Studio에서 모델별 한도를 확인하고 기록하세요.");
            if (IsServiceActive("cursor") && IsExtraShown("cursor")) RenderManual(cursorView,extras.CursorManual,"개인 구독 사용량 페이지를 확인하고 기록하세요.");
            UpdateServiceVisibility();
        }

        private void RenderClaude()
        {
            if (!IsServiceActive("claude")) return;
            ClaudeUsageResult result = null;
            string reason = null;
            if (!demo && claudeConfigured) { try { result = ClaudeUsageService.ReadLatest(settings.ClaudeWslDistribution,settings.ClaudeWindowsConfigDirectory); } catch (Exception ex) { reason = Friendly(ex); } }
            if (claudeQueryRejectsReceipt) result = null;
            // Invalidate automatic alerts even when the card falls back to a
            // saved manual reading. Manual schedules and deduplication survive.
            if (result != null && !result.HasQuotaData && !result.HasExpiredWindows) {
                string source = ClaudeUsageSource();
                var journal = ProFeaturesAvailable ? usageJournal : null;
                if (claudeUnavailableSource != source || claudeUnavailableAt != result.ObservedAt || !Object.ReferenceEquals(claudeUnavailableJournal,journal)) {
                    ForgetAutomaticUsageService("claude");
                    claudeUnavailableSource = source; claudeUnavailableAt = result.ObservedAt; claudeUnavailableJournal = journal;
                }
            }
            var manual = extras.ClaudeManual;
            if (manual.RemainingPercent.HasValue && manual.ObservedAt.HasValue && (result == null || !result.Snapshot.Rows.Any(row => row.RemainingPercent.HasValue) || manual.ObservedAt.Value >= result.ObservedAt)) {
                RenderManual(claudeView,manual,"Claude 사용량 페이지를 확인하고 기록하세요."); return;
            }
            if (result == null) {
                if (claudeQueryError != null) {
                    claudeView.Source = "cli"; claudeView.Snapshot = null; claudeView.LastSuccess = null;
                    claudeView.ReceiptState = ProviderReceiptState.None; claudeView.ReceiptObservedAt = null; claudeView.ReceiptMessage = null;
                    ApplyClaudeQueryError(); Render(claudeView, false); AddRetryStatus(claudeView); return;
                }
                claudeView.Source = "code";
                claudeView.Snapshot = null; claudeView.LastSuccess = null; claudeView.Error = null; claudeView.Issue = null;
                claudeView.ReceiptState = ProviderReceiptState.Waiting; claudeView.ReceiptObservedAt = null;
                claudeView.ReceiptMessage = reason;
                EmptyExtra(claudeView,"연결 대기","Claude Code를 연결하거나 사용량을 수동 기록하세요.");
                if (!String.IsNullOrEmpty(reason)) claudeView.Badge.ToolTip = reason;
                return;
            }
            if (!result.HasQuotaData && !result.HasExpiredWindows) {
                claudeView.Source = "code";
                claudeView.Snapshot = null; claudeView.LastSuccess = null; claudeView.Error = null; claudeView.Issue = null;
                claudeView.ReceiptState = ProviderReceiptState.QuotaUnavailable; claudeView.ReceiptObservedAt = result.ObservedAt;
                claudeView.ReceiptMessage = "Claude Code 데이터는 도착했지만 구독 한도는 없습니다. 연결 · 설정에서 지원 범위와 수동 기록을 확인하세요.";
                EmptyExtra(claudeView,"수신됨 · 한도 미제공","Claude Code 데이터는 도착했지만 구독 한도는 없습니다. 연결 · 설정에서 지원 범위와 수동 기록을 확인하세요.");
                return;
            }
            RenderClaudeRecord(result);
        }

        private void RenderClaudeRecord(ClaudeUsageResult result)
        {
            claudeUnavailableAt = null; claudeUnavailableSource = null; claudeUnavailableJournal = null;
            claudeView.Source = result.IsDirectQuery ? "cli" : "code";
            claudeView.Snapshot = result.Snapshot; claudeView.LastSuccess = result.ObservedAt; claudeView.Error = null; claudeView.Issue = null;
            ApplyClaudeQueryError();
            claudeView.ReceiptState = result.HasExpiredWindows ? ProviderReceiptState.ResetWaiting : ProviderReceiptState.Received;
            claudeView.ReceiptObservedAt = result.ObservedAt; claudeView.ReceiptMessage = null;
            if (claudeConfigured && claudeQueryError == null) ObserveUsage(claudeView,ClaudeUsageSource());
            Render(claudeView,false);
            var state = UsagePresentation.Describe(claudeView,DateTimeOffset.Now);
            bool old = state.Stale;
            claudeView.Badge.Text = !claudeConfigured ? "연결 해제" : state.Label;
            claudeView.Badge.Visibility = !claudeConfigured || state.Warning ? Visibility.Visible : Visibility.Collapsed;
            claudeView.Body.Opacity = 1;
            claudeView.Body.Children.Add(T((result.IsDirectQuery ? "마지막 확인 " : "마지막 수신 ") + result.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm"),10,"#647070",FontWeights.Normal,new Thickness(0,5,0,0)));
            if (!claudeConfigured) claudeView.Body.Children.Add(T("연결 해제 · 마지막 수신 기록입니다.",11,"#91501F",FontWeights.Normal));
            else if (old) claudeView.Body.Children.Add(T(result.IsDirectQuery ? "새로고침하여 현재 잔여량을 확인하세요." : "Claude Code에서 새 응답 후 갱신됩니다.",10,"#91501F",FontWeights.Normal));
            AddRetryStatus(claudeView);
        }

        private void RenderManual(ProviderView view, ManualUsageRecord record, string empty)
        {
            view.Source = "manual";
            view.Error = null; view.Issue = null; view.Busy = false; view.Body.Opacity = 1;
            view.ReceiptState = ProviderReceiptState.None; view.ReceiptObservedAt = null; view.ReceiptMessage = null;
            if (record == null || !record.RemainingPercent.HasValue || !record.ObservedAt.HasValue) {
                view.Snapshot = null; view.LastSuccess = null; EmptyExtra(view,"수동 확인",empty); return;
            }
            view.LastSuccess = record.ObservedAt;
            view.Snapshot = new UsageSnapshot { Plan = "수동", Note = "사용자가 입력한 기록 · 자동 조회 값이 아닙니다." };
            view.Snapshot.Rows.Add(new UsageRow { Label = record.Label, RemainingPercent = record.RemainingPercent, ResetsAt = record.ResetsAt, Detail = "수동 기록: " + record.ObservedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") });
            if (!demo) ObserveUsage(view,"manual");
            Render(view,false);
            bool expired = record.ResetsAt.HasValue && record.ResetsAt.Value <= DateTimeOffset.Now;
            view.Badge.Text = expired ? "수동 · 기한 경과" : "수동";
            view.Body.Opacity = 1;
            view.Body.Children.Add(T("기록 " + record.ObservedAt.Value.ToLocalTime().ToString("MM/dd HH:mm") + " · 자동 갱신 안 됨",10,"#647070",FontWeights.Normal,new Thickness(0,5,0,0)));
        }

        private void OpenServices(string initialId)
        {
            var ids = new[] { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };
            if (servicesWindow != null) { servicesSelector.SelectedIndex = Math.Max(0,Array.IndexOf(ids,initialId)); RefreshConnectionEditor(ids[servicesSelector.SelectedIndex]); servicesWindow.Activate(); return; }
            var dialog = CreateManagementDialog("AI 서비스",780,740,720); servicesWindow = dialog;
            var root = new DockPanel { Margin = new Thickness(24) };
            var heading = new LocalizedStackPanel { Margin = new Thickness(0,0,0,18) }; DockPanel.SetDock(heading,Dock.Top); root.Children.Add(heading);
            heading.Children.Add(ConnectionText("NEPHYLO",10,"#647070",FontWeights.SemiBold,new Thickness(0,0,0,8)));
            heading.Children.Add(ConnectionText("AI 서비스",23,"#252827",FontWeights.SemiBold));
            heading.Children.Add(ConnectionText("연결 상태를 확인하고 위젯에 표시할 서비스를 관리하세요.",12,"#647070",FontWeights.Normal,new Thickness(0,7,0,0)));
            var close = ManagementAction("닫기",() => dialog.Close());
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            var footer = new DockPanel(); DockPanel.SetDock(close,Dock.Right); footer.Children.Add(close);
            footer.Children.Add(MakeDiagnosticsControls(() => ids[Math.Max(0,Math.Min(ids.Length - 1,servicesSelector == null ? 0 : servicesSelector.SelectedIndex))]));
            var bottom = new LocalizedBorder { Child = footer, BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(0,14,0,0), Margin = new Thickness(0,16,0,0) };
            DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
            var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) }); columns.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(columns); dialog.Content = root;
            var selector = new ListBox { Background = System.Windows.Media.Brushes.Transparent, Foreground = Fore, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            selector.Resources = ServiceNavigationResources(); servicesSelector = selector;
            ScrollViewer.SetHorizontalScrollBarVisibility(selector,ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(selector,ScrollBarVisibility.Auto);
            columns.Children.Add(selector);
            var names = new[] { "Codex", "ElevenLabs", "OpenAI API", "Claude 개인 구독", "Gemini API", "Cursor 개인 구독", "Higgsfield 웹 크레딧", "Higgsfield API · 수동 잔액" };
            var navigationNames = new[] { "Codex", "ElevenLabs", "OpenAI API", "Claude", "Gemini API", "Cursor", "Higgsfield 웹", "Higgsfield API" };
            var statuses = new Dictionary<string,TextBlock>();
            var details = new Dictionary<string,TextBlock>();
            for (int index = 0; index < ids.Length; index++) {
                string id = ids[index]; var content = new LocalizedStackPanel();
                var nameRow = new Grid(); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); nameRow.ColumnDefinitions.Add(new ColumnDefinition());
                nameRow.Children.Add(new Image { Source = ServiceIcons.Get(id), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0,1,0,0) });
                var name = ConnectionText(navigationNames[index],14,"#252827",FontWeights.SemiBold,new Thickness(8,0,0,0));
                name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name,1); nameRow.Children.Add(name); content.Children.Add(nameRow);
                var status = ConnectionText("",10,"#647070",FontWeights.Normal,new Thickness(26,2,0,0));
                status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis;
                content.Children.Add(status); statuses[id] = status;
                var item = new ListBoxItem { Content = content, Tag = id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                var accessibleName = ConnectionText(names[index],12,"#252827",FontWeights.Normal);
                var tooltip = new LocalizedTextBlock { FontSize = 13, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
                details[id] = tooltip; item.ToolTip = tooltip;
                System.Windows.Data.BindingOperations.SetBinding(item,System.Windows.Automation.AutomationProperties.NameProperty,new System.Windows.Data.Binding("Text") { Source = accessibleName });
                System.Windows.Data.BindingOperations.SetBinding(item,System.Windows.Automation.AutomationProperties.ItemStatusProperty,new System.Windows.Data.Binding("Text") { Source = tooltip });
                selector.Items.Add(item);
            }
            System.Windows.Automation.AutomationProperties.SetName(selector,L.Text("관리할 AI 서비스"));
            var detailRoot = new DockPanel { Margin = new Thickness(20,0,0,0) }; Grid.SetColumn(detailRoot,1); columns.Children.Add(detailRoot);
            var top = new LocalizedStackPanel(); DockPanel.SetDock(top,Dock.Top); detailRoot.Children.Add(top);
            var selectedTitle = ConnectionText("",18,"#252827",FontWeights.SemiBold); top.Children.Add(selectedTitle);
            var selectedMode = ConnectionText("",11,"#647070",FontWeights.Normal,new Thickness(0,4,0,12)); top.Children.Add(selectedMode);
            BuildServicePlanControls(top,() => ids[Math.Max(0,selector.SelectedIndex)]);
            var body = new LocalizedStackPanel();
            var editorScroll = new ScrollViewer { Content = body, Padding = new Thickness(0,0,7,0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            detailRoot.Children.Add(editorScroll);
            // Keep each service editor alive while switching, preserving unsaved input.
            var editors = new Dictionary<string, StackPanel>();
            var orderPanels = new Dictionary<string,Expander>();
            refreshServiceNavigation = () => {
                foreach (string id in ids) {
                    string status = ServiceConnectionStatus(id);
                    statuses[id].Text = status;
                    details[id].Text = names[Array.IndexOf(ids,id)] + "\n" + ServiceConnectionMode(id) + " · " + status;
                }
                bool canArrange = ids.Count(id => IsServiceActive(id) && ServiceHasSavedConnection(id)) > 1;
                foreach (var panel in orderPanels.Values) panel.Visibility = canArrange ? Visibility.Visible : Visibility.Collapsed;
            };
            selector.SelectionChanged += (s,e) => {
                if (e.OriginalSource != selector || selector.SelectedIndex < 0) return;
                if (refreshPlanControls != null) refreshPlanControls();
                body.Children.Clear(); var id = ids[selector.SelectedIndex];
                selectedTitle.Text = names[selector.SelectedIndex]; selectedMode.Text = ServiceConnectionMode(id); editorScroll.ScrollToTop();
                StackPanel editor;
                if (editors.TryGetValue(id,out editor)) { body.Children.Add(editor); RefreshConnectionEditor(id); return; }
                Expander retainedOrder;
                if (RestoreConnectionEditor(id,out editor,out retainedOrder)) {
                    editors[id] = editor; body.Children.Add(editor);
                    if (retainedOrder != null) orderPanels[id] = retainedOrder;
                    RefreshConnectionEditor(id); refreshServiceNavigation(); return;
                }
                editor = new LocalizedStackPanel { Tag = id }; editors[id] = editor; body.Children.Add(editor);
                if (id == "codex") BuildCodexSettings(editor);
                else if (id == "eleven") BuildElevenSettings(editor);
                else if (id == "higgsfield") BuildHiggsfieldSettings(editor);
                else if (id == "higgsfield-api") BuildHiggsfieldApiSettings(editor);
                else {
                    var shown = new LocalizedCheckBox { Content = "미리보기에서 표시", IsChecked = IsExtraShown(id), Visibility = demo ? Visibility.Visible : Visibility.Collapsed }; editor.Children.Add(shown);
                    var toggleError = ConnectionText("",11,"#91501F",FontWeights.Normal); editor.Children.Add(toggleError);
                    shown.Click += async (cs,ce) => {
                        bool prior = IsExtraShown(id); SetExtraShown(id,shown.IsChecked == true);
                        try { if (!demo) ExtraSettingsStore.Save(extras); toggleError.Text = ""; RefreshLocalExtras(); if (id == "openai" && IsExtraShown(id) && !demo) await RefreshAsync(); }
                        catch { SetExtraShown(id,prior); shown.IsChecked = prior; toggleError.Text = "표시 설정을 저장하지 못했습니다."; }
                    };
                    if (id == "openai") BuildOpenAiSettings(editor);
                    else {
                        if (id == "claude") BuildClaudeSettings(editor);
                        else ConnectionSteps(editor,"수동 기록 · API 키 입력 불필요","아래 ‘사용량 페이지’에서 남은 비율을 확인하세요.","기준 이름과 남은 비율을 입력한 뒤 저장하세요.","이 서비스는 자동 조회하지 않습니다. 값이 바뀌면 다시 기록하세요.");
                        var links = new WrapPanel(); links.Children.Add(SmallAction("사용량 페이지 ↗",() => OpenUsagePage(id)));
                        if (id == "gemini") { links.Children.Add(SmallAction("모델별 한도 ↗",() => OpenOfficialPage("https://aistudio.google.com/rate-limit"))); links.Children.Add(SmallAction("결제 잔액 ↗",() => OpenOfficialPage("https://aistudio.google.com/billing"))); }
                        editor.Children.Add(links);
                        if (id == "claude") { var manual = new LocalizedStackPanel(); BuildManualEditor(manual,id); Details(editor,"수동 기록 · Free·API 키·웹 사용자",manual); }
                        else BuildManualEditor(editor,id);
                    }
                }
                var order = new LocalizedStackPanel(); BuildOrderControls(order,id);
                orderPanels[id] = Details(editor,"위젯 표시 순서",order);
                RetainConnectionEditor(id,editor,orderPanels[id]);
                refreshServiceNavigation();
            };
            selector.SelectedIndex = Math.Max(0,Array.IndexOf(ids,initialId));
            // Keep every service visible when a short work area leaves less room for the list.
            selector.SizeChanged += (s,e) => {
                bool compact = e.NewSize.Height < ids.Length * 58;
                foreach (ListBoxItem item in selector.Items) {
                    item.Padding = new Thickness(9,compact ? 2 : 6,9,compact ? 2 : 6);
                    statuses[(string)item.Tag].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                }
            };
            // On narrow/high-DPI work areas, place a compact selector above the editor instead of clipping it.
            Action adaptLayout = () => {
                bool narrow = root.ActualWidth > 0 ? root.ActualWidth < 632 : dialog.Width < 680;
                columns.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(206);
                selector.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                detailRoot.Margin = new Thickness(narrow ? 0 : 20,0,0,0);
            };
            var compactSelector = new LocalizedComboBox { FontSize = 14, MinHeight = 38, Margin = new Thickness(0,0,0,12), ItemTemplate = ServiceIcons.ChoiceTemplate(), Visibility = Visibility.Collapsed };
            for (int index = 0; index < ids.Length; index++) compactSelector.Items.Add(new ServiceChoice(ids[index],names[index]));
            compactSelector.SelectedIndex = selector.SelectedIndex;
            System.Windows.Automation.AutomationProperties.SetName(compactSelector,"관리할 AI 서비스");
            top.Children.Insert(0,compactSelector);
            compactSelector.SelectionChanged += (s,e) => { if (e.OriginalSource == compactSelector) selector.SelectedIndex = compactSelector.SelectedIndex; };
            selector.SelectionChanged += (s,e) => { if (e.OriginalSource == selector) compactSelector.SelectedIndex = selector.SelectedIndex; };
            root.SizeChanged += (s,e) => { adaptLayout(); compactSelector.Visibility = root.ActualWidth < 632 ? Visibility.Visible : Visibility.Collapsed; };
            dialog.Closed += (s,e) => { DetachServiceEditors(editors,body); servicesWindow = null; servicesSelector = null; refreshPlanControls = null; refreshServiceNavigation = null; UpdateFooter(); };
            dialog.Show();
        }

        private bool ServiceHasSavedConnection(string id)
        {
            return demo || (id == "codex" ? HasCodexConnection() : id == "eleven" ? !String.IsNullOrWhiteSpace(secret) : IsExtraReady(id));
        }

        private string ServiceConnectionMode(string id)
        {
            if (id == "gemini" || id == "cursor" || id == "higgsfield-api") return "수동 기록";
            if (id == "claude") return "직접 조회 · 응답 수신 · 수동 기록";
            if (id == "higgsfield") return "웹 구독 동기화";
            return "자동 조회";
        }

        private string ServiceConnectionStatus(string id)
        {
            if (demo) return "예시 데이터";
            var view = ViewFor(id);
            if (view.Issue != null || !String.IsNullOrEmpty(view.Error)) return "연결 확인 필요";
            if (!ServiceHasSavedConnection(id)) return id == "gemini" || id == "cursor" || id == "higgsfield-api" ? "기록 없음" : "연결 전";
            if (id == "gemini" || id == "cursor" || id == "higgsfield-api" || id == "claude" && !claudeConfigured) return "기록 있음";
            if (view.LastSuccess.HasValue) return "연결됨";
            return id == "codex" ? "로그인 발견" : id == "claude" ? "조회 대기" : id == "higgsfield" ? "로그인 저장됨" : "키 저장됨";
        }

        private static ResourceDictionary ServiceNavigationResources()
        {
            return WidgetPalette.ParseResources(@"
                <ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                    <Style TargetType='ListBoxItem'>
                        <Setter Property='Padding' Value='9,6'/><Setter Property='Margin' Value='0,0,4,2'/>
                        <Setter Property='Background' Value='Transparent'/><Setter Property='BorderBrush' Value='Transparent'/>
                        <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                        <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
                            <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Stretch'/></Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F2F3EF'/></Trigger>
                                <Trigger Property='IsSelected' Value='True'><Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/><Setter TargetName='Surface' Property='BorderBrush' Value='#C8CECF'/></Trigger>
                                <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#2448F5'/></Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate></Setter.Value></Setter>
                    </Style>
                </ResourceDictionary>");
        }

        private bool claudeConnectionBusy;
        private const string ClaudeQuotaUnavailable = "Claude Code 데이터는 도착했지만 5시간·주간 한도는 포함되지 않았습니다. 이 한도는 Claude Pro·Max에서 제공됩니다. Free·API 키 사용자는 아래 수동 기록을 이용하세요. 첫 응답 전에도 한도가 없을 수 있습니다.";

        private bool IsSavedClaudeConfigured()
        {
            return IsClaudeConfigured(Assembly.GetExecutingAssembly().Location,settings.ClaudeWslDistribution,settings.ClaudeWindowsConfigDirectory,settings.ClaudeUseExistingStatusLine);
        }

        private static bool IsClaudeConfigured(string exe, string distribution, string configDirectory, bool useExisting)
        {
            return ClaudeUsageService.IsStatusLineInstalled(exe,distribution,configDirectory)
                || useExisting && ClaudeUsageService.HasCustomStatusLine(exe,distribution,configDirectory);
        }

        private bool IsSavedClaudeTarget(string distribution, string configDirectory)
        {
            if (!String.Equals(distribution,settings.ClaudeWslDistribution,StringComparison.Ordinal)) return false;
            if (!String.IsNullOrEmpty(distribution)) return true;
            try { return String.Equals(ClaudeUsageService.ResolveWindowsConfigDirectory(configDirectory),ClaudeUsageService.ResolveWindowsConfigDirectory(settings.ClaudeWindowsConfigDirectory),StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { return false; }
        }

        private void SaveClaudeEnvironment(string distribution, string configDirectory, bool useExisting)
        {
            string previous = settings.ClaudeWslDistribution, previousDirectory = settings.ClaudeWindowsConfigDirectory;
            bool previousExisting = settings.ClaudeUseExistingStatusLine;
            if (String.IsNullOrEmpty(distribution) && (!String.IsNullOrWhiteSpace(configDirectory) || !String.IsNullOrEmpty(ClaudeUsageService.GetWindowsSourceId(configDirectory))))
                configDirectory = ClaudeUsageService.ResolveWindowsConfigDirectory(configDirectory);
            if (previous == distribution && previousDirectory == configDirectory && previousExisting == useExisting) return;
            settings.ClaudeWslDistribution = distribution;
            settings.ClaudeWindowsConfigDirectory = configDirectory;
            settings.ClaudeUseExistingStatusLine = useExisting;
            try { SettingsStore.Save(settings); }
            catch { settings.ClaudeWslDistribution = previous; settings.ClaudeWindowsConfigDirectory = previousDirectory; settings.ClaudeUseExistingStatusLine = previousExisting; throw; }
            ForgetUsageService("claude");
            claudeQueryError = null; claudeQueryRejectsReceipt = false;
            claudeView.Snapshot = null; claudeView.LastSuccess = null; claudeView.Error = null; claudeView.Issue = null;
            claudeView.ReceiptState = ProviderReceiptState.None; claudeView.ReceiptObservedAt = null; claudeView.ReceiptMessage = null;
        }

        private void BuildClaudeSettings(StackPanel body)
        {
            var support = new LocalizedStackPanel();
            support.Children.Add(ConnectionText("자동 잔량 연동 · Claude Pro·Max",14,"#252827",FontWeights.SemiBold));
            support.Children.Add(ConnectionText("Claude Free는 자동 잔량 연동을 지원하지 않습니다. Free·API 키·웹 사용자는 아래 수동 기록을 이용해 주세요.",11,"#566369",FontWeights.Normal,new Thickness(0,5,0,0)));
            body.Children.Add(new LocalizedBorder { Child = support, Background = BrushOf("#F2F3EF"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0,0,0,10) });
            ConnectionSteps(body,"Claude Code 연결 · API 키 불필요","Claude Code에서 Pro 또는 Max 계정으로 로그인하세요.","‘Claude Code 연결’을 눌러 선택한 환경을 연결하세요.","Claude Code 2.1.304 이상은 대화 없이 ‘잔여량 조회’와 위젯 새로고침으로 확인할 수 있습니다.");
            var environment = AddConnectionEnvironment(body,"claude",settings.ClaudeWslDistribution);
            var actions = new WrapPanel();
            var connect = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "Claude Code 연결", Margin = new Thickness(0,0,8,8) };
            var check = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "잔여량 조회", Margin = new Thickness(0,0,0,8) };
            var disconnect = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "위젯 연결 해제", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0) };
            actions.Children.Add(connect); actions.Children.Add(check); body.Children.Add(actions);
            var feedback = MakeConnectionFeedback(body);
            string exe = Assembly.GetExecutingAssembly().Location;
            var advanced = new LocalizedStackPanel();
            advanced.Children.Add(ConnectionText("Windows Claude 설정 폴더",14,"#647070",FontWeights.SemiBold));
            var configDirectory = new LocalizedTextBox { Text = settings.ClaudeWindowsConfigDirectory, MaxLength = 32767 };
            System.Windows.Automation.AutomationProperties.SetName(configDirectory,"Windows Claude 설정 폴더");
            advanced.Children.Add(configDirectory);
            advanced.Children.Add(ConnectionText("비워 두면 CLAUDE_CONFIG_DIR 또는 기본 .claude 폴더를 사용합니다. 설정할 폴더 안의 settings.json을 변경합니다.",11,"#647070",FontWeights.Normal,new Thickness(0,6,0,6)));
            var browseDirectory = SmallAction("설정 폴더 찾기",() => {
                using (var picker = new System.Windows.Forms.FolderBrowserDialog()) {
                    picker.ShowNewFolderButton = false;
                    if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) configDirectory.Text = picker.SelectedPath;
                }
            }); advanced.Children.Add(browseDirectory);
            var existing = new LocalizedCheckBox { Content = "기존 상태 표시줄에서 수신", IsChecked = settings.ClaudeUseExistingStatusLine, Margin = new Thickness(0,12,0,8) };
            advanced.Children.Add(existing);
            advanced.Children.Add(ConnectionText("기존 상태 표시줄을 유지하려면 체크한 뒤 ‘잔여량 조회’를 누르세요. 아래 명령으로 같은 JSON 입력을 전달하면 기존 응답 수신도 함께 사용할 수 있습니다.",11,"#647070",FontWeights.Normal));
            var copyCommand = SmallAction("수신 명령 복사",async () => {
                if (demo || claudeConnectionBusy) return;
                try {
                    string command = await ClaudeUsageService.GetStatusLineCommandAsync(exe,environment.Distribution,configDirectory.Text.Trim());
                    if (exiting) return;
                    Clipboard.SetText(command);
                    Feedback(feedback,"수신 명령을 복사했습니다","기존 스크립트에서 같은 JSON 입력을 이 명령에 전달하면 응답 수신도 함께 사용할 수 있습니다.",false);
                } catch (Exception ex) { if (!exiting) FeedbackIssue(feedback,"claude",ex); }
            }); advanced.Children.Add(copyCommand);
            Details(body,"고급 연결 · 설정 폴더 · 기존 상태 표시줄",advanced);
            Action update = () => {
                if (claudeConnectionBusy) return;
                configDirectory.IsEnabled = browseDirectory.IsEnabled = environment.Distribution.Length == 0;
                connect.IsEnabled = existing.IsChecked != true;
                if (demo) { Feedback(feedback,"미리보기","실제 Claude 설정을 읽거나 변경하지 않습니다.",false); return; }
                if (!RequireServiceConnection("claude",feedback)) return;
                string target = environment.Distribution;
                string directory = configDirectory.Text.Trim(); bool useExisting = existing.IsChecked == true;
                bool configured = IsClaudeConfigured(exe,target,directory,useExisting);
                if (IsSavedClaudeTarget(target,directory) && useExisting == settings.ClaudeUseExistingStatusLine) claudeConfigured = configured;
                string status = ClaudeUsageService.StatusLineStatus(exe,target,directory);
                connect.Content = status.Contains("복구") ? "Claude Code 연결 복구" : "Claude Code 연결";
                if (!configured) {
                    if (status.Contains("복구")) Feedback(feedback,"위젯 위치가 변경되었습니다","이전 위젯 연결을 찾았습니다. ‘Claude Code 연결 복구’를 누르면 현재 위치로 연결하고 이전 설정을 백업합니다.",false);
                    else if (status.Contains("기존")) Feedback(feedback,"기존 상태 표시줄 사용 중","고급 연결에서 수신 명령을 기존 스크립트에 연결하고 ‘기존 상태 표시줄에서 수신’을 선택하세요. 수동 기록도 사용할 수 있습니다.",false);
                    else Feedback(feedback,"연결 전",status + "\n위의 연결 버튼을 누르면 사용량 수신을 설정합니다.",false);
                    return;
                }
                try {
                    var result = ClaudeUsageService.ReadLatest(target,directory);
                    bool expired = result.HasExpiredWindows || DateTimeOffset.Now - result.ObservedAt > TimeSpan.FromMinutes(result.IsDirectQuery ? 10 : 30);
                    bool candidate = !IsSavedClaudeTarget(target,directory) || useExisting != settings.ClaudeUseExistingStatusLine;
                    Feedback(feedback,expired ? "이전 값 · 새로고침 필요" : !result.HasQuotaData ? "수신됨 · 한도 미제공" : candidate ? "잔여량 조회 후 연결을 저장하세요" : result.IsDirectQuery ? "잔여량 조회 완료" : "사용량 수신 완료",
                        !expired && !result.HasQuotaData ? ClaudeQuotaUnavailable : (result.IsDirectQuery ? "마지막 확인 " : "마지막 수신 ") + result.ObservedAt.ToLocalTime().ToString("MM/dd HH:mm") + (expired ? "\n‘잔여량 조회’로 현재 한도를 확인하세요." : candidate ? "\n‘잔여량 조회’를 누르면 선택한 환경을 저장합니다." : result.IsDirectQuery ? "\n새로고침으로 대화 없이 잔여량을 갱신합니다." : "\n기존 응답 수신도 유지하며 새로고침으로 직접 조회할 수 있습니다."),expired);
                } catch (ConnectionException ex) {
                    FeedbackIssue(feedback,"claude",ex);
                } catch {
                    Feedback(feedback,"연결 설정됨 · 조회 대기","‘잔여량 조회’로 대화 없이 현재 한도를 확인하세요. Claude Code 2.1.304 이상과 구독 로그인이 필요합니다.",false);
                }
            };
            Action<bool> busy = value => {
                claudeConnectionBusy = value; environment.SetEnabled(!value);
                connect.IsEnabled = !value && existing.IsChecked != true;
                check.IsEnabled = disconnect.IsEnabled = existing.IsEnabled = copyCommand.IsEnabled = !value;
                configDirectory.IsEnabled = browseDirectory.IsEnabled = !value && environment.Distribution.Length == 0;
            };
            Func<bool> canStart = () => {
                if (!RequireServiceConnection("claude",feedback)) return false;
                if (claudeConnectionBusy || IsServiceConnectionPending("claude")) { Feedback(feedback,"조회 진행 중","조회 또는 로그인을 마친 뒤 전환해 주세요.",false); return false; }
                return true;
            };
            environment.Changed = update;
            configDirectory.TextChanged += (s,e) => update();
            existing.Checked += (s,e) => update(); existing.Unchecked += (s,e) => update();
            connectionStateRefreshers["claude"] = update; update();
            connect.Click += async (s,e) => {
                if (!canStart()) return;
                if (demo) { update(); return; }
                string target = environment.Distribution;
                string directory = configDirectory.Text.Trim();
                IDisposable lease;
                if (!TryReserveServiceConnection("claude",out lease)) return;
                busy(true); Feedback(feedback,"연결 확인 중…","선택한 환경의 Claude Code 연결을 설정합니다.",false);
                try {
                    await WaitForServiceRefreshAsync("claude",ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged("claude",lease);
                    if (String.IsNullOrEmpty(target)) await ClaudeUsageService.VerifyWindowsBridgeAsync(exe,directory,ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged("claude",lease);
                    await ClaudeUsageService.InstallStatusLineAsync(exe,target,directory);
                    if (exiting) return;
                    ThrowIfServiceConnectionChanged("claude",lease);
                    SaveClaudeEnvironment(target,directory,false);
                    ForgetUsageService("claude");
                    configDirectory.Text = settings.ClaudeWindowsConfigDirectory;
                    claudeConfigured = true;
                    RefreshLocalExtras(); busy(false); update();
                } catch (Exception ex) { if (!exiting) FeedbackIssue(feedback,"claude",ex); }
                finally { lease.Dispose(); busy(false); }
            };
            check.Click += async (s,e) => {
                if (!canStart()) return;
                if (demo) { update(); return; }
                string target = environment.Distribution;
                string directory = configDirectory.Text.Trim(); bool useExisting = existing.IsChecked == true;
                IDisposable lease;
                if (!TryReserveServiceConnection("claude",out lease)) return;
                busy(true);
                Feedback(feedback,"잔여량 조회 중…","질문을 보내지 않고 Claude 구독 한도를 확인합니다.",false);
                try {
                    await WaitForServiceRefreshAsync("claude",ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged("claude",lease);
                    await ClaudeUsageService.RefreshStatusLineAsync(exe,target,directory);
                    if (exiting) return;
                    ThrowIfServiceConnectionChanged("claude",lease);
                    if (!IsClaudeConfigured(exe,target,directory,useExisting)) { busy(false); update(); return; }
                    var received = await claudeQuota.ReadAsync(target,directory,ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged("claude",lease);
                    SaveClaudeEnvironment(target,directory,useExisting);
                    string source = String.IsNullOrEmpty(target) ? ClaudeUsageService.GetWindowsSourceId(directory) : ClaudeUsageService.GetSourceId(target);
                    if (!ClaudeUsageService.CaptureJson(received.QuotaJson,received.ObservedAt,source,received.AccountFingerprint)) throw new InvalidOperationException("Claude 사용량 기록이 사용 중입니다. 잠시 후 다시 조회해 주세요.");
                    claudeQueryError = null; claudeQueryRejectsReceipt = false; RefreshSucceeded(claudeView);
                    ForgetUsageService("claude");
                    configDirectory.Text = settings.ClaudeWindowsConfigDirectory;
                    claudeConfigured = true;
                    if (received.HasQuotaData && received.Snapshot.Rows.Any(row => row.RemainingPercent.HasValue) && !IsServiceActive("claude")) CommitVerifiedServiceSelection("claude");
                    RefreshLocalExtras(); busy(false); update();
                } catch (ConnectionException ex) { if (!exiting) FeedbackIssue(feedback,"claude",ex); }
                catch (Exception ex) { if (!exiting) Feedback(feedback,"사용량 수신 대기",Friendly(ex) + "\n기존 서비스 선택은 유지했습니다. 아래에서 연결을 확인해 주세요.",false); }
                finally { lease.Dispose(); busy(false); }
            };
            var links = new WrapPanel();
            links.Children.Add(SmallAction("설치 · 로그인 안내 ↗",() => OpenOfficialPage(ClaudeGuideUrl)));
            links.Children.Add(SmallAction("기존 상태 표시줄 연결 ↗",() => OpenOfficialPage("https://code.claude.com/docs/en/statusline"))); body.Children.Add(links);
            var details = new LocalizedStackPanel();
            details.Children.Add(ConnectionText("• 데이터 수신과 구독 한도 제공 여부는 다릅니다. 한도 미제공만으로 계정의 요금제를 판단하지 않습니다.\n• 프로젝트 설정이 상태 표시줄을 덮어쓰거나 실행을 막고 있는지 확인하세요.\n• Free·API 키·웹 전용 사용자는 수동 기록을 이용할 수 있습니다.",11,"#647070",FontWeights.Normal));
            details.Children.Add(ConnectionText("WSL은 배포판 기본 사용자의 ~/.claude/settings.json에 연결합니다. Windows 프로그램 실행(interop)이 허용되어 있어야 합니다. WSL의 별도 설정 폴더는 지원하지 않습니다.",11,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)));
            details.Children.Add(ConnectionText("실행 확인 성공은 Claude의 실제 데이터 수신이나 백신 허용을 보장하지 않습니다. 차단 팝업이 보이면 보안 프로그램의 진단 로그를 확인하세요. 보호 기능을 끄거나 예외를 등록하지 마세요.",11,"#647070",FontWeights.Normal,new Thickness(0,8,0,0)));
            disconnect.Click += async (s,e) => {
                if (!canStart()) return;
                string target = environment.Distribution;
                string directory = configDirectory.Text.Trim();
                IDisposable lease;
                if (!TryReserveServiceConnection("claude",out lease)) return;
                busy(true);
                try {
                    await WaitForServiceRefreshAsync("claude",ServiceConnectionsToken);
                    ThrowIfServiceConnectionChanged("claude",lease);
                    if (!demo) {
                        // A custom script belongs to the user. Disconnect only our saved
                        // acceptance of its receipts, never remove or rewrite that script.
                        if (ClaudeUsageService.IsStatusLineInstalled(exe,target,directory) || ClaudeUsageService.StatusLineStatus(exe,target,directory).Contains("복구"))
                            await ClaudeUsageService.RemoveStatusLineAsync(exe,target,directory);
                        if (exiting) return;
                        ThrowIfServiceConnectionChanged("claude",lease);
                        if (IsSavedClaudeTarget(target,directory)) {
                            SaveClaudeEnvironment(target,directory,false);
                            claudeConfigured = false; ForgetUsageService("claude");
                        }
                        existing.IsChecked = false;
                    }
                    RefreshLocalExtras();
                    Feedback(feedback,demo ? "미리보기" : "연결 해제됨",demo ? "실제 설정을 변경하지 않았습니다." : "자동 수신 연결을 해제했습니다. 보관된 사용 이력은 유지하며, 직접 입력한 기록이 없으면 위젯에서 숨깁니다.",false);
                } catch (Exception ex) { if (!exiting) FeedbackIssue(feedback,"claude",ex); }
                finally { lease.Dispose(); busy(false); }
            };
            details.Children.Add(disconnect); Details(body,"연결 문제 해결 · 연결 해제",details);
        }

        private ManualUsageRecord GetManual(string id) { return id == "claude" ? extras.ClaudeManual : id == "gemini" ? extras.GeminiManual : extras.CursorManual; }
        private void SetManual(string id, ManualUsageRecord record) { if (id == "claude") extras.ClaudeManual = record; else if (id == "gemini") extras.GeminiManual = record; else extras.CursorManual = record; }

        private void BuildManualEditor(StackPanel body, string id)
        {
            var record = GetManual(id);
            body.Children.Add(ConnectionText("수동 기록",14,"#252827",FontWeights.SemiBold,new Thickness(0,15,0,6)));
            body.Children.Add(ConnectionText("기록한 값과 시각이 표시되며 자동으로 최신화되지 않습니다.",10,"#647070",FontWeights.Normal,new Thickness(0,0,0,12)));
            body.Children.Add(ConnectionText("기준 이름",14,"#647070",FontWeights.Normal,new Thickness(0,0,0,5)));
            var label = new LocalizedTextBox { Text = record.Label, MaxLength = 80 }; System.Windows.Automation.AutomationProperties.SetName(label,id + " 기록 기준 이름"); body.Children.Add(label);
            body.Children.Add(ConnectionText(id == "gemini" ? "예: 사용 중인 모델명 / 일일 요청 한도" : "예: 5시간 한도 / 주간 한도 / 월 포함량",10,"#647070",FontWeights.Normal,new Thickness(0,4,0,10)));
            body.Children.Add(ConnectionText("남은 비율 (%)",14,"#647070",FontWeights.Normal,new Thickness(0,0,0,5)));
            var percent = new LocalizedTextBox { Text = record.RemainingPercent.HasValue ? record.RemainingPercent.Value.ToString("0.#",CultureInfo.InvariantCulture) : "" }; System.Windows.Automation.AutomationProperties.SetName(percent,id + " 남은 비율"); body.Children.Add(percent);
            body.Children.Add(ConnectionText("초기화 시각 (선택 · PC 현지 시간)",14,"#647070",FontWeights.Normal,new Thickness(0,12,0,5)));
            var reset = new LocalizedTextBox { Text = record.ResetsAt.HasValue ? record.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "" }; System.Windows.Automation.AutomationProperties.SetName(reset,id + " 초기화 시각"); body.Children.Add(reset);
            body.Children.Add(ConnectionText("형식: yyyy-MM-dd HH:mm · 비우면 초기화 시각을 표시하지 않습니다.",10,"#647070",FontWeights.Normal,new Thickness(0,4,0,12)));
            var message = ConnectionText(record.ObservedAt.HasValue ? "마지막 기록 " + record.ObservedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "아직 기록이 없습니다.",10,"#647070",FontWeights.Normal,new Thickness(0,9,0,0));
            var actions = new WrapPanel();
            var save = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "지금 확인한 값으로 저장", Margin = new Thickness(0,0,8,8) };
            var clear = new LocalizedButton { ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Content = "기록 비우기", Margin = new Thickness(0,0,0,8) };
            save.Click += (s,e) => {
                try {
                    double amount;
                    if (!Double.TryParse(percent.Text.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out amount) || amount < 0 || amount > 100) throw new InvalidOperationException("남은 비율을 0~100 사이로 입력하세요.");
                    if (String.IsNullOrWhiteSpace(label.Text)) throw new InvalidOperationException("어떤 한도인지 기준 이름을 입력하세요.");
                    DateTimeOffset? when = null;
                    if (!String.IsNullOrWhiteSpace(reset.Text)) {
                        DateTime local;
                        if (!DateTime.TryParseExact(reset.Text.Trim(),"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out local) || TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)) throw new InvalidOperationException("초기화 시각을 yyyy-MM-dd HH:mm 형식의 유효한 현지 시간으로 입력하세요.");
                        when = new DateTimeOffset(local,TimeZoneInfo.Local.GetUtcOffset(local));
                    }
                    var next = new ManualUsageRecord { Label = label.Text.Trim(), RemainingPercent = amount, ResetsAt = when, ObservedAt = DateTimeOffset.Now };
                    SaveManual(id,next); message.Text = "기록했습니다 · " + next.ObservedAt.Value.ToLocalTime().ToString("MM/dd HH:mm");
                } catch (Exception ex) { message.Text = Friendly(ex); }
            };
            clear.Click += (s,e) => { try { SaveManual(id,new ManualUsageRecord()); label.Text = ""; percent.Text = ""; reset.Text = ""; message.Text = "기록을 비웠습니다."; } catch (Exception ex) { message.Text = Friendly(ex); } };
            actions.Children.Add(save); actions.Children.Add(clear); body.Children.Add(actions); body.Children.Add(message);
        }
        private void SaveManual(string id, ManualUsageRecord next)
        {
            if (next.ObservedAt.HasValue && IsServiceConnectionPending(id)) throw new InvalidOperationException("조회 또는 로그인을 마친 뒤 전환해 주세요.");
            if (next.ObservedAt.HasValue && (String.IsNullOrWhiteSpace(next.Label) || !next.RemainingPercent.HasValue || Double.IsNaN(next.RemainingPercent.Value) || Double.IsInfinity(next.RemainingPercent.Value) || next.RemainingPercent < 0 || next.RemainingPercent > 100)) throw new InvalidOperationException("남은 비율을 0~100 사이로 입력하세요.");
            var previous = GetManual(id); SetManual(id,next);
            try { if (!demo) ExtraSettingsStore.Save(extras); }
            catch { SetManual(id,previous); throw; }
            if (next.ObservedAt.HasValue) CommitVerifiedServiceSelection(id);
            if (!next.ObservedAt.HasValue) ForgetUsageService(id);
            if (demo && id == "claude" && !next.ObservedAt.HasValue) {
                claudeView.Snapshot = null; claudeView.LastSuccess = null; claudeView.Source = "code";
                claudeView.ReceiptState = ProviderReceiptState.Waiting; claudeView.ReceiptObservedAt = null; claudeView.ReceiptMessage = null;
            }
            RefreshLocalExtras();
        }

        private void ShowExtraDemo()
        {
            showingExtraDemo = true;
            var now = DateTimeOffset.Now;
            openAiView.Snapshot = new UsageSnapshot { Plan = "예산 기준", Note = "예시 · 실제 선불 잔액이 아님", Rows = new List<UsageRow> { new UsageRow { Label = "설정 예산 잔여", DisplayValue = "$38.25", DisplaySubtitle = "이번 달 비용 $11.75 · 설정 예산 $50.00", RemainingPercent = 76.5 } } };
            claudeView.Snapshot = new UsageSnapshot { Plan = "CODE 예시", Rows = new List<UsageRow> { new UsageRow { Label = "5시간 한도", RemainingPercent = 65, ResetsAt = now.AddHours(2) }, new UsageRow { Label = "주간 한도", RemainingPercent = 82, ResetsAt = now.AddDays(3) } } };
            claudeView.Source = "code"; claudeView.LastSuccess = now; claudeView.Error = null; claudeView.Issue = null;
            claudeView.ReceiptState = ProviderReceiptState.Received; claudeView.ReceiptObservedAt = now; claudeView.ReceiptMessage = null;
            extras.GeminiManual = new ManualUsageRecord { Label = "모델별 일일 요청 (예시)", RemainingPercent = 70, ObservedAt = now };
            extras.CursorManual = new ManualUsageRecord { Label = "월 포함량 (예시)", RemainingPercent = 48, ObservedAt = now };
            higgsfieldView.Snapshot = new UsageSnapshot { Plan = "웹 구독 예시", Rows = new List<UsageRow> { new UsageRow { Label = "남은 크레딧", Remaining = 120, DisplayValue = "120", DisplaySubtitle = "웹 구독 크레딧 · 예시" } } };
            higgsfieldView.LastSuccess = now;
            higgsfieldSettings.ApiBalanceUsd = 12.50; higgsfieldSettings.ApiObservedAt = now;
            RefreshLocalExtras();
        }
    }
}
