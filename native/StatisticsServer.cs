using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    // Read-only loopback server. No authentication files or session text are exposed.
    internal sealed class StatisticsServer : IDisposable
    {
        private TcpListener listener;
        private readonly string secret = Guid.NewGuid().ToString("N");
        private volatile string json = "{\"Daily\":null,\"RefreshedAt\":0}";
        private volatile bool disposed;
        internal string Url { get; private set; }
        internal Func<WebPreferences, string> SavePreferences;
        internal Action RefreshRequested;
        internal Func<string> RebuildReminders;
        internal Func<string, string, QuotaHistoryResult> QuotaHistoryRequested;
        internal Func<Dictionary<string, ModelPrice>, string> SavePricing;
        internal Func<Task<ResetAnnouncementsResult>> ResetAnnouncementsRequested;
        private volatile ReminderStatus reminderStatus;
        internal void PublishReminderStatus(ReminderStatus value) { reminderStatus = value; }
        private volatile string preferences = "{}";
        private long preferenceRevision;
        private volatile AppSnapshot taskbarSnapshot;
        internal void PublishPreferences(WebPreferences value)
        {
            value.TaskbarDisplay = TaskbarDisplayPreferences.Merge(null, value.TaskbarDisplay);
            value.Revision = Interlocked.Increment(ref preferenceRevision);
            preferences = new JavaScriptSerializer().Serialize(value);
        }

        internal void Publish(AppSnapshot snapshot)
        {
            if (snapshot == null) snapshot = new AppSnapshot();
            taskbarSnapshot = snapshot;
            long now = DataFreshness.Now;
            System.Collections.Generic.List<ValidityResult> validity = new System.Collections.Generic.List<ValidityResult>();
            if (snapshot.Quota != null && snapshot.Quota.Limits != null) foreach (RateLimitWindow window in snapshot.Quota.Limits) validity.Add(DataFreshness.EvaluateQuota(window, snapshot.QuotaState, now));
            json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new {
                RefreshState = snapshot.RefreshState, QuotaState = snapshot.QuotaState, UsageState = snapshot.UsageState,
                ReminderStatus = reminderStatus,
                QuotaValidity = validity, UsageValidity = DataFreshness.EvaluateUsage(snapshot.UsageState, snapshot.Daily != null, now), EvaluatedAt = now,
                Daily = snapshot.Daily, RefreshedAt = snapshot.RefreshedAt, Error = snapshot.Error ?? (snapshot.Quota == null ? null : snapshot.Quota.Warning),
                Quota = snapshot.Quota == null ? null : snapshot.Quota.Limits, Today = snapshot.UsageState != null && snapshot.UsageState.CoverageEndDate != DateTime.Today.ToString("yyyy-MM-dd") ? null : snapshot.Today,
                LocalDate = DateTime.Today.ToString("yyyy-MM-dd"), PricingDate = "2026-09-06"
            });
        }

        internal void Start()
        {
            if (listener != null) return;
            for (int port = 47831; port < 47841; port++)
            {
                TcpListener candidate = new TcpListener(IPAddress.Loopback, port);
                try { candidate.Start(); listener = candidate; break; }
                catch (SocketException) { candidate.Stop(); }
            }
            if (listener == null) throw new IOException("本地统计端口不可用，请稍后重试。");
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/" + secret + "/";
            Task.Run(async delegate {
                while (!disposed)
                {
                    try
                    {
                        TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { if (disposed) break; }
                }
            });
        }

        internal void Open(string page)
        {
            Start();
            Process.Start(new ProcessStartInfo(Url + "#" + page) { UseShellExecute = true });
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        // A strict, bounded header parser keeps this small server read-only.
                        StringBuilder header = new StringBuilder();
                        while (header.Length < 8192)
                        {
                            int value = stream.ReadByte();
                            if (value < 0) return;
                            header.Append((char)value);
                            if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                        }
                        string[] lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                        string[] request = lines[0].Split(' ');
                        string host = "", requestOrigin = "";
                        int length = 0;
                        foreach (string line in lines) if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line.Substring(5).Trim();
                        foreach (string line in lines)
                        {
                            if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) requestOrigin = line.Substring(7).Trim();
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out length);
                        }
                        Uri origin = new Uri(Url);
                        if (request.Length != 3 || (request[0] != "GET" && request[0] != "POST") || host != origin.Authority || !request[1].StartsWith(origin.AbsolutePath, StringComparison.Ordinal))
                        { Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
                        string path = request[1].Substring(origin.AbsolutePath.Length);
                        string query = "";
                        int queryAt = path.IndexOf('?');
                        if (queryAt >= 0) { query = path.Substring(queryAt + 1); path = path.Substring(0, queryAt); }
                        if (request[0] == "POST")
                        {
                            if (requestOrigin != origin.GetLeftPart(UriPartial.Authority) || length < 0 || length > 4096)
                            { Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("Forbidden")); return; }
                            byte[] body = new byte[length];
                            for (int offset = 0; offset < length;)
                            {
                                int count = stream.Read(body, offset, length - offset);
                                if (count == 0) return; offset += count;
                            }
                            try
                            {
                                if (path == "api/preferences" && SavePreferences != null)
                                {
                                    WebPreferences value = WebPreferencePatch.Parse(Encoding.UTF8.GetString(body));
                                    // Validate against the current settings, but leave omitted fields
                                    // untouched for the UI-thread merge (concurrent requests).
                                    WebPreferencePatch.Merge(new JavaScriptSerializer().Deserialize<WebPreferences>(preferences), value);
                                    string error = SavePreferences(value);
                                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                                    Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(preferences)); return;
                                }
                                if (path == "api/taskbar-preview")
                                {
                                    WebPreferences current = new JavaScriptSerializer().Deserialize<WebPreferences>(preferences);
                                    WebPreferences draft = WebPreferencePatch.Merge(current, WebPreferencePatch.Parse(Encoding.UTF8.GetString(body)));
                                    draft.Revision = current.Revision;
                                    RespondTaskbar(stream, draft); return;
                                }
                                if (path == "api/refresh" && RefreshRequested != null)
                                { RefreshRequested(); Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes("{}")); return; }
                                if (path == "api/reminders/rebuild" && RebuildReminders != null)
                                {
                                    string error = RebuildReminders();
                                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                                    Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes("{}")); return;
                                }
                                if (path == "api/pricing" && SavePricing != null)
                                {
                                    Dictionary<string, ModelPrice> value = new JavaScriptSerializer().Deserialize<Dictionary<string, ModelPrice>>(Encoding.UTF8.GetString(body));
                                    string error = SavePricing(value);
                                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                                    Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(PricingCatalog.Snapshot()))); return;
                                }
                            }
                            catch (Exception error)
                            { Respond(stream, 400, "text/plain", Encoding.UTF8.GetBytes(error.Message)); return; }
                            Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return;
                        }
                        if (path == "api/preferences") { Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(preferences)); return; }
                        if (path == "api/taskbar-preview") { RespondTaskbar(stream, new JavaScriptSerializer().Deserialize<WebPreferences>(preferences)); return; }
                        if (path == "api/usage") { Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(json)); return; }
                        if (path == "api/pricing") { Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(PricingCatalog.Snapshot()))); return; }
                        if (path == "api/reset-announcements" && ResetAnnouncementsRequested != null)
                        {
                            ResetAnnouncementsResult announcements = ResetAnnouncementsRequested().GetAwaiter().GetResult();
                            Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(announcements))); return;
                        }
                        if (path == "api/quota-history" && QuotaHistoryRequested != null)
                        {
                            string range = QueryValue(query, "range");
                            // Account hashes are an internal isolation key and never a web API field.
                            string account = taskbarSnapshot == null || taskbarSnapshot.QuotaState == null ? null : taskbarSnapshot.QuotaState.AccountKey;
                            QuotaHistoryResult result = QuotaHistoryRequested(range, account);
                            Respond(stream, result.ErrorCode == "invalid_range" ? 400 : 200, "application/json", Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new {
                                Range = result.Range, From = result.From, To = result.To, Available = result.Available, ErrorCode = result.ErrorCode,
                                Points = result.Points.ConvertAll(delegate(QuotaHistoryPoint point) { return new {
                                    WindowKey = point.WindowKey, CycleEnd = point.CycleEnd, ObservedAt = point.ObservedAt,
                                    ObservationId = point.ObservationId, UsedPercent = point.UsedPercent,
                                    RemainingPercent = point.RemainingPercent, Source = point.Source
                                }; })
                            }))); return;
                        }
                        string resource = path == "" ? "index.html" : path;
                        if (resource != "index.html" && resource != "app.js" && resource != "style.css")
                        { Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
                        using (Stream asset = Assembly.GetExecutingAssembly().GetManifestResourceStream("Statistics." + resource))
                        {
                            if (asset == null) { Respond(stream, 500, "text/plain", Encoding.UTF8.GetBytes("Missing embedded asset")); return; }
                            using (MemoryStream buffer = new MemoryStream())
                            {
                                asset.CopyTo(buffer);
                                Respond(stream, 200, resource.EndsWith(".js") ? "application/javascript" : resource.EndsWith(".css") ? "text/css" : "text/html", buffer.ToArray());
                            }
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void RespondTaskbar(NetworkStream stream, WebPreferences value)
        {
            TaskbarPresentation view = TaskbarPresenter.Build(taskbarSnapshot, value, DataFreshness.Now);
            Respond(stream, 200, "application/json", Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(view)));
        }

        private static string QueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string part in query.Split('&'))
            {
                int equals = part.IndexOf('=');
                string key = equals < 0 ? part : part.Substring(0, equals);
                if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal)) continue;
                string value = equals < 0 ? "" : part.Substring(equals + 1);
                try { value = Uri.UnescapeDataString(value); }
                catch { return null; }
                return value.Length > 256 ? null : value;
            }
            return null;
        }

        private static void Respond(Stream stream, int status, string type, byte[] body)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") +
                "\r\nContent-Type: " + type + "; charset=utf-8\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer" +
                "\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'" +
                "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length);
        }

        public void Dispose() { disposed = true; if (listener != null) listener.Stop(); }
    }
}
