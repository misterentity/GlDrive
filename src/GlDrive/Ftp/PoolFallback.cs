namespace GlDrive.Ftp;

/// <summary>
/// Borrow from a preferred pool, and take a second pool when the first cannot lend within a
/// short budget. For one-shot control commands (SITE INVITE) whose cost of waiting is far
/// higher than the cost of briefly holding another pool's connection.
///
/// 2026-10-15: superbnc's single main connection was busy behind spread scans for the whole
/// 30s invite budget 7 times in one day, leaving the announce channels unjoined for 5–65 min.
/// </summary>
internal static class PoolFallback
{
    public static async Task<T> BorrowAsync<T>(
        Func<CancellationToken, Task<T>> primary,
        Func<CancellationToken, Task<T>>? fallback,
        TimeSpan primaryBudget,
        CancellationToken ct)
    {
        if (fallback == null) return await primary(ct);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(primaryBudget);
        try
        {
            return await primary(budget.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Busy past the budget, parked in a cooldown, or disposed by a remount: none of
            // these say anything about the fallback pool.
            return await fallback(ct);
        }
    }
}
