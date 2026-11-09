using System;
using System.Collections.Generic;

namespace CodexTrayStatus
{
    public sealed class ModelPrice
    {
        public decimal Input { get; set; }
        public decimal Cached { get; set; }
        public decimal Output { get; set; }
    }

    internal sealed class RemoteModelPrice
    {
        public decimal? Input { get; set; }
        public decimal? Cached { get; set; }
        public decimal? Output { get; set; }
    }

    internal static class PricingCatalog
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, ModelPrice> overrides = NewOverrides();
        private static Dictionary<string, RemoteModelPrice> remote = NewRemote();

        // Manual overrides remain separate from downloaded prices so that saving or
        // clearing settings never persists or removes the remote snapshot.
        internal static bool TryGet(string model, out ModelPrice price)
        {
            lock (Sync)
            {
                ModelPrice value;
                bool found = overrides.TryGetValue(model ?? string.Empty, out value);
                price = found ? Copy(value) : null;
                return found;
            }
        }

        internal static void Set(string model, ModelPrice price)
        {
            ValidateOverride(model, price);
            lock (Sync) { overrides[model] = Copy(price); }
        }

        internal static void Reset(string model)
        {
            lock (Sync) { overrides.Remove(model ?? string.Empty); }
        }

        internal static Dictionary<string, ModelPrice> Snapshot()
        {
            lock (Sync)
            {
                Dictionary<string, ModelPrice> result = NewOverrides();
                foreach (KeyValuePair<string, ModelPrice> item in overrides)
                    result[item.Key] = Copy(item.Value);
                return result;
            }
        }

        internal static void Replace(Dictionary<string, ModelPrice> values)
        {
            Dictionary<string, ModelPrice> replacement = NewOverrides();
            if (values != null)
            {
                foreach (KeyValuePair<string, ModelPrice> item in values)
                {
                    ValidateOverride(item.Key, item.Value);
                    replacement[item.Key] = Copy(item.Value);
                }
            }
            lock (Sync) { overrides = replacement; }
        }

        // Missing values stay nullable; a source that omits a rate cannot turn it
        // into a free rate. Invalid rows are excluded before the atomic publish.
        internal static int ReplaceRemote(Dictionary<string, RemoteModelPrice> values)
        {
            Dictionary<string, RemoteModelPrice> replacement = NewRemote();
            if (values != null)
            {
                foreach (KeyValuePair<string, RemoteModelPrice> item in values)
                {
                    RemoteModelPrice value = item.Value;
                    if (string.IsNullOrWhiteSpace(item.Key) || !ValidRemote(value)) continue;
                    replacement[item.Key] = Copy(value);
                }
            }
            lock (Sync) { remote = replacement; }
            return replacement.Count;
        }

        internal static ModelPrice ApplyRemote(string model, ModelPrice fallback)
        {
            RemoteModelPrice value;
            lock (Sync)
            {
                RemoteModelPrice found;
                value = remote.TryGetValue(model ?? string.Empty, out found) ? Copy(found) : null;
            }
            if (value == null) return Copy(fallback);
            if (fallback == null && (!value.Input.HasValue || !value.Cached.HasValue || !value.Output.HasValue))
                return null;

            ModelPrice merged = new ModelPrice
            {
                Input = value.Input ?? fallback.Input,
                Cached = value.Cached ?? fallback.Cached,
                Output = value.Output ?? fallback.Output
            };
            // A partial remote rate can also become inconsistent with a built-in
            // value. Keep the whole trusted fallback rather than mixing that row.
            if (merged.Cached > merged.Input) return Copy(fallback);
            return merged;
        }

        private static bool ValidRate(decimal value)
        {
            return value >= 0m && value <= 100000m;
        }

        private static bool ValidRemote(RemoteModelPrice value)
        {
            if (value == null || (!value.Input.HasValue && !value.Cached.HasValue && !value.Output.HasValue))
                return false;
            if ((value.Input.HasValue && !ValidRate(value.Input.Value)) ||
                (value.Cached.HasValue && !ValidRate(value.Cached.Value)) ||
                (value.Output.HasValue && !ValidRate(value.Output.Value))) return false;
            return !value.Input.HasValue || !value.Cached.HasValue || value.Cached.Value <= value.Input.Value;
        }

        private static void ValidateOverride(string model, ModelPrice price)
        {
            if (string.IsNullOrWhiteSpace(model) || price == null ||
                !ValidRate(price.Input) || !ValidRate(price.Cached) || !ValidRate(price.Output))
                throw new ArgumentException("invalid_price");
        }

        private static ModelPrice Copy(ModelPrice value)
        {
            return value == null ? null : new ModelPrice { Input = value.Input, Cached = value.Cached, Output = value.Output };
        }

        private static RemoteModelPrice Copy(RemoteModelPrice value)
        {
            return new RemoteModelPrice { Input = value.Input, Cached = value.Cached, Output = value.Output };
        }

        private static Dictionary<string, ModelPrice> NewOverrides()
        {
            return new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, RemoteModelPrice> NewRemote()
        {
            return new Dictionary<string, RemoteModelPrice>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
