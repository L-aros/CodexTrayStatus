using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    public sealed class QuotaService
    {
        private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
        private const int RequestTimeoutMilliseconds = 10000;
        private const int FileCollectionLimit = 150;
        private const int FileReadLimit = 100;
        private const int LocalFallbackReadLimit = 12;
        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        private readonly HttpClient _httpClient;
        private readonly string _codexHome;
        private string currentAccountKey;
        private long usageScanCompletedAt;
        private string usageCoverageStart;
        private string usageCoverageEnd;

        public QuotaService()
            : this(SharedHttpClient, null)
        {
        }

        public QuotaService(HttpClient httpClient, string codexHome)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException("httpClient");
            }

            _httpClient = httpClient;
            _codexHome = string.IsNullOrWhiteSpace(codexHome) ? GetCodexHome() : codexHome;
        }

        public string GetAuthPath()
        {
            return Path.Combine(_codexHome, "auth.json");
        }

        public async Task<AppSnapshot> RefreshAsync()
        {
            long attemptedAt = UtcNowMilliseconds();
            Task<QuotaResult> quotaTask = FetchQuotaAsync();
            Task<List<DailyUsage>> todayTask = FetchDailyUsageAsync();
            List<DailyUsage> daily = null;
            QuotaResult quota = null;
            TodayUsage today = null;
            string error = null;
            long? usageSuccessAt = null;

            try
            {
                quota = await quotaTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                error = "额度暂不可用";
            }

            try
            {
                daily = await todayTask.ConfigureAwait(false);
                usageSuccessAt = usageScanCompletedAt;
                today = daily.Find(delegate(DailyUsage day) { return day.Date == DateTime.Today; }) ?? new TodayUsage();
            }
            catch (Exception)
            {
                error = string.IsNullOrEmpty(error) ? "用量统计暂不可用，保留上次结果" : error + "；用量统计暂不可用";
            }

            return new AppSnapshot
            {
                Quota = quota,
                RequestedAccountKey = currentAccountKey,
                RefreshState = new RefreshState { AttemptId = Guid.NewGuid().ToString("N"), AttemptedAt = attemptedAt, CompletedAt = UtcNowMilliseconds() },
                QuotaState = quota == null ? new DataState { Source = "none", Delivery = "unavailable", AccountKey = currentAccountKey, LastAttemptAt = attemptedAt, LastAttemptOutcome = "failed", ErrorCode = "quota_failed", TimeBasis = "unknown" } : quota.State,
                UsageState = usageSuccessAt.HasValue ? new DataState { Source = "local", Scope = "local_logs", Delivery = "updated", ObservedAt = usageSuccessAt, LastSuccessAt = usageSuccessAt, LastAttemptAt = attemptedAt, LastAttemptOutcome = "success", TimeBasis = "scan_completed", IsComplete = true, CoverageStartDate = usageCoverageStart, CoverageEndDate = usageCoverageEnd } : DataFreshness.Unavailable(attemptedAt, "usage_scan_failed"),
                // Keep null distinct from a legitimate zero-usage day. The application
                // can then preserve its last good totals when a session file is briefly
                // locked or unavailable instead of flashing every metric back to zero.
                Today = today,
                Daily = daily,
                Error = error,
                RefreshedAt = UtcNowMilliseconds()
            };
        }

        public async Task<QuotaResult> FetchQuotaAsync()
        {
            Exception officialError;
            long attemptedAt = UtcNowMilliseconds();
            currentAccountKey = null;
            try
            {
                Credentials credentials = await LoadCredentialsAsync().ConfigureAwait(false);
                currentAccountKey = AccountIdentity(credentials.AccountId);
                string json;

                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageUrl))
                using (CancellationTokenSource timeout = new CancellationTokenSource(RequestTimeoutMilliseconds))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");
                    if (!string.IsNullOrEmpty(credentials.AccountId))
                    {
                        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
                    }

                    using (HttpResponseMessage response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead,
                        timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException(
                                "额度接口返回 HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                        }

                        json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }

                List<RateLimitWindow> limits = ParseRateLimits(json, UtcNowMilliseconds());
                if (limits.Count == 0)
                {
                    throw new InvalidOperationException("额度接口未返回可显示的窗口");
                }

                return new QuotaResult
                {
                    Limits = limits,
                    RefreshedAt = UtcNowMilliseconds(),
                    Source = "official",
                    State = new DataState { Source = "official", Scope = "account", Delivery = "updated", AccountKey = currentAccountKey, ObservedAt = UtcNowMilliseconds(), LastSuccessAt = UtcNowMilliseconds(), LastOfficialSuccessAt = UtcNowMilliseconds(), LastAttemptAt = attemptedAt, LastAttemptOutcome = "success", TimeBasis = "response_received", IsComplete = true, IsNewObservation = true, ObservationId = Guid.NewGuid().ToString("N") }
                };
            }
            catch (Exception exception)
            {
                officialError = exception;
            }

            QuotaResult local = await FetchLocalQuotaAsync().ConfigureAwait(false);
            if (local.Limits.Count != 0)
            {
                local.Source = "local";
                local.State.LastAttemptAt = attemptedAt;
                local.State.LastAttemptOutcome = "partial";
                local.State.ErrorCode = IsTimeout(officialError) ? "official_timeout" : "official_failed";
                local.Warning = IsTimeout(officialError)
                    ? "官方额度请求超时，已回退本机会话数据"
                    : "官方额度不可用，已回退本机会话数据";
                return local;
            }

            if (IsTimeout(officialError))
            {
                throw new TimeoutException("额度请求超时", officialError);
            }

            ExceptionDispatchInfo.Capture(officialError).Throw();
            throw officialError;
        }

        private readonly Dictionary<string, UsageCache> usageCache = new Dictionary<string, UsageCache>(StringComparer.OrdinalIgnoreCase);
        private sealed class UsageCache
        {
            internal long Length;
            internal DateTime Modified;
            internal List<DailyUsage> Days;
        }

        public async Task<TodayUsage> FetchTodayUsageAsync()
        {
            List<DailyUsage> days = await FetchDailyUsageAsync().ConfigureAwait(false);
            return days.Find(delegate(DailyUsage day) { return day.Date == DateTime.Today; }) ?? new TodayUsage();
        }

        public Task<List<DailyUsage>> FetchDailyUsageAsync()
        {
            return Task.Run(delegate
            {
                lock (usageCache)
                {
                    List<FileEntry> files = new List<FileEntry>();
                    CollectJsonlFiles(Path.Combine(_codexHome, "sessions"), files, int.MaxValue, true);
                    CollectJsonlFiles(Path.Combine(_codexHome, "archived_sessions"), files, int.MaxValue, true);
                    SortedDictionary<DateTime, DailyUsage> totals = new SortedDictionary<DateTime, DailyUsage>();
                    const int retentionDays = 90;
                    DateTime first = DateTime.Today.AddDays(-(retentionDays - 1));
                    for (int i = 0; i < retentionDays; i++) totals[first.AddDays(i)] = new DailyUsage { Date = first.AddDays(i) };
                    foreach (FileEntry file in files)
                    {
                        FileInfo info = new FileInfo(file.Path);
                        if (info.LastWriteTime.Date < first) continue;
                        UsageCache cached;
                        if (!usageCache.TryGetValue(file.Path, out cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc)
                        {
                            cached = new UsageCache { Length = info.Length, Modified = info.LastWriteTimeUtc,
                                Days = SummarizeDailyJsonl(ReadLinesShared(file.Path)) };
                            usageCache[file.Path] = cached;
                        }
                        foreach (DailyUsage part in cached.Days)
                        {
                            DailyUsage total;
                            if (!totals.TryGetValue(part.Date, out total)) continue;
                            AddUsage(total, part);
                            foreach (ModelUsage model in part.Models)
                            {
                                ModelUsage combined = total.Models.Find(delegate(ModelUsage item) { return item.Model == model.Model; });
                                if (combined == null)
                                {
                                    combined = new ModelUsage { Model = model.Model, InputRate = model.InputRate,
                                        CachedRate = model.CachedRate, OutputRate = model.OutputRate };
                                    total.Models.Add(combined);
                                }
                                AddUsage(combined, model);
                            }
                        }
                    }
                    usageScanCompletedAt = UtcNowMilliseconds();
                    usageCoverageStart = first.ToString("yyyy-MM-dd");
                    usageCoverageEnd = first.AddDays(retentionDays - 1).ToString("yyyy-MM-dd");
                    return new List<DailyUsage>(totals.Values);
                }
            });
        }

        public static List<RateLimitWindow> ParseRateLimits(string json, long nowMilliseconds)
        {
            IDictionary<string, object> payload = DeserializeObject(json);
            List<RateLimitWindow> result = new List<RateLimitWindow>();

            AppendOfficialRateLimitGroup(
                result,
                AsObject(GetFirst(payload, "rate_limit", "rateLimit")),
                "account",
                nowMilliseconds);

            IEnumerable additional = GetFirst(payload, "additional_rate_limits", "additionalRateLimits") as IEnumerable;
            if (additional != null)
            {
                foreach (object additionalItem in additional)
                {
                    IDictionary<string, object> item = AsObject(additionalItem);
                    if (item == null) continue;
                    string name = Convert.ToString(
                        GetFirst(item, "limit_name", "limitName"),
                        CultureInfo.InvariantCulture);
                    AppendOfficialRateLimitGroup(
                        result,
                        AsObject(GetFirst(item, "rate_limit", "rateLimit")),
                        string.IsNullOrEmpty(name) ? null : "pool:" + name,
                        nowMilliseconds);
                }
            }

            result.Sort(CompareRateLimitWindows);
            return result;
        }

        private static void AppendOfficialRateLimitGroup(
            List<RateLimitWindow> result,
            IDictionary<string, object> rateLimit,
            string groupId,
            long nowMilliseconds)
        {
            if (rateLimit == null) return;

            foreach (KeyValuePair<string, object> pair in rateLimit)
            {
                IDictionary<string, object> raw = AsObject(pair.Value);
                if (raw == null)
                {
                    continue;
                }

                double? seconds = AsFiniteNumber(GetFirst(raw, "limit_window_seconds", "limitWindowSeconds"));
                double? used = AsFiniteNumber(GetFirst(raw, "used_percent", "usedPercent"));
                long? resetAt = NormalizeEpoch(GetFirst(raw, "reset_at", "resetAt", "resets_at", "resetsAt"));
                double? resetIn = AsFiniteNumber(GetFirst(
                    raw,
                    "reset_after_seconds",
                    "resetAfterSeconds",
                    "resets_in_seconds",
                    "resetsInSeconds"));
                long? resetsAt = resetAt.HasValue && resetAt.Value != 0
                    ? resetAt
                    : AddSeconds(nowMilliseconds, resetIn);

                if (!used.HasValue && !resetsAt.HasValue)
                {
                    continue;
                }

                double? clampedUsed = used.HasValue && used.Value >= 0 && used.Value <= 100 ? used : null;
                string label = ChooseLabel(pair.Key, seconds);
                bool duplicate = false;
                for (int index = 0; index < result.Count; index++)
                {
                    if (groupId != null && string.Equals(result[index].Id, groupId + "." + pair.Key, StringComparison.Ordinal))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;

                result.Add(new RateLimitWindow
                {
                    Id = groupId + "." + pair.Key,
                    WindowKey = groupId == null ? null : groupId.Length.ToString(CultureInfo.InvariantCulture) + ":" + groupId + ":" + pair.Key,
                    WindowSeconds = seconds.HasValue && seconds.Value > 0 && seconds.Value < long.MaxValue ? (long?)seconds.Value : null,
                    Label = label,
                    UsedPercent = clampedUsed,
                    RemainingPercent = clampedUsed.HasValue ? 100.0 - clampedUsed.Value : (double?)null,
                    ResetsAt = resetsAt
                });
            }
        }

        public static List<RateLimitWindow> ParseLocalRateLimits(
            IEnumerable<string> lines,
            long fileModifiedMilliseconds,
            long nowMilliseconds)
        {
            LocalParseResult parsed = ParseLocalObservation(lines, fileModifiedMilliseconds, nowMilliseconds);
            return parsed == null ? new List<RateLimitWindow>() : parsed.Limits;
        }

        public void InvalidateUsageCache()
        {
            lock (usageCache) usageCache.Clear();
        }

        private static LocalParseResult ParseLocalObservation(IEnumerable<string> lines, long fileModifiedMilliseconds, long nowMilliseconds)
        {
            JavaScriptSerializer serializer = CreateSerializer();
            LocalParseResult latest = null;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                    line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                IDictionary<string, object> eventObject;
                try
                {
                    eventObject = AsObject(serializer.DeserializeObject(line));
                }
                catch
                {
                    continue;
                }

                IDictionary<string, object> payload = AsObject(GetFirst(eventObject, "payload"));
                if (!StringEquals(GetFirst(eventObject, "type"), "event_msg") ||
                    !StringEquals(GetFirst(payload, "type"), "token_count"))
                {
                    continue;
                }

                IDictionary<string, object> rateLimits = AsObject(GetFirst(payload, "rate_limits"));
                if (rateLimits == null)
                {
                    continue;
                }

                long observedAt;
                bool trustedTime = TryGetEventTimestamp(eventObject, out observedAt) && observedAt > 0;
                if (!trustedTime)
                {
                    observedAt = fileModifiedMilliseconds;
                }

                List<RateLimitWindow> limits = new List<RateLimitWindow>();
                RateLimitWindow primary = ParseLocalWindow(
                    "primary", AsObject(GetFirst(rateLimits, "primary")), observedAt, nowMilliseconds);
                RateLimitWindow secondary = ParseLocalWindow(
                    "secondary", AsObject(GetFirst(rateLimits, "secondary")), observedAt, nowMilliseconds);
                if (primary != null)
                {
                    limits.Add(primary);
                }
                if (secondary != null)
                {
                    limits.Add(secondary);
                }

                if (limits.Count != 0 && (latest == null || (trustedTime && !latest.TrustedTime) || (trustedTime == latest.TrustedTime && observedAt >= latest.ObservedAt)))
                {
                    latest = new LocalParseResult { Limits = limits, ObservedAt = observedAt, TrustedTime = trustedTime };
                }
            }

            return latest;
        }

        public static TodayUsage SummarizeTodayJsonl(IEnumerable<string> lines, DateTimeOffset now)
        {
            List<DailyUsage> days = SummarizeDailyJsonl(lines);
            return days.Find(delegate(DailyUsage day) { return day.Date == now.LocalDateTime.Date; }) ?? new TodayUsage();
        }

        public static List<DailyUsage> SummarizeDailyJsonl(IEnumerable<string> lines)
        {
            JavaScriptSerializer serializer = CreateSerializer();
            SortedDictionary<DateTime, DailyUsage> days = new SortedDictionary<DateTime, DailyUsage>();
            string model = null;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            long previousInput = 0, previousCached = 0, previousOutput = 0;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 &&
                    line.IndexOf("\"turn_context\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                IDictionary<string, object> eventObject;
                try
                {
                    eventObject = AsObject(serializer.DeserializeObject(line));
                }
                catch
                {
                    continue;
                }

                IDictionary<string, object> payload = AsObject(GetFirst(eventObject, "payload"));
                if (StringEquals(GetFirst(payload, "type"), "turn_context") || StringEquals(GetFirst(eventObject, "type"), "turn_context"))
                {
                    object contextModel = GetFirst(payload, "model");
                    if (contextModel is string)
                    {
                        model = (string)contextModel;
                    }
                }

                if (!StringEquals(GetFirst(eventObject, "type"), "event_msg") ||
                    !StringEquals(GetFirst(payload, "type"), "token_count"))
                {
                    continue;
                }

                long timestamp;
                DateTimeOffset eventTime;
                if (!TryGetEventTimestamp(eventObject, out timestamp, out eventTime))
                {
                    continue;
                }

                IDictionary<string, object> info = AsObject(GetFirst(payload, "info"));
                IDictionary<string, object> usage = AsObject(GetFirst(info, "last_token_usage"));
                IDictionary<string, object> cumulative = AsObject(GetFirst(info, "total_token_usage"));
                if (cumulative != null)
                {
                    long nextInput = AsNonNegativeTokenCount(GetFirst(cumulative, "input_tokens"));
                    long nextCached = AsNonNegativeTokenCount(GetFirst(cumulative, "cached_input_tokens"));
                    long nextOutput = AsNonNegativeTokenCount(GetFirst(cumulative, "output_tokens"));
                    if (nextInput >= previousInput && nextOutput >= previousOutput && nextCached >= previousCached)
                    {
                        usage = new Dictionary<string, object> {
                            { "input_tokens", nextInput - previousInput }, { "cached_input_tokens", nextCached - previousCached },
                            { "output_tokens", nextOutput - previousOutput } };
                    }
                    // A counter reset starts a new segment; use last usage when available.
                    else if (usage == null) usage = cumulative;
                    previousInput = nextInput; previousCached = nextCached; previousOutput = nextOutput;
                }
                if (usage == null)
                {
                    continue;
                }

                long input = AsNonNegativeTokenCount(GetFirst(usage, "input_tokens"));
                long cached = AsNonNegativeTokenCount(GetFirst(usage, "cached_input_tokens"));
                long output = AsNonNegativeTokenCount(GetFirst(usage, "output_tokens"));
                if (input == 0 && output == 0) continue;
                if (cached > input)
                {
                    cached = input;
                }

                object rawEventModel = GetFirst(payload, "model");
                string eventModel = rawEventModel is string ? (string)rawEventModel : model;
                string signature = timestamp.ToString(CultureInfo.InvariantCulture) + "|" +
                    input.ToString(CultureInfo.InvariantCulture) + "|" +
                    cached.ToString(CultureInfo.InvariantCulture) + "|" +
                    output.ToString(CultureInfo.InvariantCulture) + "|" + (eventModel ?? string.Empty);
                if (!seen.Add(signature))
                {
                    continue;
                }

                DailyUsage total;
                DateTime date = eventTime.LocalDateTime.Date;
                if (!days.TryGetValue(date, out total)) { total = new DailyUsage { Date = date }; days.Add(date, total); }
                eventModel = string.IsNullOrWhiteSpace(eventModel) ? "未知模型" : eventModel;
                Price price = PriceFor(eventModel);
                ModelUsage breakdown = total.Models.Find(delegate(ModelUsage item) { return item.Model == eventModel; });
                if (breakdown == null)
                {
                    breakdown = new ModelUsage { Model = eventModel, InputRate = price.Input, CachedRate = price.Cached, OutputRate = price.Output };
                    total.Models.Add(breakdown);
                }
                TodayUsage part = new TodayUsage {
                    Input = input, CachedInput = cached, Output = output, TotalTokens = SafeAdd(input, output),
                    UncachedInputCost = (input - cached) * price.Input / 1000000m,
                    CachedInputCost = cached * price.Cached / 1000000m,
                    OutputCost = output * price.Output / 1000000m,
                    UnpricedTokens = price.Input == 0 ? SafeAdd(input, output) : 0
                };
                part.EstimatedCost = part.UncachedInputCost + part.CachedInputCost + part.OutputCost;
                AddUsage(total, part);
                AddUsage(breakdown, part);
            }

            foreach (DailyUsage total in days.Values)
            {
                total.TotalTokens = SafeAdd(total.Input, total.Output);
            }
            return new List<DailyUsage>(days.Values);
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient();
        }

        private static string GetCodexHome()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        private async Task<Credentials> LoadCredentialsAsync()
        {
            string raw;
            try
            {
                using (StreamReader reader = new StreamReader(GetAuthPath()))
                {
                    raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                throw new InvalidOperationException("未找到 Codex 登录信息：" + GetAuthPath());
            }

            IDictionary<string, object> auth;
            try
            {
                auth = DeserializeObject(raw);
            }
            catch
            {
                throw new InvalidOperationException("Codex auth.json 不是有效 JSON");
            }

            IDictionary<string, object> tokens = AsObject(GetFirst(auth, "tokens"));
            string accessToken = GetTruthyString(tokens, "access_token", "accessToken");
            if (!StringEquals(GetFirst(auth, "auth_mode"), "chatgpt") || string.IsNullOrEmpty(accessToken))
            {
                throw new InvalidOperationException("当前不是可读取订阅额度的 ChatGPT 登录模式");
            }

            return new Credentials
            {
                AccessToken = accessToken,
                AccountId = GetTruthyString(tokens, "account_id", "accountId")
            };
        }

        private Task<QuotaResult> FetchLocalQuotaAsync()
        {
            return Task.Run(delegate
            {
                List<FileEntry> files = GetRecentJsonlFiles();
                List<RateLimitWindow> newestLimits = new List<RateLimitWindow>();
                LocalParseResult newest = null;
                int count = Math.Min(LocalFallbackReadLimit, files.Count);
                long nowMilliseconds = UtcNowMilliseconds();

                for (int index = 0; index < count; index++)
                {
                    FileEntry entry = files[index];
                    try
                    {
                        LocalParseResult parsed = ParseLocalObservation(ReadLinesShared(entry.Path), entry.ModifiedAtMilliseconds, nowMilliseconds);
                        if (parsed != null && (newest == null || (parsed.TrustedTime && !newest.TrustedTime) || (parsed.TrustedTime == newest.TrustedTime && parsed.ObservedAt > newest.ObservedAt)))
                        {
                            newest = parsed;
                            newestLimits = parsed.Limits;
                        }
                    }
                    catch
                    {
                        // Ignore files that disappear or become unreadable during the scan.
                    }
                }

                return new QuotaResult
                {
                    Limits = newestLimits,
                    RefreshedAt = newest != null && newest.TrustedTime ? newest.ObservedAt : 0,
                    State = new DataState { Source = "local", Scope = "unattributed_local_quota", Delivery = "fallback", ObservedAt = newest != null && newest.TrustedTime ? (long?)newest.ObservedAt : null, LastSuccessAt = UtcNowMilliseconds(), TimeBasis = newest != null && newest.TrustedTime ? "event_timestamp" : "unknown", IsComplete = true, IsNewObservation = newest != null && newest.TrustedTime, ObservationId = newest == null ? null : "local:" + newest.ObservedAt.ToString(CultureInfo.InvariantCulture) + ":" + AccountIdentity(new JavaScriptSerializer().Serialize(newestLimits)) }
                };
            });
        }

        private List<FileEntry> GetRecentJsonlFiles()
        {
            List<FileEntry> files = new List<FileEntry>();
            CollectJsonlFiles(Path.Combine(_codexHome, "sessions"), files, FileCollectionLimit);
            CollectJsonlFiles(Path.Combine(_codexHome, "archived_sessions"), files, FileCollectionLimit);
            files.Sort(delegate(FileEntry left, FileEntry right)
            {
                return right.ModifiedAtMilliseconds.CompareTo(left.ModifiedAtMilliseconds);
            });
            return files;
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }

        private static void CollectJsonlFiles(string root, List<FileEntry> entries, int limit, bool strict = false)
        {
            IEnumerable<string> children;
            try
            {
                try { File.GetAttributes(root); }
                catch (DirectoryNotFoundException) { return; }
                catch (FileNotFoundException) { return; }
                children = Directory.EnumerateFileSystemEntries(root);
            }
            catch
            {
                if (strict) throw;
                return;
            }

            try
            {
                foreach (string child in children)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                CollectJsonlFiles(child, entries, limit, strict);
                            }
                        }
                        else if (child.EndsWith(".jsonl", StringComparison.Ordinal))
                        {
                            DateTime modifiedUtc = File.GetLastWriteTimeUtc(child);
                            FileEntry candidate = new FileEntry
                            {
                                Path = child,
                                ModifiedAtMilliseconds = ToUnixMilliseconds(new DateTimeOffset(modifiedUtc, TimeSpan.Zero))
                            };
                            AddRecentFile(entries, candidate, limit);
                        }
                    }
                    catch
                    {
                        if (strict) throw;
                    }
                }
            }
            catch
            {
                if (strict) throw;
            }
        }

        private static void AddRecentFile(List<FileEntry> entries, FileEntry candidate, int limit)
        {
            if (limit <= 0)
            {
                return;
            }
            if (entries.Count < limit)
            {
                entries.Add(candidate);
                return;
            }

            int oldestIndex = 0;
            long oldestTime = entries[0].ModifiedAtMilliseconds;
            for (int index = 1; index < entries.Count; index++)
            {
                if (entries[index].ModifiedAtMilliseconds < oldestTime)
                {
                    oldestIndex = index;
                    oldestTime = entries[index].ModifiedAtMilliseconds;
                }
            }
            if (candidate.ModifiedAtMilliseconds > oldestTime)
            {
                entries[oldestIndex] = candidate;
            }
        }

        private static RateLimitWindow ParseLocalWindow(
            string id,
            IDictionary<string, object> raw,
            long observedAt,
            long nowMilliseconds)
        {
            if (raw == null)
            {
                return null;
            }

            double? windowMinutes = AsFiniteNumber(GetFirst(raw, "window_minutes", "windowMinutes"));
            double? usedPercent = AsFiniteNumber(GetFirst(raw, "used_percent", "usedPercent"));
            long? resetAt = NormalizeEpoch(GetFirst(raw, "resets_at", "reset_at", "resetsAt", "resetAt"));
            double? resetIn = AsFiniteNumber(GetFirst(
                raw, "resets_in_seconds", "reset_in_seconds", "resetsInSeconds"));
            long? resetsAt = resetAt.HasValue && resetAt.Value != 0
                ? resetAt
                : AddSeconds(observedAt, resetIn);
            if (!usedPercent.HasValue && !resetsAt.HasValue)
            {
                return null;
            }

            double? used = usedPercent.HasValue && usedPercent.Value >= 0 && usedPercent.Value <= 100 ? usedPercent : null;
            return new RateLimitWindow
            {
                Id = id,
                Label = ChooseLabel(id, windowMinutes.HasValue ? windowMinutes.Value * 60 : (double?)null),
                UsedPercent = used,
                RemainingPercent = used.HasValue ? 100 - used.Value : (double?)null,
                WindowSeconds = windowMinutes.HasValue && windowMinutes.Value > 0 && windowMinutes.Value < long.MaxValue / 60 ? (long?)(windowMinutes.Value * 60) : null,
                ResetsAt = resetsAt
            };
        }

        private static IDictionary<string, object> DeserializeObject(string json)
        {
            return AsObject(CreateSerializer().DeserializeObject(json));
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        }

        private static IDictionary<string, object> AsObject(object value)
        {
            return value as IDictionary<string, object>;
        }

        private static object GetFirst(IDictionary<string, object> dictionary, params string[] names)
        {
            if (dictionary == null)
            {
                return null;
            }

            for (int index = 0; index < names.Length; index++)
            {
                object value;
                if (dictionary.TryGetValue(names[index], out value) && value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static string GetTruthyString(IDictionary<string, object> dictionary, params string[] names)
        {
            for (int index = 0; index < names.Length; index++)
            {
                object value = GetFirst(dictionary, names[index]);
                string text = value as string;
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            return null;
        }

        private static bool StringEquals(object value, string expected)
        {
            return string.Equals(value as string, expected, StringComparison.Ordinal);
        }

        private static double? AsFiniteNumber(object value)
        {
            if (value == null || value is string || value is bool || value is char)
            {
                return null;
            }

            try
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
            }
            catch
            {
                return null;
            }
        }

        private static long AsNonNegativeTokenCount(object value)
        {
            double number;
            if (value == null)
            {
                return 0;
            }

            if (value is bool)
            {
                number = (bool)value ? 1 : 0;
            }
            else
            {
                string text = value as string;
                if (text != null && text.Trim().Length == 0)
                {
                    number = 0;
                }
                else if (!double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number))
                {
                    return 0;
                }
            }

            if (double.IsNaN(number) || number <= 0)
            {
                return 0;
            }
            if (double.IsPositiveInfinity(number) || number >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)number;
        }

        private static long? NormalizeEpoch(object value)
        {
            double? number = AsFiniteNumber(value);
            if (!number.HasValue)
            {
                return null;
            }

            double milliseconds = number.Value < 10000000000d ? number.Value * 1000d : number.Value;
            if (milliseconds >= long.MaxValue)
            {
                return long.MaxValue;
            }
            if (milliseconds <= long.MinValue)
            {
                return long.MinValue;
            }
            return (long)milliseconds;
        }

        private static long? AddSeconds(long milliseconds, double? seconds)
        {
            if (!seconds.HasValue)
            {
                return null;
            }

            double result = milliseconds + seconds.Value * 1000d;
            if (result >= long.MaxValue)
            {
                return long.MaxValue;
            }
            if (result <= long.MinValue)
            {
                return long.MinValue;
            }
            return (long)result;
        }

        private static string ChooseLabel(string id, double? seconds)
        {
            string safeId = id ?? string.Empty;
            string lowered = safeId.ToLowerInvariant();
            if (seconds == 18000)
            {
                return "5h";
            }
            if (seconds == 604800)
            {
                return "7d";
            }
            if (lowered.IndexOf("primary", StringComparison.Ordinal) >= 0)
            {
                return "5h";
            }
            if (lowered.IndexOf("secondary", StringComparison.Ordinal) >= 0)
            {
                return "7d";
            }
            if (seconds.HasValue && seconds.Value != 0 && seconds.Value % 86400 == 0)
            {
                return FormatCompactNumber(seconds.Value / 86400) + "d";
            }
            if (seconds.HasValue && seconds.Value != 0 && seconds.Value % 3600 == 0)
            {
                return FormatCompactNumber(seconds.Value / 3600) + "h";
            }
            return safeId;
        }

        private static string FormatCompactNumber(double value)
        {
            return value.ToString("0.################", CultureInfo.InvariantCulture);
        }

        private static double ClampPercentage(double value)
        {
            return Math.Min(100, Math.Max(0, value));
        }

        private static int CompareRateLimitWindows(RateLimitWindow left, RateLimitWindow right)
        {
            double leftNumber;
            double rightNumber;
            bool hasLeftNumber = TryReadLeadingNumber(left.Label, out leftNumber);
            bool hasRightNumber = TryReadLeadingNumber(right.Label, out rightNumber);
            if (hasLeftNumber && hasRightNumber)
            {
                int numeric = leftNumber.CompareTo(rightNumber);
                if (numeric != 0)
                {
                    return numeric;
                }
            }
            return StringComparer.CurrentCulture.Compare(left.Label, right.Label);
        }

        private static bool TryReadLeadingNumber(string value, out double number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            int length = 0;
            while (length < value.Length && (char.IsDigit(value[length]) || value[length] == '.'))
            {
                length++;
            }
            return length != 0 && double.TryParse(
                value.Substring(0, length), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static bool TryGetEventTimestamp(IDictionary<string, object> eventObject, out long milliseconds)
        {
            DateTimeOffset ignored;
            return TryGetEventTimestamp(eventObject, out milliseconds, out ignored);
        }

        private static bool TryGetEventTimestamp(
            IDictionary<string, object> eventObject,
            out long milliseconds,
            out DateTimeOffset timestamp)
        {
            milliseconds = 0;
            timestamp = default(DateTimeOffset);
            object raw = GetFirst(eventObject, "timestamp", "time", "created_at");
            string text = raw as string;
            if (string.IsNullOrEmpty(text) || !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out timestamp))
            {
                return false;
            }

            milliseconds = ToUnixMilliseconds(timestamp);
            return true;
        }

        private static bool IsSameLocalDay(DateTimeOffset timestamp, DateTimeOffset reference)
        {
            DateTime left = TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).Date;
            DateTime right = TimeZoneInfo.ConvertTime(reference, TimeZoneInfo.Local).Date;
            return left == right;
        }

        private static Price PriceFor(string model)
        {
            ModelPrice custom;
            if (PricingCatalog.TryGet(model, out custom)) return new Price(custom.Input, custom.Cached, custom.Output);
            string name = (model ?? string.Empty).ToLowerInvariant();
            // OpenAI's Codex rate card identifies auto review as GPT-5.4.
            if (name == "codex-auto-review") return new Price(2.5m, 0.25m, 15m);
            if (name == "gpt-6-astra" || name.StartsWith("gpt-6-astra-", StringComparison.Ordinal)) return new Price(10m, 1m, 50m);
            if (name.Contains("-pro") || name.Contains("spark") || name.Contains("-nano")) return new Price(0m, 0m, 0m);
            if (name.IndexOf("gpt-5.6-sol", StringComparison.Ordinal) >= 0)
            {
                return new Price(4m, 0.4m, 20m);
            }
            if (name.IndexOf("gpt-5.6-terra", StringComparison.Ordinal) >= 0)
            {
                return new Price(2m, 0.2m, 12m);
            }
            if (name.IndexOf("gpt-5.6-luna", StringComparison.Ordinal) >= 0)
            {
                return new Price(0.2m, 0.02m, 1.2m);
            }
            if (name.IndexOf("gpt-5.5", StringComparison.Ordinal) >= 0)
            {
                return new Price(5m, 0.5m, 30m);
            }
            if (name.IndexOf("gpt-5.4-mini", StringComparison.Ordinal) >= 0)
            {
                return new Price(0.75m, 0.075m, 4.5m);
            }
            if (name.IndexOf("gpt-5.4", StringComparison.Ordinal) >= 0)
            {
                return new Price(2.5m, 0.25m, 15m);
            }
            if (name.IndexOf("gpt-5.3-codex", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("gpt-5.2", StringComparison.Ordinal) >= 0)
            {
                return new Price(1.75m, 0.175m, 14m);
            }
            if (name.IndexOf("gpt-5-mini", StringComparison.Ordinal) >= 0)
            {
                return new Price(0.25m, 0.025m, 2m);
            }
            if (name == "gpt-5" || name == "gpt-5-codex" || name == "gpt-5.1" || name.StartsWith("gpt-5.1-codex", StringComparison.Ordinal))
                return new Price(1.25m, 0.125m, 10m);
            return new Price(0m, 0m, 0m);
        }

        private static void AddUsage(TodayUsage total, TodayUsage part)
        {
            total.Input = SafeAdd(total.Input, part.Input);
            total.CachedInput = SafeAdd(total.CachedInput, part.CachedInput);
            total.Output = SafeAdd(total.Output, part.Output);
            total.TotalTokens = SafeAdd(total.Input, total.Output);
            total.UnpricedTokens = SafeAdd(total.UnpricedTokens, part.UnpricedTokens);
            total.CachedInputCost += part.CachedInputCost;
            total.UncachedInputCost += part.UncachedInputCost;
            total.OutputCost += part.OutputCost;
            total.EstimatedCost += part.EstimatedCost;
        }

        private static decimal RoundCost(decimal value)
        {
            return Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        private static bool IsTimeout(Exception exception)
        {
            return exception is TaskCanceledException || exception is OperationCanceledException || exception is TimeoutException;
        }

        private static long SafeAdd(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right)
            {
                return long.MaxValue;
            }
            if (right < 0 && left < long.MinValue - right)
            {
                return long.MinValue;
            }
            return left + right;
        }

        private static long UtcNowMilliseconds()
        {
            return ToUnixMilliseconds(DateTimeOffset.UtcNow);
        }

        private static long ToUnixMilliseconds(DateTimeOffset value)
        {
            return (value.ToUniversalTime().Ticks - UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
        }

        public static string AccountIdentity(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return null;
            using (SHA256 hash = SHA256.Create())
                return "sha256:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes("CodexTrayStatus/account/v1:" + accountId))).Replace("-", "").ToLowerInvariant();
        }

        private sealed class Credentials
        {
            public string AccessToken { get; set; }
            public string AccountId { get; set; }
        }

        private sealed class FileEntry
        {
            public string Path { get; set; }
            public long ModifiedAtMilliseconds { get; set; }
        }

        private sealed class LocalParseResult
        {
            public bool TrustedTime { get; set; }
            public List<RateLimitWindow> Limits { get; set; }
            public long ObservedAt { get; set; }
        }

        private struct Price
        {
            public Price(decimal input, decimal cached, decimal output)
            {
                Input = input;
                Cached = cached;
                Output = output;
            }

            public decimal Input;
            public decimal Cached;
            public decimal Output;
        }
    }
}
