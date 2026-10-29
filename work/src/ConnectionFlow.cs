using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace UsageWidget
{
    public static class FreeServiceSelectionFlow
    {
        public static async Task<UsageSnapshot> VerifyThenSelectAsync(Func<Task<UsageSnapshot>> verify, Action select)
        {
            if (verify == null || select == null) throw new ArgumentNullException("selection callbacks");
            var snapshot = await verify();
            if (snapshot == null) throw new InvalidOperationException("먼저 아래에서 연결하거나 사용량을 기록해 주세요. 기존 서비스는 계속 이용할 수 있습니다.");
            select();
            return snapshot;
        }
    }

    // A rejected candidate never replaces a working saved key.
    public static class ApiConnectionFlow
    {
        public static async Task<UsageSnapshot> CheckAndSaveAsync(string candidate, Func<string,Task<UsageSnapshot>> check, Action<string> save)
        {
            if (check == null || save == null) throw new ArgumentNullException("connection callbacks");
            string raw = candidate ?? "";
            string key = raw.Trim();
            if (key.Length == 0) throw new ArgumentException("API 키를 먼저 붙여 넣어 주세요.");
            if (raw.Length > 8192 || Array.Exists(key.ToCharArray(), Char.IsWhiteSpace) || Array.Exists(raw.ToCharArray(), Char.IsControl))
                throw new ArgumentException("API 키에 공백이나 줄바꿈이 있습니다. 키 전체를 다시 복사해 주세요.");
            var result = await check(key);
            if (result == null) throw new InvalidOperationException("사용량 응답을 확인하지 못했습니다.");
            save(key);
            return result;
        }
    }

    public partial class WidgetWindow
    {
        private readonly Dictionary<string,Action> connectionStateRefreshers = new Dictionary<string,Action>();

        private void RefreshConnectionEditor(string id)
        {
            Action refresh;
            if (connectionStateRefreshers.TryGetValue(id,out refresh)) refresh();
            if (refreshServiceNavigation != null) refreshServiceNavigation();
        }
        private const string ElevenKeysUrl = "https://elevenlabs.io/app/developers/api-keys";
        private const string OpenAiKeysUrl = "https://platform.openai.com/settings/organization/admin-keys";
        private const string CodexGuideUrl = "https://developers.openai.com/codex/cli/";
        private const string ClaudeGuideUrl = "https://code.claude.com/docs/en/setup";

        // Keep connection guidance readable without enlarging the compact widget rows.
        private static TextBlock ConnectionText(string text, double size, string color, FontWeight weight, Thickness? margin = null)
        {
            size = Math.Max(13,size);
            var block = T(text,size,color,weight,margin);
            block.LineHeight = Math.Ceiling(size * 1.5);
            block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            return block;
        }

        private sealed class ConnectionFeedback
        {
            public Border Root;
            public TextBlock Title;
            public TextBlock Detail;
            public Button Action;
            public string Url;
        }

        private ConnectionFeedback MakeConnectionFeedback(StackPanel parent)
        {
            var result = new ConnectionFeedback();
            var panel = new LocalizedStackPanel();
            result.Title = ConnectionText("",15,"#2448F5",FontWeights.SemiBold);
            result.Detail = ConnectionText("",13,"#566369",FontWeights.Normal,new Thickness(0,7,0,0));
            AutomationProperties.SetLiveSetting(result.Title,AutomationLiveSetting.Polite);
            result.Action = SmallAction("",() => { if (!String.IsNullOrEmpty(result.Url)) OpenOfficialPage(result.Url); });
            result.Action.Visibility = Visibility.Collapsed;
            result.Action.Margin = new Thickness(0,8,0,0);
            panel.Children.Add(result.Title); panel.Children.Add(result.Detail); panel.Children.Add(result.Action);
            result.Root = new LocalizedBorder { Child = panel, Background = BrushOf("#F0F3FA"), BorderBrush = BrushOf("#D5DDEA"), BorderThickness = new Thickness(1,1,1,1), CornerRadius = new CornerRadius(3), Padding = new Thickness(15), Margin = new Thickness(0,12,0,0) };
            parent.Children.Add(result.Root);
            return result;
        }

        private void Feedback(ConnectionFeedback target, string title, string detail, bool failed)
        {
            target.Title.Text = title; target.Detail.Text = detail;
            target.Title.Foreground = BrushOf(failed ? "#91501F" : "#2448F5");
            target.Root.BorderBrush = BrushOf(failed ? "#C9A987" : "#D5DDEA");
            target.Action.Visibility = Visibility.Collapsed; target.Url = null;
            if (refreshServiceNavigation != null) refreshServiceNavigation();
        }

        private void FeedbackIssue(ConnectionFeedback target, string id, Exception error)
        {
            RememberDiagnosticFailure(id,error);
            var issue = ConnectionDiagnostics.For(id,error);
            Feedback(target,issue.Title,issue.Message,true);
            if (!String.IsNullOrEmpty(issue.ActionUrl)) {
                target.Url = issue.ActionUrl; target.Action.Content = issue.ActionLabel + " ↗"; target.Action.Visibility = Visibility.Visible;
            }
        }

        private void ConnectionSteps(StackPanel body, string mode, params string[] steps)
        {
            var content = new LocalizedStackPanel();
            content.Children.Add(ConnectionText(mode,14,"#2448F5",FontWeights.SemiBold,new Thickness(0,0,0,8)));
            for (int i = 0; i < steps.Length; i++)
                content.Children.Add(ConnectionText((i+1) + "  " + steps[i],14,"#475256",FontWeights.Normal,new Thickness(0,0,0,7)));
            string id = body.Tag as string;
            var help = Details(body,id == "gemini" || id == "cursor" || id == "higgsfield-api" ? "기록 방법" : "연결 방법",content);
            help.Margin = new Thickness(0,0,0,12);
            help.IsExpanded = String.IsNullOrEmpty(id) || !ServiceHasSavedConnection(id);
        }

        private Expander Details(StackPanel body, string title, StackPanel content)
        {
            content.Margin = new Thickness(0);
            var expander = new LocalizedExpander { Header = title, Content = content, Foreground = Fore, Margin = new Thickness(0,10,0,0), FontSize = 14, IsExpanded = false };
            System.Windows.Data.BindingOperations.SetBinding(expander,AutomationProperties.NameProperty,new System.Windows.Data.Binding("Header") { Source = expander });
            body.Children.Add(expander); return expander;
        }

        private void AddConnectionHelp(ProviderView view)
        {
            var help = SmallAction("해결 방법",() => OpenServices(view.Id));
            AutomationProperties.SetName(help,view.Id + " 연결 문제 해결 방법");
            view.Body.Children.Add(help);
        }
    }
}
