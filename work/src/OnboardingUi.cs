using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private bool initialOnboardingPending;
        private Window onboardingWindow;
        private OnboardingState onboardingState;
        private StackPanel onboardingBody, onboardingActions;
        private readonly DispatcherTimer onboardingTimer = new DispatcherTimer();
        private int onboardingStep, onboardingProbeGeneration;
        private string onboardingDetection;
        private string onboardingLanguage;
        private TextBlock onboardingFeedback;
        private Button onboardingFinish;
        private bool onboardingStorageFailed;

        private static string OnboardingText(string korean, string english) { return L.Language == "en" ? english : korean; }

        private void InitializeOnboarding()
        {
            initialOnboardingPending = !demo && OnboardingStore.ShouldOffer(SettingsStore.DataDirectory);
            IsVisibleChanged += (s,e) => { if (IsLoaded && IsVisible) ShowInitialOnboarding(); };
            onboardingTimer.Interval = TimeSpan.FromMilliseconds(700);
            onboardingTimer.Tick += (s,e) => RefreshOnboardingProgress();
        }

        private void ShowInitialOnboarding()
        {
            if (!initialOnboardingPending || demo || exiting || !IsVisible || Opacity <= 0 || !ShowActivated) return;
            initialOnboardingPending = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => { if (!exiting && IsVisible && Opacity > 0 && ShowActivated) OpenOnboarding(); else initialOnboardingPending = true; }));
        }

        private void OpenOnboarding()
        {
            if (exiting) return;
            if (onboardingWindow != null) { onboardingWindow.Activate(); return; }
            var dialog = CreateOnboardingDialog();
            onboardingTimer.Start(); dialog.Show();
        }

        private Window CreateOnboardingDialog()
        {
            initialOnboardingPending = false;
            onboardingState = demo ? new OnboardingState() : OnboardingStore.Load(SettingsStore.DataDirectory);
            onboardingState.Shown = true;
            PersistOnboarding(); // Dismissal, X, or interruption must not repeatedly force setup.
            onboardingStep = 0; onboardingDetection = null;
            var dialog = CreateManagementDialog(OnboardingText("연결 시작 도우미","Connection guide"),620,690,390);
            onboardingWindow = dialog;
            BuildOnboardingShell();
            dialog.Closed += (s,e) => {
                onboardingProbeGeneration++; onboardingTimer.Stop();
                onboardingWindow = null; onboardingBody = null; onboardingActions = null; onboardingFeedback = null; onboardingFinish = null;
                // The service editor owns its connection operations. Closing this guide
                // must never cancel a browser login or discard an editor's unsaved input.
            };
            RenderOnboarding();
            return dialog;
        }

        private void BuildOnboardingShell()
        {
            onboardingLanguage = L.Language;
            onboardingWindow.Title = "Nephylo · " + OnboardingText("연결 시작 도우미","Connection guide");
            onboardingBody = ManagementBody(onboardingWindow,OnboardingText("첫 서비스를 연결해 보세요","Connect your first service"),
                OnboardingText("Nephylo 로그인 없이 모든 기능을 무료로 이용합니다. 연결할 AI 서비스를 선택해 주세요.","Use every feature free without a Nephylo account. Choose an AI service to connect."),out onboardingActions);
        }

        private void StopOnboarding()
        {
            onboardingProbeGeneration++; onboardingTimer.Stop();
            if (onboardingWindow != null) onboardingWindow.Close();
        }

        private void PersistOnboarding()
        {
            if (demo) return;
            try { OnboardingStore.Save(SettingsStore.DataDirectory,onboardingState); onboardingStorageFailed = false; }
            catch (IOException) { onboardingStorageFailed = true; }
            catch (UnauthorizedAccessException) { onboardingStorageFailed = true; }
        }

        private void RenderOnboarding()
        {
            if (onboardingBody == null) return;
            onboardingBody.Children.Clear(); onboardingActions.Children.Clear(); onboardingFeedback = null; onboardingFinish = null;
            string progress = onboardingStep == 0 ? OnboardingText("1 / 3  서비스 선택","1 / 3  Choose a service") : onboardingStep == 1 ? OnboardingText("2 / 3  연결 준비","2 / 3  Prepare connection") : OnboardingText("3 / 3  첫 사용량 확인","3 / 3  Verify your first reading");
            onboardingBody.Children.Add(ConnectionText(progress,12,"#647070",FontWeights.SemiBold,new Thickness(0,0,0,16)));
            if (onboardingStep == 0) BuildOnboardingChoices();
            else if (onboardingStep == 1) BuildOnboardingPreparation();
            else BuildOnboardingVerification();
            if (onboardingStorageFailed) onboardingBody.Children.Add(ConnectionText(OnboardingText("도우미 진행 상태를 저장하지 못했습니다. 연결 기능은 계속 사용할 수 있습니다.","Guide progress could not be saved. You can still connect a service."),11,"#647070",FontWeights.Normal,new Thickness(0,12,0,0)));
            onboardingActions.Children.Add(ManagementAction(OnboardingText("나중에","Later"),() => { if (onboardingWindow != null) onboardingWindow.Close(); }));
            if (onboardingStep > 0) onboardingActions.Children.Add(ManagementAction(OnboardingText("이전","Back"),() => { onboardingStep--; onboardingProbeGeneration++; RenderOnboarding(); if (onboardingStep == 1) StartOnboardingDiscovery(); }));
            if (onboardingStep == 0) onboardingActions.Children.Add(ManagementAction(OnboardingText("다음","Next"),() => { onboardingStep = 1; RenderOnboarding(); StartOnboardingDiscovery(); }));
            if (onboardingStep == 2) {
                onboardingFinish = ManagementAction(OnboardingText("위젯에서 보기","Show widget"),() => { if (onboardingWindow != null) onboardingWindow.Close(); Reveal(); });
                onboardingFinish.Visibility = Visibility.Collapsed; onboardingActions.Children.Add(onboardingFinish); UpdateOnboardingFeedback();
            }
        }

        private void BuildOnboardingChoices()
        {
            onboardingBody.Children.Add(ConnectionText(OnboardingText("사용 중인 서비스를 하나 골라 주세요.","Choose a service you already use."),15,"#252827",FontWeights.SemiBold,new Thickness(0,0,0,12)));
            var choices = new ListBox { Background = System.Windows.Media.Brushes.Transparent, Foreground = Fore, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Resources = ServiceNavigationResources() };
            System.Windows.Automation.AutomationProperties.SetName(choices,OnboardingText("처음 연결할 서비스","First service to connect"));
            ScrollViewer.SetHorizontalScrollBarVisibility(choices,ScrollBarVisibility.Disabled);
            foreach (string id in OnboardingStore.ServiceIds) {
                var row = new Grid(); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Image { Source = ServiceIcons.Get(id), Width = 18, Height = 18 });
                var name = ConnectionText(OnboardingName(id),14,"#252827",FontWeights.SemiBold,new Thickness(5,2,8,2)); Grid.SetColumn(name,1); row.Children.Add(name);
                var kind = ConnectionText(OnboardingMode(id),10,"#647070",FontWeights.Normal,new Thickness(0,2,0,2)); Grid.SetColumn(kind,2); row.Children.Add(kind);
                row.SizeChanged += (s,e) => {
                    bool narrow = e.NewSize.Width < 360;
                    Grid.SetColumn(kind,narrow ? 1 : 2); Grid.SetRow(kind,narrow ? 1 : 0);
                    kind.Margin = new Thickness(narrow ? 5 : 0,narrow ? 1 : 2,0,2);
                };
                var item = new ListBoxItem { Content = row, Tag = id, Padding = new Thickness(9,9,9,9), IsSelected = onboardingState.ServiceId == id };
                System.Windows.Automation.AutomationProperties.SetName(item,OnboardingName(id) + " · " + OnboardingMode(id)); choices.Items.Add(item);
            }
            choices.SelectionChanged += (s,e) => { if (e.OriginalSource != choices || choices.SelectedItem == null) return; onboardingState.ServiceId = (string)((ListBoxItem)choices.SelectedItem).Tag; onboardingState.Completed = false; PersistOnboarding(); };
            onboardingBody.Children.Add(choices);
            onboardingBody.Children.Add(ConnectionText(OnboardingText("선택만으로 기존 서비스가 바뀌지 않습니다. 연결 확인이나 기록 저장에 성공한 뒤 전환됩니다.","Choosing here keeps your current service. It switches only after a successful connection check or manual save."),11,"#647070",FontWeights.Normal,new Thickness(0,12,0,0)));
        }

        private string OnboardingName(string id) { return id == "higgsfield" ? OnboardingText("Higgsfield 웹","Higgsfield web") : UsageServiceName(id); }
        private string OnboardingMode(string id)
        {
            if (id == "eleven" || id == "openai") return OnboardingText("API 키","API key");
            if (id == "gemini" || id == "cursor" || id == "higgsfield-api") return OnboardingText("수동 기록","Manual entry");
            if (id == "claude") return OnboardingText("직접 조회 · 수동","Direct query / manual");
            return OnboardingText("브라우저 로그인","Browser sign-in");
        }

        private void BuildOnboardingPreparation()
        {
            string id = onboardingState.ServiceId;
            onboardingBody.Children.Add(ConnectionText(OnboardingName(id),20,"#252827",FontWeights.SemiBold,new Thickness(0,0,0,14)));
            onboardingFeedback = ConnectionText(onboardingDetection ?? OnboardingText("이 PC의 연결 준비 상태를 확인하고 있습니다…","Checking this PC for connection setup…"),12,"#647070",FontWeights.Normal,new Thickness(0,0,0,18)); onboardingBody.Children.Add(onboardingFeedback);
            string instructions;
            if (id == "codex") instructions = OnboardingText("Codex 앱 또는 CLI를 설치하고 로그인하세요. 연결 화면에서 ‘연결 확인’을 누르면 기존 로그인을 확인합니다. 로그인이 필요하면 ‘브라우저로 로그인’을 이용하세요. WSL 사용자는 연결 환경에서 배포판을 선택하세요.","Install and sign in to the Codex app or CLI. Choose ‘Check connection’ in service settings to use your existing sign-in, or ‘Sign in with browser’ if needed. For WSL, choose your distribution in connection settings.");
            else if (id == "claude") instructions = OnboardingText("Claude Pro·Max: Claude Code 2.1.304 이상에서 구독 계정으로 로그인한 뒤 ‘Claude Code 연결’을 누르세요. ‘잔여량 조회’와 위젯 새로고침으로 대화 없이 한도를 확인합니다. Free·API 키·웹 사용자는 수동 기록을 이용하세요. 설치만으로 잔량 조회가 완료되지는 않습니다.","Claude Pro/Max: sign in with your subscription account in Claude Code 2.1.304 or later, then choose ‘Connect Claude Code’. Use ‘Check remaining usage’ or refresh the widget to query limits without sending a conversation. Free, API-key and web users can enter readings manually. Installation alone does not verify usage.");
            else if (id == "eleven") instructions = OnboardingText("ElevenLabs에서 API 키를 만들고 ‘사용자 → 읽기’ 권한을 켜세요. 연결 화면에 키를 입력하고 ‘연결 확인 후 저장’을 누릅니다. 조회에 성공한 키만 이 PC에 암호화 저장합니다.","Create an ElevenLabs API key with User → Read permission. Enter it in service settings and choose ‘Verify and save’. Only successfully verified keys are encrypted and saved on this PC.");
            else if (id == "openai") instructions = OnboardingText("OpenAI 조직 소유자가 발급한 Admin API 키가 필요합니다. 일반 API 키와 ChatGPT 개인 구독은 이 연결에서 조회하지 않습니다. 월 API 비용을 표시하며, 예산은 선택 사항입니다.","Use an Admin API key from your OpenAI organization owner. This connection cannot read a personal ChatGPT subscription or use a regular API key. It shows monthly API cost; a budget is optional.");
            else if (id == "higgsfield") instructions = OnboardingText("연결 화면에서 ‘힉스필드 로그인’을 누르고 브라우저에서 로그인·CLI 접근을 승인하세요. 작업 공간이 여러 개면 사용할 공간을 선택한 뒤 잔량을 확인합니다. 이 창을 닫아도 로그인 대기는 유지됩니다.","Select ‘Sign in to Higgsfield’ in connection settings, then sign in and approve CLI access in your browser. If you have multiple workspaces, choose one and verify its balance. Closing this guide keeps the pending sign-in running.");
            else instructions = OnboardingText("이 서비스는 자동 조회하지 않습니다. 사용량 페이지에서 직접 확인한 값을 연결 화면에 입력하고 저장하세요. 위젯에는 수동 기록과 확인 시각이 표시됩니다. API 키는 필요하지 않습니다.","This service uses manual readings. Check its usage page, then enter and save the value in service settings. The widget labels it as manual and shows when you checked it. No API key is needed.");
            onboardingBody.Children.Add(ConnectionText(instructions,14,"#252827",FontWeights.Normal,new Thickness(0,0,0,18)));
            onboardingBody.Children.Add(ManagementAction(OnboardingText("연결 화면 열기","Open connection settings"),OpenOnboardingConnection));
        }

        private void OpenOnboardingConnection()
        {
            onboardingProbeGeneration++; onboardingStep = 2;
            onboardingState.Completed = false; PersistOnboarding(); RenderOnboarding();
            OpenServices(onboardingState.ServiceId);
            RefreshOnboardingProgress();
        }

        private void BuildOnboardingVerification()
        {
            onboardingBody.Children.Add(ConnectionText(OnboardingName(onboardingState.ServiceId),20,"#252827",FontWeights.SemiBold,new Thickness(0,0,0,14)));
            onboardingFeedback = ConnectionText("",13,"#252827",FontWeights.Normal,new Thickness(0,0,0,16)); onboardingBody.Children.Add(onboardingFeedback);
            onboardingBody.Children.Add(ManagementAction(OnboardingText("연결 화면 다시 열기","Return to connection settings"),() => OpenServices(onboardingState.ServiceId)));
        }

        private void RefreshOnboardingProgress()
        {
            if (exiting || onboardingWindow == null) return;
            if (onboardingLanguage != L.Language) {
                onboardingDetection = null; onboardingProbeGeneration++; BuildOnboardingShell(); RenderOnboarding();
                if (onboardingStep == 1) StartOnboardingDiscovery();
            }
            if (onboardingStep != 2 || onboardingFeedback == null) return;
            UpdateOnboardingFeedback();
        }

        private void UpdateOnboardingFeedback()
        {
            var view = ViewFor(onboardingState.ServiceId);
            bool ready = !demo && !IsServiceConnectionPending(onboardingState.ServiceId)
                && OnboardingStore.HasVerifiedUsage(view,IsServiceActive(onboardingState.ServiceId),ServiceHasSavedConnection(onboardingState.ServiceId),DateTimeOffset.Now);
            if (ready) {
                bool manual = UsageFreshness.Source(view) == "manual";
                onboardingFeedback.Text = manual ? OnboardingText("첫 수동 기록을 확인했습니다. 위젯에서 남은 양과 기록 시각을 볼 수 있습니다. 값이 바뀌면 다시 기록해 주세요.","Your first manual reading is ready. The widget shows its value and recorded time. Update it when usage changes.") : OnboardingText("첫 사용량을 확인했습니다. 이제 위젯에서 남은 양을 확인할 수 있습니다.","Your first usage reading is ready. You can now check it in the widget.");
                onboardingFeedback.Text += "\n\n" + L.Text(CompactUsageSummary.From(view,DateTimeOffset.Now).Value);
                if (!onboardingState.Completed) { onboardingState.Completed = true; PersistOnboarding(); }
                if (onboardingFinish != null) onboardingFinish.Visibility = Visibility.Visible;
            } else {
                onboardingState.Completed = false;
                string message = demo ? OnboardingText("미리보기에서는 실제 연결을 확인하지 않습니다.","Preview does not verify a real connection.") : !String.IsNullOrEmpty(view.Error) || view.Issue != null ? OnboardingText("아직 연결을 확인하지 못했습니다. 연결 화면의 해결 안내를 확인해 주세요. 기존 연결 정보는 유지됩니다.","The connection is not verified yet. Follow the help in connection settings. Your existing connection is preserved.") : OnboardingText("연결 화면에서 조회 또는 기록 저장을 완료해 주세요. 실제 사용량이 확인되면 여기에 완료 안내가 표시됩니다. 브라우저 로그인 중에는 이 창을 닫아도 괜찮습니다.","Finish the connection check or save a manual reading in service settings. This guide confirms completion when real usage is available. You can close this guide while browser sign-in is pending.");
                onboardingFeedback.Text = message;
                if (onboardingFinish != null) onboardingFinish.Visibility = Visibility.Collapsed;
            }
        }

        private async void StartOnboardingDiscovery()
        {
            int generation = ++onboardingProbeGeneration;
            string id = onboardingState.ServiceId;
            string codexPath = settings.CodexPath, distribution = id == "codex" ? settings.CodexWslDistribution : settings.ClaudeWslDistribution, claudeDirectory = settings.ClaudeWindowsConfigDirectory;
            bool saved = ServiceHasSavedConnection(id);
            onboardingDetection = OnboardingText("이 PC의 연결 준비 상태를 확인하고 있습니다…","Checking this PC for connection setup…");
            if (onboardingFeedback != null) onboardingFeedback.Text = onboardingDetection;
            // Passive local discovery only. No process launch, network, secret reads or
            // automatic environment switch. WSL discovery stays in the existing editor.
            string result = await Task.Run(() => {
                try {
                    if (saved) return "saved";
                    if (!String.IsNullOrEmpty(distribution) && (id == "codex" || id == "claude")) return "wsl";
                    if (id == "codex") { CodexService.FindExecutable(codexPath); return "installed"; }
                    if (id == "claude") return Directory.Exists(ClaudeUsageService.ResolveWindowsConfigDirectory(claudeDirectory)) ? "configuration" : "claude";
                    return "setup";
                } catch { return "setup"; }
            });
            if (exiting || onboardingWindow == null || generation != onboardingProbeGeneration || onboardingStep != 1 || onboardingState.ServiceId != id) return;
            onboardingDetection = result == "saved" ? OnboardingText("저장된 연결 정보가 있습니다. 연결 화면에서 실제 조회를 확인해 주세요.","Saved connection information was found. Verify a real reading in connection settings.")
                : result == "installed" ? OnboardingText("Codex 설치를 찾았습니다. 다음으로 로그인과 사용량을 확인합니다.","A Codex installation was found. Next, verify sign-in and usage.")
                : result == "configuration" ? OnboardingText("Claude 설정 폴더를 찾았습니다. 연결 화면의 ‘잔여량 조회’에서 CLI 버전·구독 로그인과 실제 한도를 확인하세요.","A Claude configuration folder was found. Use ‘Check remaining usage’ in connection settings to verify the CLI version, subscription sign-in and actual limits.")
                : result == "wsl" ? OnboardingText("저장된 WSL 환경이 있습니다. 연결 화면에서 배포판을 확인해 주세요.","A saved WSL environment was found. Check the distribution in connection settings.")
                : OnboardingText("아래 안내에 따라 연결을 준비해 주세요. 설치 여부만으로 연결 완료로 표시하지 않습니다.","Follow the steps below to prepare a connection. An installation alone is not a verified connection.");
            if (onboardingFeedback != null) onboardingFeedback.Text = onboardingDetection;
        }
    }
}
