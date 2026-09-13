namespace PicoSchedule;

/// <summary>
/// A parsed cron pattern — the compiled form of the full cron dialect
/// (framework spec §5.0 language-completeness contract). IMMUTABLE and
/// STATELESS: same inputs, same answers. It only answers "when"
/// (NextFire / EstimateInterval); it never schedules, fires, or stores
/// state. The Scheduler asks, the pattern answers.
/// </summary>
public sealed class CronPattern
{
    private const int MaxSearchDays = 400; // every legal pattern matches within this

    private static readonly Dictionary<string, string> AtShorthands = new(StringComparer.Ordinal)
    {
        ["@hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@weekly"] = "0 0 * * 0",
        ["@monthly"] = "0 0 1 * *",
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
    };

    private static readonly Dictionary<string, int> DowAliases = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["sun"] = 0,
        ["mon"] = 1,
        ["tue"] = 2,
        ["wed"] = 3,
        ["thu"] = 4,
        ["fri"] = 5,
        ["sat"] = 6,
    };

    private static readonly Dictionary<string, int> MonthAliases = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["jan"] = 1,
        ["feb"] = 2,
        ["mar"] = 3,
        ["apr"] = 4,
        ["may"] = 5,
        ["jun"] = 6,
        ["jul"] = 7,
        ["aug"] = 8,
        ["sep"] = 9,
        ["oct"] = 10,
        ["nov"] = 11,
        ["dec"] = 12,
    };

    private readonly bool[][] _fields; // [0]=sec?, [1]=min, [2]=hour, [4]=month as bit-sets
    private readonly bool _hasSeconds;
    private readonly DayOfMonthSpec _dom;
    private readonly DayOfWeekSpec _dow;

    private readonly record struct DayOfMonthSpec(bool[] Bits, bool LastDay, int? NearestDay)
    {
        public bool Restricted => LastDay || NearestDay is not null || Bits.Count(true) < 31;
    }

    private readonly record struct DayOfWeekSpec(bool[] Bits, (int Dow, int Nth)? Nth, int? LastDow)
    {
        public bool Restricted => Nth is not null || LastDow is not null || Bits.Count(true) < 8;
    }

    private CronPattern(bool[][] fields, bool hasSeconds, DayOfMonthSpec dom, DayOfWeekSpec dow)
    {
        _fields = fields;
        _hasSeconds = hasSeconds;
        _dom = dom;
        _dow = dow;
    }

    /// <summary>Parses a 5-field (min hour dom mon dow) or 6-field (leading
    /// seconds) cron expression covering the full dialect: `*`, `*/n`, `a-b`,
    /// `a,b,c`, `?` (dom/dow), dow `N#K` (K-th weekday), `L` / `NL` (last),
    /// dom `Nw` (nearest weekday), name aliases (mon..sun / jan..dec —
    /// composable inside ranges/lists and the dow `N#K` / `NL` modifiers)
    /// and `@hourly/@daily/@weekly/@monthly/@yearly/@annually/@midnight`.
    /// Throws <see cref="ArgumentException"/> on syntax errors and
    /// out-of-range values.</summary>
    public static CronPattern Parse(string expr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expr);
        if (AtShorthands.TryGetValue(expr.Trim(), out var expanded))
            expr = expanded;

        var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not 5 and not 6)
            throw new ArgumentException(
                $"cron pattern must have 5 or 6 fields, got {parts.Length}"
            );

        var hasSeconds = parts.Length == 6;
        var i = hasSeconds ? 1 : 0;
        var fields = new bool[6][];
        if (hasSeconds)
            fields[0] = ParseBits(parts[0], 0, 59);
        fields[1] = ParseBits(parts[i++], 0, 59);
        fields[2] = ParseBits(parts[i++], 0, 23);
        var dom = ParseDom(parts[i++]);
        fields[4] = ParseBits(parts[i++], 1, 12, MonthAliases);
        var dow = ParseDow(parts[i], DowAliases);

        return new CronPattern(fields, hasSeconds, dom, dow);
    }

    /// <summary>Next absolute fire time strictly after <paramref name="afterUtc"/>,
    /// with fields matched inside <paramref name="tz"/> wall-clock. DST gaps are
    /// skipped naturally (wall clock advances to a valid minute before the next
    /// match; if the probe lands inside a skipped minute the next candidate is
    /// probed). Throws <see cref="InvalidOperationException"/> when no match
    /// occurs within <see cref="MaxSearchDays"/> days (unreachable for legal
    /// patterns — the contract guarantees any pattern hits within 400 days).</summary>
    public DateTimeOffset NextFire(DateTimeOffset afterUtc, TimeZoneInfo tz)
    {
        // Probe forward minute by minute (second by second within the candidate
        // minute when a seconds field exists) until the 400-day deadline — the
        // language-completeness contract guarantees any legal pattern hits.
        // The probe starts at the next full minute (a naked second within the
        // start minute must not count as a fire time).
        // Probe start: with a seconds field — at after+1s (exact second matching);
        // without — at the next full minute (a naked second within the start
        // minute must not count as a fire time).
        var deadline = afterUtc.AddDays(MaxSearchDays);
        var probe = _hasSeconds
            ? afterUtc.AddSeconds(1)
            : TimeZoneInfo.ConvertTime(NextMinute(afterUtc), tz);
        while (probe <= deadline)
        {
            var local = TimeZoneInfo.ConvertTime(probe, tz);
            if (!_hasSeconds)
            {
                if (Matches(local))
                    return local;
                probe = TimeZoneInfo.ConvertTime(NextMinute(local), tz);
                continue;
            }

            // seconds field: scan every second of the candidate minute, then advance
            var minuteStart = local.AddSeconds(-local.Second);
            for (var s = local.Second; s < 60; s++)
            {
                var candidate = minuteStart.AddSeconds(s);
                if (Matches(candidate))
                    return candidate;
            }
            probe = TimeZoneInfo.ConvertTime(NextMinute(minuteStart), tz);
        }
        throw new InvalidOperationException("cron pattern did not match within 400 days");
    }

    /// <summary>Period estimate — the difference between the next two fire
    /// times. Feed for the catch-up grace window (half period, clamped).</summary>
    public TimeSpan EstimateInterval(DateTimeOffset afterUtc, TimeZoneInfo tz)
    {
        var a = NextFire(afterUtc, tz);
        var b = NextFire(a, tz);
        return b - a;
    }

    internal bool Matches(DateTimeOffset local)
    {
        if (_hasSeconds && !_fields[0][local.Second])
            return false;
        if (!_fields[1][local.Minute])
            return false;
        if (!_fields[2][local.Hour])
            return false;
        if (!_fields[4][local.Month])
            return false;

        var domHit = DomHits(local);
        var dowHit = DowHits(local);
        if (_dom.Restricted && _dow.Restricted)
            return domHit || dowHit; // OR semantics (croniter parity)
        if (_dom.Restricted)
            return domHit;
        if (_dow.Restricted)
            return dowHit;
        return true;
    }

    private bool DomHits(DateTimeOffset local)
    {
        if (_dom.LastDay)
            return local.Day == DateTime.DaysInMonth(local.Year, local.Month);
        if (_dom.NearestDay is { } nd)
        {
            var closest = ClosestWeekday(local.Year, local.Month, nd);
            return local.Day == closest;
        }
        return _dom.Bits[local.Day];
    }

    private bool DowHits(DateTimeOffset local)
    {
        if (_dow.Nth is { } nth)
            return (int)local.DayOfWeek == nth.Dow
                && DayOfNth(local.Year, local.Month, nth) == local.Day;
        if (_dow.LastDow is { } ld)
        {
            var days = DateTime.DaysInMonth(local.Year, local.Month);
            return (int)local.DayOfWeek == ld && local.Day > days - 7;
        }
        return _dow.Bits[(int)local.DayOfWeek];
    }

    private static int DayOfNth(int year, int month, (int Dow, int Nth) nth)
    {
        var first = new DateTime(year, month, 1);
        var offset = (nth.Dow - (int)first.DayOfWeek + 7) % 7;
        return 1 + offset + (nth.Nth - 1) * 7;
    }

    private static int ClosestWeekday(int year, int month, int day)
    {
        var probe = new DateTime(year, month, day);
        if (probe.DayOfWeek is DayOfWeek.Saturday)
        {
            var back = probe.AddDays(-1);
            if (back.Month == month)
                return back.Day;
            return probe.AddDays(1).Day; // month edge: shift forward instead
        }
        if (probe.DayOfWeek is DayOfWeek.Sunday)
        {
            var forward = probe.AddDays(1);
            if (forward.Month == month)
                return forward.Day;
            return probe.AddDays(-1).Day; // month edge: shift backward instead
        }
        return day;
    }

    internal bool RequiresSeconds => _hasSeconds;

    private static DayOfMonthSpec ParseDom(string raw)
    {
        if (raw == "L")
            return new DayOfMonthSpec(Bits: new bool[32], LastDay: true, NearestDay: null);
        if (raw.Length >= 2 && raw[^1] is 'w' or 'W')
        {
            var day = ParseInt(raw[..^1], 1, 31);
            var bits = new bool[32];
            bits[day] = true;
            return new DayOfMonthSpec(bits, LastDay: false, NearestDay: day);
        }
        return new DayOfMonthSpec(ParseBits(raw, 1, 31), LastDay: false, NearestDay: null);
    }

    private static DayOfWeekSpec ParseDow(string raw, Dictionary<string, int> aliases)
    {
        if (raw.Contains('#', StringComparison.Ordinal))
        {
            var idx = raw.IndexOf('#');
            var dow = ParseAtom(raw[..idx], 0, 7, aliases) % 7;
            var nth = ParseInt(raw[(idx + 1)..], 1, 5);
            var bits = new bool[8];
            bits[dow] = true;
            return new DayOfWeekSpec(bits, Nth: (dow, nth), LastDow: null);
        }
        if (raw.Length >= 2 && raw[^1] is 'L' or 'l')
        {
            var dow = ParseAtom(raw[..^1], 0, 7, aliases) % 7;
            var bits = new bool[8];
            bits[dow] = true;
            return new DayOfWeekSpec(bits, Nth: null, LastDow: dow);
        }
        return new DayOfWeekSpec(ParseBits(raw, 0, 7, aliases), Nth: null, LastDow: null);
    }

    private static bool[] ParseBits(
        string raw,
        int lo,
        int hi,
        Dictionary<string, int>? aliases = null
    )
    {
        if (raw is "*" or "?")
            return Range(lo, hi);
        if (raw.StartsWith("*/", StringComparison.Ordinal))
            return Step(lo, hi, ParseStep(raw[2..]));

        var set = new bool[hi + 1];
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Contains('-', StringComparison.Ordinal))
            {
                var rangeParts = item.Split('-');
                if (rangeParts.Length != 2)
                    throw new ArgumentException($"invalid field element '{item}'");
                var a = ParseAtom(rangeParts[0], lo, hi, aliases);
                var b = ParseAtom(rangeParts[1], lo, hi, aliases);
                if (a > b)
                    throw new ArgumentException($"range '{item}' is descending");
                for (var v = a; v <= b; v++)
                    set[v] = true;
            }
            else
            {
                set[ParseAtom(item, lo, hi, aliases)] = true;
            }
        }
        return set;
    }

    /// <summary>Resolve one field atom — an exact name alias (case-insensitive)
    /// or a numeric value in range. Aliases compose with ranges, lists and the
    /// dow `N#K` / `NL` modifiers because resolution happens per atom.</summary>
    private static int ParseAtom(string raw, int lo, int hi, Dictionary<string, int>? aliases)
    {
        if (aliases is not null && aliases.TryGetValue(raw, out var named))
            return named;
        return ParseInt(raw, lo, hi);
    }

    private static bool[] Step(int lo, int hi, int step)
    {
        var set = new bool[hi + 1];
        for (var v = lo; v <= hi; v += step)
            set[v] = true;
        return set;
    }

    private static bool[] Range(int lo, int hi)
    {
        var set = new bool[hi + 1];
        for (var v = lo; v <= hi; v++)
            set[v] = true;
        return set;
    }

    private static int ParseStep(string raw)
    {
        if (!int.TryParse(raw, out var step) || step < 1)
            throw new ArgumentException($"invalid step '{raw}'");
        return step;
    }

    private static int ParseInt(string raw, int lo, int hi)
    {
        if (!int.TryParse(raw, out var v) || v < lo || v > hi)
            throw new ArgumentException($"value '{raw}' out of range {lo}..{hi}");
        return v;
    }

    private static DateTimeOffset NextMinute(DateTimeOffset local) =>
        local.AddSeconds(60 - local.Second).AddMilliseconds(-local.Millisecond);
}

file static class BoolArrayExtensions
{
    public static int Count(this bool[] arr, bool value)
    {
        var n = 0;
        foreach (var b in arr)
            if (b == value)
                n++;
        return n;
    }
}
