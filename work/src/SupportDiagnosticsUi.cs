using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        readonly Dictionary<string,DiagnosticFailure> diagnosticFailures = new Dictionary<string,DiagnosticFailure>();

        private void RememberDiagnosticFailure(string serviceId, Exception error)
        {
            if (!SupportDiagnostics.Services.Contains(serviceId)) return;
            diagnosticFailures[serviceId] = new DiagnosticFailure { Kind = SupportDiagnostics.Classify(error), At = DateTimeOffset.UtcNow };
        }

        private string BuildDiagnosticReport(string selectedService)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshots = new List<DiagnosticService>();
            foreach (string id in SupportDiagnostics.Services) {
                if (selectedService != null && selectedService != id) continue;
                var view = ViewFor(id); if (view == null) continue;
                string source = UsageFreshness.Source(view);
                bool manual = source == "manual";
                var state = view.Busy ? DiagnosticState.Checking : !String.IsNullOrEmpty(view.Error) || view.Issue != null ? DiagnosticState.Error :
                    !UsageFreshness.ObservedAt(view).HasValue || view.Snapshot == null || view.Snapshot.Rows == null || view.Snapshot.Rows.Count == 0 ? DiagnosticState.NoData : manual ? DiagnosticState.Manual :
                    UsagePresentation.Describe(view,now).Stale ? DiagnosticState.Stale : DiagnosticState.Ready;
                var environment = manual ? DiagnosticEnvironment.Manual : id == "codex" ? (String.IsNullOrEmpty(settings.CodexWslDistribution) ? DiagnosticEnvironment.Windows : DiagnosticEnvironment.Wsl) :
                    id == "claude" ? (String.IsNullOrEmpty(settings.ClaudeWslDistribution) ? DiagnosticEnvironment.Windows : DiagnosticEnvironment.Wsl) :
                    id == "higgsfield" ? DiagnosticEnvironment.Windows : DiagnosticEnvironment.Api;
                DiagnosticFailure failure; diagnosticFailures.TryGetValue(id,out failure);
                snapshots.Add(new DiagnosticService { Id = id, Environment = environment, State = state, Source = source,
                    CliVersion = id == "claude" ? claudeQuota.KnownVersion(settings.ClaudeWslDistribution,settings.ClaudeWindowsConfigDirectory) : null,
                    LastSuccess = UsageFreshness.ObservedAt(view), Failure = failure,
                    RetryAt = view.RefreshSchedule.Failures > 0 ? (DateTimeOffset?)view.RefreshSchedule.NextAttempt : null });
            }
            return SupportDiagnostics.Create(Assembly.GetExecutingAssembly().GetName().Version,Environment.OSVersion.Version,Environment.Is64BitProcess,settings.AutoUpdateEnabled,snapshots,now);
        }

        private void CopyDiagnosticReport(string serviceId, TextBlock feedback, Action<string> copy)
        {
            try {
                copy(BuildDiagnosticReport(serviceId));
                feedback.Text = L.Text("진단 정보를 복사했습니다. 문의할 때 붙여 넣어 주세요.");
            } catch {
                feedback.Text = L.Text("진단 정보를 복사하지 못했습니다. 잠시 후 다시 시도해 주세요.");
            }
            feedback.Visibility = Visibility.Visible;
        }

        private StackPanel MakeDiagnosticsControls(Func<string> selectedService)
        {
            var panel = new LocalizedStackPanel();
            var feedback = ConnectionText("",11,"#647070",FontWeights.Normal,new Thickness(0,5,0,0));
            feedback.MaxWidth = 400;
            feedback.Visibility = Visibility.Collapsed;
            var copy = ManagementAction("진단 정보 복사",() => CopyDiagnosticReport(selectedService == null ? null : selectedService(),feedback,Clipboard.SetText));
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            copy.ToolTip = L.Text("앱·확인된 CLI 버전, 연결 상태와 오류 종류를 복사합니다. 계정·키·경로·사용량·원본 로그는 포함하지 않습니다.");
            panel.Children.Add(copy); panel.Children.Add(feedback);
            return panel;
        }
    }
}
