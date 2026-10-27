using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace UsageWidget
{
    public static class Program
    {
        internal static bool IsWidgetInvocation(string[] args)
        {
            return args != null && !args.Any(value => value != null && value.StartsWith("--ade-dev", StringComparison.Ordinal));
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // Retired development launchers must not fall through into a user's widget profile.
            if (!IsWidgetInvocation(args)) { Console.WriteLine("Nephylo Widget: use the separate Nephylo ADE application."); Environment.ExitCode = 2; return; }
            if (AutoUpdateInstaller.TryRunWorker(args)) return;
            if (args.Contains("--install")) { WidgetInstallation.InstallInteractive(null,false); return; }
            if (args.Length == 1 && args[0] == "--claude-bridge-check") {
                try { WidgetDataProfile.Initialize(); ClaudeUsageService.VerifyCaptureStorage(); Console.WriteLine("nephylo-claude-bridge-v1"); }
                catch { Console.WriteLine("nephylo-claude-bridge-storage"); Environment.ExitCode = 3; }
                return;
            }
            if (args.Contains("--claude-statusline")) {
                int sourceIndex = Array.IndexOf(args,"--claude-source");
                if (sourceIndex >= 0 && sourceIndex + 1 >= args.Length) return;
                // A bridge must not create an alternative profile or block Claude with a dialog.
                try { WidgetDataProfile.Initialize(); }
                catch (ProfileInitializationException) { Console.WriteLine("Claude storage unavailable"); Environment.ExitCode = 3; return; }
                ClaudeUsageService.CaptureFromStandardInput(sourceIndex < 0 ? "" : args[sourceIndex + 1]); return;
            }
            bool first;
            bool demo = args.Contains("--demo");
            bool handoffRequested = false;
            string instanceSuffix = demo ? ".Demo" : "";
            using (var mutex = new Mutex(true, "Local\\UsageWidget.SingleInstance.v1" + instanceSuffix, out first))
            using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\UsageWidget.Show.v1" + instanceSuffix))
            {
                if (!first) { wake.Set(); return; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) => {
                    LocalizedMessageBox.Show("작업을 완료하지 못했습니다. 설정과 연결 상태를 확인해 주세요.", "Nephylo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                };
                try
                {
                    if (!demo) WidgetDataProfile.Initialize();
                    var window = new WidgetWindow(demo);
                    app.MainWindow = window;
                    app.SessionEnding += (s, e) => window.SaveForSessionEnd();
                    if (!demo) window.ContentRendered += (s, e) => { WidgetStartup.MarkReady(args); window.ConfirmUpdateStartup(); };
                    var thread = new Thread(() => {
                        while (true) {
                            try { wake.WaitOne(); } catch (ObjectDisposedException) { return; }
                            if (app.Dispatcher.HasShutdownStarted) return;
                            app.Dispatcher.BeginInvoke(new Action(window.Reveal));
                        }
                    }) { IsBackground = true };
                    thread.Start();
                    app.Run(window);
                    wake.Set();
                    thread.Join(1000);
                }
                catch (ProfileInitializationException ex) {
                    if (WidgetStartup.ShouldHandoff(args, ex)) handoffRequested = true;
                    else { WidgetStartup.MarkFailed(args, "profile"); MessageBox.Show(ex.Message + "\n\n" + ex.EnglishMessage, "Nephylo", MessageBoxButton.OK, MessageBoxImage.Warning); }
                }
                catch (Exception) { if (!demo) WidgetStartup.MarkFailed(args, "initialization"); LocalizedMessageBox.Show("위젯을 시작하지 못했습니다. Nephylo-Setup.exe로 다시 설치해 주세요. ZIP 버전은 전체 파일을 다시 압축 해제한 뒤 실행해 주세요.", "Nephylo", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
            // Release the single-instance mutex before the normal Windows process starts.
            if (handoffRequested && !WidgetStartup.TryHandoff(args)) {
                WidgetStartup.MarkFailed(args, "handoff");
                MessageBox.Show("Windows 실행 환경으로 전환하지 못했습니다. 시작 메뉴에서 Nephylo를 다시 열어 주세요. 기존 로그인과 연결 정보는 보존되어 있습니다.\n\nCould not switch to the normal Windows launch environment. Open Nephylo from the Start menu. Your saved sign-in and connections are preserved.", "Nephylo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public class ProviderView
    {
        public string Id;
        public string Source;
        public StackPanel Body;
        public TextBlock Summary;
        public System.Windows.Shapes.Path Chevron;
        public Button FoldButton;
        public TextBlock Badge;
        public TextBlock Time;
        public UsageSnapshot Snapshot;
        public DateTimeOffset? LastSuccess;
        public string Error;
        public ConnectionIssue Issue;
        public bool Busy;
        internal bool BackgroundRefresh;
        internal bool AwaitingInitialRead;
        public string Accent;
        internal ProviderReceiptState ReceiptState;
        internal DateTimeOffset? ReceiptObservedAt;
        internal string ReceiptMessage;
        internal readonly ProviderRefreshSchedule RefreshSchedule = new ProviderRefreshSchedule();
        internal readonly Dictionary<string, UsageRowVisual> RowVisuals = new Dictionary<string, UsageRowVisual>();
    }

    public partial class WidgetWindow : LocalizedWindow
    {
        internal static readonly Brush Fore = BrushOf("#252827");
        internal static readonly Brush Muted = BrushOf("#647070");
        internal static readonly Brush Back = BrushOf("#FAFAF7");
        private WidgetSettings settings;
        private readonly CodexService codex = new CodexService();
        private readonly ElevenLabsService eleven = new ElevenLabsService();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        private readonly ProviderView codexView = new ProviderView { Accent = "#2448F5" };
        private readonly ProviderView elevenView = new ProviderView { Accent = "#2448F5" };
        private readonly bool demo;
        private bool refreshing, exiting, loginPending;
        private bool automaticRefreshInProgress;
        private bool automaticRefreshPending, automaticRefreshQueued;
        private bool shutdownFlushed;
        private DateTimeOffset nextRefresh;
        private Button refreshButton;
        private TextBlock footer;
        private string secret = "";
        private Window settingsWindow;
        private FrameworkElement elevenCard;

        public WidgetWindow(bool demoMode) : this(demoMode, null) { }

        internal WidgetWindow(bool demoMode, MembershipClient membershipClient)
        {
            demo = demoMode;
            settings = demo ? new WidgetSettings() : SettingsStore.Load();
            InitializeOnboarding();
            L.Language = settings.Language;
            InitializeMembership(membershipClient);
            ApplyRequestedTheme(settings.Theme);
            InitializeUsageTracking();
            if (!demo) secret = SettingsStore.LoadApiKey();
            LoadExtraServices();
            InitializeServicePlan();
            Title = demo ? "Nephylo — 미리보기" : "Nephylo";
            var windowIcon = AppIcon.CreateWindowIcon();
            if (windowIcon != null) Icon = windowIcon;
            if (settings.LayoutVersion < 2) { settings.Width = 320; settings.Height = WidgetSettings.DefaultHeight; settings.LayoutVersion = 2; }
            Width = settings.Width; Height = settings.Height;
            MinWidth = 280; MinHeight = WidgetSettings.MinimumHeight;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
            Background = Back; Foreground = Fore; FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Topmost = settings.AlwaysOnTop;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(14), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
            Resources = MakeResources();
            BuildUi();
            InitializeWidgetGeometry();
            SourceInitialized += (s, e) => { try { int value = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref value, sizeof(int)); } catch { } };
            MakeTray();
            IsVisibleChanged += (s,e) => { if (!IsVisible) StopWidgetMotion(); else UpdateRefreshMotion(); };
            SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            Loaded += async (s, e) => { RequestFitHeight(); if (demo) ShowDemo(); else { ShowInitialOnboarding(); InitializeAutoUpdates(); await RefreshAsync(); } };
            SizeChanged += (s,e) => { if (e.WidthChanged) { RequestFitHeight(); RefreshCompactBar(); } };
            timer.Interval = TimeSpan.FromSeconds(15);
            timer.Tick += async (s, e) => {
                if (exiting) return;
                if (exiting) return;
                await PollAutoUpdateAsync();
                if (exiting) return;
                CheckUsageResetReminders();
                if (!demo && !refreshing && (automaticRefreshPending || DateTimeOffset.Now >= nextRefresh)) await RefreshDueAsync(false);
                else { Render(codexView, true); Render(elevenView, false); RefreshLocalExtras(); UpdateFooter(); }
            };
            timer.Start();
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) { Hide(); WindowState = WindowState.Normal; } };
            Closing += async (s, e) => {
                if (!shutdownFlushed && !demo) {
                    e.Cancel = true;
                    if (exiting) { e.Cancel = true; return; }
                    exiting = true; timer.Stop(); CancelCodexLogin(); CancelHiggsfieldOperation();
                    if (!await TryFlushUsageTrackingForExitAsync()) {
                        exiting = false; timer.Start();
                        return;
                    }
                    shutdownFlushed = true;
                    // Defer the second close until this cancelled Closing event has unwound.
                    await Dispatcher.InvokeAsync(new Action(Close)); return;
                }
                exiting = true; timer.Stop();
                StopOnboarding();
                StopAutoUpdates();
                StopServiceConnections();
                CancelCodexLogin();
                higgsfieldLifetime.Cancel(); CancelHiggsfieldOperation();
                noticeTimer.Stop(); pendingUsageNotices.Clear();
                SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged; StopWidgetMotion();
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                CloseDashboard();
                if (settingsWindow != null) settingsWindow.Close();
                if (servicesWindow != null) servicesWindow.Close();
                if (historyWindow != null) historyWindow.Close();
                if (notificationWindow != null) notificationWindow.Close();
                if (planWindow != null) planWindow.Close();
                if (releaseWindow != null) releaseWindow.Close();
                if (!demo) { CaptureWidgetGeometry(); TrySaveSettings(); }
                codex.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; }
            };
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
        internal static Brush BrushOf(string color) { return WidgetPalette.Brush(color); }
        internal static ResourceDictionary MakeResources()
        {
            var resources = WidgetPalette.ParseResources(@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                <Style TargetType='Button'><Setter Property='Foreground' Value='#2448F5'/><Setter Property='Background' Value='#EEF1FC'/><Setter Property='BorderBrush' Value='#CBD5F4'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='12,8'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FontSize' Value='12'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='3' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='#2448F5'/><Setter TargetName='b' Property='BorderThickness' Value='1'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='#173AD2'/><Setter TargetName='b' Property='BorderThickness' Value='1'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
                <Style TargetType='TextBox'><Setter Property='Background' Value='#FFFFFF'/><Setter Property='Foreground' Value='#252827'/><Setter Property='CaretBrush' Value='#252827'/><Setter Property='BorderBrush' Value='#7E8983'/><Setter Property='Padding' Value='9'/><Setter Property='FontSize' Value='12'/></Style>
                <Style TargetType='PasswordBox'><Setter Property='Background' Value='#FFFFFF'/><Setter Property='Foreground' Value='#252827'/><Setter Property='CaretBrush' Value='#252827'/><Setter Property='BorderBrush' Value='#7E8983'/><Setter Property='Padding' Value='9'/><Setter Property='FontSize' Value='13'/></Style>
                <Style TargetType='ScrollViewer'><Setter Property='VerticalScrollBarVisibility' Value='Auto'/><Setter Property='HorizontalScrollBarVisibility' Value='Disabled'/></Style>
                <Style TargetType='ScrollBar'><Setter Property='Width' Value='7'/><Setter Property='Background' Value='Transparent'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='Vertical'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='#ADB7B9' CornerRadius='2' Width='4' HorizontalAlignment='Center'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton></Track></ControlTemplate></Setter.Value></Setter></Style>
            </ResourceDictionary>");
            resources.MergedDictionaries.Add(MakeChromeResources());
            resources.MergedDictionaries.Add(MakeDialogResources());
            UiL.AddStyles(resources);
            return resources;
        }

        private void BuildUi()
        {
            var outer = new LocalizedBorder { BorderBrush = BrushOf("#C8CECF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(18, 11, 18, 11) };
            var shell = new LocalizedGrid(); Content = shell;
            normalWidgetRoot = outer; shell.Children.Add(outer);
            var grid = new LocalizedGrid(); outer.Child = grid;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new LocalizedGrid { Background = Brushes.Transparent, Margin = new Thickness(0,0,0,6) };
            widgetHeader = header;
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.MouseLeftButtonDown += (s,e) => {
                if (e.OriginalSource is TextBlock || e.OriginalSource == header) { try { DragMove(); } catch { } }
            };
            var title = new LocalizedGrid { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch, ClipToBounds = true };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.ColumnDefinitions.Add(new ColumnDefinition());
            title.Children.Add(CreateWidgetPictogram());
            if (demo) {
                var subtitle = T("미리보기",11,"#647070",FontWeights.Normal);
                subtitle.VerticalAlignment = VerticalAlignment.Center;
                subtitle.TextWrapping = TextWrapping.NoWrap; subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(subtitle,1); title.Children.Add(subtitle);
            }
            title.MouseLeftButtonDown += (s,e) => { try { DragMove(); } catch { } e.Handled = true; };
            System.Windows.Automation.AutomationProperties.SetName(title,demo ? "Nephylo 남은 사용량 미리보기" : "Nephylo 남은 사용량");
            header.Children.Add(title);
            var tools = new LocalizedStackPanel { Orientation = Orientation.Horizontal };
            refreshButton = ToolButton("\uE72C", "새로고침", async (s,e) => {
                if (String.Equals(refreshButton.Content as string,"\uE7BA",StringComparison.Ordinal)) ShowRefreshDetails(refreshButton);
                else if (!demo) await RefreshAsync();
            });
            tools.Children.Add(refreshButton);
            tools.Children.Add(ToolButton("\uE710", "서비스 연결 · 관리", (s,e) => OpenServices(CurrentServiceSelection())));
            tools.Children.Add(ToolButton("\uE713", "위젯 설정", (s,e) => OpenSettings()));
            var more = ToolButton("\uE712", "더 보기", null);
            more.Click += (s,e) => OpenMoreMenu(more);
            tools.Children.Add(more); Grid.SetColumn(tools,1); header.Children.Add(tools);
            var headerDivider = new Border { Height = 1, Background = BrushOf("#C8CECF"),
                Margin = new Thickness(0,4,0,0), IsHitTestVisible = false, SnapsToDevicePixels = true };
            Grid.SetRow(headerDivider,1); Grid.SetColumnSpan(headerDivider,2); header.Children.Add(headerDivider);
            grid.Children.Add(header);

            var providers = new LocalizedStackPanel(); providerStack = providers;
            providers.SizeChanged += (s,e) => RequestFitHeight();
            AddProviderCard(providers,"codex","Codex",codexView);
            elevenCard = AddProviderCard(providers,"eleven","ElevenLabs",elevenView);
            AddExtraCards(providers);
            var scroll = new ScrollViewer { Content = providers, Padding = new Thickness(0,0,1,0) };
            var contentArea = new DockPanel();
            widgetMembershipNotice = BuildMembershipStatusNotice();
            widgetMembershipNotice.SizeChanged += (s,e) => RequestFitHeight();
            DockPanel.SetDock(widgetMembershipNotice,Dock.Top); contentArea.Children.Add(widgetMembershipNotice);
            contentArea.Children.Add(scroll);
            Grid.SetRow(contentArea,1); grid.Children.Add(contentArea);
            var footerArea = BuildWidgetStatusFooter();
            footerArea.SizeChanged += (s,e) => RequestFitHeight();
            Grid.SetRow(footerArea,2); grid.Children.Add(footerArea);
            compactWidgetRoot = BuildCompactBar(); shell.Children.Add(compactWidgetRoot);
            ApplyWidgetViewVisibility();
        }

        private Button ToolButton(string glyph, string tip, RoutedEventHandler action)
        {
            var button = new LocalizedButton { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Style = (Style)Resources["WidgetToolbarButton"], ToolTip = tip };
            ToolTipService.SetInitialShowDelay(button,400);
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            if (action != null) button.Click += action;
            return button;
        }
        private void UpdatePin() { UpdateFooter(); }
        internal static TextBlock T(string text, double size, string color, FontWeight weight, Thickness? margin = null)
        {
            if (size < 12) size = 12;
            return new LocalizedTextBlock { Text = text, FontSize = size, Foreground = BrushOf(color), FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        }

        private Border CreateCard(string title, string id, ProviderView view)
        {
            view.Id = id;
            var panel = new LocalizedStackPanel();
            var heading = new LocalizedGrid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            var label = new LocalizedGrid();
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            label.ColumnDefinitions.Add(new ColumnDefinition());
            label.Children.Add(new Image { Source = ServiceIcons.Get(id), Width = 17, Height = 17, Stretch = Stretch.Uniform, Margin = new Thickness(0,0,8,0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            var name = T(title,14,"#252827",FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center; name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(name,1); label.Children.Add(name);
            heading.Children.Add(label);
            var fold = new LocalizedButton { Content = heading, Style = (Style)Resources["WidgetServiceToggle"], Tag = title };
            view.FoldButton = fold;
            AttachKeyboardStatusTooltip(fold);
            System.Windows.Automation.AutomationProperties.SetName(fold,title + " 접기 / 펼치기");
            fold.Click += (s,e) => ToggleProvider(id);
            view.Badge = T("확인 중",9,"#647070",FontWeights.Normal);
            view.Badge.VerticalAlignment = VerticalAlignment.Center; view.Badge.MaxWidth = 110; view.Badge.TextAlignment = TextAlignment.Right;
            view.Badge.TextWrapping = TextWrapping.NoWrap; view.Badge.TextTrimming = TextTrimming.CharacterEllipsis;
            view.Badge.Margin = new Thickness(10,0,10,0);
            Grid.SetColumn(view.Badge,1); heading.Children.Add(view.Badge);
            view.Chevron = new System.Windows.Shapes.Path {
                Data = Geometry.Parse("M 4 2 L 8 6 L 4 10"), Width = 12, Height = 12,
                StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5,0.5), RenderTransform = new RotateTransform(90), IsHitTestVisible = false
            };
            view.Chevron.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground") { Source = fold });
            Grid.SetColumn(view.Chevron,2); heading.Children.Add(view.Chevron); panel.Children.Add(fold);
            view.Summary = T("",12,"#475256",FontWeights.Medium,new Thickness(0,5,0,0)); panel.Children.Add(view.Summary);
            view.Body = new LocalizedStackPanel { Margin = new Thickness(0,4,0,0) }; panel.Children.Add(view.Body);
            view.Time = T("",9,"#647070",FontWeights.Normal); view.Time.Visibility = Visibility.Collapsed;
            panel.Children.Add(view.Time);
            Render(view,title == "Codex");
            return new LocalizedBorder { Background = Brushes.Transparent, Child = panel };
        }

        private void Render(ProviderView view, bool isCodex)
        {
            view.AwaitingInitialRead = view.Snapshot == null && (isCodex
                || view.Id == "eleven" && !String.IsNullOrWhiteSpace(secret)
                || view.Id == "openai" && !String.IsNullOrWhiteSpace(openAiKey));
            RefreshDashboardIfOpen();
            if (view.Body == null) return;
            view.Body.Children.Clear();
            bool stale = !string.IsNullOrEmpty(view.Error) && view.Snapshot != null;
            bool failed = !string.IsNullOrEmpty(view.Error);
            var presentation = UsagePresentation.Describe(view,DateTimeOffset.Now);
            bool showBusy = UsagePresentation.ShowBusy(view);
            view.Badge.Text = showBusy ? "갱신 중" : stale ? "이전 값" : failed ? "연결 확인" : view.Snapshot == null ? presentation.Label : (view.Snapshot.Plan ?? "연결됨").ToUpperInvariant();
            bool showStatus = showBusy || failed || view.Snapshot == null || presentation.Warning || UsageFreshness.Source(view) == "manual";
            if (!showBusy && !failed && view.Snapshot != null && presentation.Warning) view.Badge.Text = presentation.Label;
            view.Badge.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
            view.Badge.Foreground = BrushOf(failed ? "#91501F" : "#647070");
            string info = demo ? "예시 데이터 · 실제 API 호출 없음" : view.LastSuccess.HasValue ? "마지막 확인: " + view.LastSuccess.Value.ToLocalTime().ToString("MM/dd HH:mm:ss") : "아직 확인된 사용량이 없습니다.";
            if (view.Snapshot != null && !string.IsNullOrWhiteSpace(view.Snapshot.Note)) info += "\n" + view.Snapshot.Note;
            if (failed) info += "\n" + view.Error;
            view.Badge.ToolTip = info; view.Body.ToolTip = info; view.Time.Text = info;
            UpdateProviderHeaderDetails(view,info);
            UpdateProviderFold(view);
            if (view.Snapshot == null)
            {
                PruneUsageVisuals(view,new HashSet<string>());
                view.Body.Children.Add(T("—",24,"#647070",FontWeights.Medium));
                var message = failed ? view.Error : view.Busy || view.AwaitingInitialRead ? presentation.Message : "설정에서 API 키를 연결해 주세요.";
                var empty = T(message,13,failed ? "#91501F" : "#566369",FontWeights.Normal,new Thickness(0,5,0,0));
                empty.LineHeight = 20; empty.ToolTip = message;
                view.Body.Children.Add(empty);
                if (failed) { AddRetryStatus(view); AddConnectionHelp(view); }
                return;
            }

            var rowKeys = new HashSet<string>();
            for (int index = 0; index < view.Snapshot.Rows.Count; index++)
            {
                var row = view.Snapshot.Rows[index];
                var section = new LocalizedStackPanel { Margin = new Thickness(0,index == 0 ? 0 : 8,0,0) };
                var line = new LocalizedGrid(); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string labelText = row.Label ?? "사용 한도";
                if (labelText.StartsWith("Codex · ")) labelText = labelText.Substring("Codex · ".Length);
                if (labelText == "구독 크레딧" || labelText == "기본 제공 크레딧") labelText = "구독 잔여량";
                if (isCodex && (labelText == "주간" || labelText == "주간 한도")) labelText = "주간 구독 잔여량";
                if (isCodex && (labelText == "5시간" || labelText == "5시간 한도")) labelText = "5시간 구독 잔여량";
                if (isCodex && labelText == "추가 크레딧") labelText = "추가 크레딧 잔액";
                bool unlimited = !string.IsNullOrWhiteSpace(row.Detail) && row.Detail.Contains("무제한");
                var label = T(labelText,13,"#566369",FontWeights.Normal,new Thickness(0,0,10,0));
                if (!demo && UsageFreshness.Source(view) == "manual") { label.Text = row.Label ?? ""; UiL.Raw(label); }
                label.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(label);
                string quantity = UsageValue(row);
                string rowKey = (row.Label ?? "") + "\u001f" + index.ToString(CultureInfo.InvariantCulture); rowKeys.Add(rowKey);
                var value = UsageNumber(view,rowKey,quantity,row.RemainingPercent.HasValue ? (settings.CompactView ? 21 : 24) : 18);
                Grid.SetColumn(value,1); line.Children.Add(value); section.Children.Add(line);
                if (row.RemainingPercent.HasValue && !settings.CompactView)
                {
                    section.Children.Add(UsageMeter(view,rowKey,row.RemainingPercent.Value));
                }
                else HideUsageMeter(view,rowKey,row.RemainingPercent);
                if (!settings.CompactView && !String.IsNullOrWhiteSpace(row.DisplaySubtitle))
                    section.Children.Add(T(row.DisplaySubtitle,13,"#566369",FontWeights.Normal,new Thickness(0,4,0,0)));
                else if (!settings.CompactView && row.Remaining.HasValue && row.Limit.HasValue)
                    section.Children.Add(T(row.Remaining.Value.ToString("N0") + " / " + row.Limit.Value.ToString("N0") + " 크레딧",13,"#566369",FontWeights.Normal,new Thickness(0,4,0,0)));
                if (row.ResetsAt.HasValue)
                {
                    var reset = T(ResetText(row.ResetsAt.Value).Replace("초기화까지 ","" ) + (row.ResetsAt.Value > DateTimeOffset.Now ? " 후 초기화" : ""),13,"#566369",FontWeights.Normal,new Thickness(0,5,0,0));
                    reset.LineHeight = 20;
                    reset.ToolTip = "초기화 예정: " + row.ResetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz"); section.Children.Add(reset);
                }
                if (!settings.CompactView) {
                    var insight = BuildInlineUsageInsight(view,row,DateTimeOffset.Now);
                    if (insight != null) section.Children.Add(insight);
                }
                if (!row.RemainingPercent.HasValue && !row.Remaining.HasValue && !unlimited && String.IsNullOrEmpty(row.DisplayValue))
                    section.Children.Add(T("정보 없음",10,"#647070",FontWeights.Normal,new Thickness(0,2,0,0)));
                if (!string.IsNullOrWhiteSpace(row.Detail))
                {
                    line.ToolTip = info + "\n" + row.Detail;
                    if (row.Detail.Contains("초과"))
                    {
                        string detail = row.Detail.Substring(row.Detail.LastIndexOf('·') + 1).Trim();
                        section.Children.Add(T(detail,10,"#91501F",FontWeights.Normal,new Thickness(0,3,0,0)));
                    }
                }
                view.Body.Children.Add(section);
            }
            PruneUsageVisuals(view,rowKeys);
            string note = view.Snapshot.Note ?? "";
            string important = note.Contains("지출 한도 도달") ? "지출 한도 도달" : note.Contains("포함 사용량 이용 제한 상태") ? "구독 제공량 이용 제한 · 크레딧 잔액은 별도" : note.Contains("구독 상태:") ? note.Substring(note.IndexOf("구독 상태:")) : null;
            if (important != null) view.Body.Children.Add(T(important,13,"#91501F",FontWeights.Medium,new Thickness(0,8,0,0)));
            if (stale)
            {
                var message = T("갱신 실패 · " + (view.LastSuccess.HasValue ? "마지막 확인 " + UsageFreshness.Stamp(view.LastSuccess.Value,DateTimeOffset.Now) : "이전 값"),13,"#91501F",FontWeights.SemiBold,new Thickness(0,9,0,0));
                message.ToolTip = view.Error; view.Body.Children.Add(message);
                var reason = T(view.Error,13,"#91501F",FontWeights.Normal,new Thickness(0,5,0,0)); reason.LineHeight = 20; reason.ToolTip = view.Error; view.Body.Children.Add(reason);
                AddRetryStatus(view);
                AddConnectionHelp(view);
            }
        }

        internal static string ResetText(DateTimeOffset reset)
        {
            var span = reset - DateTimeOffset.Now;
            if (span.TotalSeconds <= 0) return "초기화 시각 경과 · 다음 조회 대기";
            if (span.TotalDays >= 1) return "초기화까지 " + (int)span.TotalDays + "일 " + span.Hours + "시간";
            if (span.TotalHours >= 1) return "초기화까지 " + (int)span.TotalHours + "시간 " + span.Minutes + "분";
            return "초기화까지 " + Math.Max(1,(int)Math.Ceiling(span.TotalMinutes)) + "분";
        }
        private Task RefreshAsync() { return RefreshDueAsync(true); }

        // An access change can happen during a provider read. Keep that request
        // separately from the periodic deadline so completion cannot overwrite it.
        private void RequestAutomaticRefresh()
        {
            automaticRefreshPending = true;
            nextRefresh = DateTimeOffset.MinValue;
            QueueAutomaticRefresh();
        }

        private void QueueAutomaticRefresh()
        {
            if (!automaticRefreshPending || automaticRefreshQueued || refreshing
                || demo || exiting || !IsLoaded || Dispatcher.HasShutdownStarted) return;
            automaticRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(async () => {
                automaticRefreshQueued = false;
                if (!automaticRefreshPending || demo || exiting || !IsLoaded) return;
                // This is an automatic follow-up: successful providers and server
                // Retry-After/backoff deadlines must keep their existing schedules.
                await RefreshDueAsync(false);
            }));
        }

        private async Task RefreshDueAsync(bool manual)
        {
            if (refreshing || exiting) return;
            automaticRefreshPending = false;
            var now = DateTimeOffset.Now;
            if (IsServiceActive("eleven") && String.IsNullOrWhiteSpace(secret)) {
                elevenView.Snapshot = null; elevenView.LastSuccess = null; elevenView.Error = null; elevenView.Issue = null; elevenView.Busy = false;
            }
            bool readCodex = IsServiceActive("codex") && !IsServiceConnectionPending("codex") && !loginPending && ShouldAttemptCodexRead(now,manual) && codexView.RefreshSchedule.CanAttempt(now,manual);
            bool readEleven = IsServiceActive("eleven") && !IsServiceConnectionPending("eleven") && !String.IsNullOrWhiteSpace(secret) && elevenView.RefreshSchedule.CanAttempt(now,manual);
            bool readOpenAi = IsServiceActive("openai") && !IsServiceConnectionPending("openai") && IsExtraShown("openai") && !String.IsNullOrWhiteSpace(openAiKey) && openAiView.RefreshSchedule.CanAttempt(now,manual);
            bool readHiggsfield = IsServiceActive("higgsfield") && !IsServiceConnectionPending("higgsfield") && higgsfieldSettings.WebConnected && higgsfieldOperation == null && higgsfieldView.RefreshSchedule.CanAttempt(now,manual);
            bool readClaude = IsServiceActive("claude") && !IsServiceConnectionPending("claude") && !claudeConnectionBusy && IsSavedClaudeConfigured() && claudeView.RefreshSchedule.CanAttempt(now,manual);
            if (!readCodex && !readEleven && !readOpenAi && !readHiggsfield && !readClaude) {
                nextRefresh = now.AddSeconds(15);
                Render(codexView,true); Render(elevenView,false); RefreshLocalExtras(); UpdateFooter(); return;
            }
            refreshing = true; automaticRefreshInProgress = !manual;
            // Everything after the guard is inside try: a UI exception here must
            // not leave polling, the refresh button and auto-update stopped.
            try {
                refreshButton.IsEnabled = false;
                codexView.BackgroundRefresh = readCodex && !manual; elevenView.BackgroundRefresh = readEleven && !manual;
                openAiView.BackgroundRefresh = readOpenAi && !manual; higgsfieldView.BackgroundRefresh = readHiggsfield && !manual;
                claudeView.BackgroundRefresh = readClaude && !manual;
                UpdateRefreshMotion();
                codexView.Busy = readCodex; elevenView.Busy = readEleven;
                Render(codexView,true); Render(elevenView,false);
                RefreshLocalExtras();
                var reads = new List<Task>();
                if (readCodex) reads.Add(ReadCodexCore(manual));
                if (readEleven) reads.Add(ReadEleven());
                if (readOpenAi) reads.Add(ReadOpenAi());
                if (readHiggsfield) reads.Add(ReadHiggsfield());
                if (readClaude) reads.Add(ReadClaude());
                // Extra providers set Busy when their readers start. Refresh shared
                // feedback afterwards so first reads still announce progress. A
                // feedback error waits for started reads so the guard still covers them.
                Exception feedbackError = null;
                try { UpdateFooter(); } catch (Exception error) { feedbackError = error; }
                await Task.WhenAll(reads);
                if (feedbackError != null) throw feedbackError;
            }
            finally {
                refreshing = false; automaticRefreshInProgress = false;
                codexView.BackgroundRefresh = elevenView.BackgroundRefresh = openAiView.BackgroundRefresh = higgsfieldView.BackgroundRefresh = false;
                claudeView.BackgroundRefresh = false;
                nextRefresh = DateTimeOffset.Now.AddSeconds(15); UpdateRefreshMotion();
                if (!exiting) { refreshButton.IsEnabled = true; Render(codexView,true); Render(elevenView,false); RefreshLocalExtras(); UpdateFooter(); }
                QueueAutomaticRefresh();
            }
        }
        private Task ReadCodex() { return ReadCodexCore(false); }
        private async Task ReadCodexCore(bool manual)
        {
            if (!IsServiceActive("codex") || loginPending || exiting) { codexView.Busy = false; if (!exiting) Render(codexView,true); return; }
            string owner = ServiceReadingOwner;
            try {
                if (!await CanReadCodexCandidateAsync(manual) || exiting || !IsServiceActive("codex") || loginPending) return;
                var data = await codex.ReadAsync(settings.CodexPath,settings.CodexWslDistribution);
                if (!CanApplyServiceReading("codex",owner)) return;
                codexView.Snapshot = data; codexView.LastSuccess = DateTimeOffset.Now; codexView.Error = null; codexView.Issue = null; RefreshSucceeded(codexView); ObserveUsage(codexView,CodexUsageSource());
            }
            catch (Exception ex) { if (CanApplyServiceReading("codex",owner)) { RefreshFailed(codexView,ex); codexView.Issue = ConnectionDiagnostics.For("codex",ex); codexView.Error = codexView.Issue.Title; } }
            finally { codexView.Busy = false; codexView.BackgroundRefresh = false; if (!exiting) Render(codexView,true); }
        }
        private async Task ReadEleven()
        {
            if (!IsServiceActive("eleven")) { elevenView.Busy = false; return; }
            if (string.IsNullOrWhiteSpace(secret)) { elevenView.Snapshot = null; elevenView.LastSuccess = null; elevenView.Error = null; elevenView.Issue = null; elevenView.Busy = false; return; }
            string owner = ServiceReadingOwner;
            try { var data = await eleven.ReadAsync(secret); if (!CanApplyServiceReading("eleven",owner)) return; elevenView.Snapshot = data; elevenView.LastSuccess = DateTimeOffset.Now; elevenView.Error = null; elevenView.Issue = null; RefreshSucceeded(elevenView); ObserveUsage(elevenView,"api"); }
            catch (Exception ex) { if (CanApplyServiceReading("eleven",owner)) { RefreshFailed(elevenView,ex); elevenView.Issue = ConnectionDiagnostics.For("eleven",ex); elevenView.Error = elevenView.Issue.Title; } }
            finally { elevenView.Busy = false; elevenView.BackgroundRefresh = false; if (!exiting) Render(elevenView,false); }
        }
        private static string Friendly(Exception ex)
        {
            if (ex is TaskCanceledException || ex is TimeoutException) return "응답 시간이 초과되었습니다. 잠시 후 다시 시도합니다.";
            var msg = ex.Message ?? "연결을 확인해 주세요.";
            return msg.Length > 220 ? "조회하지 못했습니다. 설정과 인터넷 연결을 확인해 주세요." : msg;
        }
        private void UpdateFooter()
        {
            RefreshDashboardIfOpen();
            if (footer == null) return;
            var warnings = new List<string>();
            if (ActivePresentationViews().Any(v => !String.IsNullOrEmpty(v.Error))) warnings.Add("일부 갱신 실패");
            if (loginPending) warnings.Add("Codex 로그인 대기");
            if (higgsfieldOperation != null) warnings.Add("Higgsfield 연결 확인 중");
            if (!String.IsNullOrEmpty(planStorageError)) warnings.Add("선택 저장 확인");
            if (!String.IsNullOrEmpty(usageTrackingError)) warnings.Add("이력 저장 확인");
            footer.Text = demo ? "예시 데이터 · 실제 API 호출 없음" : String.Join(" · ",warnings);
            footer.Foreground = BrushOf("#647070");
            footer.ToolTip = StatusTooltip();
            UpdateRefreshIndicator();
            RefreshCompactBar();
        }
        private void MakeTray()
        {
            trayIcon = AppIcon.CreateTrayIcon();
            tray = new Forms.NotifyIcon { Text = "Nephylo · AI 사용량", Icon = trayIcon, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("위젯 열기", null, (s,e) => Dispatcher.BeginInvoke(new Action(Reveal)));
            menu.Items.Add("대시보드 열기", null, (s,e) => Dispatcher.BeginInvoke(new Action(OpenDashboard)));
            menu.Items.Add("새로고침", null, (s,e) => Dispatcher.BeginInvoke(new Action(async () => { if (!demo) await RefreshAsync(); })));
            menu.Items.Add("사용 이력", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenUsageHistory(); })));
            menu.Items.Add("주간 요약", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenWeeklySummary(); })));
            menu.Items.Add("알림 · 이력 설정", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenNotificationSettings(); })));
            menu.Items.Add("무료 이용 안내", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenPlan(); })));
            menu.Items.Add("후원하기 ↗", null, (s,e) => Dispatcher.BeginInvoke(new Action(OpenSponsorship)));
            menu.Items.Add("위젯 설정", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenSettings(); })));
            menu.Items.Add("서비스 연결 · 관리", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenServices(CurrentServiceSelection()); })));
            menu.Items.Add("연결 시작 도우미", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { Reveal(); OpenOnboarding(); })));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("종료", null, (s,e) => Dispatcher.BeginInvoke(new Action(Close)));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (s,e) => Dispatcher.BeginInvoke(new Action(Reveal));
            tray.BalloonTipClicked += (s,e) => Dispatcher.BeginInvoke(new Action(() => { if (!exiting) { Reveal(); OpenRecentUsageNotices(); } }));
            foreach (Forms.ToolStripItem item in menu.Items) item.Tag = item.Text;
            UpdateTrayLanguage();
        }
        private void UpdateTrayLanguage()
        {
            if (tray == null) return;
            tray.Text = L.Text("Nephylo · AI 사용량");
            if (tray.ContextMenuStrip != null) foreach (Forms.ToolStripItem item in tray.ContextMenuStrip.Items) item.Text = L.Text(item.Tag as string ?? item.Text);
        }
        private void ApplyLanguage(string language)
        {
            L.Language = language;
            UiL.Refresh(); UpdateTrayLanguage(); RequestFitHeight(); UpdateFooter();
            RefreshUsageHistoryIfOpen();
        }
        public void Reveal() { Show(); WindowState = WindowState.Normal; Activate(); }
        private void TrySaveSettings() { try { SettingsStore.Save(settings); } catch { if (footer != null) footer.Text = "설정 저장 실패 · 폴더 쓰기 권한을 확인해 주세요"; } }

        // Logoff and shutdown close this window with cancellation ignored, so the
        // asynchronous flush in Closing never resumes. Save synchronously instead.
        internal void SaveForSessionEnd()
        {
            if (demo || shutdownFlushed) return;
            exiting = true; timer.Stop();
            CaptureWidgetGeometry(); TrySaveSettings();
            try { FlushUsageTrackingAsync().Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { /* The previous journal file is left intact. */ }
            shutdownFlushed = true;
        }

        private void OpenSettings()
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            var dialog = CreateManagementDialog("위젯 설정", 540, 790);
            settingsWindow = dialog;
            StackPanel actions;
            var body = ManagementBody(dialog, "위젯 설정", "화면 표시와 자동 갱신을 설정합니다.", out actions);
            var cancel = ManagementAction("취소", () => dialog.Close()); cancel.IsCancel = true;
            var save = ManagementAction("저장", null, true);
            actions.Children.Add(cancel); actions.Children.Add(save);
            var appearance = ManagementSection(body, "화면과 언어");
            var language = new LocalizedComboBox { Width = 155, FontSize = 14 };
            language.Items.Add(new LocalizedComboBoxItem { Content = "한국어", Tag = "ko" });
            language.Items.Add(new LocalizedComboBoxItem { Content = "English", Tag = "en" });
            language.SelectedIndex = settings.Language == "en" ? 1 : 0;
            System.Windows.Automation.AutomationProperties.SetName(language,"언어 / Language");
            ManagementRow(appearance, "언어 / Language", null, language);
            var theme = new LocalizedComboBox { Width = 155, FontSize = 14, IsEnabled = ProFeaturesAvailable };
            theme.Items.Add(new LocalizedComboBoxItem { Content = "다크 · 블랙", Tag = "dark" });
            theme.Items.Add(new LocalizedComboBoxItem { Content = "라이트 · 페이퍼", Tag = "light" });
            theme.SelectedIndex = EffectiveTheme(settings.Theme) == "light" ? 1 : 0;
            System.Windows.Automation.AutomationProperties.SetName(theme,"화면 테마");
            theme.SelectionChanged += (s,e) => ApplyRequestedTheme((string)((ComboBoxItem)theme.SelectedItem).Tag);
            ManagementRow(appearance, "화면 테마", "선택하면 미리 보고, 저장하면 다음 실행에도 유지됩니다.", theme);
            var animations = new LocalizedCheckBox { IsChecked = settings.AnimationsEnabled };
            System.Windows.Automation.AutomationProperties.SetName(animations,"부드러운 애니메이션");
            ManagementRow(appearance, "부드러운 애니메이션", "수치 갱신과 카드 접기에 적용합니다. Windows 애니메이션 설정도 따릅니다.", animations);

            var display = ManagementSection(body, "위젯 표시");
            var pin = new LocalizedCheckBox { IsChecked = settings.AlwaysOnTop };
            System.Windows.Automation.AutomationProperties.SetName(pin,"항상 다른 창 위에 표시");
            ManagementRow(display, "항상 다른 창 위에 표시", null, pin);
            var compact = new LocalizedCheckBox { IsChecked = settings.CompactView };
            System.Windows.Automation.AutomationProperties.SetName(compact,"간략 보기");
            ManagementRow(display, "간략 보기", "아이콘·잔량·초기화 날짜를 한 줄의 작은 위젯으로 표시합니다.", compact);
            var fitHeight = new LocalizedCheckBox { IsChecked = settings.FitContentHeight };
            System.Windows.Automation.AutomationProperties.SetName(fitHeight,"내용에 맞춰 위젯 높이 조절");
            ManagementRow(display, "내용에 맞춰 위젯 높이 조절", "끄면 직접 조절한 높이를 유지합니다.", fitHeight);

            var automatic = ManagementSection(body, "자동 실행과 갱신");
            string startupCommand = null; string startupNotice = "";
            try { if (!demo) startupCommand = SettingsStore.ReadStartupCommand(); }
            catch { startupNotice = "Windows 자동 실행 등록을 읽지 못했습니다."; }
            var startup = new LocalizedCheckBox { IsChecked = demo ? settings.StartWithWindows : startupCommand != null };
            System.Windows.Automation.AutomationProperties.SetName(startup,"Windows 로그인 시 자동 실행");
            ManagementRow(automatic, "Windows 로그인 시 자동 실행", null, startup);
            if (!demo && startupCommand != null && startupNotice.Length == 0) {
                try { if (!SettingsStore.IsStartupAtCurrentPath()) startupNotice = "이전 실행 위치가 등록되어 있습니다. 저장하면 현재 위치로 갱신합니다."; } catch { startupNotice = "자동 실행 경로를 확인하지 못했습니다."; }
            }
            if (startupNotice.Length > 0) automatic.Children.Add(T(startupNotice,11,"#91501F",FontWeights.Normal,new Thickness(0,0,0,8)));
            var interval = new LocalizedComboBox { Width = 155, FontSize = 14 }; int[] seconds = {60,120,300,600}; foreach (var n in seconds) interval.Items.Add(new LocalizedComboBoxItem { Content = (n/60)+"분마다", Tag = n }); interval.SelectedIndex = Array.IndexOf(seconds,settings.RefreshSeconds); if (interval.SelectedIndex < 0) interval.SelectedIndex = 1;
            System.Windows.Automation.AutomationProperties.SetName(interval,"자동 갱신 간격");
            ManagementRow(automatic, "자동 갱신", "API 연결 서비스에 적용됩니다. Claude Code 수신값과 수동 기록은 각각의 확인 시각을 표시합니다.", interval);
            var shortcuts = ManagementSection(body, "연결과 기능");
            shortcuts.Children.Add(SmallAction("서비스 연결 · 순서 관리", () => OpenServices(CurrentServiceSelection())));
            shortcuts.Children.Add(SmallAction("알림 · 이력 설정", OpenNotificationSettings));
            var error = T("",11,"#91501F",FontWeights.Normal,new Thickness(0,12,0,0)); body.Children.Add(error);
            save.Click += (s,e) => {
                try {
                    CaptureWidgetGeometry();
                    var desired = new WidgetSettings { AutoUpdateEnabled = settings.AutoUpdateEnabled, Language = (string)((ComboBoxItem)language.SelectedItem).Tag, FitContentHeight = fitHeight.IsChecked == true, AnimationsEnabled = animations.IsChecked == true, Theme = ThemeForSettingsSave((string)((ComboBoxItem)theme.SelectedItem).Tag), CodexPath = settings.CodexPath, CodexWslDistribution = settings.CodexWslDistribution, ClaudeWslDistribution = settings.ClaudeWslDistribution, ClaudeWindowsConfigDirectory = settings.ClaudeWindowsConfigDirectory, ClaudeUseExistingStatusLine = settings.ClaudeUseExistingStatusLine, AlwaysOnTop = pin.IsChecked == true, CompactView = compact.IsChecked == true, ServiceOrder = settings.ServiceOrder, CollapsedServices = settings.CollapsedServices, StartWithWindows = startup.IsChecked == true, RefreshSeconds = (int)((ComboBoxItem)interval.SelectedItem).Tag, Left = Left, Top = Top, Width = ActualWidth, Height = ActualHeight };
                    CopyWidgetGeometry(settings,desired);
                    CopyDashboardGeometry(settings,desired);
                    CopyDashboardPreferences(settings,desired);
                    if (!demo) {
                        string previousCommand = SettingsStore.ReadStartupCommand(); bool startupSaved = false;
                        try {
                            SettingsStore.SetStartup(desired.StartWithWindows); startupSaved = true;
                            SettingsStore.Save(desired);
                        } catch {
                            if (startupSaved) {
                                try { SettingsStore.RestoreStartupCommand(previousCommand); }
                                catch { throw new InvalidOperationException("자동 실행 설정을 복구하지 못했습니다. Windows 시작 프로그램 설정을 확인해 주세요."); }
                            }
                            throw;
                        }
                    }
                    bool intervalChanged = settings.RefreshSeconds != desired.RefreshSeconds;
                    bool viewChanged = settings.CompactView != desired.CompactView;
                    settings = desired; Topmost = settings.AlwaysOnTop;
                    ApplyWidgetViewVisibility();
                    if (viewChanged) ApplyWidgetGeometry();
                    if (intervalChanged) foreach (var view in new[] { codexView,elevenView,openAiView,higgsfieldView,claudeView }) view.RefreshSchedule.ChangeInterval(DateTimeOffset.Now,settings.RefreshSeconds);
                    ApplyLanguage(settings.Language);
                    RequestFitHeight();
                    if (!MotionAllowed) StopWidgetMotion(); else UpdateRefreshMotion();
                    nextRefresh = DateTimeOffset.Now.AddSeconds(settings.RefreshSeconds);
                    Render(codexView,true); Render(elevenView,false); RefreshLocalExtras(); UpdateFooter(); dialog.Close();
                } catch (Exception ex) { error.Text = Friendly(ex); error.BringIntoView(); }
            };
            dialog.Closed += (s,e) => { ApplyRequestedTheme(settings.Theme); settingsWindow = null; };
            dialog.Show();
        }
        private void ShowDemo()
        {
            var now = DateTimeOffset.Now;
            codexView.Snapshot = new UsageSnapshot { Plan = "PLUS", Rows = new List<UsageRow> {
                new UsageRow { Label = "5시간 한도", RemainingPercent = 72, ResetsAt = now.AddHours(3).AddMinutes(18) },
                new UsageRow { Label = "주간 한도", RemainingPercent = 86, ResetsAt = now.AddDays(4).AddHours(7) }
            }};
            elevenView.Snapshot = new UsageSnapshot { Plan = "CREATOR", Rows = new List<UsageRow> {
                new UsageRow { Label = "기본 제공 크레딧", RemainingPercent = 64.2, Remaining = 64200, Limit = 100000, ResetsAt = now.AddDays(12) }
            }};
            codexView.LastSuccess = now; elevenView.LastSuccess = now;
            SeedDemoUsageHistory();
            ShowExtraDemo(); Render(codexView,true); Render(elevenView,false); UpdateFooter();
        }
    }
}
