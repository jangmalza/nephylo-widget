using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UsageWidget
{
    internal static class UsageFreshness
    {
        internal static string Source(ProviderView view)
        {
            if (!String.IsNullOrEmpty(view.Source)) return view.Source;
            if (view.Id == "gemini" || view.Id == "cursor" || view.Id == "higgsfield-api") return "manual";
            if (view.Id == "claude") return "code";
            return "api";
        }

        internal static bool Automatic(ProviderView view) { return Source(view) == "api" || Source(view) == "cli"; }

        internal static bool IsCode(ProviderView view)
        {
            string source = Source(view);
            return source == "code" || source.StartsWith("code:",StringComparison.Ordinal);
        }

        internal static DateTimeOffset? ObservedAt(ProviderView view)
        { return IsCode(view) ? view.ReceiptObservedAt ?? view.LastSuccess : view.LastSuccess; }

        internal static bool IsObservationStale(ProviderView view, DateTimeOffset now)
        {
            bool code = IsCode(view);
            if (!code && !Automatic(view)) return false;
            var observed = ObservedAt(view);
            // Preserve undated legacy API snapshots. Code readings, unlike API
            // callers, require a confirmed receipt time before they are current.
            if (!observed.HasValue) return code;
            return observed.Value > now || now - observed.Value > TimeSpan.FromMinutes(code ? 30 : 10);
        }

        internal static string Stamp(DateTimeOffset time, DateTimeOffset now)
        {
            return time.ToLocalTime().ToString(time.ToLocalTime().Date == now.ToLocalTime().Date ? "HH:mm" : "MM/dd HH:mm", CultureInfo.InvariantCulture);
        }

        internal static string Footer(IEnumerable<ProviderView> services, int refreshSeconds)
        {
            var items = services.ToList();
            if (items.Count == 0) return "서비스 연결 필요";
            var parts = new List<string>();
            if (items.Any(Automatic)) parts.Add("자동 조회 " + (refreshSeconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + "분");
            int manual = items.Count(v => Source(v) == "manual");
            if (manual > 0) parts.Add(items.Count == manual ? "수동 기록 · 자동 갱신 안 됨" : "수동 기록 " + manual + "개");
            if (items.Any(v => Source(v) == "code")) parts.Add("Claude 응답 시 갱신");
            if (items.Any(v => !String.IsNullOrEmpty(v.Error))) parts.Add("일부 갱신 실패");
            else if (items.All(v => !v.LastSuccess.HasValue)) parts.Add("연결 확인 필요");
            return String.Join(" · ", parts);
        }
    }

    public partial class WidgetWindow
    {
        private void RefreshSucceeded(ProviderView view)
        {
            if (view.Id != null) diagnosticFailures.Remove(view.Id);
            view.RefreshSchedule.Succeeded(DateTimeOffset.Now,settings.RefreshSeconds);
        }

        private void RefreshFailed(ProviderView view, Exception error)
        {
            RememberDiagnosticFailure(view.Id,error);
            var connection = error as ConnectionException;
            view.RefreshSchedule.Failed(DateTimeOffset.Now,settings.RefreshSeconds,connection == null ? null : connection.RetryNotBefore);
        }

        private void AddRetryStatus(ProviderView view)
        {
            if (demo || view.Busy || view.RefreshSchedule.Failures == 0) return;
            string retry = view.RefreshSchedule.NextAttempt > DateTimeOffset.Now
                ? "다음 재시도 " + view.RefreshSchedule.NextAttempt.ToLocalTime().ToString("HH:mm:ss",CultureInfo.InvariantCulture)
                : "연결 재시도 대기";
            var label = T(retry,13,"#566369",System.Windows.FontWeights.Normal,new System.Windows.Thickness(0,7,0,0));
            label.ToolTip = "연결 오류가 반복되면 조회 간격을 늘립니다. 서비스의 요청 제한 시간이 지나면 다시 확인합니다.";
            view.Body.Children.Add(label);
        }

        private void NetworkAvailabilityChanged(object sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable || demo || Dispatcher.HasShutdownStarted) return;
            try {
                Dispatcher.BeginInvoke(new Action(() => {
                    if (exiting) return;
                    var now = DateTimeOffset.Now;
                    foreach (var view in new[] { codexView,elevenView,openAiView,higgsfieldView,claudeView }) view.RefreshSchedule.NetworkRestored(now);
                    nextRefresh = DateTimeOffset.MinValue;
                }));
            } catch (InvalidOperationException) { /* The dispatcher can shut down after the check above. */ }
        }

        private string CurrentServiceSelection()
        {
            if (!ProFeaturesAvailable) return servicePlan.ActiveServiceId;
            return settings.ServiceOrder.FirstOrDefault(id => serviceCards.ContainsKey(id) && serviceCards[id].Visibility == System.Windows.Visibility.Visible) ?? "codex";
        }
    }
}
