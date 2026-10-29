using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    internal sealed class WidgetRelease
    {
        public string version { get; set; }
        public string file { get; set; }
        public string sha256 { get; set; }
        internal static WidgetRelease Parse(string json)
        {
            var data = new JavaScriptSerializer { MaxJsonLength = 16384 }.Deserialize<WidgetRelease>(json);
            Version parsed;
            if (data == null || !Version.TryParse(data.version,out parsed) || parsed.Major < 0 || parsed.Build < 0 || parsed.Revision > 0 ||
                data.file != "UsageWidget-v" + data.version + "-Windows11.zip" || data.sha256 == null ||
                !System.Text.RegularExpressions.Regex.IsMatch(data.sha256,"\\A[a-fA-F0-9]{64}\\z")) throw new InvalidOperationException("배포 정보를 확인하지 못했습니다.");
            return data;
        }
    }

    public partial class WidgetWindow
    {
        private const string WidgetWebsite = "https://nephylo.com/";
        private Window releaseWindow;
        private void AddInstallationControl(StackPanel body, TextBlock status)
        {
            var install = new LocalizedButton { Content = "이 PC에 설치 · 업데이트", ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,10,0,0) };
            install.Click += (s,e) => {
                if (demo) { status.Text = "미리보기에서는 실제 파일이나 시작 프로그램을 변경하지 않습니다."; return; }
                WidgetInstallation.InstallInteractive(this,false);
            };
            body.Children.Add(install);
        }
        private void OpenReleaseInfo()
        {
            if (releaseWindow != null) { releaseWindow.Activate(); return; }
            var dialog = CreateManagementDialog("버전과 업데이트",520,620);
            releaseWindow = dialog;
            var installed = Assembly.GetExecutingAssembly().GetName().Version;
            StackPanel footer;
            var page = ManagementBody(dialog,"버전과 업데이트","현재 버전 " + installed.ToString(3),out footer);
            var body = ManagementSection(page,"자동 업데이트");
            var automatic = new LocalizedCheckBox { IsChecked = settings.AutoUpdateEnabled };
            ManagementRow(body,"자동으로 최신 버전 유지","새 버전을 확인하고 다운로드·설치한 뒤 다시 시작합니다.",automatic);
            body.Children.Add(ManagementDescription("시작 후와 6시간마다 확인합니다. 설정·로그인 화면을 사용 중이면 설치를 기다리며, 연결 정보와 이력을 유지합니다.",new Thickness(0,0,0,12)));
            if (!demo && !AutoUpdateInstaller.CanAutoInstall())
                body.Children.Add(ManagementDescription("ZIP 실행 중에는 자동 설치하지 않습니다. Nephylo-Setup.exe로 한 번 설치하면 자동 업데이트를 사용할 수 있습니다.",new Thickness(0,0,0,12)));
            updateStatusText = T(updateStatus,14,"#252827",FontWeights.Medium); updateStatusText.LineHeight = 21;
            body.Children.Add(new Border { Child = updateStatusText, Padding = new Thickness(12), Margin = new Thickness(0,4,0,16),
                Background = BrushOf("#F0F3FA"), BorderBrush = BrushOf("#D6D9D7"), BorderThickness = new Thickness(3,0,0,0) });
            bool reverting = false;
            automatic.Click += (s,e) => {
                if (reverting) return;
                bool previous = settings.AutoUpdateEnabled;
                settings.AutoUpdateEnabled = automatic.IsChecked == true;
                try {
                    if (!demo) SettingsStore.Save(settings);
                    if (settings.AutoUpdateEnabled) nextUpdateCheckUtc = DateTime.UtcNow;
                    SetUpdateStatus(settings.AutoUpdateEnabled ? "자동 업데이트가 켜졌습니다." : "자동 업데이트가 꺼졌습니다. 새 버전 확인으로 직접 업데이트할 수 있습니다.");
                } catch {
                    settings.AutoUpdateEnabled = previous;
                    reverting = true; automatic.IsChecked = previous; reverting = false;
                    SetUpdateStatus("설정을 저장하지 못했습니다. 저장 폴더의 쓰기 권한을 확인하고 다시 시도해 주세요.");
                }
            };
            var actions = new WrapPanel();
            updateCheckButton = new LocalizedButton { Content = "새 버전 확인", ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Margin = new Thickness(0,0,8,8) };
            updateCheckButton.Click += async (s,e) => await CheckForUpdateAsync(true);
            actions.Children.Add(updateCheckButton);
            updateInstallButton = new LocalizedButton { Content = "지금 설치하고 다시 시작", ContentTemplate = ActionTextTemplate(), FontSize = 14, MinHeight = 38, Margin = new Thickness(0,0,8,8) };
            updateInstallButton.Click += async (s,e) => await ApplyPreparedUpdateAsync(true);
            actions.Children.Add(updateInstallButton); body.Children.Add(actions);
            body.Children.Add(SmallAction("다운로드 페이지 ↗",() => OpenOfficialPage(WidgetWebsite + "#download")));
            body.Children.Add(MakeDiagnosticsControls(null));
            var options = new LocalizedStackPanel();
            options.Children.Add(ManagementDescription("다운로드한 업데이트의 배포 서명과 파일 무결성을 검증합니다. Windows 코드 서명 인증서는 아직 적용되지 않았습니다.",new Thickness(0,0,0,8)));
            AddInstallationControl(options,updateStatusText);
            var details = Details(page,"추가 설치 옵션",options);
            details.FontSize = 13; details.Foreground = BrushOf("#566369");
            footer.Children.Add(ManagementAction("닫기",() => dialog.Close()));
            dialog.Closed += (s,e) => { releaseWindow = null; updateStatusText = null; updateCheckButton = null; updateInstallButton = null; };
            SetUpdateStatus(updateStatus);
            dialog.Show();
        }
    }
}
