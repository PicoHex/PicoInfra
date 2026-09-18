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
    /// <summary>Safety bound for the structural search. The Gregorian calendar
    /// repeats exactly every 400 years (146,097 days = 20,871 whole weeks), so
    /// every satisfiable pattern has a match within 400 years of any instant —
    /// this bound is provably unreachable for satisfiable patterns and only
    /// guarantees termination if parse-time validation ever misses one.</summary>
    private const int MaxSearchYears = 400;

    /// <summary>Upper bound for the fall-back overlap walk, counted in loop steps
    /// (one second or one minute of instants per step, depending on the seconds
    /// field) — far above any real overlap, so the walk always leaves ambiguity first.</summary>
    private const int MaxOverlapWalkIterations = 4 * 60 * 60;

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
    /// out-of-range values, and for day/month combinations that can never match
    /// (e.g. "31 2", "30W 2").</summary>
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
        ValidateDayMonthCombination(fields[4], dom, dow);

        return new CronPattern(fields, hasSeconds, dom, dow);
    }

    /// <summary>Next absolute fire time strictly after <paramref name="afterUtc"/>,
    /// with fields matched inside <paramref name="tz"/> wall-clock. The search jumps
    /// by calendar fields (month → day → hour → minute → second) instead of scanning
    /// elapsed minutes, so sparse patterns — including month-restricted 5th-weekday
    /// patterns that can be decades apart — cost ~10³ steps rather than ~10⁶. DST
    /// gaps are skipped (a wall time that does not exist never matches); on fall-back
    /// overlap days both instants of a repeated wall time are fire times (instant
    /// order). Throws <see cref="ArgumentException"/> at parse time for patterns that
    /// can never match; the runtime <see cref="InvalidOperationException"/> is a
    /// defensive backstop bounded by the 400-year Gregorian cycle.</summary>
    public DateTimeOffset NextFire(DateTimeOffset afterUtc, TimeZoneInfo tz) =>
        NextFireCore(afterUtc, tz, out _);

    /// <summary>NextFire plus the number of search steps taken — deterministic
    /// instrumentation for the performance contracts (steps scale with field jumps,
    /// not with elapsed minutes). No state: the pattern stays semantically stateless.</summary>
    internal DateTimeOffset NextFireCore(DateTimeOffset afterUtc, TimeZoneInfo tz, out long steps)
    {
        steps = 0;
        var afterLocal = TimeZoneInfo.ConvertTime(afterUtc, tz);
        var probeInstant = afterUtc;

        if (tz.IsAmbiguousTime(afterLocal.DateTime))
        {
            // Fall-back overlap: the same wall time occurs twice. A local-monotonic
            // field search would skip the second occurrence, so walk instants through
            // the overlap (bounded by its length) exactly like the pre-rewrite probe.
            for (var walked = 0; walked < MaxOverlapWalkIterations; walked++)
            {
                steps++;
                probeInstant = probeInstant.Add(
                    _hasSeconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1)
                );
                var local = TimeZoneInfo.ConvertTime(probeInstant, tz);
                if (Matches(local))
                    return local;
                if (!tz.IsAmbiguousTime(local.DateTime))
                    break;
            }
        }

        // Field-jumping search over wall-clock time; every branch advances the
        // candidate strictly forward, so the loop cannot spin.
        var start = TimeZoneInfo.ConvertTime(probeInstant, tz);
        var candidate = _hasSeconds
            ? FloorToSecond(start.DateTime).AddSeconds(1)
            : FloorToMinute(start.DateTime).AddMinutes(1);
        var maxYear = afterLocal.Year + MaxSearchYears;

        while (true)
        {
            steps++;
            if (candidate.Year > maxYear)
                throw new InvalidOperationException(
                    $"cron pattern did not match within {MaxSearchYears} years"
                );

            if (!_fields[4][candidate.Month])
            {
                candidate = NextMonthStart(candidate);
                continue;
            }

            if (!DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_fields[2][candidate.Hour])
            {
                candidate = NextHourStart(candidate);
                continue;
            }

            if (!_fields[1][candidate.Minute])
            {
                candidate = NextMinuteStart(candidate);
                continue;
            }

            if (_hasSeconds && !_fields[0][candidate.Second])
            {
                candidate = candidate.AddSeconds(1);
                continue;
            }

            // Wall clock fully matches — resolve through the time zone.
            if (tz.IsInvalidTime(candidate))
            {
                // Spring-forward gap: the wall time does not exist; step past it.
                candidate = candidate.AddSeconds(_hasSeconds ? 1 : 60);
                continue;
            }

            foreach (var instant in ResolveInstants(candidate, tz))
            {
                // Round-trip guard: the instant must read back as the same wall
                // time (instant → local). Some tz databases make the two
                // directions disagree (Pacific/Apia 2011 on Windows tz data);
                // the scheduling contract is the wall clock the caller sees.
                if (TimeZoneInfo.ConvertTime(instant, tz).DateTime != candidate)
                    continue;

                if (instant.UtcDateTime > afterUtc.UtcDateTime)
                    return instant;
            }

            // Overlap candidate whose occurrences are both ≤ after is unreachable
            // (the walk above starts inside the overlap); advance defensively.
            candidate = candidate.AddSeconds(_hasSeconds ? 1 : 60);
        }
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
        return DayMatches(local.DateTime);
    }

    private bool DayMatches(DateTime local)
    {
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

    private bool DomHits(DateTime local)
    {
        if (_dom.LastDay)
            return local.Day == DateTime.DaysInMonth(local.Year, local.Month);
        if (_dom.NearestDay is { } nd)
        {
            var days = DateTime.DaysInMonth(local.Year, local.Month);
            if (nd > days)
                return false; // this month has no such day (e.g. "30W" in February)
            return local.Day == ClosestWeekday(local.Year, local.Month, nd);
        }
        return _dom.Bits[local.Day];
    }

    private bool DowHits(DateTime local)
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

        // A field that selects no values can never match; reject it here so a
        // malformed expression fails fast instead of driving a multi-year search
        // (which would hold the scheduler gate for minutes for wildcard fields).
        for (var v = lo; v <= hi; v++)
        {
            if (set[v])
                return set;
        }
        throw new ArgumentException($"field '{raw}' selects no values");
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

    private static DateTime FloorToSecond(DateTime local) =>
        new(
            local.Year,
            local.Month,
            local.Day,
            local.Hour,
            local.Minute,
            local.Second,
            DateTimeKind.Unspecified
        );

    private static DateTime FloorToMinute(DateTime local) =>
        new(
            local.Year,
            local.Month,
            local.Day,
            local.Hour,
            local.Minute,
            0,
            DateTimeKind.Unspecified
        );

    private static DateTime NextMonthStart(DateTime c) =>
        new DateTime(c.Year, c.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);

    private static DateTime NextHourStart(DateTime c) =>
        new DateTime(c.Year, c.Month, c.Day, c.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);

    private static DateTime NextMinuteStart(DateTime c) =>
        new DateTime(
            c.Year,
            c.Month,
            c.Day,
            c.Hour,
            c.Minute,
            0,
            DateTimeKind.Unspecified
        ).AddMinutes(1);

    /// <summary>Wall time → instants, ascending. A gap (spring-forward) yields
    /// nothing; an overlap (fall-back) yields both occurrences.</summary>
    private static DateTimeOffset[] ResolveInstants(DateTime local, TimeZoneInfo tz)
    {
        if (tz.IsInvalidTime(local))
            return [];

        if (tz.IsAmbiguousTime(local))
        {
            var offsets = tz.GetAmbiguousTimeOffsets(local);
            var a = new DateTimeOffset(local, offsets[0]);
            var b = new DateTimeOffset(local, offsets[1]);
            return a <= b ? [a, b] : [b, a];
        }

        return [new DateTimeOffset(local, tz.GetUtcOffset(local))];
    }

    /// <summary>Rejects day-of-month values that exist in no selected month
    /// (e.g. "31 2", "30 2", "30W 2") at parse time. A restricted day-of-week
    /// makes the pattern satisfiable through OR semantics, so it is not checked.</summary>
    private static void ValidateDayMonthCombination(
        bool[] months,
        DayOfMonthSpec dom,
        DayOfWeekSpec dow
    )
    {
        if (dow.Restricted || dom.LastDay)
            return;

        // 2000 is a leap year, so Feb yields 29 — the leap-day case stays valid.
        if (dom.NearestDay is { } nearest)
        {
            for (var month = 1; month <= 12; month++)
            {
                if (months[month] && nearest <= DateTime.DaysInMonth(2000, month))
                    return;
            }
            throw new ArgumentException(
                $"day-of-month '{nearest}W' does not exist in any selected month"
            );
        }

        for (var day = 1; day <= 31; day++)
        {
            if (!dom.Bits[day])
                continue;
            for (var month = 1; month <= 12; month++)
            {
                if (months[month] && day <= DateTime.DaysInMonth(2000, month))
                    return;
            }
        }
        throw new ArgumentException("day-of-month values do not exist in any selected month");
    }
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
