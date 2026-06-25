using System.Text.RegularExpressions;
using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class GitHubUsageForecastService(AppDbContext db, GitHubCodespaceService codespaces, IClock clock)
{
    private const string TimeZoneId = "Asia/Tehran";
    private const int ForecastDays = 30;
    private const decimal DefaultMachineCoreCount = 2;
    private static readonly Regex CoreCountPattern = new(@"(?<cores>\d+)\s*(?:-| )?\s*(?:core|cores|cpu|cpus|vcpu|vcpus)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<GitHubUsageForecastResponse> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var warnings = new List<string>();
        var accounts = await db.GitHubAccounts
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return new GitHubUsageForecastResponse(
                now,
                NextResetAt(now.Year, now.Month),
                DaysUntil(NextResetAt(now.Year, now.Month), now),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                null,
                "NoAccounts",
                "Add GitHub accounts to estimate Codespaces quota runway.",
                0,
                0,
                0,
                0,
                DefaultMachineCoreCount,
                []);
        }

        decimal totalComputeUsed = 0;
        decimal totalComputeLimit = 0;
        decimal totalComputeRemaining = 0;
        var includedAccountCount = 0;
        var unavailableAccountCount = 0;
        var usableAccountCount = 0;
        var limitedAccountCount = 0;
        DateTimeOffset? resetAt = null;

        foreach (var account in accounts)
        {
            GitHubUsageResponse usage;
            try
            {
                usage = await codespaces.GetUsageAsync(account.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unavailableAccountCount++;
                warnings.Add($"Could not read usage for {account.Username}: {ex.Message}");
                continue;
            }

            if (usage.ResetAt is not null)
            {
                resetAt = resetAt is null || usage.ResetAt.Value < resetAt.Value
                    ? usage.ResetAt.Value
                    : resetAt;
            }

            if (usage.State == GitHubAccountQuotaState.Unavailable)
            {
                unavailableAccountCount++;
                warnings.Add($"Codespaces billing usage is unavailable for {account.Username}.");
                continue;
            }

            var compute = usage.Quotas.FirstOrDefault(x => x.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase));
            if (compute is null)
            {
                warnings.Add($"No compute usage was returned for {account.Username}.");
                continue;
            }

            if (compute.Limit is null || compute.Remaining is null)
            {
                warnings.Add($"Account {account.Username} has no known Free/Pro compute limit. Set its plan to include it in the aggregate forecast.");
                continue;
            }

            includedAccountCount++;
            totalComputeUsed += compute.Used;
            totalComputeLimit += compute.Limit.Value;
            totalComputeRemaining += compute.Remaining.Value;
            if (compute.Remaining.Value > 0)
            {
                usableAccountCount++;
            }
            else
            {
                limitedAccountCount++;
            }
        }

        if (resetAt is null)
        {
            resetAt = NextResetAt(now.Year, now.Month);
            warnings.Add("GitHub usage did not return a billing period reset; using the next UTC month boundary.");
        }

        var dailyCompute = await GetDailyComputeUsageAsync(now, cancellationToken);
        var average7 = AverageLast(dailyCompute, 7);
        var average14 = AverageLast(dailyCompute, 14);
        var average30 = AverageLast(dailyCompute, 30);
        var estimatedDailyComputeUsage = EstimateDailyComputeUsage(dailyCompute);
        var daysUntilReset = DaysUntil(resetAt.Value, now);
        decimal? estimatedQuotaDays = estimatedDailyComputeUsage <= 0
            ? null
            : totalComputeRemaining / estimatedDailyComputeUsage;
        int? estimatedUsableDays = estimatedQuotaDays is null
            ? null
            : Math.Min(daysUntilReset, Math.Max(0, (int)Math.Floor(estimatedQuotaDays.Value)));
        var status = ResolveStatus(includedAccountCount, totalComputeRemaining, estimatedDailyComputeUsage, estimatedUsableDays, daysUntilReset);

        return new GitHubUsageForecastResponse(
            now,
            resetAt,
            daysUntilReset,
            Round(totalComputeUsed),
            Round(totalComputeLimit),
            Round(totalComputeRemaining),
            Round(average7),
            Round(average14),
            Round(average30),
            Round(estimatedDailyComputeUsage),
            estimatedQuotaDays is null ? null : Round(estimatedQuotaDays.Value),
            estimatedUsableDays,
            status,
            BuildMessage(status, estimatedUsableDays, daysUntilReset, usableAccountCount),
            includedAccountCount,
            unavailableAccountCount,
            usableAccountCount,
            limitedAccountCount,
            DefaultMachineCoreCount,
            warnings);
    }

    internal static decimal EstimateDailyComputeUsage(IReadOnlyList<decimal> dailyCompute)
    {
        if (dailyCompute.Count == 0 || dailyCompute.All(x => x <= 0))
        {
            return 0;
        }

        var average7 = AverageLast(dailyCompute, 7);
        var average14 = AverageLast(dailyCompute, 14);
        var average30 = AverageLast(dailyCompute, 30);
        var stdDev14 = StandardDeviationLast(dailyCompute, 14);
        return Math.Max(0, average7 * 0.5m + average14 * 0.3m + average30 * 0.2m + stdDev14 * 0.2m);
    }

    internal static decimal GetMachineCoreCount(string? machineDisplayName)
    {
        if (string.IsNullOrWhiteSpace(machineDisplayName))
        {
            return DefaultMachineCoreCount;
        }

        var match = CoreCountPattern.Match(machineDisplayName);
        return match.Success && decimal.TryParse(match.Groups["cores"].Value, out var cores) && cores > 0
            ? cores
            : DefaultMachineCoreCount;
    }

    internal static DateTimeOffset NextResetAt(int year, int month)
    {
        var resetYear = month == 12 ? year + 1 : year;
        var resetMonth = month == 12 ? 1 : month + 1;
        return new DateTimeOffset(resetYear, resetMonth, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private async Task<IReadOnlyList<decimal>> GetDailyComputeUsageAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var timeZone = ResolveTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var startLocal = localNow.DateTime.AddDays(-ForecastDays);
        var buckets = Enumerable.Range(0, ForecastDays)
            .Select(index =>
            {
                var start = ConvertLocalToUtc(startLocal.AddDays(index), timeZone);
                var end = index == ForecastDays - 1
                    ? now
                    : ConvertLocalToUtc(startLocal.AddDays(index + 1), timeZone);
                return new UsageBucket(start, end);
            })
            .ToList();
        var rangeStart = buckets[0].Start;
        var rangeEnd = now;
        var machineByCodespace = await db.CodespaceSnapshots
            .AsNoTracking()
            .ToDictionaryAsync(x => MachineKey(x.AccountId, x.Name), x => x.MachineDisplayName, cancellationToken);
        var sessions = await db.LocalProxySessions
            .AsNoTracking()
            .Where(x => x.AccountId != null && x.CodespaceName != null && x.CodespaceName != "")
            .ToListAsync(cancellationToken);
        var daily = Enumerable.Repeat(0m, ForecastDays).ToArray();

        foreach (var session in sessions)
        {
            var end = ResolveSessionEnd(session, now);
            if (end <= rangeStart || session.StartedAt >= rangeEnd || session.AccountId is null || string.IsNullOrWhiteSpace(session.CodespaceName))
            {
                continue;
            }

            var cores = machineByCodespace.TryGetValue(MachineKey(session.AccountId.Value, session.CodespaceName), out var machine)
                ? GetMachineCoreCount(machine)
                : DefaultMachineCoreCount;
            for (var index = 0; index < buckets.Count; index++)
            {
                var overlapSeconds = OverlapSeconds(session.StartedAt, end, buckets[index].Start, buckets[index].End);
                if (overlapSeconds > 0)
                {
                    daily[index] += (decimal)overlapSeconds / 3600m * cores;
                }
            }
        }

        return daily;
    }

    private static DateTimeOffset ResolveSessionEnd(LocalProxySession session, DateTimeOffset now)
    {
        if (session.StoppedAt is not null)
        {
            return session.StoppedAt.Value;
        }

        return session.Status switch
        {
            LocalProxySessionStatus.Starting or LocalProxySessionStatus.Running or LocalProxySessionStatus.Stopping => now,
            _ => session.LastActivityAt > session.StartedAt ? session.LastActivityAt : session.StartedAt
        };
    }

    private static string ResolveStatus(int includedAccountCount, decimal totalComputeRemaining, decimal estimatedDailyComputeUsage, int? estimatedUsableDays, int daysUntilReset)
    {
        if (includedAccountCount == 0)
        {
            return "Unavailable";
        }

        if (totalComputeRemaining <= 0)
        {
            return "Limited";
        }

        if (estimatedDailyComputeUsage <= 0)
        {
            return "NoUsageHistory";
        }

        return estimatedUsableDays >= daysUntilReset ? "Healthy" : "Warning";
    }

    private static string BuildMessage(string status, int? estimatedUsableDays, int daysUntilReset, int usableAccountCount) =>
        status switch
        {
            "NoAccounts" => "Add GitHub accounts to estimate Codespaces quota runway.",
            "Unavailable" => "No accounts with known compute quota are available for forecasting.",
            "Limited" => "Aggregate Codespaces compute quota is exhausted.",
            "NoUsageHistory" => "No recent app-managed Codespaces usage was found; remaining days cannot be estimated yet.",
            "Healthy" => $"Estimated {estimatedUsableDays} of {daysUntilReset} day(s) until reset across {usableAccountCount} usable account(s).",
            _ => $"Estimated {estimatedUsableDays} of {daysUntilReset} day(s) until reset across {usableAccountCount} usable account(s)."
        };

    private static decimal AverageLast(IReadOnlyList<decimal> values, int days)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var slice = values.Skip(Math.Max(0, values.Count - days)).ToList();
        return slice.Count == 0 ? 0 : slice.Sum() / slice.Count;
    }

    private static decimal StandardDeviationLast(IReadOnlyList<decimal> values, int days)
    {
        var slice = values.Skip(Math.Max(0, values.Count - days)).ToList();
        if (slice.Count == 0)
        {
            return 0;
        }

        var average = slice.Sum() / slice.Count;
        var variance = slice.Sum(value => Math.Pow((double)(value - average), 2)) / slice.Count;
        return (decimal)Math.Sqrt(variance);
    }

    private static int DaysUntil(DateTimeOffset resetAt, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Floor((resetAt - now).TotalDays));

    private static double OverlapSeconds(DateTimeOffset firstStart, DateTimeOffset firstEnd, DateTimeOffset secondStart, DateTimeOffset secondEnd)
    {
        var start = firstStart >= secondStart ? firstStart : secondStart;
        var end = firstEnd <= secondEnd ? firstEnd : secondEnd;
        return end <= start ? 0 : (end - start).TotalSeconds;
    }

    private static string MachineKey(Guid accountId, string codespaceName) =>
        $"{accountId:N}:{codespaceName.Trim().ToLowerInvariant()}";

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset ConvertLocalToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);
    }

    private sealed record UsageBucket(DateTimeOffset Start, DateTimeOffset End);
}
