using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using UsageWidget;

internal static class UsageTrackingTests
{
    private static int checks;
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddDays(-2);

    private static void Expect(bool condition, string name)
    {
        checks++;
        if (!condition) throw new Exception(name);
    }

    private static UsageSnapshot Snapshot(double? percent, DateTimeOffset? reset, string label)
    {
        return new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label = label ?? "주간", RemainingPercent = percent, Remaining = percent, Limit = 100, ResetsAt = reset } } };
    }

    private static List<UsageNotice> Observe(UsageJournal journal, double? percent, DateTimeOffset at, DateTimeOffset? reset, string source)
    {
        return journal.Observe("codex", Snapshot(percent, reset, null), at, source ?? "api", at);
    }

    private static void DedupAndHysteresis()
    {
        var journal = new UsageJournal(null, true);
        var reset = Start.AddDays(7);
        Expect(Observe(journal, 20, Start, reset, null).Count == 1, "First low sample at inclusive threshold alerts.");
        Expect(Observe(journal, 19, Start, reset, null).Count == 0, "Repeated render cannot alert.");
        Expect(Observe(journal, 19, Start.AddMinutes(1), reset, null).Count == 0, "Low observations remain quiet.");
        Expect(journal.GetHistory().Count == 1, "API samples throttle below five minutes.");
        Observe(journal, 25, Start.AddMinutes(2), reset, null);
        Expect(Observe(journal, 19, Start.AddMinutes(3), reset, null).Count == 0, "Exactly threshold plus five does not rearm.");
        Observe(journal, 26, Start.AddMinutes(4), reset, null);
        Expect(Observe(journal, 19, Start.AddMinutes(5), reset, null).Count == 1, "Recovery beyond hysteresis rearms low alert.");
        Expect(journal.GetHistory().Count == 2, "Five-minute fresh sample is recorded.");
        Expect(Observe(journal, 19, Start.AddMinutes(6), reset.AddDays(7), null).Count == 1, "New cycle allows a new low alert.");
        Expect(journal.GetHistory().Count == 3, "New cycle records immediately.");
        var options = journal.Options;
        options.LowEnabled = false;
        Expect(journal.Options.LowEnabled, "Options getter is a defensive copy.");
        var history = journal.GetHistory();
        history[0].Label = "modified";
        Expect(journal.GetHistory()[0].Label == "주간", "History entries are defensive copies.");
        journal.ClearHistory();
        Observe(journal, 19, Start.AddMinutes(6), reset.AddDays(7), null);
        Expect(journal.GetHistory().Count == 0, "Clear does not replay cached snapshot or alarm.");
        journal.ForgetService("codex");
        Expect(Observe(journal, 19, Start.AddMinutes(7), reset, null).Count == 1, "Account change forgets low-alert state.");
        Expect(journal.GetHistory().Count == 1, "Fresh account observations create history.");
        journal.ForgetService("codex");
        Expect(journal.GetHistory().Count == 1, "Forget retains labeled historical observations.");
    }

    private static void Resets()
    {
        var journal = new UsageJournal(null, true);
        var deadline = Start.AddMinutes(1);
        Observe(journal, 50, Start, deadline, null);
        Expect(journal.CheckResetReminders(deadline.AddSeconds(-1), new[] { "codex" }).Count == 0, "Reset never fires early.");
        var notice = journal.CheckResetReminders(deadline, new[] { "codex" });
        Expect(notice.Count == 1 && notice[0].Kind == "reset" && notice[0].Message.Contains("예정된 초기화 시각"), "Deadline is clearly a scheduled reminder.");
        Expect(journal.CheckResetReminders(deadline.AddMinutes(1), new[] { "codex" }).Count == 0, "Deadline fires once.");
        Expect(journal.GetHistory().Count == 1 && journal.GetHistory()[0].RemainingPercent == 50, "Reset cannot synthesize a replenished balance.");

        journal = new UsageJournal(null, true);
        Observe(journal, 50, Start, deadline, null);
        var next = Observe(journal, 100, deadline.AddSeconds(5), deadline.AddDays(7), null);
        Expect(next.Count == 2 && next[0].Kind == "reset" && next[1].Kind == "reset-confirmed", "Next-cycle response preserves the due schedule and separately confirms newly received replenishment.");
        Expect(journal.GetHistory().Count == 2, "Next cycle records without waiting five minutes.");

        journal = new UsageJournal(null, true);
        Observe(journal, 50, Start, deadline, null);
        var late = journal.CheckResetReminders(deadline.AddMinutes(6), new[] { "codex" });
        Expect(late.Count == 1 && late[0].Kind == "reset-late" && late[0].ScheduledAt == deadline, "Missed known schedules become a catch-up notice with the original deadline.");
        Expect(journal.CheckResetReminders(deadline.AddMinutes(7), new[] { "codex" }).Count == 0, "Missed deadline is consumed.");
        Expect(journal.GetRecentNotices().Count == 1 && journal.GetRecentNotices()[0].Message.Contains("예정된 초기화 시각"), "Late notices are retained without claiming confirmed replenishment.");

        journal = new UsageJournal(null, true);
        Observe(journal, 50, Start, deadline, null);
        Expect(journal.CheckResetReminders(deadline.AddDays(31), new[] { "codex" }).Count == 0, "Ancient schedules beyond the 30-day retention do not create catch-up noise.");

        journal = new UsageJournal(null, true);
        Observe(journal, 5, Start, Start.AddMinutes(-1), null);
        Expect(journal.CheckResetReminders(Start, new[] { "codex" }).Count == 0, "Initial expired window causes no startup reset storm.");
        Expect(Observe(journal, 5, Start.AddMinutes(1), Start.AddMinutes(-1), null).Count == 0, "An expired low balance is not a current low alert.");
        journal = new UsageJournal(null, true);
        Expect(journal.Observe("codex", Snapshot(5, Start.AddMinutes(1), null), Start, "code", Start.AddMinutes(2)).Count == 0, "A fresh-enough source payload with an already-expired window cannot cause a low alert.");
        Expect(journal.CheckResetReminders(Start.AddMinutes(2), new[] { "codex" }).Count == 0, "Registration after a deadline cannot retroactively invent an upcoming schedule.");

        journal = new UsageJournal(null, true);
        journal.Observe("cursor", Snapshot(50, Start.AddDays(1), "월간"), Start, "manual", Start);
        Expect(journal.CheckResetReminders(Start.AddDays(1), new[] { "cursor" }).Count == 1, "A registered manual deadline survives one idle day.");
        journal.Observe("claude", Snapshot(50, Start.AddHours(2), "세션"), Start, "code", Start);
        Expect(journal.CheckResetReminders(Start.AddHours(2), new[] { "claude" }).Count == 1, "A registered Claude Code deadline survives two idle hours.");

        journal = new UsageJournal(null, true);
        Observe(journal, 50, Start, deadline, null);
        Expect(journal.CheckResetReminders(deadline, new[] { "eleven" }).Count == 0, "Disconnected service cannot notify.");
        journal.ForgetService("codex");
        Expect(journal.CheckResetReminders(deadline, new[] { "codex" }).Count == 0, "Forgotten deadline cannot notify.");
        Observe(journal, 50, Start, deadline, null);
        journal.Observe("codex", Snapshot(50, null, "추가 크레딧"), Start.AddSeconds(5), "api", Start.AddSeconds(5));
        Expect(journal.CheckResetReminders(deadline, new[] { "codex" }).Count == 0, "Removed service rows cannot leave ghost reset alarms.");

        journal = new UsageJournal(null, true);
        Observe(journal, 50, Start, deadline, "code");
        var renamed = journal.Observe("codex", Snapshot(null, null, "5시간 한도 · 수신 대기"), deadline.AddSeconds(1), "code", deadline.AddSeconds(1));
        Expect(renamed.Count == 1 && renamed[0].Kind == "reset", "Renamed completed row preserves a due scheduled reminder.");
        Expect(journal.CheckResetReminders(deadline.AddSeconds(2), new[] { "codex" }).Count == 0, "Renamed deadline is consumed once.");

        journal = new UsageJournal(null, true);
        Observe(journal, 50, Start, deadline, "code");
        Expect(Observe(journal, 50, deadline, deadline.AddDays(1), "manual").Count == 0, "Switching sources cancels the previous source deadline.");
        var twoRows = Snapshot(50, deadline, null);
        twoRows.Rows.Add(new UsageRow { Label = "다른 한도", RemainingPercent = 50, ResetsAt = deadline.AddMinutes(1) });
        journal = new UsageJournal(null, true);
        journal.Observe("codex", twoRows, Start.AddSeconds(10), "api", Start.AddSeconds(10));
        Observe(journal, 50, Start.AddSeconds(5), deadline, null);
        Expect(journal.CheckResetReminders(deadline.AddMinutes(1), new[] { "codex" }).Count == 2, "Out-of-order full snapshots cannot prune newer row schedules.");
    }

    private static void FreshnessManualAndOptions()
    {
        var journal = new UsageJournal(null, true);
        Expect(journal.Observe("codex", Snapshot(5, null, null), Start, "code", Start.AddMinutes(6)).Count == 0 && journal.GetHistory().Count == 0, "Stale source timestamps never record or alert.");
        Expect(journal.Observe("codex", Snapshot(5, null, null), Start.AddMinutes(2), "code", Start).Count == 0, "Far-future source timestamp is rejected.");
        Observe(journal, 50, Start, null, "manual");
        Observe(journal, 49, Start.AddSeconds(1), null, "manual");
        Observe(journal, 48, Start.AddSeconds(1), null, "manual");
        Expect(journal.GetHistory().Count == 2, "Manual entries record each new timestamp once.");
        Observe(journal, 45, Start.AddSeconds(2), null, "api");
        Expect(journal.GetHistory().Count == 3, "Source changes record immediately.");

        var options = journal.Options;
        options.HistoryEnabled = false; options.LowEnabled = false; options.ResetEnabled = false;
        journal.SaveOptions(options);
        Expect(Observe(journal, 5, Start.AddMinutes(6), Start.AddMinutes(7), null).Count == 0, "Disabled low alerts stay silent.");
        Expect(journal.CheckResetReminders(Start.AddMinutes(7), new[] { "codex" }).Count == 0, "Disabled reset alerts stay silent.");
        Expect(journal.GetHistory().Count == 3, "Disabled history stops collection and retains earlier samples.");
        options.ResetEnabled = true;
        journal.SaveOptions(options);
        Expect(journal.CheckResetReminders(Start.AddMinutes(8), new[] { "codex" }).Count == 0, "Re-enabling does not replay disabled deadline.");
        options.LowEnabled = true; options.HistoryEnabled = true; options.LowThresholdPercent = 999;
        journal.SaveOptions(options);
        Expect(journal.Options.LowThresholdPercent == 99, "Threshold normalizes outside valid range.");

        journal = new UsageJournal(null, true);
        Expect(Observe(journal, Double.NaN, Start, null, null).Count == 0, "NaN percentage cannot alert.");
        Expect(Observe(journal, -1, Start.AddMinutes(5), null, null).Count == 0, "Negative percentage cannot alert.");
        Expect(Observe(journal, Double.PositiveInfinity, Start.AddMinutes(10), null, null).Count == 0, "Infinite percentage cannot alert.");
        Expect(Observe(journal, 101, Start.AddMinutes(15), null, null).Count == 0, "Over-100 percentage cannot alert.");
        Expect(journal.GetHistory().All(point => !point.RemainingPercent.HasValue), "Invalid percentages persist as null, never NaN.");
    }

    private static void Disk(string directory)
    {
        var journal = new UsageJournal(directory, false);
        DateTimeOffset deadline = Start.AddMinutes(1);
        var snapshot = Snapshot(10, deadline, null);
        snapshot.Note = "sk-secret-in-note"; snapshot.Plan = "sk-secret-in-plan"; snapshot.Rows[0].Detail = "sk-secret-in-detail";
        journal.Observe("codex", snapshot, Start, "api", Start);
        string path = Path.Combine(directory, "usage-journal.json");
        string json = File.ReadAllText(path);
        var stored = new JavaScriptSerializer().Deserialize<UsageJournal.JournalDocument>(json);
        Expect(!json.Contains("sk-secret") && !json.Contains("Detail") && stored.Analyses.Single().Plan.Length == 64, "Persistence excludes raw response details, plan, note and credentials; only an opaque tier fingerprint is retained.");
        journal = new UsageJournal(directory, false);
        Expect(Observe(journal, 9, Start.AddSeconds(1), deadline, null).Count == 0, "Low dedup survives process restart.");
        Expect(journal.GetHistory().Count == 1, "Sample throttle survives process restart.");
        Expect(journal.CheckResetReminders(deadline, new[] { "codex" }).Count == 1, "Known imminent deadline survives restart.");
        journal = new UsageJournal(directory, false);
        Expect(journal.CheckResetReminders(deadline.AddSeconds(1), new[] { "codex" }).Count == 0, "Reset dedup survives process restart.");
        var options = journal.Options; options.LowThresholdPercent = 15; options.HistoryEnabled = false; journal.SaveOptions(options);
        journal = new UsageJournal(directory, false);
        Expect(journal.Options.LowThresholdPercent == 15 && !journal.Options.HistoryEnabled, "Options persist.");
        Expect(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic persistence leaves no temporary files.");

        options = journal.Options; options.LowEnabled = true; options.HistoryEnabled = true; journal.SaveOptions(options);
        journal.ForgetService("codex");
        Observe(journal, 50, Start.AddHours(1), Start.AddHours(2), null);
        int countBeforeFailure = journal.GetHistory().Count;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool readFailed = false;
            try { new UsageJournal(directory, false); } catch (IOException) { readFailed = true; }
            Expect(readFailed, "Real read errors are not mistaken for corrupt or missing history.");
            options = journal.Options; options.LowThresholdPercent = 35;
            bool saveFailed = false;
            try { journal.SaveOptions(options); } catch (IOException) { saveFailed = true; }
            Expect(saveFailed && journal.Options.LowThresholdPercent == 15, "Failed option save rolls back in-memory options.");
            saveFailed = false;
            try { journal.ClearHistory(); } catch (IOException) { saveFailed = true; }
            Expect(saveFailed && journal.GetHistory().Count == countBeforeFailure, "Failed clear keeps existing history in memory.");
            saveFailed = false;
            try { Observe(journal, 5, Start.AddHours(1).AddMinutes(5), Start.AddHours(2), null); } catch (IOException) { saveFailed = true; }
            Expect(saveFailed && journal.GetHistory().Count == countBeforeFailure, "Failed observation save rolls back history and notification state.");
        }
        Expect(Observe(journal, 5, Start.AddHours(1).AddMinutes(5), Start.AddHours(2), null).Count == 1, "Failed low notification can retry the same observation after storage recovers.");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool saveFailed = false;
            try { journal.CheckResetReminders(Start.AddHours(2), new[] { "codex" }); } catch (IOException) { saveFailed = true; }
            Expect(saveFailed, "Failed reset dedup persistence surfaces the error.");
        }
        Expect(journal.CheckResetReminders(Start.AddHours(2), new[] { "codex" }).Count == 1, "Failed reset notification retries after storage recovers.");

        File.WriteAllText(path, "{broken JSON");
        journal = new UsageJournal(directory, false);
        Expect(journal.GetHistory().Count == 0 && journal.Options.HistoryEnabled, "Corrupt journal recovers with empty history and defaults.");
        string[] preserved = Directory.GetFiles(directory, "usage-journal.unreadable-*.json");
        Expect(preserved.Length == 1 && File.ReadAllText(preserved[0]) == "{broken JSON" && !File.Exists(path), "A corrupt journal is moved aside before anything can save over it.");
        Observe(journal, 60, Start, null, null);
        Expect(new UsageJournal(directory, false).GetHistory().Count == 1 && File.ReadAllText(preserved[0]) == "{broken JSON", "The next valid save starts a new journal and leaves the preserved file untouched.");
        string newer = "{\"Version\": 9, \"Options\": {\"LowThresholdPercent\": 1}}";
        File.WriteAllText(path, newer);
        Expect(new UsageJournal(directory, false).Options.LowThresholdPercent == 20, "Unknown schema is not misinterpreted.");
        Expect(Directory.GetFiles(directory, "usage-journal.unreadable-*.json").Count(file => File.ReadAllText(file) == newer) == 1, "A newer-format journal left by a rolled-back update is preserved.");
        foreach (string mistyped in new[] {
            "{\"Version\":99999999999}",
            "{\"Version\":1,\"States\":[{\"ServiceId\":\"codex\",\"Label\":\"x\",\"Source\":\"api\",\"LowAlerted\":\"yes\"}]}",
            "{\"Version\":1,\"Points\":[{\"ServiceId\":\"codex\",\"Label\":\"x\",\"Source\":\"api\",\"ResolutionMinutes\":\"x\"}]}" }) {
            File.WriteAllText(path, mistyped);
            bool constructed = true;
            try { Expect(new UsageJournal(directory, false).GetHistory().Count == 0, "A mistyped journal starts empty."); }
            catch (Exception) { constructed = false; }
            Expect(constructed && !File.Exists(path) && Directory.GetFiles(directory, "usage-journal.unreadable-*.json").Count(file => File.ReadAllText(file) == mistyped) == 1, "Valid JSON with a wrong field type or range is preserved instead of failing every start: " + mistyped);
        }
        File.WriteAllText(path, "{held while moving");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            // Reading is allowed, but the move needs delete sharing until the handle closes.
            var release = new Thread(() => { Thread.Sleep(120); held.Dispose(); });
            release.Start();
            bool moved = true;
            try { new UsageJournal(directory, false); }
            catch (IOException) { moved = false; }
            finally { release.Join(); }
            Expect(moved && !File.Exists(path) && Directory.GetFiles(directory, "usage-journal.unreadable-*.json").Count(file => File.ReadAllText(file) == "{held while moving") == 1, "A briefly held unreadable journal is moved aside after a retry.");
        }
        using (var large = new FileStream(path, FileMode.Create)) large.SetLength(16777217);
        Expect(new UsageJournal(directory, false).GetHistory().Count == 0 && !File.Exists(path), "An oversized journal starts empty.");
        Expect(Directory.GetFiles(directory, "usage-journal.unreadable-*.json").Count(file => new FileInfo(file).Length == 16777217) == 1, "An oversized journal is preserved instead of being replaced.");
        File.WriteAllText(path, "{\"Version\":1,\"Options\":{\"LowEnabled\":false,\"ResetEnabled\":false,\"HistoryEnabled\":true,\"LowThresholdPercent\":30},\"Points\":[],\"States\":[]}");
        var migrated = new UsageJournal(directory,false).Options;
        Expect(!migrated.LowEnabled && migrated.LowThresholdPercent==30 && migrated.HiggsfieldCreditsThreshold==20 && migrated.HiggsfieldApiUsdThreshold==5,"Old settings preserve existing choices and acquire safe absolute-threshold defaults.");

        var document = new UsageJournal.JournalDocument { Version = 1, Options = new UsageTrackingOptions(), Points = new List<UsageJournal.PointDocument>() };
        for (int index = 0; index < UsageJournal.MaximumPoints + 12; index++)
            document.Points.Add(new UsageJournal.PointDocument { ServiceId = "codex", Label = "주간", Source = "api", ObservedAt = Start.AddSeconds(index).ToString("o", CultureInfo.InvariantCulture), RemainingPercent = 50 });
        document.Points.Add(new UsageJournal.PointDocument { ServiceId = "codex", Label = "old", Source = "api", ObservedAt = DateTimeOffset.UtcNow.AddDays(-31).ToString("o", CultureInfo.InvariantCulture), RemainingPercent = 50 });
        File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = 16777216 }.Serialize(document));
        journal = new UsageJournal(directory, false);
        Expect(journal.GetHistory().Count <= UsageJournal.MaximumPoints, "Record count remains bounded.");
        Expect(journal.GetHistory()[0].ObservedAt == Start, "Compaction preserves the oldest in-range observation instead of deleting its date range.");
        Expect(journal.GetHistory().All(point => point.Label != "old"), "Thirty-day retention removes older measurements.");

        string invalidDirectory = Path.Combine(directory, "blocked");
        File.WriteAllText(invalidDirectory, "not a directory");
        var blocked = new UsageJournal(invalidDirectory, false);
        bool failed = false;
        try { blocked.SaveOptions(new UsageTrackingOptions()); }
        catch (IOException) { failed = true; }
        Expect(failed, "Real write errors surface to the UI instead of silently losing history.");
        new UsageJournal(invalidDirectory, true).SaveOptions(new UsageTrackingOptions());
        Expect(File.ReadAllText(invalidDirectory) == "not a directory", "Preview mode performs no disk writes.");
    }

    private static List<UsageNotice> Absolute(UsageJournal journal, string service, double value, DateTimeOffset now, string source)
    {
        return journal.Observe(service, new UsageSnapshot { Rows = new List<UsageRow> { new UsageRow { Label="Balance", Remaining=value } } }, now, source, now);
    }

    private static void WslSourceIsolation(string directory)
    {
        string ubuntu = UsageSourceIdentity.ForWsl("api", "Ubuntu");
        string kali = UsageSourceIdentity.ForWsl("api", "kali-linux");
        string claude = UsageSourceIdentity.ForWsl("code", "Ubuntu");
        string customWindows = "code:win:" + new string('a',64);
        Expect(UsageSourceIdentity.IsValid(customWindows) && UsageSourceIdentity.IsWindowsConfig(customWindows) && !UsageSourceIdentity.IsWsl(customWindows),"Custom Windows Claude sources remain distinct from legacy Windows and WSL.");
        Expect(!UsageSourceIdentity.IsValid("code:win:" + new string('A',64)) && !UsageSourceIdentity.IsValid("code:win:../path"),"Only bounded canonical Windows source hashes enter history.");
        Expect(UsageSourceIdentity.ForWsl("api", "") == "api" && UsageSourceIdentity.ForWsl("code", null) == "code", "Windows retains legacy journal identities.");
        Expect(ubuntu == UsageSourceIdentity.ForWsl("api", "Ubuntu") && ubuntu != kali && ubuntu != claude, "WSL identities are stable and separate distribution and provider transport.");
        Expect(UsageSourceIdentity.IsValid(ubuntu) && UsageSourceIdentity.IsValid(claude) && !ubuntu.Contains("Ubuntu"), "Valid WSL identity contains only an opaque distribution hash.");
        foreach (string bad in new[] { "api:wsl:Ubuntu", "api:wsl:" + new string('g',64), "api:wsl:" + new string('a',63), "manual:wsl:" + new string('a',64), ubuntu + "\n", "api:wsl:" + new string('A',64) })
            Expect(!UsageSourceIdentity.IsValid(bad), "Noncanonical WSL source identity is rejected.");

        var journal = new UsageJournal(Path.Combine(directory, "wsl-sources"), false);
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        var deadline = at.AddMinutes(1);
        Observe(journal, 50, at, deadline, "api");
        Expect(Observe(journal, 50, deadline, deadline.AddHours(3), ubuntu).Count == 0, "WSL source change cancels the Windows deadline before it can notify.");
        Observe(journal, 10, deadline.AddSeconds(1), deadline.AddHours(3), ubuntu);
        Expect(Observe(journal, 9, deadline.AddSeconds(2), deadline.AddHours(3), ubuntu).Count == 0, "Low alert dedup is retained within one WSL source.");
        Expect(Observe(journal, 9, deadline.AddSeconds(3), deadline.AddHours(3), kali).Count == 1, "A different distribution gets its own initial low state.");
        Observe(journal, 40, deadline.AddSeconds(4), deadline.AddHours(3), "api");
        journal = new UsageJournal(Path.Combine(directory, "wsl-sources"), false);
        Expect(journal.GetHistory().Select(p => p.Source).SequenceEqual(new[] { "api", ubuntu, kali, "api" }), "Legacy and WSL histories survive restart with every source switch sampled immediately.");
        string path = Path.Combine(directory, "wsl-sources", "usage-journal.json");
        string json = File.ReadAllText(path);
        Expect(!json.Contains("Ubuntu") && !json.Contains("kali-linux"), "Journal persists no distribution names or paths.");
        var document = new JavaScriptSerializer().Deserialize<UsageJournal.JournalDocument>(json);
        document.Points[1].Source = "api:wsl:untrusted-user-input";
        File.WriteAllText(path, new JavaScriptSerializer().Serialize(document));
        Expect(new UsageJournal(Path.Combine(directory, "wsl-sources"), false).GetHistory().Count == 3, "Untrusted persisted source values are skipped without dropping valid history.");

        journal = new UsageJournal(null,true);
        var history = (List<UsageHistoryPoint>)typeof(UsageJournal).GetField("history",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(journal);
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        string[] sourceRuns = { "api", "api", ubuntu, ubuntu, "api", "api", kali, kali, "api", "api" };
        for (int i=0; i<sourceRuns.Length; i++) history.Add(new UsageHistoryPoint { ServiceId="codex",Label="Weekly",Source=sourceRuns[i],ObservedAt=old.AddSeconds(i),RemainingPercent=90-i });
        var compacted = journal.GetHistory();
        Expect(compacted.Count == 10, "Hourly compaction retains the first and last actual samples of each source run.");
        Expect(compacted.Select(p=>p.Source).SequenceEqual(sourceRuns), "Returning to Windows does not erase intermediate WSL boundaries.");
    }

    private static void AbsoluteThresholds()
    {
        var journal = new UsageJournal(null,true);
        var notice = Absolute(journal,"higgsfield",20,Start,"cli");
        Expect(notice.Count == 1 && notice[0].Message.Contains("20 크레딧"),"Credit-only balance alerts at inclusive absolute threshold with unit.");
        Expect(Absolute(journal,"higgsfield",19,Start.AddSeconds(1),"cli").Count == 0,"Repeated credit low balance does not repeat.");
        Absolute(journal,"higgsfield",22,Start.AddSeconds(2),"cli");
        Expect(Absolute(journal,"higgsfield",19,Start.AddSeconds(3),"cli").Count == 0,"Credit threshold recovery margin is strict.");
        Absolute(journal,"higgsfield",23,Start.AddSeconds(4),"cli");
        Expect(Absolute(journal,"higgsfield",19,Start.AddSeconds(5),"cli").Count == 1,"Recovered credit balance rearms.");
        notice = Absolute(journal,"higgsfield-api",5,Start,"manual");
        Expect(notice.Count == 1 && notice[0].Message.Contains("5.00 USD") && notice[0].Message.Contains("수동 입력"),"USD low balance identifies both unit and manual provenance.");
        Observe(journal,10,Start,null,"api");
        var options = journal.Options; options.HiggsfieldCreditsThreshold=null; options.HiggsfieldApiUsdThreshold=0; journal.SaveOptions(options);
        Expect(Observe(journal,9,Start.AddSeconds(1),null,"api").Count==0,"Changing a Higgsfield threshold cannot rearm unrelated Codex alerts.");
        Expect(Absolute(journal,"higgsfield",0,Start.AddSeconds(6),"cli").Count == 0,"Credit threshold can be independently disabled.");
        Expect(Absolute(journal,"higgsfield-api",0.01,Start.AddSeconds(1),"manual").Count == 0,"Zero threshold only alerts an exhausted balance.");
        Expect(Absolute(journal,"higgsfield-api",0,Start.AddSeconds(2),"manual").Count == 1,"Zero USD is a valid inclusive threshold.");
        Expect(Absolute(journal,"higgsfield-api",Double.NaN,Start.AddSeconds(3),"manual").Count == 0,"Invalid amounts cannot alert.");
        options=journal.Options; options.HiggsfieldCreditsThreshold=Double.PositiveInfinity; options.HiggsfieldApiUsdThreshold=-1; journal.SaveOptions(options);
        Expect(!journal.Options.HiggsfieldCreditsThreshold.HasValue && !journal.Options.HiggsfieldApiUsdThreshold.HasValue,"Invalid persisted absolute thresholds are disabled.");
    }

    private static void MultiServiceRetention()
    {
        var journal = new UsageJournal(null,true);
        var history = (List<UsageHistoryPoint>)typeof(UsageJournal).GetField("history",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(journal);
        var now=DateTimeOffset.UtcNow;
        string[] services={"codex","eleven","openai","claude","gemini","cursor","higgsfield","higgsfield-api"};
        foreach(string service in services)
            for(int i=0;i<8640;i++) history.Add(new UsageHistoryPoint { ServiceId=service,Label="Balance",Source="api",ObservedAt=now.AddDays(-30).AddMinutes(1+i*5),Remaining=100,RemainingPercent=50 });
        var kept=journal.GetHistory();
        Expect(kept.Count <= UsageJournal.MaximumPoints,"Eight simultaneous services fit the bounded history after compaction.");
        foreach(string service in services) {
            var points=kept.Where(p=>p.ServiceId==service).ToList();
            Expect(points.Min(p=>p.ObservedAt) < now.AddDays(-29),"Every service retains its earliest 30-day range.");
            Expect(points.Count(p=>p.ObservedAt >= now.AddDays(-1) && p.ResolutionMinutes==0)==288,"Every service keeps 24 hours of five-minute detail.");
            Expect(points.Any(p=>p.ResolutionMinutes==60),"Older actual observations are identified as hourly representatives.");
            Expect(points.All(p=>p.Remaining==100 && p.RemainingPercent==50),"Compaction does not fabricate average or usage values.");
        }
        Expect(kept.SequenceEqual(kept.OrderBy(p=>p.ObservedAt)),"History remains chronologically ordered across services.");
    }

    private static void BackgroundPersistence(string directory)
    {
        string path=Path.Combine(directory,"usage-journal.json");
        var seed=new UsageJournal(directory,false); seed.ClearHistory();
        var journal=new UsageJournal(directory,false,true);
        var now=DateTimeOffset.UtcNow;
        for(int i=0;i<30;i++) Absolute(journal,"higgsfield-api",100-i,now.AddMilliseconds(i),"manual");
        journal.FlushAsync().GetAwaiter().GetResult();
        var loaded=new UsageJournal(directory,false);
        Expect(loaded.GetHistory().Count==30 && loaded.GetHistory().Last().Remaining==71,"Background batching preserves ordered rapid manual writes and flush waits for disk.");
        string prior=File.ReadAllText(path);
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            Absolute(journal,"higgsfield-api",1,now.AddMilliseconds(31),"manual");
            bool failed=false;
            try { journal.FlushAsync().GetAwaiter().GetResult(); } catch(IOException) { failed=true; }
            Expect(failed && !String.IsNullOrEmpty(journal.LastSaveError),"Asynchronous write failure is observable by explicit flush.");
            Expect(journal.GetHistory().Count==31,"A failed background save preserves newer in-memory readings for retry.");
            Expect(File.ReadAllText(path)==prior,"A failed replacement preserves the previous on-disk journal.");
            string backup = Path.Combine(directory,"recovery.json");
            journal.WriteRecoveryBackup(backup);
            var document = new JavaScriptSerializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(backup));
            Expect(document.Points.Count==31 && document.Points.Last().Remaining==1,"Recovery backup includes unsaved in-memory readings while the primary file is locked.");
            Expect(document.Notices.Any(n=>n.Kind=="low") && File.ReadAllText(path)==prior,"Recovery backup retains recent notices and never alters the previous saved journal.");
            bool primaryRejected=false;
            try { journal.WriteRecoveryBackup(path); } catch(IOException) { primaryRejected=true; }
            Expect(primaryRejected,"Recovery export cannot overwrite the primary journal.");
            bool backupFailed=false;
            try { journal.WriteRecoveryBackup(Path.Combine(backup,"blocked.json")); } catch(IOException) { backupFailed=true; }
            Expect(backupFailed && journal.GetHistory().Count==31,"Failed recovery export preserves unsaved data for another attempt.");
        }
        journal.FlushAsync().GetAwaiter().GetResult();
        Expect(journal.LastSaveError==null && new UsageJournal(directory,false).GetHistory().Count==31,"Flush retries pending data after storage recovers without needing a new reading.");
        journal.ClearHistory();
        Absolute(journal,"higgsfield-api",7,now.AddMilliseconds(32),"manual");
        var options=journal.Options; options.HiggsfieldCreditsThreshold=null; options.HiggsfieldApiUsdThreshold=2.5; journal.SaveOptions(options);
        journal.FlushAsync().GetAwaiter().GetResult();
        loaded=new UsageJournal(directory,false);
        Expect(loaded.GetHistory().Count==1 && loaded.GetHistory()[0].Remaining==7,"Older queued writes cannot resurrect cleared records.");
        Expect(!loaded.Options.HiggsfieldCreditsThreshold.HasValue && loaded.Options.HiggsfieldApiUsdThreshold==2.5,"Per-service threshold and disabled state persist across restart.");
        Expect(Directory.GetFiles(directory,"*.tmp").Length==0,"Background success and failure clean temporary snapshots.");
    }

    private static void TransientReplacement(string directory)
    {
        directory = Path.Combine(directory,"transient-lock");
        var journal = new UsageJournal(directory,false);
        var now = DateTimeOffset.UtcNow;
        Absolute(journal,"higgsfield-api",8,now,"manual");
        string path = Path.Combine(directory,"usage-journal.json");
        using (var held = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            var release = new Thread(() => { Thread.Sleep(120); held.Dispose(); });
            release.Start();
            try { Absolute(journal,"higgsfield-api",7,now.AddSeconds(1),"manual"); }
            finally { release.Join(); }
        }
        var loaded = new UsageJournal(directory,false);
        Expect(loaded.GetHistory().Count == 2 && loaded.GetHistory().Last().Remaining == 7,"A briefly held journal read handle does not discard a new measurement.");
        Expect(Directory.GetFiles(directory,"*.tmp").Length == 0,"Successful journal replacement retry cleans its temporary snapshot.");
        using (var held = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            var release = new Thread(() => { Thread.Sleep(120); held.Dispose(); });
            release.Start();
            try { loaded = new UsageJournal(directory,false); }
            finally { release.Join(); }
        }
        Expect(loaded.GetHistory().Count == 2,"A journal briefly locked at startup is read after a retry instead of failing.");
    }

    private static void RecentNotices(string directory)
    {
        directory = Path.Combine(directory, "recent-notices");
        var journal = new UsageJournal(directory, false);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddHours(-12), deadline = at.AddMinutes(1);
        Observe(journal, 50, at, deadline, null);
        var restored = new UsageJournal(directory, false);
        var late = restored.CheckResetReminders(at.AddHours(4), new[] { "codex" });
        Expect(late.Count==1 && late[0].Kind=="reset-late", "An overdue schedule is recovered after an app restart.");
        restored = new UsageJournal(directory, false);
        Expect(restored.GetRecentNotices().Count==1 && restored.GetRecentNotices()[0].ScheduledAt==deadline,"Recent catch-up notices and scheduled times survive restart.");
        Expect(restored.CheckResetReminders(at.AddHours(5),new[]{"codex"}).Count==0,"A recovered late notice is not replayed after another restart.");
        var copy = restored.GetRecentNotices(); copy[0].Message="changed";
        Expect(restored.GetRecentNotices()[0].Message!="changed","Recent notice entries are defensive copies.");
        restored.ClearNotices();
        Expect(restored.GetRecentNotices().Count==0 && restored.CheckResetReminders(at.AddHours(6),new[]{"codex"}).Count==0,"Clearing the recent list does not rearm previously consumed schedules.");
        var historyCount=restored.GetHistory().Count;
        Expect(historyCount==1 && restored.Options.ResetEnabled,"Clearing recent notices preserves history and options.");
        for(int i=0;i<UsageJournal.MaximumNotices+7;i++) Observe(restored,5,at.AddHours(7).AddMinutes(i),at.AddDays(7).AddMinutes(i*2),null);
        var notices=restored.GetRecentNotices();
        Expect(notices.Count==UsageJournal.MaximumNotices,"The recent-notice journal is bounded to 100 entries.");
        Expect(notices[0].CreatedAt>notices[notices.Count-1].CreatedAt,"Recent notices are returned newest first.");
        restored=new UsageJournal(directory,false);
        Expect(restored.GetRecentNotices().Count==UsageJournal.MaximumNotices,"The bounded recent list survives restart.");
        string path=Path.Combine(directory,"usage-journal.json");
        using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            bool failed=false; try { restored.ClearNotices(); } catch(IOException) { failed=true; }
            Expect(failed && restored.GetRecentNotices().Count==UsageJournal.MaximumNotices,"Failed synchronous clear restores recent notices alongside measurement history.");
        }
        var document=new JavaScriptSerializer().Deserialize<UsageJournal.JournalDocument>(File.ReadAllText(path));
        document.Notices[0].CreatedAt=DateTimeOffset.UtcNow.AddDays(-31).ToString("o",CultureInfo.InvariantCulture);
        document.Notices[1].CreatedAt=DateTimeOffset.UtcNow.AddHours(2).ToString("o",CultureInfo.InvariantCulture);
        document.Notices[2].Kind="unknown";
        File.WriteAllText(path,new JavaScriptSerializer().Serialize(document));
        Expect(new UsageJournal(directory,false).GetRecentNotices().Count==UsageJournal.MaximumNotices-3,"Expired, future-dated and unknown-kind persisted notices are excluded.");
    }

    public static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "UsageWidgetTrackingTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            DedupAndHysteresis(); Resets(); FreshnessManualAndOptions(); Disk(directory);
            AbsoluteThresholds(); MultiServiceRetention(); BackgroundPersistence(directory); RecentNotices(directory); WslSourceIsolation(directory); TransientReplacement(directory);
            Console.WriteLine("PASS: " + checks + " tracking checks (dedup, hysteresis, scheduled reminders, manual/code timestamps, retention, persistence, privacy). ");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
