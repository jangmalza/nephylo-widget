using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static class SupportDiagnosticsTests
{
    static int checks;
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value,string message) { if (!value) throw new Exception(message); checks++; }
    static object Get(object target,string name) { return target.GetType().GetField(name,Flags).GetValue(target); }
    static object Call(object target,string name,params object[] args) { return target.GetType().GetMethod(name,Flags).Invoke(target,args); }
    internal static int Run()
    {
        var now=DateTimeOffset.UtcNow;
        string privateText="PRIVATE_SENTINEL account@example.invalid C:\\Users\\PRIVATE_USER key-secret usage-987654";
        var supplied=new[] {
            new DiagnosticService { Id="claude",Environment=DiagnosticEnvironment.Wsl,State=DiagnosticState.Error,CliVersion="2.1.304",Source="code:"+privateText,LastSuccess=now,Failure=new DiagnosticFailure {Kind=DiagnosticError.Authentication,At=now} },
            new DiagnosticService { Id="codex",Environment=(DiagnosticEnvironment)999,State=(DiagnosticState)999,CliVersion="2.1.304\n"+privateText,Source=privateText,Failure=new DiagnosticFailure {Kind=(DiagnosticError)999,At=now} },
            new DiagnosticService { Id=privateText,CliVersion=privateText,Source=privateText }
        };
        string report=SupportDiagnostics.Create(new Version(1,25,33),new Version(10,0,26100),true,true,supplied,now);
        Check(report.Contains("[claude]")&&report.Contains("CLI version (last observed): 2.1.304")&&report.Contains("authentication"),"Observed version and typed errors are useful without provider text");
        foreach(string forbidden in privateText.Split(' '))Check(!report.Contains(forbidden),"Provider identity/path/key/value sentinel must not be serialized");
        Check(report.Contains("Source: code")&&report.Contains("Environment: unknown")&&report.Contains("State: unknown"),"Source suffixes and undefined enum values are bounded");
        Check(!report.Contains("999")&&!report.Contains("2.1.304\nPRIVATE"),"Unknown enum integers and multiline version injection are excluded");
        foreach(string version in new[]{"2.1.304 PRIVATE","C:/private.exe","private@example.invalid",new string('9',8000),"2.1.3\n","2.1.3\u001b[31m"})
            Check(SupportDiagnostics.SafeVersion(version)=="not_recorded","Only bounded numeric versions enter support output");
        var untrusted=new ConnectionException(new ConnectionIssue {Title=privateText,Message=privateText,ActionUrl="https://invalid.example/"+privateText});
        untrusted.Data["ClaudeQuotaKind"]=privateText;
        Check(SupportDiagnostics.Classify(untrusted)==DiagnosticError.Connection,"Unrecognized provider Data values are not returned as a category");
        Check(SupportDiagnostics.Classify(ClaudeQuotaService.Problem("version"))==DiagnosticError.Version,"Known CLI incompatibility has a stable diagnostic category");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.CodexMissing()))==DiagnosticError.Installation,"Codex installation errors retain a structured category");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.CodexLogin()))==DiagnosticError.Authentication,"Codex authentication is distinct from generic connectivity");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.CodexUnsupported()))==DiagnosticError.Version,"Codex version problems are diagnosable without raw server output");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.Network("codex")))==DiagnosticError.Network,"App-authored network errors retain their category");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.Http("openai",403,privateText)))==DiagnosticError.Permission,"Permission failures do not copy HTTP body text");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.Http("eleven",429,privateText)))==DiagnosticError.RateLimited,"Rate limits are distinguished from other server errors");
        Check(SupportDiagnostics.Classify(new ConnectionException(ConnectionDiagnostics.Http("eleven",401,"{\"detail\":{\"code\":\"missing_permissions\"}}")))==DiagnosticError.Permission,"Recognized provider permission codes remain distinct from generic HTTP 401 authentication");
        Check(SupportDiagnostics.Classify(new IOException(privateText))==DiagnosticError.Storage,"Storage failures do not require the exception message");
        Check(SupportDiagnostics.Classify(new TimeoutException(privateText))==DiagnosticError.Timeout,"Timeouts have a stable category");
        Check(SupportDiagnostics.Classify(new AggregateException(new UnauthorizedAccessException(privateText)))==DiagnosticError.Permission,"A single wrapped failure is classified without its stack");
        Check(report.Length<4096,"Support output remains small and contains only known services");

        string root=Path.Combine(Path.GetTempPath(),"NephyloDiagnosticTests-"+Guid.NewGuid().ToString("N"));
        string previous=SettingsStore.DataDirectoryOverride,language=L.Language;
        WidgetWindow window=null;
        try {
            Directory.CreateDirectory(root);SettingsStore.DataDirectoryOverride=root;L.Language="ko";
            window=new WidgetWindow(true);
            ((DispatcherTimer)Get(window,"timer")).Stop();((Forms.NotifyIcon)Get(window,"tray")).Visible=false;
            var view=(ProviderView)Get(window,"claudeView");
            view.Source="cli";view.LastSuccess=now;view.Error=privateText;view.Issue=new ConnectionIssue {Title=privateText,Message=privateText};
            view.Snapshot=new UsageSnapshot {Plan=privateText,Note=privateText,Rows=new List<UsageRow>{new UsageRow {Label=privateText,RemainingPercent=75,DisplayValue=privateText}}};
            ((WidgetSettings)Get(window,"settings")).ClaudeWindowsConfigDirectory=Path.Combine(root,"PRIVATE_PROFILE");
            Call(window,"RememberDiagnosticFailure","claude",ClaudeQuotaService.Problem("authentication"));
            string selected=(string)Call(window,"BuildDiagnosticReport","claude");
            Check(selected.Contains("[claude]")&&!selected.Contains("[codex]")&&selected.Contains("State: error"),"Selected-service report uses the actual view state and isolates the selected service");
            foreach(string forbidden in privateText.Split(' '))Check(!selected.Contains(forbidden),"Actual view text must not leak into a diagnostic");
            Check(!selected.Contains("PRIVATE_PROFILE")&&!selected.Contains("75%")&&selected.Contains("Last error category: authentication"),"Settings paths and usage are omitted while failure category is retained");
            string copied=null;var feedback=new TextBlock {Visibility=Visibility.Collapsed};
            Call(window,"CopyDiagnosticReport","claude",feedback,new Action<string>(text=>copied=text));
            Check(copied!=null&&copied.Contains("[claude]")&&feedback.Text.Contains("복사했습니다")&&feedback.Visibility==Visibility.Visible,"The actual copy handler provides the selected report and exposes success feedback without using the real clipboard");
            L.Language="en";
            Call(window,"CopyDiagnosticReport","claude",feedback,new Action<string>(text=>copied=text));
            Check(feedback.Text.StartsWith("Diagnostics copied"),"Copy feedback follows the selected language");
            Call(window,"CopyDiagnosticReport","claude",feedback,new Action<string>(text=>{throw new InvalidOperationException(privateText);}));
            Check(feedback.Text.StartsWith("Could not copy")&&!feedback.Text.Contains("PRIVATE"),"Clipboard contention is recoverable and cannot expose exception text");
            var controls=(StackPanel)Call(window,"MakeDiagnosticsControls",new Func<string>(()=>"claude"));
            var button=controls.Children.OfType<Button>().Single();
            Check(controls.Children.OfType<TextBlock>().Single().Visibility==Visibility.Collapsed,"Empty copy feedback does not reserve a blank footer row");
            Check((string)button.Content=="Copy diagnostics"&&((string)button.ToolTip).Contains("Accounts, keys, paths"),"Support action has a localized privacy explanation");
            controls.Measure(new Size(260,200));controls.Arrange(new Rect(0,0,260,200));
            Check(controls.DesiredSize.Width<=260&&!window.IsVisible&&!window.IsLoaded,"Diagnostic controls fit a narrow area without opening real UI");
            view.Error=null;view.Issue=null;Call(window,"RefreshSucceeded",view);
            selected=(string)Call(window,"BuildDiagnosticReport","claude");
            Check(selected.Contains("State: ready")&&selected.Contains("Last error category: none"),"Successful refresh clears obsolete diagnostic failure state");
            view.Snapshot=null;
            Check(((string)Call(window,"BuildDiagnosticReport","claude")).Contains("State: nodata"),"A timestamp alone cannot report a successful current reading");
            string all=(string)Call(window,"BuildDiagnosticReport",new object[]{null});
            Check(SupportDiagnostics.Services.All(id=>all.Contains("["+id+"]")),"Version screen can obtain all supported service states without new queries");
        } finally {
            if(window!=null){((DispatcherTimer)Get(window,"timer")).Stop();((Forms.NotifyIcon)Get(window,"tray")).Visible=false;window.Close();((Forms.NotifyIcon)Get(window,"tray")).Dispose();}
            SettingsStore.DataDirectoryOverride=previous;L.Language=language;
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
        return checks;
    }
}
