using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    // Source identities separate local installations without storing their
    // distribution names, paths, or account details in the usage journal.
    internal static class UsageSourceIdentity
    {
        internal static string ForWsl(string kind, string distribution)
        {
            if (kind != "api" && kind != "code") throw new ArgumentException("Unknown usage source.", "kind");
            if (String.IsNullOrEmpty(distribution)) return kind;
            if (distribution.Length > 256 || distribution != distribution.Trim() || distribution.Any(Char.IsControl))
                throw new ArgumentException("Invalid WSL distribution.", "distribution");
            using (var sha = SHA256.Create())
                return kind + ":wsl:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(distribution))).Replace("-", "").ToLowerInvariant();
        }

        internal static bool IsWsl(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            string prefix = value.StartsWith("api:wsl:", StringComparison.Ordinal) ? "api:wsl:" : value.StartsWith("code:wsl:", StringComparison.Ordinal) ? "code:wsl:" : null;
            return prefix != null && value.Length == prefix.Length + 64 && value.Skip(prefix.Length).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }

        internal static bool IsValid(string value)
        {
            return value == "api" || value == "code" || value == "cli" || value == "manual" || IsWsl(value) || IsWindowsConfig(value);
        }

        internal static bool IsWindowsConfig(string value)
        {
            const string prefix = "code:win:";
            return value != null && value.StartsWith(prefix,StringComparison.Ordinal) && value.Length == prefix.Length + 64
                && value.Skip(prefix.Length).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        }
    }

    public sealed class UsageTrackingOptions
    {
        public bool LowEnabled = true;
        public bool ResetEnabled = true;
        public bool HistoryEnabled = true;
        public bool ForecastEnabled = true;
        public int LowThresholdPercent = 20;
        public int CriticalThresholdPercent = 10;
        public double? HiggsfieldCreditsThreshold = 20;
        public double? HiggsfieldApiUsdThreshold = 5;
    }

    public sealed class UsageHistoryPoint
    {
        public string ServiceId, Label, Source, DisplayValue;
        public DateTimeOffset ObservedAt;
        public double? RemainingPercent, Remaining, Limit;
        public DateTimeOffset? ResetsAt;
        public int ResolutionMinutes;
    }

    public sealed class UsageNotice
    {
        public string Kind, ServiceId, Label, Message;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset? ScheduledAt;
        // Pending delivery provenance only; the persisted notice history is unchanged.
        internal string Source;
    }

    public sealed class UsageAnalysis
    {
        public string ServiceId, Label, Source, Unit, Status;
        public double? AveragePerDay, TrendPerDay, RecommendedPerDay;
        public int SampleDays, ForecastSampleDays, RiskLevel;
        public double ObservedHours, ForecastObservedHours, TodayObservedHours, TodayConsumed, CollectionHours;
        public int RequiredSampleDays = 3;
        public double RequiredObservedHours = 12, RequiredDailyHours = 4, RequiredCollectionHours = 72;
        public DateTimeOffset? PredictedEmptyAt, EarliestEmptyAt, LatestEmptyAt, ResetAt;
        public bool BeforeReset, TodaySpike;
    }

    // Exact sums of accepted observation intervals, independent of lossy chart
    // samples. A bucket that crosses a query boundary is excluded, never split
    // or extrapolated. These contain no provider/account identifiers.
    internal sealed class UsageObservedBucket
    {
        internal DateTimeOffset Start, End;
        internal double Consumption, Hours, MaximumGapMinutes;
        internal int Intervals;
    }

    internal sealed class UsageObservedHistory
    {
        internal string Source, Unit;
        internal DateTimeOffset Since;
        internal List<UsageObservedBucket> Buckets;
    }

    // This journal stores measurements and notices generated from those measurements.
    // It never serializes a provider response,
    // account, request, error message, API key, or the snapshot's free-form details.
    public sealed class UsageJournal
    {
        internal const int MaximumPoints = 20000;
        internal const int MaximumNotices = 100;
        internal const int MaximumObservedBuckets = 8000;
        private const int MaximumBytes = 16777216;
        private const int MaximumRows = 96;
        private readonly object gate = new object();
        private readonly string path;
        private readonly bool memoryOnly;
        private readonly bool backgroundPersistence;
        private long revision, savedRevision;
        private bool writerRunning;
        private Task writerTask;
        private string persistenceError;
        private DateTimeOffset nextCompaction;
        public event EventHandler PersistenceChanged;
        private UsageTrackingOptions options = new UsageTrackingOptions();
        private JournalBackup committed;
        private readonly List<UsageHistoryPoint> history = new List<UsageHistoryPoint>();
        private readonly List<UsageNotice> recentNotices = new List<UsageNotice>();
        private readonly Dictionary<string, RowState> states = new Dictionary<string, RowState>(StringComparer.Ordinal);
        private readonly Dictionary<string, AnalysisState> analyses = new Dictionary<string, AnalysisState>(StringComparer.Ordinal);
        private string analysisOwner;
        private static readonly string[] ServiceIds = { "codex", "eleven", "openai", "claude", "gemini", "cursor", "higgsfield", "higgsfield-api" };

        public UsageJournal(string directory, bool memoryOnly) : this(directory, memoryOnly, false) { }

        public UsageJournal(string directory, bool memoryOnly, bool backgroundPersistence)
        {
            this.memoryOnly = memoryOnly;
            this.backgroundPersistence = backgroundPersistence;
            if (!memoryOnly)
            {
                if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("저장 폴더가 필요합니다.", "directory");
                path = Path.Combine(Path.GetFullPath(directory), "usage-journal.json");
                Load();
                committed = Capture();
            }
        }

        public UsageTrackingOptions Options { get { lock (gate) return CopyOptions(options); } }
        public string LastSaveError { get { lock (gate) return persistenceError; } }

        public List<UsageHistoryPoint> GetHistory()
        {
            lock (gate)
            {
                Prune(DateTimeOffset.UtcNow);
                return history.Select(CopyPoint).ToList();
            }
        }

        public List<UsageNotice> GetRecentNotices()
        {
            lock (gate)
            {
                Prune(DateTimeOffset.UtcNow);
                return recentNotices.OrderByDescending(notice => notice.CreatedAt).Select(CopyNotice).ToList();
            }
        }

        public void ClearNotices()
        {
            lock (gate) { recentNotices.Clear(); Save(); }
        }

        public void SaveOptions(UsageTrackingOptions value)
        {
            if (value == null) throw new ArgumentNullException("value");
            lock (gate)
            {
                UsageTrackingOptions next = CopyOptions(value);
                if (next.HistoryEnabled != options.HistoryEnabled)
                    foreach (AnalysisState analysis in analyses.Values) { analysis.IncludePrevious = false; analysis.ObservedContinue = false; }
                foreach (RowState state in states.Values)
                    if ((state.ServiceId != "higgsfield" && state.ServiceId != "higgsfield-api" && next.LowThresholdPercent != options.LowThresholdPercent)
                        || (state.ServiceId == "higgsfield" && next.HiggsfieldCreditsThreshold != options.HiggsfieldCreditsThreshold)
                        || (state.ServiceId == "higgsfield-api" && next.HiggsfieldApiUsdThreshold != options.HiggsfieldApiUsdThreshold)) state.LowAlerted = false;
                if (next.CriticalThresholdPercent != options.CriticalThresholdPercent)
                    foreach (RowState state in states.Values) state.CriticalAlerted = false;
                options = next;
                Save();
            }
        }

        public void ClearHistory()
        {
            lock (gate)
            {
                history.Clear();
                analyses.Clear();
                // Keep last-observation and notification state: clearing the history
                // must not recreate a sample or repeat an alarm on a cached render.
                Save();
            }
        }

        public void ForgetService(string serviceId)
        {
            ForgetServiceCore(serviceId, false);
        }

        internal void ForgetAutomaticService(string serviceId)
        {
            ForgetServiceCore(serviceId, true);
        }

        private void ForgetServiceCore(string serviceId, bool preserveManual)
        {
            ValidateService(serviceId);
            lock (gate)
            {
                var removed = states.Where(pair => pair.Value.ServiceId == serviceId && (!preserveManual || pair.Value.Source != "manual")).Select(pair => pair.Key).ToList();
                foreach (string key in removed) states.Remove(key);
                var analysisKeys = analyses.Where(pair => pair.Value.ServiceId == serviceId && (!preserveManual || pair.Value.Source != "manual")).Select(pair => pair.Key).ToList();
                foreach (string key in analysisKeys) analyses.Remove(key);
                if (!preserveManual || removed.Count > 0 || analysisKeys.Count > 0) Save();
            }
        }

        public List<UsageNotice> Observe(string serviceId, UsageSnapshot snapshot, DateTimeOffset observedAt, string source, DateTimeOffset now)
        {
            ValidateService(serviceId);
            if (!ValidSource(source)) throw new ArgumentException("알 수 없는 기록 출처입니다.", "source");
            var notices = new List<UsageNotice>();
            if (snapshot == null || snapshot.Rows == null || observedAt > now.AddMinutes(1) || observedAt < now.AddMinutes(-5)) return notices;
            lock (gate)
            {
                if (states.Values.Any(state => state.ServiceId == serviceId && state.LastObserved.HasValue && observedAt <= state.LastObserved.Value)) return notices;
                bool changed = Prune(now);
                string planIdentity = AnalysisPlanIdentity(snapshot.Plan);
                // Providers can rename a completed row to a waiting-state label.
                // A due, previously known schedule still deserves its reminder.
                foreach (RowState previous in states.Values.Where(state => state.ServiceId == serviceId && state.Source == source))
                    if (ConsumeDeadline(previous, now, notices)) changed = true;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (UsageRow row in snapshot.Rows.Take(MaximumRows))
                {
                    if (row == null) continue;
                    string label = CleanText(row.Label, 120);
                    if (String.IsNullOrEmpty(label)) continue;
                    string key = serviceId + "\n" + label;
                    if (!seen.Add(key)) continue;
                    RowState state;
                    bool isNew = !states.TryGetValue(key, out state);
                    if (isNew)
                    {
                        if (states.Count >= MaximumRows) continue;
                        state = new RowState { ServiceId = serviceId, Label = label };
                        states.Add(key, state);
                    }
                    if (state.LastObserved.HasValue && observedAt <= state.LastObserved.Value) continue;

                    bool sourceChanged = state.Source != null && state.Source != source;
                    if (sourceChanged)
                    {
                        state.ResetAt = null;
                        state.ResetEligible = false;
                        state.ResetConsumed = false;
                    }
                    // A provider may already return its NEXT deadline on this poll.
                    // Process the previous known deadline before replacing it.
                    if (ConsumeDeadline(state, now, notices)) changed = true;
                    DateTimeOffset? deadline = row.ResetsAt;
                    bool newCycle = state.ResetAt.HasValue && deadline.HasValue && Math.Abs((deadline.Value - state.ResetAt.Value).TotalSeconds) > 60;
                    if (newCycle || sourceChanged) { state.LowAlerted = false; state.CriticalAlerted = false; state.EmptyAlerted = false; }
                    if (deadline.HasValue && (!state.ResetAt.HasValue || newCycle))
                    {
                        state.ResetAt = deadline;
                        // Only a deadline known before it occurs is eligible. An
                        // initial snapshot with an expired deadline is not a reset.
                        state.ResetEligible = deadline.Value > observedAt && deadline.Value > now;
                        state.ResetConsumed = false;
                    }
                    else if (!deadline.HasValue)
                    {
                        state.ResetAt = null;
                        state.ResetEligible = false;
                        state.ResetConsumed = false;
                    }
                    state.Source = source;
                    state.LastObserved = observedAt;
                    double? percent = ValidPercent(row.RemainingPercent);
                    double? absoluteThreshold = serviceId == "higgsfield" ? options.HiggsfieldCreditsThreshold : serviceId == "higgsfield-api" ? options.HiggsfieldApiUsdThreshold : null;
                    double? amount = percent.HasValue ? percent : absoluteThreshold.HasValue ? ValidAmount(row.Remaining) : null;
                    double threshold = percent.HasValue ? options.LowThresholdPercent : absoluteThreshold.GetValueOrDefault();
                    double recoveryMargin = percent.HasValue ? 5 : Math.Max(serviceId == "higgsfield-api" ? 0.5 : 1, threshold * 0.1);
                    AnalysisState priorAnalysis;
                    if (analyses.TryGetValue(key,out priorAnalysis) && (planIdentity != null && priorAnalysis.Plan != planIdentity || priorAnalysis.Limit != ValidAmount(row.Limit))) {
                        state.LowAlerted = false; state.CriticalAlerted = false; state.EmptyAlerted = false;
                    }
                    if (amount.HasValue)
                    {
                        if (amount.Value > threshold + recoveryMargin) { state.LowAlerted = false; state.CriticalAlerted = false; state.EmptyAlerted = false; }
                        string stage = amount.Value <= 0 ? "empty" : percent.HasValue && options.CriticalThresholdPercent > 0 && amount.Value <= options.CriticalThresholdPercent ? "critical" : amount.Value <= threshold ? "low" : null;
                        bool alerted = stage == "empty" ? state.EmptyAlerted : stage == "critical" ? state.CriticalAlerted : state.LowAlerted;
                        if (options.LowEnabled && stage != null && !alerted && (!deadline.HasValue || deadline.Value > now))
                        {
                            state.LowAlerted = true;
                            if (stage == "critical" || stage == "empty") state.CriticalAlerted = true;
                            if (stage == "empty") state.EmptyAlerted = true;
                            notices.Add(new UsageNotice
                            {
                                Kind = stage, ServiceId = serviceId, Label = label, CreatedAt = now, Source = source,
                                Message = label + " 잔량이 " + amount.Value.ToString(percent.HasValue ? "0.#" : serviceId == "higgsfield-api" ? "0.00" : "0.##", CultureInfo.GetCultureInfo("ko-KR")) + (percent.HasValue ? "%" : serviceId == "higgsfield-api" ? " USD" : " 크레딧") + "입니다." + (source == "manual" ? " 수동 입력값 기준입니다." : "")
                            });
                        }
                    }
                    ObserveAnalysis(serviceId, label, source, planIdentity, row, observedAt, now, notices);
                    if (options.HistoryEnabled && (isNew || newCycle || sourceChanged || source == "manual" || !state.LastSample.HasValue || observedAt - state.LastSample.Value >= TimeSpan.FromMinutes(5)))
                    {
                        // Rows containing only explanatory text are not measurements.
                        double? remaining = ValidAmount(row.Remaining), limit = ValidAmount(row.Limit);
                        string display = CleanText(row.DisplayValue, 80);
                        if (percent.HasValue || remaining.HasValue || limit.HasValue || !String.IsNullOrEmpty(display))
                        {
                            history.Add(new UsageHistoryPoint { ServiceId = serviceId, Label = label, Source = source, ObservedAt = observedAt, RemainingPercent = percent, Remaining = remaining, Limit = limit, ResetsAt = deadline, DisplayValue = display });
                            state.LastSample = observedAt;
                        }
                    }
                    changed = true;
                }
                foreach (string key in states.Where(pair => pair.Value.ServiceId == serviceId && !seen.Contains(pair.Key)).Select(pair => pair.Key).ToList())
                {
                    states.Remove(key);
                    changed = true;
                }
                foreach (var pair in analyses.Where(pair => pair.Value.ServiceId == serviceId && !seen.Contains(pair.Key))) {
                    pair.Value.IncludePrevious = false; pair.Value.ObservedContinue = false;
                }
                if (RememberNotices(notices)) changed = true;
                if (Prune(now)) changed = true;
                if (changed) Save();
                return notices;
            }
        }

        public List<UsageNotice> CheckResetReminders(DateTimeOffset now, IEnumerable<string> activeServiceIds)
        {
            var active = new HashSet<string>(activeServiceIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var notices = new List<UsageNotice>();
            lock (gate)
            {
                bool changed = Prune(now);
                foreach (RowState state in states.Values)
                    if (active.Contains(state.ServiceId) && ConsumeDeadline(state, now, notices)) changed = true;
                if (RememberNotices(notices)) changed = true;
                if (changed) Save();
                return notices;
            }
        }

        // Analytics consume original successful observations, never the lossy chart
        // samples. A new connection/source/limit gets a new opaque local epoch.
        public UsageAnalysis GetAnalysis(string serviceId, string label, string source, DateTimeOffset now)
        {
            ValidateService(serviceId);
            lock (gate) { return Analyze(serviceId, CleanText(label,120), source, now); }
        }

        internal UsageObservedHistory GetObservedHistory(string serviceId, string label, string source, DateTimeOffset now)
        {
            ValidateService(serviceId);
            lock (gate) {
                AnalysisState state;
                if (!options.HistoryEnabled || label == null || !analyses.TryGetValue(serviceId + "\n" + CleanText(label,120),out state)
                    || state.Source != source || !state.ObservedSince.HasValue || state.ObservedSince > now) return null;
                return new UsageObservedHistory { Source=state.Source, Unit=state.ObservedUnit, Since=state.ObservedSince.Value,
                    Buckets=state.ObservedBuckets.Where(bucket => bucket.End >= now.AddDays(-30) && bucket.End <= now).Select(CopyObservedBucket).ToList() };
            }
        }

        public void PauseAnalysis()
        {
            lock (gate) {
                bool changed = false;
                foreach (var state in analyses.Values) {
                    if (state.IncludePrevious || state.ObservedContinue) changed = true;
                    state.IncludePrevious = false; state.ObservedContinue = false;
                }
                if (changed) Save();
            }
        }

        public void SetAnalysisOwner(string fingerprint)
        {
            if (fingerprint == null || fingerprint.Length != 64 || fingerprint.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new ArgumentException("An opaque account fingerprint is required.","fingerprint");
            lock (gate) {
                if (analysisOwner == fingerprint) return;
                // First adoption migrates a pre-owner journal without replaying
                // old low alerts or discarding its registered reset reminders.
                if (analysisOwner != null) states.Clear();
                analysisOwner = fingerprint; analyses.Clear(); Save();
            }
        }

        private UsageAnalysis Analyze(string serviceId, string label, string source, DateTimeOffset now)
        {
            var result = new UsageAnalysis { ServiceId = serviceId, Label = label, Source = source, Status = "데이터 수집 중" };
            if (!options.HistoryEnabled) { result.Status = "이력 기록 꺼짐"; return result; }
            AnalysisState state;
            if (label == null || !analyses.TryGetValue(serviceId + "\n" + label,out state) || state.Source != source) {
                if (source == "manual") result.Status = "수동 기록";
                else if (IsShortLimit(label)) result.Status = "짧은 한도";
                return result;
            }
            result.Unit = state.Unit; result.ResetAt = state.ResetAt;
            if (!state.Supported) { result.Status = "지원하지 않는 항목"; return result; }
            if (state.TimeZoneId != TimeZoneInfo.Local.Id) return result;
            DateTime today = now.ToLocalTime().Date;
            var observed = state.Days.Where(day => day.Day >= today.AddDays(-7) && day.Day <= today && !day.Uncertain).ToList();
            var days = observed.Where(day => day.Day < today && day.Hours >= 1).ToList();
            var forecastDays = days.Where(day => day.Hours >= result.RequiredDailyHours).ToList();
            AnalysisDay current = observed.FirstOrDefault(day => day.Day == today);
            result.SampleDays = days.Count;
            result.ObservedHours = observed.Sum(day => day.Hours);
            result.ForecastSampleDays = forecastDays.Count;
            result.ForecastObservedHours = forecastDays.Sum(day => day.Hours);
            result.TodayObservedHours = current == null ? 0 : current.Hours;
            result.TodayConsumed = current == null ? 0 : current.Consumed;
            result.CollectionHours = state.FirstObserved.HasValue ? Math.Max(0,(now-state.FirstObserved.Value).TotalHours) : 0;
            // Mean observed daily consumption, not an active-hours rate extrapolated
            // to 24 hours. Partial/unknown days are never filled with zero usage.
            if (days.Count > 0) result.AveragePerDay = days.Sum(day => day.Consumed) / days.Count;
            if (source == "manual") { result.Status = "수동 기록"; return result; }
            if (IsShortLimit(label)) { result.Status = "짧은 한도"; return result; }
            if (!state.IncludePrevious) return result;
            if (!state.LastObserved.HasValue || state.LastObserved.Value > now || now - state.LastObserved.Value > TimeSpan.FromMinutes(source == "code" || source.StartsWith("code:",StringComparison.Ordinal) ? 30 : 10)) {
                result.Status = "오래된 기록"; return result;
            }
            if (state.ResetAt.HasValue && state.ResetAt.Value <= now) { result.Status = "초기화 확인 대기"; return result; }
            if (state.ResetAt.HasValue && state.Remaining.HasValue)
                result.RecommendedPerDay = state.Remaining.Value / (state.ResetAt.Value-now).TotalDays;
            if (!state.FirstObserved.HasValue || result.CollectionHours < result.RequiredCollectionHours) return result;
            if (forecastDays.Count < result.RequiredSampleDays || result.ForecastObservedHours < result.RequiredObservedHours) { result.Status = "기록 부족"; return result; }
            if (!state.ResetAt.HasValue) { result.Status = "초기화 시각 없음"; return result; }
            double weights = forecastDays.Sum(day => 8-(today-day.Day).TotalDays);
            double trend = forecastDays.Sum(day => day.Consumed*(8-(today-day.Day).TotalDays))/weights;
            double variation = Math.Sqrt(forecastDays.Sum(day => Math.Pow(day.Consumed-trend,2)*(8-(today-day.Day).TotalDays))/weights);
            double slower = Math.Max(0,trend-variation), faster = trend+variation;
            // Today's observed total is a known lower bound on today's usage.
            // It can raise a mature trend, never extrapolate a short burst to 24h.
            result.TodaySpike = current != null && current.Hours >= result.RequiredDailyHours && current.Consumed > trend;
            if (result.TodaySpike) { trend = current.Consumed; faster = Math.Max(faster,trend); }
            result.TrendPerDay = trend;
            if (trend <= 0 || !state.Remaining.HasValue) { result.Status = "소비 변화 없음"; return result; }
            result.PredictedEmptyAt = ExhaustionAt(now,state.Remaining.Value,trend);
            result.EarliestEmptyAt = ExhaustionAt(now,state.Remaining.Value,faster);
            result.LatestEmptyAt = ExhaustionAt(now,state.Remaining.Value,slower);
            if (result.EarliestEmptyAt.HasValue && result.EarliestEmptyAt.Value < state.ResetAt.Value)
                result.RiskLevel = result.LatestEmptyAt.HasValue && result.LatestEmptyAt.Value < state.ResetAt.Value ? 2 : 1;
            if (result.RiskLevel == 2 && result.LatestEmptyAt.Value <= now.AddHours(24)) result.RiskLevel = 3;
            result.BeforeReset = result.RiskLevel > 0;
            result.Status = "분석 가능";
            return result;
        }

        private static DateTimeOffset? ExhaustionAt(DateTimeOffset now, double remaining, double perDay)
        {
            if (perDay <= 0) return null;
            double days = remaining/perDay;
            if (Double.IsNaN(days) || Double.IsInfinity(days) || days < 0 || days > 36500 || days > (DateTimeOffset.MaxValue-now).TotalDays) return null;
            return now.AddDays(days);
        }

        private void ObserveAnalysis(string serviceId, string label, string source, string plan, UsageRow row, DateTimeOffset at, DateTimeOffset now, List<UsageNotice> notices)
        {
            string key = serviceId + "\n" + label, unit;
            double? value = AnalysisValue(serviceId,row,out unit), limit = ValidAmount(row.Limit);
            AnalysisState state;
            bool previous = analyses.TryGetValue(key,out state);
            if (plan == null && previous) plan = state.Plan;
            if (!previous || state.Source != source || state.Unit != unit || state.Limit != limit || state.Plan != plan || state.TimeZoneId != TimeZoneInfo.Local.Id) {
                if (!previous && analyses.Count >= MaximumRows) return;
                state = new AnalysisState { ServiceId = serviceId, Label = label, Source = source, Unit = unit,
                    Epoch = Guid.NewGuid().ToString("N"), TimeZoneId = TimeZoneInfo.Local.Id, Plan = plan, Limit = limit, FirstObserved = at };
                analyses[key] = state;
                previous = false;
            }
            bool cycleChanged = state.ResetAt.HasValue != row.ResetsAt.HasValue
                || state.ResetAt.HasValue && row.ResetsAt.HasValue && Math.Abs((state.ResetAt.Value - row.ResetsAt.Value).TotalSeconds) > 60;
            ObserveComparison(state,row,at);
            bool increased = value.HasValue && state.Remaining.HasValue && value.Value > state.Remaining.Value;
            if (previous && source != "manual" && value.HasValue && increased && cycleChanged && state.ResetAt.HasValue
                && state.ResetEligible && state.ResetAt.Value <= at
                && row.ResetsAt.HasValue && row.ResetsAt.Value > now && state.ConfirmedReset != state.ResetAt) {
                state.ConfirmedReset = state.ResetAt;
                if (options.ResetEnabled) notices.Add(new UsageNotice { Kind = "reset-confirmed", ServiceId = serviceId, Label = label, Source = source,
                    CreatedAt = now, ScheduledAt = state.ResetAt, Message = label + "의 초기화 후 새 잔량을 확인했습니다." });
            }
            if (cycleChanged) { state.ForecastAlerted = false; state.ForecastAlertedRisk = 0; state.ForecastRecovered = false; }
            if (!previous || cycleChanged) state.ResetEligible = row.ResetsAt.HasValue && row.ResetsAt.Value > at && row.ResetsAt.Value > now;
            if (options.HistoryEnabled && previous && state.IncludePrevious && state.LastObserved.HasValue && state.Remaining.HasValue && value.HasValue) {
                DateTimeOffset before = state.LastObserved.Value;
                double hours = (at - before).TotalHours;
                bool resetCrossed = state.ResetAt.HasValue && state.ResetAt.Value > before && state.ResetAt.Value <= at;
                if (cycleChanged || resetCrossed || increased) {
                    MarkUncertainDay(state,before.ToLocalTime().Date);
                    MarkUncertainDay(state,at.ToLocalTime().Date);
                }
                else if (hours > 0 && hours <= 2 && (!row.ResetsAt.HasValue || row.ResetsAt.Value > at))
                    AddConsumption(state,before,at,state.Remaining.Value - value.Value);
            }
            state.Supported = value.HasValue;
            state.LastObserved = at; state.Remaining = value; state.ResetAt = row.ResetsAt;
            state.IncludePrevious = options.HistoryEnabled && value.HasValue;
            state.Days.RemoveAll(day => day.Day < now.ToLocalTime().Date.AddDays(-8) || day.Day > now.ToLocalTime().Date);
            if (options.HistoryEnabled && value.HasValue && value.Value > 0) {
                UsageAnalysis result = Analyze(serviceId,label,source,now);
                bool known = result.Status == "분석 가능" && result.PredictedEmptyAt.HasValue || result.Status == "소비 변화 없음";
                if (known && result.RiskLevel == 0 && state.ForecastAlertedRisk > 0) state.ForecastRecovered = true;
                bool cooled = !state.ForecastLastAlertAt.HasValue || now-state.ForecastLastAlertAt.Value >= TimeSpan.FromHours(6);
                if (known && options.ForecastEnabled && result.RiskLevel > 0 && cooled
                    && (result.RiskLevel > state.ForecastAlertedRisk || state.ForecastRecovered)) {
                    state.ForecastAlerted = true;
                    state.ForecastAlertedRisk = result.RiskLevel;
                    state.ForecastLastAlertAt = now;
                    state.ForecastRecovered = false;
                    notices.Add(new UsageNotice { Kind = "forecast", ServiceId = serviceId, Label = label, Source = source, CreatedAt = now,
                        ScheduledAt = result.PredictedEmptyAt, Message = label + (result.RiskLevel == 3
                            ? " 잔량이 하루 안에 소진될 것으로 예상됩니다."
                            : result.RiskLevel == 2 ? " 잔량이 초기화 전에 소진될 것으로 예상됩니다."
                            : "의 사용 추세에 따라 초기화 전에 잔량이 부족해질 수 있습니다.") });
                }
            }
        }

        private void ObserveComparison(AnalysisState state, UsageRow row, DateTimeOffset at)
        {
            if (!options.HistoryEnabled) { state.ObservedContinue=false; return; }
            double? percent=ValidPercent(row.RemainingPercent), value=percent ?? ValidAmount(row.Remaining);
            string unit=!value.HasValue ? null : percent.HasValue ? "%p"
                : state.ServiceId=="openai" || state.ServiceId=="higgsfield-api" ? "USD" : "크레딧";
            if (!state.ObservedSince.HasValue || unit!=null && state.ObservedUnit!=null && unit!=state.ObservedUnit) {
                state.ObservedSince=at; state.ObservedContinue=false; state.ObservedBuckets.Clear();
            }
            if (unit!=null) state.ObservedUnit=unit;
            double hours=state.LastObserved.HasValue ? (at-state.LastObserved.Value).TotalHours : 0;
            if (state.ObservedContinue && state.Source!="manual" && value.HasValue && state.ObservedValue.HasValue
                && hours>0 && hours<=0.5 && state.ResetAt==row.ResetsAt
                && (!row.ResetsAt.HasValue || row.ResetsAt.Value>at) && value<=state.ObservedValue) {
                double consumed=state.ObservedValue.Value-value.Value;
                DateTimeOffset before=state.LastObserved.Value;
                // Keep the whole interval in its starting UTC hour. Never
                // divide its consumption at midnight or another bucket edge.
                long hour=before.UtcTicks/TimeSpan.TicksPerHour;
                var bucket=state.ObservedBuckets.LastOrDefault();
                if (bucket==null || bucket.Start.UtcTicks/TimeSpan.TicksPerHour!=hour) {
                    bucket=new UsageObservedBucket {Start=before,End=at}; state.ObservedBuckets.Add(bucket);
                }
                if (ValidAmount(bucket.Consumption+consumed).HasValue) {
                    bucket.Consumption+=consumed; bucket.Hours+=hours; bucket.Intervals++;
                    bucket.End=at; bucket.MaximumGapMinutes=Math.Max(bucket.MaximumGapMinutes,hours*60);
                }
            }
            state.ObservedValue=value;
            state.ObservedContinue=value.HasValue && state.Source!="manual";
        }

        private static UsageObservedBucket CopyObservedBucket(UsageObservedBucket bucket)
        { return new UsageObservedBucket { Start=bucket.Start,End=bucket.End,Consumption=bucket.Consumption,Hours=bucket.Hours,
            MaximumGapMinutes=bucket.MaximumGapMinutes,Intervals=bucket.Intervals }; }

        private bool PruneObserved(DateTimeOffset now)
        {
            bool changed=false;
            foreach(var state in analyses.Values)
                if(state.ObservedBuckets.RemoveAll(bucket=>bucket.Start<now.AddDays(-30) || bucket.End>now.AddMinutes(1))>0) changed=true;
            if(analyses.Values.Sum(state=>state.ObservedBuckets.Count)<=MaximumObservedBuckets) return changed;
            // At 96 simultaneous rows, hourly buckets would exceed the file
            // budget. Coarsen only these exact sums; even the daily bound is
            // under 3,200 entries. No estimated coverage is introduced.
            foreach(int hours in new[]{2,4,8,12,24}) {
                foreach(var state in analyses.Values) {
                    var compacted=new List<UsageObservedBucket>();
                    foreach(var group in state.ObservedBuckets.GroupBy(bucket=>bucket.Start.UtcTicks/(TimeSpan.TicksPerHour*hours))) {
                        var rows=group.OrderBy(bucket=>bucket.Start).ToList();
                        double consumption=rows.Sum(bucket=>bucket.Consumption);
                        // Invalid overflow drops this aggregate rather than
                        // returning a false finite total or preventing saves.
                        if(!ValidAmount(consumption).HasValue) continue;
                        compacted.Add(new UsageObservedBucket {Start=rows[0].Start,End=rows.Max(bucket=>bucket.End),Consumption=consumption,
                            Hours=rows.Sum(bucket=>bucket.Hours),Intervals=rows.Sum(bucket=>bucket.Intervals),MaximumGapMinutes=rows.Max(bucket=>bucket.MaximumGapMinutes)});
                    }
                    state.ObservedBuckets=compacted; changed=true;
                }
                if(analyses.Values.Sum(state=>state.ObservedBuckets.Count)<=MaximumObservedBuckets) break;
            }
            return changed;
        }

        internal static bool IsAnalysisCandidate(string serviceId, UsageRow row)
        { string unit; return row != null && AnalysisValue(serviceId,row,out unit).HasValue; }

        internal static bool IsShortAnalysisLimit(string label) { return IsShortLimit(label); }

        private static double? AnalysisValue(string serviceId, UsageRow row, out string unit)
        {
            unit = null;
            double? remaining = ValidAmount(row.Remaining), percent = ValidPercent(row.RemainingPercent);
            if (serviceId == "openai") {
                // Costs are spend, never a wallet balance. Only the explicit budget
                // row provides a structured remaining USD amount.
                if (row.Label == "설정 예산 잔여" && ValidAmount(row.Limit).GetValueOrDefault() > 0 && remaining.HasValue) { unit = "USD"; return remaining; }
                return null;
            }
            if (serviceId == "higgsfield-api" && remaining.HasValue) { unit = "USD"; return remaining; }
            if ((serviceId == "eleven" || serviceId == "higgsfield" || serviceId == "codex" && row.Label != null && row.Label.Contains("크레딧")) && remaining.HasValue) { unit = "credits"; return remaining; }
            if (percent.HasValue) { unit = "%p"; return percent; }
            return null;
        }

        private static bool IsShortLimit(string label)
        {
            string value = (label ?? "").ToLowerInvariant();
            if (value.Contains("세션") || value.Contains("session")) return true;
            var match = System.Text.RegularExpressions.Regex.Match(value,@"(\d+(?:\.\d+)?)\s*(시간|hours?|h|분|minutes?|min|m)(?:\b|\s|$)");
            double length;
            if (!match.Success || !Double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out length)) return false;
            string unit = match.Groups[2].Value;
            return length < (unit == "분" || unit.StartsWith("m",StringComparison.Ordinal) ? 1440 : 24);
        }

        private static string AnalysisPlanIdentity(string plan)
        {
            plan = CleanText(plan,80);
            if (plan == null) return null;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plan))).Replace("-","").ToLowerInvariant();
        }

        private static AnalysisDay DayFor(AnalysisState state, DateTime day)
        {
            var found = state.Days.FirstOrDefault(item => item.Day == day);
            if (found == null) { found = new AnalysisDay { Day = day }; state.Days.Add(found); }
            return found;
        }

        private static void MarkUncertainDay(AnalysisState state, DateTime day) { DayFor(state,day).Uncertain = true; }

        private static void AddConsumption(AnalysisState state, DateTimeOffset before, DateTimeOffset after, double consumed)
        {
            double totalHours = (after - before).TotalHours;
            while (before < after) {
                DateTime midnight = DateTime.SpecifyKind(before.ToLocalTime().Date.AddDays(1),DateTimeKind.Unspecified);
                while (TimeZoneInfo.Local.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
                DateTimeOffset end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight,TimeZoneInfo.Local),TimeSpan.Zero);
                if (end > after) end = after;
                double hours = (end - before).TotalHours;
                var day = DayFor(state,before.ToLocalTime().Date);
                day.Hours += hours; day.Consumed += consumed * hours / totalHours; day.Intervals++;
                before = end;
            }
        }

        private bool ConsumeDeadline(RowState state, DateTimeOffset now, List<UsageNotice> notices)
        {
            if (!state.ResetAt.HasValue || state.ResetConsumed || state.ResetAt.Value > now) return false;
            state.ResetConsumed = true;
            if (options.ResetEnabled && state.ResetEligible && now - state.ResetAt.Value <= TimeSpan.FromDays(30))
            {
                bool late = now - state.ResetAt.Value > TimeSpan.FromMinutes(5);
                notices.Add(new UsageNotice
                {
                    Kind = late ? "reset-late" : "reset", ServiceId = state.ServiceId, Label = state.Label, CreatedAt = now, ScheduledAt = state.ResetAt, Source = state.Source,
                    Message = state.Label + "의 예정된 초기화 시각이 지났습니다. 새로고침하여 실제 잔량을 확인하세요." + (state.Source == "manual" ? " 수동 입력한 일정 기준입니다." : "")
                });
            }
            return true;
        }

        private bool Prune(DateTimeOffset now)
        {
            bool changed = history.RemoveAll(point => point.ObservedAt < now.AddDays(-30) || point.ObservedAt > now.AddMinutes(1)) > 0;
            if (PruneObserved(now)) changed=true;
            foreach (var state in analyses.Values)
                if (state.Days.RemoveAll(day => day.Day < now.ToLocalTime().Date.AddDays(-8) || day.Day > now.ToLocalTime().Date) > 0) changed = true;
            foreach (string key in analyses.Where(pair => !pair.Value.LastObserved.HasValue || pair.Value.LastObserved < now.AddDays(-30)).Select(pair => pair.Key).ToList()) {
                analyses.Remove(key); changed = true;
            }
            if (recentNotices.RemoveAll(notice => notice.CreatedAt < now.AddDays(-30) || notice.CreatedAt > now.AddMinutes(1)) > 0) changed = true;
            if (recentNotices.Count > MaximumNotices) { recentNotices.RemoveRange(0, recentNotices.Count - MaximumNotices); changed = true; }
            if (now >= nextCompaction || history.Count > MaximumPoints)
            {
                // Preserve the entire 30-day range instead of deleting the oldest
                // services. Each bucket keeps its last actual measurement, not a
                // fabricated average. The first observation of each series is kept.
                var recent = history.Where(p => p.ObservedAt >= now.AddDays(-1)).ToList();
                var older = history.Where(p => p.ObservedAt < now.AddDays(-1)).ToList();
                var compacted = Compact(older, 60);
                foreach (int minutes in new[] { 120, 240, 480, 720, 1440 })
                {
                    if (compacted.Count + recent.Count <= MaximumPoints) break;
                    compacted = Compact(older, minutes);
                }
                foreach (int minutes in new[] { 10, 15, 30, 60, 120, 240, 480, 1440 })
                {
                    if (compacted.Count + recent.Count <= MaximumPoints) break;
                    recent = Compact(recent, minutes);
                }
                compacted.AddRange(recent);
                compacted.Sort((left, right) => left.ObservedAt.CompareTo(right.ObservedAt));
                if (compacted.Count > MaximumPoints)
                {
                    // Defensive bound for pathological label churn: evenly retain
                    // observations across the full time range, including both ends.
                    var bounded = new List<UsageHistoryPoint>(MaximumPoints);
                    for (int i = 0; i < MaximumPoints; i++) bounded.Add(compacted[(int)((long)i * (compacted.Count - 1) / (MaximumPoints - 1))]);
                    compacted = bounded;
                }
                if (compacted.Count != history.Count || !compacted.SequenceEqual(history))
                {
                    history.Clear(); history.AddRange(compacted); changed = true;
                }
                nextCompaction = now.AddMinutes(5);
            }
            return changed;
        }

        private bool RememberNotices(List<UsageNotice> notices)
        {
            if (notices.Count == 0) return false;
            recentNotices.AddRange(notices.Select(CopyNotice));
            if (recentNotices.Count > MaximumNotices) recentNotices.RemoveRange(0, recentNotices.Count - MaximumNotices);
            return true;
        }

        private static List<UsageHistoryPoint> Compact(List<UsageHistoryPoint> points, int minutes)
        {
            var result = new List<UsageHistoryPoint>();
            foreach (var metric in points.GroupBy(p => p.ServiceId + "\n" + p.Label + "\n" + (p.RemainingPercent.HasValue ? "%" : "amount")))
            {
                // Compact each continuous source run independently. Returning
                // to Windows or a previous distribution must retain the switch
                // boundary even when several switches share one hourly bucket.
                var runs = new List<List<UsageHistoryPoint>>();
                foreach (var point in metric.OrderBy(p => p.ObservedAt))
                {
                    if (runs.Count == 0 || runs[runs.Count - 1][0].Source != point.Source) runs.Add(new List<UsageHistoryPoint>());
                    runs[runs.Count - 1].Add(point);
                }
                foreach (var run in runs)
                {
                    var first = run[0];
                    result.Add(first);
                    foreach (var bucket in run.GroupBy(p => p.ObservedAt.UtcTicks / TimeSpan.FromMinutes(minutes).Ticks))
                    {
                        var last = bucket.Last();
                        if (Object.ReferenceEquals(first, last)) continue;
                        if (bucket.Count() > 1 || last.ResolutionMinutes > 0)
                        {
                            last = CopyPoint(last);
                            last.ResolutionMinutes = Math.Max(last.ResolutionMinutes, minutes);
                        }
                        result.Add(last);
                    }
                }
            }
            return result;
        }

        private static void ValidateService(string value)
        {
            if (!ServiceIds.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("알 수 없는 서비스입니다.", "serviceId");
        }

        private static UsageTrackingOptions CopyOptions(UsageTrackingOptions value)
        {
            int low = Math.Max(1, Math.Min(99, value.LowThresholdPercent));
            return new UsageTrackingOptions { LowEnabled = value.LowEnabled, ResetEnabled = value.ResetEnabled, HistoryEnabled = value.HistoryEnabled, ForecastEnabled = value.ForecastEnabled, LowThresholdPercent = low, CriticalThresholdPercent = Math.Max(0,Math.Min(low-1,value.CriticalThresholdPercent)), HiggsfieldCreditsThreshold = ValidAmount(value.HiggsfieldCreditsThreshold), HiggsfieldApiUsdThreshold = ValidAmount(value.HiggsfieldApiUsdThreshold) };
        }

        private static UsageHistoryPoint CopyPoint(UsageHistoryPoint value)
        {
            return new UsageHistoryPoint { ServiceId = value.ServiceId, Label = value.Label, Source = value.Source, ObservedAt = value.ObservedAt, RemainingPercent = value.RemainingPercent, Remaining = value.Remaining, Limit = value.Limit, ResetsAt = value.ResetsAt, DisplayValue = value.DisplayValue, ResolutionMinutes = value.ResolutionMinutes };
        }

        private static UsageNotice CopyNotice(UsageNotice value)
        {
            return new UsageNotice { Kind = value.Kind, ServiceId = value.ServiceId, Label = value.Label, Message = value.Message, CreatedAt = value.CreatedAt, ScheduledAt = value.ScheduledAt, Source = value.Source };
        }

        private static string CleanText(string value, int maximum)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string result = new String(value.Where(c => !Char.IsControl(c)).Take(maximum).ToArray()).Trim();
            return result.Length == 0 ? null : result;
        }

        private static double? ValidAmount(double? value) { return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) && value.Value >= 0 ? value : null; }
        private static double? ValidPercent(double? value) { return ValidAmount(value).HasValue && value.Value <= 100 ? value : null; }
        private static string Stamp(DateTimeOffset? value) { return value.HasValue ? value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null; }
        private static DateTimeOffset? ReadStamp(string value)
        {
            DateTimeOffset result;
            return DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out result) ? (DateTimeOffset?)result : null;
        }

        private void Save()
        {
            if (memoryOnly) return;
            if (backgroundPersistence)
            {
                revision++;
                StartWriter();
                return;
            }
            try
            {
                SaveCore(Capture());
                committed = Capture();
            }
            catch
            {
                Restore(committed);
                throw;
            }
        }

        // Call while holding gate. Exactly one worker owns writes; it takes a
        // short immutable snapshot, then serializes and flushes outside the lock.
        private void StartWriter()
        {
            if (writerRunning || revision <= savedRevision) return;
            writerRunning = true;
            writerTask = Task.Run(async () => {
                while (true)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    JournalBackup snapshot; long version;
                    lock (gate) { snapshot = Capture(); version = revision; }
                    try { SaveCore(snapshot); }
                    catch
                    {
                        lock (gate)
                        {
                            // Keep newer in-memory data for retry. The previous
                            // atomically committed file has not been replaced.
                            persistenceError = "사용 이력을 저장하지 못했습니다. 기존 파일과 이번 실행의 기록을 보관하고 다음 저장에서 재시도합니다. 저장 공간과 폴더 권한을 확인해 주세요.";
                            writerRunning = false;
                        }
                        RaisePersistenceChanged();
                        return;
                    }
                    bool finished;
                    lock (gate)
                    {
                        committed = snapshot; savedRevision = version; persistenceError = null;
                        finished = savedRevision >= revision;
                        if (finished) writerRunning = false;
                    }
                    RaisePersistenceChanged();
                    if (finished) return;
                }
            });
        }

        private void RaisePersistenceChanged()
        {
            var handler = PersistenceChanged;
            if (handler != null) { try { handler(this, EventArgs.Empty); } catch { } }
        }

        public async Task FlushAsync()
        {
            if (memoryOnly || !backgroundPersistence) return;
            while (true)
            {
                Task pending;
                lock (gate)
                {
                    if (savedRevision >= revision) return;
                    StartWriter(); pending = writerTask;
                }
                await pending.ConfigureAwait(false);
                lock (gate)
                {
                    if (!String.IsNullOrEmpty(persistenceError)) throw new IOException(persistenceError);
                    if (savedRevision >= revision) return;
                }
            }
        }

        // Recovery export is independent of the normal writer. It never marks the
        // primary file as saved or overwrites it, even after a failed flush.
        public void WriteRecoveryBackup(string destination)
        {
            if (String.IsNullOrWhiteSpace(destination)) throw new ArgumentException("백업 위치를 선택해 주세요.", "destination");
            destination = Path.GetFullPath(destination);
            if (!String.IsNullOrEmpty(path) && String.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("기존 이력 파일과 다른 위치를 선택해 주세요.");
            JournalBackup snapshot;
            lock (gate) { snapshot = Capture(); }
            SaveCore(snapshot, destination);
        }

        private JournalBackup Capture()
        {
            return new JournalBackup { Options = CopyOptions(options), History = history.ToList(), Notices = recentNotices.Select(CopyNotice).ToList(), States = states.ToDictionary(pair => pair.Key, pair => CopyState(pair.Value), StringComparer.Ordinal), AnalysisOwner = analysisOwner, Analyses = analyses.ToDictionary(pair => pair.Key,pair => CopyAnalysisState(pair.Value),StringComparer.Ordinal) };
        }

        private void Restore(JournalBackup backup)
        {
            options = CopyOptions(backup.Options);
            history.Clear();
            history.AddRange(backup.History);
            recentNotices.Clear(); recentNotices.AddRange(backup.Notices.Select(CopyNotice));
            states.Clear();
            foreach (var pair in backup.States) states.Add(pair.Key, CopyState(pair.Value));
            analyses.Clear(); analysisOwner = backup.AnalysisOwner;
            foreach (var pair in backup.Analyses) analyses.Add(pair.Key, CopyAnalysisState(pair.Value));
        }

        private static RowState CopyState(RowState state)
        {
            return new RowState { ServiceId = state.ServiceId, Label = state.Label, Source = state.Source, LastObserved = state.LastObserved, LastSample = state.LastSample, ResetAt = state.ResetAt, LowAlerted = state.LowAlerted, CriticalAlerted = state.CriticalAlerted, EmptyAlerted = state.EmptyAlerted, ResetEligible = state.ResetEligible, ResetConsumed = state.ResetConsumed };
        }

        private void SaveCore(JournalBackup snapshot) { SaveCore(snapshot, path); }

        private void SaveCore(JournalBackup snapshot, string destination)
        {
            var document = new JournalDocument { Version = 1, Options = CopyOptions(snapshot.Options) };
            document.Points = snapshot.History.Select(point => new PointDocument { ServiceId = point.ServiceId, Label = point.Label, Source = point.Source, DisplayValue = point.DisplayValue, ObservedAt = Stamp(point.ObservedAt), RemainingPercent = point.RemainingPercent, Remaining = point.Remaining, Limit = point.Limit, ResetsAt = Stamp(point.ResetsAt), ResolutionMinutes = point.ResolutionMinutes }).ToList();
            document.States = snapshot.States.Values.Select(state => new StateDocument { ServiceId = state.ServiceId, Label = state.Label, Source = state.Source, LastObserved = Stamp(state.LastObserved), LastSample = Stamp(state.LastSample), ResetAt = Stamp(state.ResetAt), LowAlerted = state.LowAlerted, CriticalAlerted = state.CriticalAlerted, EmptyAlerted = state.EmptyAlerted, ResetEligible = state.ResetEligible, ResetConsumed = state.ResetConsumed }).ToList();
            document.AnalysisOwner = snapshot.AnalysisOwner;
            document.Analyses = snapshot.Analyses.Values.Select(AnalysisDocument).ToList();
            document.Notices = snapshot.Notices.Select(notice => new NoticeDocument { Kind = notice.Kind, ServiceId = notice.ServiceId, Label = notice.Label, Message = notice.Message, CreatedAt = Stamp(notice.CreatedAt), ScheduledAt = Stamp(notice.ScheduledAt) }).ToList();
            string json = new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 32 }.Serialize(document);
            byte[] content = new UTF8Encoding(false).GetBytes(json);
            if (content.Length > MaximumBytes) throw new IOException("사용 이력 파일의 크기 제한을 초과했습니다.");
            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".usage-journal." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(true);
                }
                for (int attempt = 0; ; attempt++) {
                    try {
                        if (File.Exists(destination)) File.Replace(temporary, destination, null);
                        else File.Move(temporary, destination);
                        break;
                    } catch (IOException error) {
                        int code = error.HResult & 0xffff;
                        // Brief scanner/read handles must not lose a measurement.
                        // Never delete the old journal to force a replacement.
                        if (attempt >= 4 || (code != 32 && code != 33 && code != 1175) || !File.Exists(temporary)) throw;
                        System.Threading.Thread.Sleep(50 * (attempt + 1));
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private void Load()
        {
            string json = null;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length <= MaximumBytes)
                            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) json = reader.ReadToEnd();
                    }
                    break;
                }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                catch (IOException error)
                {
                    // A scanner's brief exclusive handle is not a missing journal.
                    int code = error.HResult & 0xffff;
                    if (attempt >= 4 || (code != 32 && code != 33)) throw;
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
            }
            JournalDocument document = null;
            try { if (json != null) document = new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 32 }.Deserialize<JournalDocument>(json); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (document == null || document.Version != 1)
            {
                // Oversized, corrupt or newer-format history is kept under a new name
                // so the next save cannot replace it. A failed move throws, and the
                // app then keeps this run in memory instead of overwriting the file.
                File.Move(path, Path.Combine(Path.GetDirectoryName(path), "usage-journal.unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json"));
                return;
            }
            if (document.Options != null) options = CopyOptions(document.Options);
            foreach (PointDocument point in (document.Points ?? new List<PointDocument>()).Take(MaximumPoints * 2))
            {
                if (point == null || !ServiceIds.Contains(point.ServiceId) || !ValidSource(point.Source)) continue;
                DateTimeOffset? observed = ReadStamp(point.ObservedAt);
                string label = CleanText(point.Label, 120);
                if (!observed.HasValue || label == null) continue;
                history.Add(new UsageHistoryPoint { ServiceId = point.ServiceId, Label = label, Source = point.Source, ObservedAt = observed.Value, RemainingPercent = ValidPercent(point.RemainingPercent), Remaining = ValidAmount(point.Remaining), Limit = ValidAmount(point.Limit), ResetsAt = ReadStamp(point.ResetsAt), DisplayValue = CleanText(point.DisplayValue, 80), ResolutionMinutes = Math.Max(0, Math.Min(1440, point.ResolutionMinutes)) });
            }
            foreach (StateDocument state in (document.States ?? new List<StateDocument>()).Take(MaximumRows))
            {
                if (state == null || !ServiceIds.Contains(state.ServiceId) || !ValidSource(state.Source)) continue;
                string label = CleanText(state.Label, 120);
                DateTimeOffset? observed = ReadStamp(state.LastObserved), reset = ReadStamp(state.ResetAt);
                if (label == null || !observed.HasValue || observed.Value > DateTimeOffset.UtcNow.AddMinutes(1)) continue;
                states[state.ServiceId + "\n" + label] = new RowState { ServiceId = state.ServiceId, Label = label, Source = state.Source, LastObserved = observed, LastSample = ReadStamp(state.LastSample), ResetAt = reset, LowAlerted = state.LowAlerted, CriticalAlerted = state.CriticalAlerted, EmptyAlerted = state.EmptyAlerted, ResetConsumed = state.ResetConsumed, ResetEligible = state.ResetEligible && reset.HasValue && observed.Value < reset.Value };
            }
            foreach (NoticeDocument notice in (document.Notices ?? new List<NoticeDocument>()).Take(MaximumNotices))
            {
                if (notice == null || !ServiceIds.Contains(notice.ServiceId) || !new[] { "low", "critical", "empty", "reset", "reset-late", "reset-confirmed", "forecast" }.Contains(notice.Kind)) continue;
                DateTimeOffset? created = ReadStamp(notice.CreatedAt);
                string label = CleanText(notice.Label, 120), message = CleanText(notice.Message, 400);
                if (!created.HasValue || label == null || message == null) continue;
                recentNotices.Add(new UsageNotice { Kind = notice.Kind, ServiceId = notice.ServiceId, Label = label, Message = message, CreatedAt = created.Value, ScheduledAt = ReadStamp(notice.ScheduledAt) });
            }
            analysisOwner = document.AnalysisOwner != null && document.AnalysisOwner.Length == 64 && document.AnalysisOwner.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f') ? document.AnalysisOwner : null;
            foreach (var entry in (document.Analyses ?? new List<AnalysisStateDocument>()).Take(MaximumRows)) {
                AnalysisState restored = RestoreAnalysisDocument(entry,DateTimeOffset.UtcNow);
                if (restored != null) analyses[restored.ServiceId + "\n" + restored.Label] = restored;
            }
            Prune(DateTimeOffset.UtcNow);
        }

        private static bool ValidSource(string value) { return UsageSourceIdentity.IsValid(value); }

        private sealed class AnalysisState
        {
            public string ServiceId, Label, Source, Unit, Epoch, TimeZoneId, Plan;
            public DateTimeOffset? FirstObserved, LastObserved, ResetAt, ConfirmedReset, ForecastLastAlertAt;
            public double? Remaining, Limit;
            public bool Supported, IncludePrevious, ForecastAlerted, ResetEligible, ForecastRecovered;
            public int ForecastAlertedRisk;
            public List<AnalysisDay> Days = new List<AnalysisDay>();
            public string ObservedUnit;
            public DateTimeOffset? ObservedSince;
            public double? ObservedValue;
            public bool ObservedContinue;
            public List<UsageObservedBucket> ObservedBuckets = new List<UsageObservedBucket>();
        }

        private sealed class AnalysisDay
        {
            public DateTime Day;
            public double Consumed, Hours;
            public int Intervals;
            public bool Uncertain;
        }

        private static AnalysisState CopyAnalysisState(AnalysisState state)
        {
            return new AnalysisState { ServiceId = state.ServiceId, Label = state.Label, Source = state.Source, Unit = state.Unit, Epoch = state.Epoch, TimeZoneId = state.TimeZoneId, Plan = state.Plan,
                FirstObserved = state.FirstObserved, LastObserved = state.LastObserved, ResetAt = state.ResetAt, ConfirmedReset = state.ConfirmedReset,
                Remaining = state.Remaining, Limit = state.Limit, Supported = state.Supported, IncludePrevious = state.IncludePrevious, ForecastAlerted = state.ForecastAlerted, ResetEligible = state.ResetEligible,
                ForecastLastAlertAt = state.ForecastLastAlertAt, ForecastAlertedRisk = state.ForecastAlertedRisk, ForecastRecovered = state.ForecastRecovered,
                ObservedSince=state.ObservedSince,ObservedUnit=state.ObservedUnit,ObservedValue=state.ObservedValue,ObservedContinue=state.ObservedContinue,
                ObservedBuckets=state.ObservedBuckets.Select(CopyObservedBucket).ToList(),
                Days = state.Days.Select(day => new AnalysisDay { Day = day.Day, Consumed = day.Consumed, Hours = day.Hours, Intervals = day.Intervals, Uncertain = day.Uncertain }).ToList() };
        }

        private static AnalysisStateDocument AnalysisDocument(AnalysisState state)
        {
            return new AnalysisStateDocument { ServiceId = state.ServiceId, Label = state.Label, Source = state.Source, Unit = state.Unit, Epoch = state.Epoch, TimeZoneId = state.TimeZoneId, Plan = state.Plan,
                FirstObserved = Stamp(state.FirstObserved), LastObserved = Stamp(state.LastObserved), ResetAt = Stamp(state.ResetAt), ConfirmedReset = Stamp(state.ConfirmedReset),
                Remaining = state.Remaining, Limit = state.Limit, Supported = state.Supported, IncludePrevious = state.IncludePrevious, ForecastAlerted = state.ForecastAlerted, ResetEligible = state.ResetEligible,
                ForecastPolicyVersion = 2, ForecastLastAlertAt = Stamp(state.ForecastLastAlertAt), ForecastAlertedRisk = state.ForecastAlertedRisk, ForecastRecovered = state.ForecastRecovered,
                ObservedSince=Stamp(state.ObservedSince),ObservedUnit=state.ObservedUnit,
                ObservedBuckets=state.ObservedBuckets.Select(bucket=>new ObservedBucketDocument {s=bucket.Start.UtcTicks,e=bucket.End.UtcTicks,
                    c=bucket.Consumption,h=bucket.Hours,g=bucket.MaximumGapMinutes,n=bucket.Intervals}).ToList(),
                Days = state.Days.Select(day => new AnalysisDayDocument { Day = day.Day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture), Consumed = day.Consumed, Hours = day.Hours, Intervals = day.Intervals, Uncertain = day.Uncertain }).ToList() };
        }

        private static AnalysisState RestoreAnalysisDocument(AnalysisStateDocument entry, DateTimeOffset now)
        {
            if (entry == null || !ServiceIds.Contains(entry.ServiceId) || !ValidSource(entry.Source)) return null;
            string label = CleanText(entry.Label,120), zone = CleanText(entry.TimeZoneId,256);
            DateTimeOffset? first = ReadStamp(entry.FirstObserved), last = ReadStamp(entry.LastObserved);
            Guid epoch;
            if (label == null || zone == null || !Guid.TryParseExact(entry.Epoch,"N",out epoch) || !first.HasValue || !last.HasValue
                || first > last || last > now.AddMinutes(1) || last < now.AddDays(-30) || (entry.Unit != null && entry.Unit != "%p" && entry.Unit != "credits" && entry.Unit != "USD")) return null;
            var result = new AnalysisState { ServiceId = entry.ServiceId, Label = label, Source = entry.Source, Unit = entry.Unit, Epoch = entry.Epoch, TimeZoneId = zone, Plan = CleanText(entry.Plan,80),
                FirstObserved = first, LastObserved = last, ResetAt = ReadStamp(entry.ResetAt), ConfirmedReset = ReadStamp(entry.ConfirmedReset), Remaining = ValidAmount(entry.Remaining), Limit = ValidAmount(entry.Limit),
                Supported = entry.Supported && ValidAmount(entry.Remaining).HasValue, IncludePrevious = entry.IncludePrevious && ValidAmount(entry.Remaining).HasValue, ForecastAlerted = entry.ForecastAlerted, ResetEligible = entry.ResetEligible };
            // Old once-per-cycle records have no exact dispatch time or severity.
            // Treat the last observation as a recent strongest warning, so an
            // upgrade cannot replay a weaker warning or bypass the cooldown.
            bool legacyAlert = entry.ForecastPolicyVersion < 2 && entry.ForecastAlerted;
            result.ForecastAlertedRisk = legacyAlert ? 3 : Math.Max(0,Math.Min(3,entry.ForecastAlertedRisk));
            DateTimeOffset? alertedAt = legacyAlert ? last : ReadStamp(entry.ForecastLastAlertAt);
            result.ForecastLastAlertAt = alertedAt.HasValue && alertedAt >= first && alertedAt <= now.AddMinutes(1) ? alertedAt : result.ForecastAlertedRisk > 0 ? last : null;
            result.ForecastRecovered = !legacyAlert && entry.ForecastRecovered && result.ForecastAlertedRisk > 0;
            var seen = new HashSet<DateTime>();
            foreach (var day in (entry.Days ?? new List<AnalysisDayDocument>()).Take(10)) {
                DateTime date;
                if (day == null || !DateTime.TryParseExact(day.Day,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date)
                    || date < now.ToLocalTime().Date.AddDays(-8) || date > now.ToLocalTime().Date || !seen.Add(date)
                    || !ValidAmount(day.Hours).HasValue || day.Hours > 26 || !ValidAmount(day.Consumed).HasValue || day.Intervals < 0 || day.Intervals > 1000000) continue;
                result.Days.Add(new AnalysisDay { Day = date, Consumed = day.Consumed, Hours = day.Hours, Intervals = day.Intervals, Uncertain = day.Uncertain });
            }
            DateTimeOffset? since=ReadStamp(entry.ObservedSince);
            if(since.HasValue && since>=first && since<=last && (entry.ObservedUnit==null || entry.ObservedUnit=="%p" || entry.ObservedUnit=="크레딧" || entry.ObservedUnit=="USD")) {
                result.ObservedSince=since; result.ObservedUnit=entry.ObservedUnit;
                // A restart is a collection boundary. Retain exact previous
                // aggregates, but do not bridge the downtime with a new poll.
                result.ObservedContinue=false;
                long previousEnd=0;
                foreach(var bucket in (entry.ObservedBuckets ?? new List<ObservedBucketDocument>()).Take(746).OrderBy(bucket=>bucket==null ? 0 : bucket.s)) {
                    if(bucket==null || bucket.s<since.Value.UtcTicks || bucket.s<now.AddDays(-30).UtcTicks || bucket.s<previousEnd
                        || bucket.e<=bucket.s || bucket.e>last.Value.UtcTicks || bucket.e>now.UtcTicks
                        || bucket.e-bucket.s>TimeSpan.TicksPerHour*27 || bucket.n<1 || bucket.n>1000000
                        || !ValidAmount(bucket.c).HasValue || !ValidAmount(bucket.h).HasValue || bucket.h<=0
                        || bucket.h>(bucket.e-bucket.s)/(double)TimeSpan.TicksPerHour+0.000001
                        || !ValidAmount(bucket.g).HasValue || bucket.g<=0 || bucket.g>30
                        || bucket.h*60>bucket.g*bucket.n+0.000001) continue;
                    result.ObservedBuckets.Add(new UsageObservedBucket {Start=new DateTimeOffset(bucket.s,TimeSpan.Zero),End=new DateTimeOffset(bucket.e,TimeSpan.Zero),
                        Consumption=bucket.c,Hours=bucket.h,MaximumGapMinutes=bucket.g,Intervals=bucket.n});
                    previousEnd=bucket.e;
                }
            }
            return result;
        }

        public sealed class AnalysisStateDocument
        {
            public string ServiceId, Label, Source, Unit, Epoch, TimeZoneId, Plan, FirstObserved, LastObserved, ResetAt, ConfirmedReset, ForecastLastAlertAt;
            public double? Remaining, Limit;
            public bool Supported, IncludePrevious, ForecastAlerted, ResetEligible, ForecastRecovered;
            public int ForecastPolicyVersion, ForecastAlertedRisk;
            public List<AnalysisDayDocument> Days;
            public string ObservedSince, ObservedUnit;
            public List<ObservedBucketDocument> ObservedBuckets;
        }

        // Short field names keep the bounded aggregate set well inside the
        // existing 16 MiB journal limit, alongside 20,000 chart measurements.
        public sealed class ObservedBucketDocument
        {
            public long s,e;
            public double c,h,g;
            public int n;
        }

        public sealed class AnalysisDayDocument
        {
            public string Day;
            public double Consumed, Hours;
            public int Intervals;
            public bool Uncertain;
        }

        private sealed class RowState
        {
            public string ServiceId, Label, Source;
            public DateTimeOffset? LastObserved, LastSample, ResetAt;
            public bool LowAlerted, CriticalAlerted, EmptyAlerted, ResetEligible, ResetConsumed;
        }

        private sealed class JournalBackup
        {
            public UsageTrackingOptions Options;
            public List<UsageHistoryPoint> History;
            public List<UsageNotice> Notices;
            public Dictionary<string, RowState> States;
            public string AnalysisOwner;
            public Dictionary<string, AnalysisState> Analyses;
        }

        // Dates are ISO strings instead of JavaScriptSerializer's DateTime format,
        // preserving UTC instants across Windows locales and time-zone changes.
        public sealed class JournalDocument
        {
            public int Version;
            public UsageTrackingOptions Options;
            public List<PointDocument> Points;
            public List<StateDocument> States;
            public List<NoticeDocument> Notices;
            public string AnalysisOwner;
            public List<AnalysisStateDocument> Analyses;
        }
        public sealed class PointDocument
        {
            public string ServiceId, Label, Source, DisplayValue, ObservedAt, ResetsAt;
            public double? RemainingPercent, Remaining, Limit;
            public int ResolutionMinutes;
        }
        public sealed class StateDocument
        {
            public string ServiceId, Label, Source, LastObserved, LastSample, ResetAt;
            public bool LowAlerted, CriticalAlerted, EmptyAlerted, ResetEligible, ResetConsumed;
        }
        public sealed class NoticeDocument
        {
            public string Kind, ServiceId, Label, Message, CreatedAt, ScheduledAt;
        }
    }
}
