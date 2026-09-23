using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTrayStatus
{
    internal static class ResetAnnouncementsSmoke
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            internal int Calls;
            internal bool Fail;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                if (Fail) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
                string json = request.RequestUri.AbsolutePath.EndsWith("/status")
                    ? "{\"data\":{\"latest_reset\":{\"id\":\"latest\",\"reset_type\":\"regular\"},\"scheduled_reset\":null,\"stats\":{\"total\":2}}}"
                    : "{\"data\":[{\"id\":\"latest\"},{\"id\":\"older\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static int Main()
        {
            FakeHandler handler = new FakeHandler();
            ResetAnnouncementsService service = new ResetAnnouncementsService(new HttpClient(handler));
            ResetAnnouncementsResult result = service.GetAsync().GetAwaiter().GetResult();
            Check(result.Available && !result.Stale && result.FetchedAt > 0, "Status and history must load");
            Check(((Dictionary<string, object>)result.Status).ContainsKey("latest_reset"), "Status payload missing");
            Check(((object[])result.Recent).Length == 2, "Recent announcements missing");
            Check(service.GetAsync().GetAwaiter().GetResult() == result && handler.Calls == 2, "Fresh result must be cached");
            FakeHandler failing = new FakeHandler { Fail = true };
            Check(!new ResetAnnouncementsService(new HttpClient(failing)).GetAsync().GetAwaiter().GetResult().Available, "API failure must not break dashboard");
            Console.WriteLine("Public reset announcement service tests passed.");
            return 0;
        }
    }
}

