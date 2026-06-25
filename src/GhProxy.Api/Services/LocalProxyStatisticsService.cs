using GhProxy.Api.Contracts;
using GhProxy.Api.Data;
using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Services;

public sealed class LocalProxyStatisticsService(AppDbContext db, IClock clock)
{
    private const string TimeZoneId = "Asia/Tehran";
    private static readonly HashSet<string> ErrorStartEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "codespace_proxy.start.failed_before_runtime",
        "local_proxy.start.failed",
        "local_proxy.port.unavailable",
        "local_proxy.startup_recovery.failed",
        "local_proxy.probe.http.failure",
        "local_proxy.probe.socks.failure",
        "local_proxy.xray.exited",
        "codespace_proxy.tunnel.interrupted",
        "codespace_proxy.tunnel.exited"
    };

    private static readonly HashSet<string> ErrorEndEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "local_proxy.xray.started",
        "local_proxy.stop.completed",
        "local_proxy.stop.none",
        "local_proxy.idle.timeout",
        "codespace_proxy.tunnel.reconnected",
        "session.stop.completed",
        "session.idle.timeout"
    };

    public async Task<LocalProxyStatisticsResponse> GetAsync(string? period, CancellationToken cancellationToken)
    {
        var normalizedPeriod = NormalizePeriod(period);
        var timeZone = ResolveTimeZone();
        var now = clock.UtcNow;
        var range = CreateRange(normalizedPeriod, now, timeZone);
        var accountNames = await db.GitHubAccounts
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Username, cancellationToken);

        var sessions = (await db.LocalProxySessions
            .AsNoTracking()
            .Where(x => x.AccountId != null && x.CodespaceName != null && x.CodespaceName != "")
            .ToListAsync(cancellationToken))
            .Select(session => ToInterval(session, range.RangeStart, range.RangeEnd, now, accountNames))
            .Where(interval => interval is not null)
            .Select(interval => interval!)
            .OrderBy(interval => interval.Start)
            .ToList();

        var operationalEvents = await db.OperationalEvents
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var errorIntervals = BuildErrorIntervals(operationalEvents, range.RangeStart, range.RangeEnd, now);
        var activeIntervals = SubtractIntervals(MergeIntervals(sessions), errorIntervals);
        var buckets = range.Buckets
            .Select(bucket => BuildBucket(bucket, activeIntervals, errorIntervals, sessions, timeZone, now))
            .ToList();
        var samples = (await db.CodespaceStateSamples
            .AsNoTracking()
            .Where(x => x.ObservedAt >= range.RangeStart && x.ObservedAt <= range.RangeEnd)
            .OrderByDescending(x => x.ObservedAt)
            .Take(500)
            .ToListAsync(cancellationToken))
            .Select(sample => new CodespaceStateSampleResponse(
                sample.AccountId,
                accountNames.GetValueOrDefault(sample.AccountId),
                sample.CodespaceName,
                sample.State,
                sample.ObservedAt,
                sample.Source,
                IsActiveGitHubState(sample.State)))
            .ToList();
        var mismatches = samples
            .Where(sample => sample.IsActive && !IsCoveredByAnyInterval(sample.ObservedAt, activeIntervals))
            .Select(sample => new LocalProxyStatisticsMismatchResponse(
                sample.ObservedAt,
                sample.AccountId,
                sample.AccountUsername,
                sample.CodespaceName,
                sample.State,
                "GitHub reported this Codespace active while the app-managed proxy was not active."))
            .ToList();

        var activeSeconds = SumIntervals(activeIntervals);
        var errorSeconds = SumIntervals(errorIntervals);
        var rangeSeconds = Math.Max(0, (long)Math.Round((range.RangeEnd - range.RangeStart).TotalSeconds));
        var offSeconds = Math.Max(0, rangeSeconds - activeSeconds - errorSeconds);
        var dayCount = normalizedPeriod == "24h" ? 1 : normalizedPeriod == "7d" ? 7 : 30;

        return new LocalProxyStatisticsResponse(
            normalizedPeriod,
            range.RangeStart,
            range.RangeEnd,
            TimeZoneId,
            new LocalProxyStatisticsTotalsResponse(
                activeSeconds,
                offSeconds,
                errorSeconds,
                Percent(activeSeconds, rangeSeconds),
                Percent(errorSeconds, rangeSeconds),
                sessions.Count,
                activeSeconds / (double)dayCount),
            normalizedPeriod == "24h" ? buckets : [],
            normalizedPeriod == "24h" ? [] : buckets,
            sessions.OrderByDescending(interval => interval.Start).Select(interval => interval.Session).ToList(),
            samples,
            mismatches);
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

    private static ProxyInterval? ToInterval(
        LocalProxySession session,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, string> accountNames)
    {
        var end = ResolveSessionEnd(session, now);
        if (end <= rangeStart || session.StartedAt >= rangeEnd)
        {
            return null;
        }

        var clippedStart = Max(session.StartedAt, rangeStart);
        var clippedEnd = Min(end, rangeEnd);
        if (clippedEnd <= clippedStart)
        {
            return null;
        }

        return new ProxyInterval(
            clippedStart,
            clippedEnd,
            new LocalProxyStatisticsSessionResponse(
                session.Id,
                session.AccountId,
                session.AccountId is null ? null : accountNames.GetValueOrDefault(session.AccountId.Value),
                session.CodespaceName,
                clippedStart,
                clippedEnd,
                (long)Math.Round((clippedEnd - clippedStart).TotalSeconds),
                session.Status.ToString(),
                session.LastError));
    }

    private static IReadOnlyList<Interval> MergeIntervals(IReadOnlyList<ProxyInterval> intervals) =>
        MergeIntervals(intervals.Select(interval => new Interval(interval.Start, interval.End)).ToList());

    private static IReadOnlyList<Interval> MergeIntervals(IReadOnlyList<Interval> intervals)
    {
        var merged = new List<Interval>();
        foreach (var interval in intervals.OrderBy(x => x.Start))
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End)
            {
                merged.Add(new Interval(interval.Start, interval.End));
                continue;
            }

            if (interval.End > merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = interval.End };
            }
        }

        return merged;
    }

    private static LocalProxyStatisticsBucketResponse BuildBucket(
        BucketRange bucket,
        IReadOnlyList<Interval> activeIntervals,
        IReadOnlyList<Interval> errorIntervals,
        IReadOnlyList<ProxyInterval> sessions,
        TimeZoneInfo timeZone,
        DateTimeOffset now)
    {
        var knownEnd = Min(now, bucket.End);
        var knownSeconds = Math.Max(0, (long)Math.Round((knownEnd - bucket.Start).TotalSeconds));
        var activeSeconds = (long)Math.Round(activeIntervals.Sum(interval => OverlapSeconds(interval.Start, interval.End, bucket.Start, knownEnd)));
        var errorSeconds = (long)Math.Round(errorIntervals.Sum(interval => OverlapSeconds(interval.Start, interval.End, bucket.Start, knownEnd)));
        var sessionCount = sessions.Count(session => session.Start < bucket.End && session.End > bucket.Start);
        var segments = BuildSegments(bucket, activeIntervals, errorIntervals, now);
        return new LocalProxyStatisticsBucketResponse(
            bucket.Start,
            bucket.End,
            bucket.Label ?? BuildLabel(bucket.Start, bucket.End, timeZone),
            activeSeconds,
            Math.Max(0, knownSeconds - activeSeconds - errorSeconds),
            errorSeconds,
            Percent(activeSeconds, knownSeconds),
            Percent(errorSeconds, knownSeconds),
            sessionCount,
            segments);
    }

    private static IReadOnlyList<Interval> BuildErrorIntervals(
        IReadOnlyList<OperationalEvent> operationalEvents,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset now)
    {
        var intervals = new List<Interval>();

        DateTimeOffset? openStart = null;
        foreach (var evt in operationalEvents.OrderBy(x => x.Timestamp))
        {
            if (evt.Timestamp > rangeEnd)
            {
                break;
            }

            if (IsErrorStartEvent(evt.EventType))
            {
                openStart ??= evt.Timestamp;
                continue;
            }

            if (openStart is not null && IsErrorEndEvent(evt.EventType))
            {
                AddClippedInterval(intervals, openStart.Value, evt.Timestamp, rangeStart, rangeEnd);
                openStart = null;
            }
        }

        if (openStart is not null)
        {
            AddClippedInterval(intervals, openStart.Value, Min(now, rangeEnd), rangeStart, rangeEnd);
        }

        return MergeIntervals(intervals);
    }

    private static IReadOnlyList<LocalProxyStatisticsSegmentResponse> BuildSegments(
        BucketRange bucket,
        IReadOnlyList<Interval> activeIntervals,
        IReadOnlyList<Interval> errorIntervals,
        DateTimeOffset now)
    {
        var bucketSeconds = Math.Max(0, (long)Math.Round((bucket.End - bucket.Start).TotalSeconds));
        if (bucket.End <= bucket.Start)
        {
            return [];
        }

        var knownEnd = Min(now, bucket.End);
        var boundaries = new SortedSet<DateTimeOffset> { bucket.Start, bucket.End };
        if (now > bucket.Start && now < bucket.End)
        {
            boundaries.Add(now);
        }

        AddIntervalBoundaries(boundaries, activeIntervals, bucket.Start, knownEnd);
        AddIntervalBoundaries(boundaries, errorIntervals, bucket.Start, knownEnd);

        var segments = new List<LocalProxyStatisticsSegmentResponse>();
        foreach (var pair in boundaries.Zip(boundaries.Skip(1)))
        {
            var start = pair.First;
            var end = pair.Second;
            if (end <= start)
            {
                continue;
            }

            var state = start >= now
                ? "future"
                : OverlapsAny(start, end, errorIntervals)
                    ? "error"
                    : OverlapsAny(start, end, activeIntervals)
                        ? "up"
                        : "off";
            AddSegment(segments, start, end, state, bucketSeconds);
        }

        return segments;
    }

    private static IReadOnlyList<Interval> SubtractIntervals(IReadOnlyList<Interval> source, IReadOnlyList<Interval> blockers)
    {
        if (source.Count == 0 || blockers.Count == 0)
        {
            return source;
        }

        var result = new List<Interval>();
        foreach (var interval in source)
        {
            var cursor = interval.Start;
            foreach (var blocker in blockers.Where(blocker => blocker.End > cursor && blocker.Start < interval.End))
            {
                if (blocker.Start > cursor)
                {
                    result.Add(new Interval(cursor, Min(blocker.Start, interval.End)));
                }

                if (blocker.End > cursor)
                {
                    cursor = Max(cursor, blocker.End);
                }

                if (cursor >= interval.End)
                {
                    break;
                }
            }

            if (cursor < interval.End)
            {
                result.Add(new Interval(cursor, interval.End));
            }
        }

        return MergeIntervals(result);
    }

    private static StatisticsRange CreateRange(string period, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        if (period == "24h")
        {
            var currentHourStart = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
            var startLocal = currentHourStart.AddHours(-23);
            var buckets = Enumerable.Range(0, 24)
                .Select(index =>
                {
                    var start = ConvertLocalToUtc(startLocal.AddHours(index), timeZone);
                    var end = ConvertLocalToUtc(startLocal.AddHours(index + 1), timeZone);
                    return new BucketRange(start, end, TimeZoneInfo.ConvertTime(start, timeZone).ToString("HH:mm"));
                })
                .ToList();
            return new StatisticsRange(buckets[0].Start, now, buckets);
        }

        var days = period == "7d" ? 7 : 30;
        var startDate = localNow.Date.AddDays(-(days - 1));
        var dailyBuckets = Enumerable.Range(0, days)
            .Select(index =>
            {
                var start = ConvertLocalToUtc(startDate.AddDays(index), timeZone);
                var end = ConvertLocalToUtc(startDate.AddDays(index + 1), timeZone);
                return new BucketRange(start, end, TimeZoneInfo.ConvertTime(start, timeZone).ToString("MMM dd"));
            })
            .ToList();
        return new StatisticsRange(dailyBuckets[0].Start, now, dailyBuckets);
    }

    private static string NormalizePeriod(string? period) =>
        period?.Trim().ToLowerInvariant() switch
        {
            "7d" => "7d",
            "30d" => "30d",
            _ => "24h"
        };

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

    private static string BuildLabel(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo timeZone)
    {
        var localStart = TimeZoneInfo.ConvertTime(start, timeZone);
        var localEnd = TimeZoneInfo.ConvertTime(end, timeZone);
        return $"{localStart:HH:mm}-{localEnd:HH:mm}";
    }

    private static bool IsActiveGitHubState(string state) =>
        state.Equals("Available", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("Provisioning", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorStartEvent(string eventType) =>
        ErrorStartEvents.Contains(eventType);

    private static bool IsErrorEndEvent(string eventType) =>
        ErrorEndEvents.Contains(eventType);

    private static bool IsCoveredByAnyInterval(DateTimeOffset timestamp, IReadOnlyList<Interval> intervals) =>
        intervals.Any(interval => timestamp >= interval.Start && timestamp <= interval.End);

    private static bool OverlapsAny(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<Interval> intervals) =>
        intervals.Any(interval => interval.Start < end && interval.End > start);

    private static void AddIntervalBoundaries(
        ISet<DateTimeOffset> boundaries,
        IReadOnlyList<Interval> intervals,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        if (rangeEnd <= rangeStart)
        {
            return;
        }

        foreach (var interval in intervals.Where(interval => interval.Start < rangeEnd && interval.End > rangeStart))
        {
            boundaries.Add(Max(interval.Start, rangeStart));
            boundaries.Add(Min(interval.End, rangeEnd));
        }
    }

    private static void AddSegment(
        IList<LocalProxyStatisticsSegmentResponse> segments,
        DateTimeOffset start,
        DateTimeOffset end,
        string state,
        long bucketSeconds)
    {
        if (segments.Count > 0 && segments[^1].State == state && segments[^1].End == start)
        {
            var previous = segments[^1];
            var mergedSeconds = (long)Math.Round((end - previous.Start).TotalSeconds);
            segments[^1] = previous with
            {
                End = end,
                Seconds = mergedSeconds,
                Percent = Percent(mergedSeconds, bucketSeconds)
            };
            return;
        }

        var seconds = (long)Math.Round((end - start).TotalSeconds);
        segments.Add(new LocalProxyStatisticsSegmentResponse(
            start,
            end,
            state,
            seconds,
            Percent(seconds, bucketSeconds)));
    }

    private static void AddClippedInterval(
        ICollection<Interval> intervals,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var clippedStart = Max(start, rangeStart);
        var clippedEnd = Min(end, rangeEnd);
        if (clippedEnd > clippedStart)
        {
            intervals.Add(new Interval(clippedStart, clippedEnd));
        }
    }

    private static long SumIntervals(IReadOnlyList<Interval> intervals) =>
        (long)Math.Round(intervals.Sum(interval => (interval.End - interval.Start).TotalSeconds));

    private static double OverlapSeconds(DateTimeOffset firstStart, DateTimeOffset firstEnd, DateTimeOffset secondStart, DateTimeOffset secondEnd)
    {
        var start = Max(firstStart, secondStart);
        var end = Min(firstEnd, secondEnd);
        return end <= start ? 0 : (end - start).TotalSeconds;
    }

    private static double Percent(long value, long total) =>
        total <= 0 ? 0 : Math.Round(value * 100d / total, 1);

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private sealed record StatisticsRange(DateTimeOffset RangeStart, DateTimeOffset RangeEnd, IReadOnlyList<BucketRange> Buckets);

    private sealed record BucketRange(DateTimeOffset Start, DateTimeOffset End, string? Label);

    private sealed record Interval(DateTimeOffset Start, DateTimeOffset End);

    private sealed record ProxyInterval(DateTimeOffset Start, DateTimeOffset End, LocalProxyStatisticsSessionResponse Session);
}
