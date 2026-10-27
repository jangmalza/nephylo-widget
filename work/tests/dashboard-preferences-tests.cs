using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using UsageWidget;

internal static class DashboardPreferencesTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int count;
    private static void Check(bool condition,string reason) { count++; if (!condition) throw new Exception(reason); }
    private static T Field<T>(object owner,string name) { return (T)owner.GetType().GetField(name,Private).GetValue(owner); }
    private static void Set(object owner,string name,object value) { owner.GetType().GetField(name,Private).SetValue(owner,value); }
    private static object Call(object owner,string name,params object[] args) {
        try { return owner.GetType().GetMethod(name,Private).Invoke(owner,args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) {
        yield return root;
        foreach (object value in LogicalTreeHelper.GetChildren(root)) {
            var child = value as DependencyObject;
            if (child != null) foreach (var nested in Walk(child)) yield return nested;
        }
    }
    private static string Text(DependencyObject root) { return String.Join("\n",Walk(root).OfType<TextBlock>().Select(value => value.Text)); }

    [STAThread]
    public static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(),"nephylo-dashboard-preferences-" + Guid.NewGuid().ToString("N"));
        SettingsStore.DataDirectoryOverride = isolated;
        SettingsStore.StartupRegistrySubKeyOverride = @"Software\UsageWidget\Tests\dashboard-preferences-" + Guid.NewGuid().ToString("N");
        ClaudeUsageService.ConfigDirectoryOverride = Path.Combine(isolated,"claude");
        WidgetWindow owner = null;
        try {
            TestStorage(isolated);
            string demoPath = Path.Combine(isolated,"demo"); SettingsStore.DataDirectoryOverride = demoPath;
            owner = new WidgetWindow(true);
            Field<DispatcherTimer>(owner,"timer").Stop(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Visible = false;
            Call(owner,"ShowDemo");
            TestSelection(owner);
            TestControls(owner,demoPath,isolated);
            TestTabOrder(owner);
            TestDashboardIntegration(owner);
            Check(!Directory.Exists(demoPath),"Demo preference changes do not create user settings or history files");
            Console.WriteLine("Dashboard preferences tests passed: " + count + " assertions. Isolated settings and unloaded WPF controls; no real accounts or network requests.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (owner != null) {
                Set(owner,"demo",true); Field<DispatcherTimer>(owner,"timer").Stop();
                owner.Close(); Field<CodexService>(owner,"codex").Dispose(); Field<System.Windows.Forms.NotifyIcon>(owner,"tray").Dispose();
            }
            SettingsStore.DataDirectoryOverride = null; SettingsStore.StartupRegistrySubKeyOverride = null;
            ClaudeUsageService.ConfigDirectoryOverride = null; L.Language = "ko";
            if (Directory.Exists(isolated)) Directory.Delete(isolated,true);
        }
    }

    private static void TestStorage(string isolated)
    {
        var empty = SettingsStore.Load();
        Check(empty.DashboardFavorites.Length == 0 && empty.DashboardPreferredMetrics.Count == 0,"Missing preference fields default to empty");
        Directory.CreateDirectory(isolated);
        string path = Path.Combine(isolated,"settings.json");
        File.WriteAllText(path,"{\"Theme\":\"light\",\"RefreshSeconds\":300}");
        var legacy = SettingsStore.Load();
        Check(legacy.Theme == "light" && legacy.RefreshSeconds == 300 && legacy.DashboardFavorites.Length == 0,"Older settings remain intact without preference fields");
        var settings = new WidgetSettings { DashboardFavorites = new[] { "codex","unknown","CLAUDE","codex","higgsfield-api" },
            DashboardPreferredMetrics = new Dictionary<string,string> {
                {"codex","주간 한도"},{"claude","Weekly ∑ 한국어"},{"unknown","Unknown"},{"openai","bad\nlabel"},
                {"gemini",new String('x',161)},{"cursor"," "},{"eleven","0 credits"},{"higgsfield",null}
            } };
        SettingsStore.Save(settings); var loaded = SettingsStore.Load();
        Check(loaded.DashboardFavorites.SequenceEqual(new[] { "codex","claude","higgsfield-api" }),"Favorites normalize known IDs and remove duplicates");
        Check(loaded.DashboardPreferredMetrics.Count == 3,"Only known services and bounded printable labels are persisted");
        Check(loaded.DashboardPreferredMetrics["codex"] == "주간 한도" && loaded.DashboardPreferredMetrics["claude"] == "Weekly ∑ 한국어","Unicode raw labels survive round trip");
        Check(!Object.ReferenceEquals(settings.DashboardFavorites,loaded.DashboardFavorites) && !Object.ReferenceEquals(settings.DashboardPreferredMetrics,loaded.DashboardPreferredMetrics),"Load does not reuse source collections");
        Check(settings.DashboardFavorites.Length == 5 && settings.DashboardPreferredMetrics.Count == 8,"Save normalization does not mutate callers");
        var destination = new WidgetSettings(); WidgetWindow.CopyDashboardPreferences(loaded,destination);
        destination.DashboardFavorites[0] = "cursor"; destination.DashboardPreferredMetrics["codex"] = "5시간 한도";
        Check(loaded.DashboardFavorites[0] == "codex" && loaded.DashboardPreferredMetrics["codex"] == "주간 한도","Settings dialog copy creates independent collections");
        Check(destination.DashboardPreferredMetrics.ContainsKey("claude"),"Disconnected provider preferences are preserved for reconnection");
        File.WriteAllText(path,"{\"DashboardFavorites\":{\"codex\":true},\"DashboardPreferredMetrics\":{\"codex\":123,\"claude\":null,\"eleven\":\"credits\"}}");
        loaded = SettingsStore.Load();
        Check(loaded.DashboardFavorites.Length == 0 && loaded.DashboardPreferredMetrics.Count == 1,"Malformed persisted collection entries fail closed independently");
        File.WriteAllText(path,"{\"DashboardPreferredMetrics\":[\"codex\",\"quota\"]}");
        Check(SettingsStore.Load().DashboardPreferredMetrics.Count == 0,"Array-shaped map is ignored");
        Check(SettingsStore.ValidDashboardMetricLabel(new String('a',160)) && !SettingsStore.ValidDashboardMetricLabel(new String('a',161)),"Metric label length is bounded at 160 characters");
        Check(!SettingsStore.ValidDashboardMetricLabel("a\t") && !SettingsStore.ValidDashboardMetricLabel("\0a"),"Control characters are rejected");
    }

    private static void TestSelection(WidgetWindow owner)
    {
        var settings = Field<WidgetSettings>(owner,"settings");
        var now = DateTimeOffset.Now;
        var shortRow = new UsageRow { Label = "짧은 한도",RemainingPercent = 10,ResetsAt = now.AddHours(3) };
        var weekRow = new UsageRow { Label = "주간 한도",RemainingPercent = 70,ResetsAt = now.AddDays(3) };
        var view = new ProviderView { Id = "codex",Source = "api",LastSuccess = now,Snapshot = new UsageSnapshot { Rows = new List<UsageRow> { shortRow,weekRow } } };
        settings.DashboardPreferredMetrics.Clear();
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),shortRow),"Automatic mode selects the lowest currently usable percentage");
        settings.DashboardPreferredMetrics["codex"] = weekRow.Label;
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),weekRow),"A unique current preferred metric overrides the automatic selection");
        weekRow.ResetsAt = now.AddSeconds(-1);
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),shortRow),"An expired preferred metric falls back to a current metric");
        Check(settings.DashboardPreferredMetrics["codex"] == weekRow.Label,"Temporary unavailability does not erase the preference");
        weekRow.ResetsAt = now.AddDays(3); view.Snapshot.Rows.Remove(weekRow);
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),shortRow),"A missing selected metric falls back without crashing");
        view.Snapshot.Rows.Add(weekRow);
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),weekRow),"A returning preferred metric is automatically restored");
        view.Snapshot.Rows.Add(new UsageRow { Label = weekRow.Label,RemainingPercent = 60 });
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),shortRow),"Ambiguous duplicate labels cannot be selected as an exact preference");
        view.Snapshot.Rows.RemoveAt(2); view.Error = "connection failed";
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",view),shortRow),"Provider errors use the same cached fallback instead of treating a saved preference as current");
        view.Error = null; view.Snapshot.Rows.Clear();
        Check(Call(owner,"DashboardOverviewRow",view) == null,"Empty snapshots return no representative row");
        Check(Call(owner,"DashboardOverviewRow",new object[] { null }) == null,"Absent providers return no representative row");
        settings.DashboardFavorites = new[] { "codex" };
        Check((bool)Call(owner,"DashboardFavorite","codex") && !(bool)Call(owner,"DashboardFavorite","eleven"),"Favorites are per service");
    }

    private static void TestControls(WidgetWindow owner,string demoPath,string isolated)
    {
        var settings = Field<WidgetSettings>(owner,"settings");
        var codex = Field<ProviderView>(owner,"codexView");
        settings.DashboardFavorites = new string[0]; settings.DashboardPreferredMetrics.Clear();
        var panel = (FrameworkElement)Call(owner,"BuildDashboardPreferencesControls",codex);
        var favorite = Walk(panel).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "favorite:codex");
        var combo = Walk(panel).OfType<ComboBox>().Single();
        Check(AutomationProperties.GetAutomationId(combo) == "metric:codex" && combo.Items.Count == 3,"Preference controls expose stable IDs and automatic plus available metric choices");
        Check(combo.IsTextSearchEnabled && combo.MaxDropDownHeight == 280 && AutomationProperties.GetHelpText(combo).Contains("Alt+"),"The metric selector supports keyboard lookup and explains how to open its list");
        Check(Text(panel).Contains("개요 표시") && Text(panel).Contains("72%"),"Automatic mode previews the actual representative allowance");
        favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((bool)Call(owner,"DashboardFavorite","codex"),"The favorite button adds a service");
        Check(favorite.FontWeight == FontWeights.SemiBold && AutomationProperties.GetItemStatus(favorite).Contains("추가됨"),"A selected favorite has visible and accessible state");
        Check(Text(panel).Contains("미리보기에 적용됨"),"Demo updates report an in-memory change without claiming a disk save");
        favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!(bool)Call(owner,"DashboardFavorite","codex"),"The favorite button removes a service");
        Check(favorite.FontWeight == FontWeights.Normal,"Removing a favorite restores its unselected visual state");
        combo.SelectedIndex = 2;
        Check(settings.DashboardPreferredMetrics["codex"] == "주간 한도","Selecting a displayed metric persists the raw label");
        Check(Object.ReferenceEquals(Call(owner,"DashboardOverviewRow",codex),codex.Snapshot.Rows[1]),"The selected overview row updates immediately");
        Check(Text(panel).Contains("86%"),"The representative metric preview updates without waiting for polling");
        combo.SelectedIndex = 0;
        Check(!settings.DashboardPreferredMetrics.ContainsKey("codex"),"Returning to automatic removes only that provider preference");
        settings.DashboardPreferredMetrics["codex"] = "Unavailable quota";
        panel = (FrameworkElement)Call(owner,"BuildDashboardPreferencesControls",codex); combo = Walk(panel).OfType<ComboBox>().Single();
        Check(!(combo.SelectedItem as ComboBoxItem).IsEnabled && Text(panel).Contains("선택은 유지됩니다"),"Missing preferences remain visible with an explicit automatic fallback notice");
        var automaticAction = Walk(panel).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "preference-auto:codex");
        automaticAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!settings.DashboardPreferredMetrics.ContainsKey("codex") && combo.SelectedIndex == 0,"The fallback action immediately restores automatic selection");
        Check(!Directory.Exists(demoPath),"Interactive demo preferences do not write settings");
        settings.DashboardPreferredMetrics["codex"] = "주간 한도";
        panel = (FrameworkElement)Call(owner,"BuildDashboardPreferencesControls",codex); combo = Walk(panel).OfType<ComboBox>().Single();
        Set(owner,"dashboardPreferenceRefreshPending",true);
        Call(owner,"DashboardPreferenceDropDownClosed",combo,EventArgs.Empty);
        Check(!Field<bool>(owner,"dashboardPreferenceRefreshPending"),"Closing a metric dropdown releases a pending provider refresh");
        Set(owner,"dashboardPreferenceRefreshPending",true); Call(owner,"ResetDashboardPreferenceState");
        Check(Field<ComboBox>(owner,"dashboardPreferenceMetric") == null && !Field<bool>(owner,"dashboardPreferenceRefreshPending"),"Closing the dashboard can release preference control references");

        string blocker = Path.Combine(isolated,"not-a-directory"); File.WriteAllText(blocker,"blocked");
        SettingsStore.DataDirectoryOverride = blocker; Set(owner,"demo",false);
        try {
            panel = (FrameworkElement)Call(owner,"BuildDashboardPreferencesControls",codex);
            combo = Walk(panel).OfType<ComboBox>().Single(); favorite = Walk(panel).OfType<Button>().Single();
            string before = settings.DashboardPreferredMetrics["codex"];
            combo.SelectedIndex = 0;
            Check(settings.DashboardPreferredMetrics["codex"] == before,"Disk failure rolls back the preferred metric");
            Check(((ComboBoxItem)combo.SelectedItem).Tag as string == before,"Disk failure restores the visible metric selection");
            favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!settings.DashboardFavorites.Contains("codex"),"Disk failure rolls back favorites");
            Check(Text(panel).Contains("저장하지 못했습니다"),"Save failures show a local actionable error");
            panel = (FrameworkElement)Call(owner,"BuildDashboardPreferencesControls",codex);
            Check(Text(panel).Contains("저장하지 못했습니다"),"Provider-driven rebuilds do not erase a failed-save explanation");
            var status = Walk(panel).OfType<TextBlock>().Single(block => AutomationProperties.GetAutomationId(block) == "preference-feedback:codex");
            Check(AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite,"Save feedback is announced to assistive technology without taking focus");
            var visible = ((List<ProviderView>)Call(owner,"DashboardViews")).Select(view => view.Id).ToList();
            Check(visible.SequenceEqual(new[] { "codex","gemini","cursor","higgsfield-api" }),"Preference controls leave every connected service listed without a single-service limit");
            var plan = Field<ServicePlan>(owner,"servicePlan"); string legacy = plan.ActiveServiceId;
            plan.Select("eleven");
            try { Check(((List<ProviderView>)Call(owner,"DashboardViews")).Select(view => view.Id).SequenceEqual(visible),"A legacy single-service selection neither hides connected services nor lists an unconnected one"); }
            finally { plan.Select(legacy); }
        }
        finally { SettingsStore.DataDirectoryOverride = demoPath; Set(owner,"demo",true); }
        codex.Error = "connection failed";
        try {
            panel = (FrameworkElement)Call(owner,"BuildDashboardPreferencesControls",codex);
            Check(Text(panel).Contains("개요의 이전 값"),"Stale representative readings are explicitly marked as previous values");
        } finally { codex.Error = null; }
        var notices = Field<System.Collections.IDictionary>(owner,"dashboardPreferenceFeedback");
        notices.Clear();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void TestTabOrder(WidgetWindow owner)
    {
        var settings = Field<WidgetSettings>(owner,"settings");
        var previous = new WidgetSettings(); WidgetWindow.CopyDashboardPreferences(settings,previous);
        try {
            // WPF sorts navigation candidates by TabIndex before visual order.
            // The preferences must participate in the detail page's ordinary
            // sequence, rather than jumping ahead of the header actions.
            foreach (bool unavailable in new[] { false,true }) {
                if (unavailable) settings.DashboardPreferredMetrics["codex"] = "Unavailable quota";
                else settings.DashboardPreferredMetrics.Remove("codex");
                var detail = (FrameworkElement)Call(owner,"BuildDashboardServiceDetail","codex");
                var controls = Walk(detail).OfType<Control>().Where(control => control is Button || control is ComboBox).ToList();
                var expected = new List<string> { "dashboard-back:services","dashboard-connect:codex","dashboard-history:codex",
                    "dashboard-analysis:codex","favorite:codex","metric:codex" };
                if (unavailable) expected.Add("preference-auto:codex");
                var visualOrder = controls.Select(AutomationProperties.GetAutomationId).ToList();
                Check(visualOrder.SequenceEqual(expected),"Detail actions, favorite, metric and optional recovery action follow the visual reading order");
                var tabOrder = controls.Select((control,index) => new { Control=control,Index=index })
                    .OrderBy(item => System.Windows.Input.KeyboardNavigation.GetTabIndex(item.Control)).ThenBy(item => item.Index)
                    .Select(item => AutomationProperties.GetAutomationId(item.Control));
                Check(tabOrder.SequenceEqual(expected),"WPF TabIndex ordering does not promote preferences ahead of preceding detail actions");
                Check(controls.Where(control => AutomationProperties.GetAutomationId(control).StartsWith("favorite:")
                    || AutomationProperties.GetAutomationId(control).StartsWith("metric:") || AutomationProperties.GetAutomationId(control).StartsWith("preference-auto:"))
                    .All(control => control.ReadLocalValue(System.Windows.Input.KeyboardNavigation.TabIndexProperty) == DependencyProperty.UnsetValue),
                    "Preference controls retain the inherited/default tab order in both normal and fallback states");
            }
        }
        finally { WidgetWindow.CopyDashboardPreferences(previous,settings); }
    }

    private static void TestDashboardIntegration(WidgetWindow owner)
    {
        new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var window = (Window)Call(owner,"BuildDashboardWindow");
        var settings = Field<WidgetSettings>(owner,"settings");
        var codex = Field<ProviderView>(owner,"codexView");
        settings.DashboardPreferredMetrics.Clear(); settings.DashboardFavorites = new string[0];
        try {
            Set(owner,"dashboardSelectedService","codex"); Call(owner,"NavigateDashboard","detail");
            var content = Field<ContentControl>(owner,"dashboardContent");
            var combo = Walk(content.Content as DependencyObject).OfType<ComboBox>().Single();
            var favorite = Walk(content.Content as DependencyObject).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "favorite:codex");
            combo.SelectedIndex = 2; favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var refresh = Field<DispatcherOperation>(owner,"dashboardRefreshOperation");
            Call(owner,"RefreshDashboardIfOpen");
            Check(refresh != null && Object.ReferenceEquals(refresh,Field<DispatcherOperation>(owner,"dashboardRefreshOperation")),"Preference and provider changes share a single queued refresh");
            DrainDispatcher();
            var after = Walk(content.Content as DependencyObject).OfType<ComboBox>().Single();
            Check(!Object.ReferenceEquals(combo,after) && ((ComboBoxItem)after.SelectedItem).Tag as string == "주간 한도","A completed detail refresh retains the saved metric selection");
            Check((bool)Call(owner,"DashboardFavorite","codex"),"A batched metric and favorite update retains both changes");
            Check(Text(content.Content as DependencyObject).Contains("미리보기에 적용됨"),"Save feedback survives the first scheduled dashboard rebuild");
            var stableContent = content.Content; Call(owner,"RenderDashboard",false);
            Check(Object.ReferenceEquals(stableContent,content.Content),"A repeated unchanged refresh does not rebuild preference controls");
            var changed = Field<Window>(owner,"dashboardWindow");
            Check(Object.ReferenceEquals(changed,window) && !window.IsVisible,"Preferences reuse the dashboard without opening extra windows");
            Call(owner,"NavigateDashboard","overview");
            Check(Field<ComboBox>(owner,"dashboardPreferenceMetric") == null,"Leaving the detail page releases its metric control");
            var grid = Field<System.Windows.Controls.Primitives.UniformGrid>(owner,"dashboardOverviewGrid");
            Check(AutomationProperties.GetAutomationId(grid.Children[0]) == "dashboard-service:codex","A favorite service moves to the beginning of the overview");
            Check(Text(grid.Children[0]).Contains("86%") && Text(grid.Children[0]).Contains("주간 한도"),"Overview uses the selected weekly allowance rather than the default shorter allowance");
            var favoriteFilter = Walk(content.Content as DependencyObject).OfType<CheckBox>().Single(check => AutomationProperties.GetAutomationId(check) == "dashboard-favorites-only");
            favoriteFilter.IsChecked = true; favoriteFilter.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            grid = Field<System.Windows.Controls.Primitives.UniformGrid>(owner,"dashboardOverviewGrid");
            Check(grid.Children.Count == 1 && AutomationProperties.GetAutomationId(grid.Children[0]) == "dashboard-service:codex","Favorites-only filtering displays only favorites among connected services");
            Call(owner,"NavigateDashboard","detail");
            after = Field<ComboBox>(owner,"dashboardPreferenceMetric");
            Set(owner,"dashboardPreferenceRefreshPending",true);
            Call(owner,"DashboardPreferenceDropDownClosed",combo,EventArgs.Empty);
            Check(Field<bool>(owner,"dashboardPreferenceRefreshPending"),"A stale dropdown close cannot consume a new page's pending refresh");
            Call(owner,"DashboardPreferenceDropDownClosed",after,EventArgs.Empty); DrainDispatcher();
            Check(!Field<bool>(owner,"dashboardPreferenceRefreshPending"),"The current dropdown close catches up queued data safely");
        }
        finally { Call(owner,"CloseDashboard"); }
        Check(Field<ComboBox>(owner,"dashboardPreferenceMetric") == null && Field<DispatcherOperation>(owner,"dashboardRefreshOperation") == null,"Dashboard close releases preference controls and pending work");
    }
}
