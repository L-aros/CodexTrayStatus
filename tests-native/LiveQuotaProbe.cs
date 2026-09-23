using System;
using CodexTrayStatus;

internal static class LiveQuotaProbe
{
    private static int Main()
    {
        AppSnapshot snapshot = new QuotaService().RefreshAsync().GetAwaiter().GetResult();
        Console.WriteLine("source=" + (snapshot.Quota == null ? "none" : snapshot.Quota.Source));
        if (snapshot.Quota != null && snapshot.Quota.Limits != null)
        {
            foreach (RateLimitWindow window in snapshot.Quota.Limits)
            {
                Console.WriteLine(window.Label + "=" +
                    (window.RemainingPercent.HasValue ? Math.Round(window.RemainingPercent.Value) + "%" : "--"));
            }
        }
        Console.WriteLine("today=" + snapshot.Today.TotalTokens);
        if (!string.IsNullOrEmpty(snapshot.Error)) Console.WriteLine("error=" + snapshot.Error);
        return snapshot.Quota != null && snapshot.Quota.Limits.Count > 0 ? 0 : 1;
    }
}
