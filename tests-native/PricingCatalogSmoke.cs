using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodexTrayStatus
{
    internal static class PricingCatalogSmoke
    {
        private static int checks;
        private static void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new Exception(message);
        }

        private static ModelPrice Price(decimal input, decimal cached, decimal output)
        {
            return new ModelPrice { Input = input, Cached = cached, Output = output };
        }

        private static bool EqualsPrice(ModelPrice value, decimal input, decimal cached, decimal output)
        {
            return value != null && value.Input == input && value.Cached == cached && value.Output == output;
        }

        private static int Main()
        {
            PricingCatalog.Replace(null);
            PricingCatalog.ReplaceRemote(null);
            ModelPrice fallback = Price(2m, 0.2m, 12m);
            Check(EqualsPrice(PricingCatalog.ApplyRemote("model", fallback), 2m, 0.2m, 12m), "No remote row keeps built-in pricing.");
            Check(PricingCatalog.ApplyRemote("unknown", null) == null, "Unknown model remains unpriced.");

            ModelPrice manual = Price(7m, 0.7m, 21m);
            PricingCatalog.Set("model", manual);
            manual.Input = 99m;
            ModelPrice read;
            Check(PricingCatalog.TryGet("MODEL", out read) && read.Input == 7m, "Manual lookup is case insensitive and copies input.");
            read.Input = 99m;
            PricingCatalog.TryGet("model", out read);
            Check(read.Input == 7m, "Manual lookup does not expose mutable internal values.");
            Dictionary<string, ModelPrice> snapshot = PricingCatalog.Snapshot();
            snapshot["model"].Input = 88m;
            snapshot.Clear();
            PricingCatalog.TryGet("model", out read);
            Check(read.Input == 7m, "Manual snapshot is a deep copy.");

            Dictionary<string, RemoteModelPrice> remote = new Dictionary<string, RemoteModelPrice>
            {
                { "model", new RemoteModelPrice { Input = 3m, Cached = 0.3m, Output = 15m } },
                { "input-only", new RemoteModelPrice { Input = 4m } },
                { "cached-only", new RemoteModelPrice { Cached = 0m } },
                { "output-only", new RemoteModelPrice { Output = 16m } },
                { "free", new RemoteModelPrice { Input = 0m, Cached = 0m, Output = 0m } },
                { "empty", new RemoteModelPrice() },
                { "null", null },
                { "swapped", new RemoteModelPrice { Input = 2m, Cached = 10m, Output = 0.2m } },
                { "negative", new RemoteModelPrice { Input = -1m } },
                { "too-high", new RemoteModelPrice { Output = 100001m } }
            };
            Check(PricingCatalog.ReplaceRemote(remote) == 5, "Remote publication excludes empty, null, swapped, negative, and excessive rates.");
            remote["model"].Input = 100m;
            Check(EqualsPrice(PricingCatalog.ApplyRemote("MODEL", fallback), 3m, 0.3m, 15m), "Remote rows are copied and matched without case sensitivity.");
            Check(PricingCatalog.TryGet("model", out read) && read.Input == 7m, "Remote publication preserves manual overrides.");
            Check(PricingCatalog.Snapshot().Count == 1, "Settings snapshot contains manual rates only.");
            Check(!PricingCatalog.TryGet("free", out read), "Manual lookup never leaks downloaded prices.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("input-only", fallback), 4m, 0.2m, 12m), "Missing cached/output rates fall back independently.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("cached-only", fallback), 2m, 0m, 12m), "An explicit zero cached rate stays zero.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("output-only", fallback), 2m, 0.2m, 16m), "Missing input/cached rates fall back independently.");
            Check(PricingCatalog.ApplyRemote("input-only", null) == null, "Partial remote rows cannot price unknown models.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("model", null), 3m, 0.3m, 15m), "Complete remote rows can price unknown models.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("free", null), 0m, 0m, 0m), "A complete zero-price model is a valid remote record.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("swapped", fallback), 2m, 0.2m, 12m), "Suspicious cached/output values are not guessed or corrected.");
            Check(PricingCatalog.ApplyRemote("model-2026-10-31", null) == null, "Catalog does not make broad alias or prefix matches.");

            PricingCatalog.Reset("MODEL");
            Check(!PricingCatalog.TryGet("model", out read) && EqualsPrice(PricingCatalog.ApplyRemote("model", null), 3m, 0.3m, 15m), "Reset removes only the manual override.");
            PricingCatalog.Replace(new Dictionary<string, ModelPrice> { { "manual", Price(5m, 0.5m, 15m) } });
            bool rejected = false;
            try
            {
                PricingCatalog.Replace(new Dictionary<string, ModelPrice> {
                    { "valid", Price(1m, 0.1m, 3m) }, { "bad", Price(-1m, 0m, 0m) }
                });
            }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && PricingCatalog.TryGet("manual", out read) && PricingCatalog.Snapshot().Count == 1, "Invalid settings replacement leaves prior overrides intact.");

            PricingCatalog.ReplaceRemote(new Dictionary<string, RemoteModelPrice> {
                { "cached-too-high", new RemoteModelPrice { Cached = 3m, Output = 50m } },
                { "input-too-low", new RemoteModelPrice { Input = 0.1m, Output = 50m } }
            });
            Check(EqualsPrice(PricingCatalog.ApplyRemote("cached-too-high", fallback), 2m, 0.2m, 12m), "Cached-only remote rate above built-in input rejects the whole row.");
            Check(EqualsPrice(PricingCatalog.ApplyRemote("input-too-low", fallback), 2m, 0.2m, 12m), "Input-only remote rate below built-in cached rate rejects the whole row.");
            Check(PricingCatalog.ApplyRemote("model", null) == null, "A new remote snapshot removes rows absent from that snapshot.");

            Task writer = Task.Run(delegate {
                for (int i = 0; i < 1000; i++)
                {
                    decimal rate = i % 2 == 0 ? 2m : 4m;
                    PricingCatalog.ReplaceRemote(new Dictionary<string, RemoteModelPrice> {
                        { "concurrent", new RemoteModelPrice { Input = rate, Cached = rate / 10m, Output = rate * 5m } }
                    });
                    PricingCatalog.Set("concurrent", Price(rate, rate / 10m, rate * 5m));
                    PricingCatalog.Reset("concurrent");
                }
            });
            Task reader = Task.Run(delegate {
                for (int i = 0; i < 1000; i++)
                {
                    ModelPrice value = PricingCatalog.ApplyRemote("concurrent", fallback);
                    if (!(EqualsPrice(value, 2m, 0.2m, 12m) || EqualsPrice(value, 2m, 0.2m, 10m) || EqualsPrice(value, 4m, 0.4m, 20m)))
                        throw new Exception("Readers observed a partially published remote row.");
                    PricingCatalog.Snapshot();
                    ModelPrice ignored;
                    PricingCatalog.TryGet("concurrent", out ignored);
                }
            });
            Task.WaitAll(writer, reader);
            Check(true, "Concurrent reads, manual edits, and remote publication finish without partial rows or enumeration failures.");

            PricingCatalog.Replace(null);
            PricingCatalog.ReplaceRemote(null);
            Console.WriteLine("Pricing catalog tests passed: " + checks + " checks.");
            return 0;
        }
    }
}
