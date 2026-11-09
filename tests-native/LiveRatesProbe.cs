using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    // Opt-in verification only; the offline smoke suite never calls public feeds.
    internal static class LiveRatesProbe
    {
        private static int Main(string[] args)
        {
            try { Run(args.Length == 0 ? null : args[0]).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }

        private static async Task Run(string output)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (HttpClient pricesClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false }))
            using (HttpClient exchangeClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false }))
            {
                pricesClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexTrayStatus");
                exchangeClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexTrayStatus");
                ModelRadarPricingService prices = new ModelRadarPricingService(pricesClient, null, delegate { return DateTimeOffset.UtcNow; });
                ExchangeRateService exchange = new ExchangeRateService(exchangeClient, null, delegate { return DateTimeOffset.UtcNow; });
                await Task.WhenAll(prices.RefreshAsync(true), exchange.RefreshAsync(true));
                if (!prices.Status.Available || !exchange.Status.Available)
                    throw new InvalidOperationException("Live feed verification failed: pricing=" + prices.Status.ErrorCode + ", exchange=" + exchange.Status.ErrorCode);
                Dictionary<string, ModelPrice> examples = new Dictionary<string, ModelPrice>();
                foreach (string id in new[] { "gpt-5.3-codex", "gpt-5.4", "gpt-6.1-sol" })
                {
                    ModelPrice price = PricingCatalog.ApplyRemote(id, null);
                    if (price == null) throw new InvalidDataException("Live source has no complete rates for " + id);
                    examples.Add(id, price);
                }
                string json = new JavaScriptSerializer().Serialize(new {
                    VerifiedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
                    PricingEndpoint = ModelRadarPricingService.Url, Pricing = prices.Status, Examples = examples,
                    ExchangeEndpoint = ExchangeRateService.Url, ExchangeRate = exchange.Status
                });
                if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, json);
                Console.WriteLine(json);
            }
        }
    }
}
