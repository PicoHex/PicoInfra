namespace PicoSchedule.Tests;

public sealed class NextFireTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero); // 2026-09-10T12:00Z

    [Test]
    public async Task Every5Minutes_Aligns()
    {
        var c = CronPattern.Parse("*/5 * * * *");
        var n = c.NextFire(
            new DateTimeOffset(2026, 9, 10, 12, 2, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 9, 10, 12, 5, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task SecondsField_Precise()
    {
        var c = CronPattern.Parse("30 * * * * *");
        var n = c.NextFire(Base, TimeZoneInfo.Utc);
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 9, 10, 12, 0, 30, TimeSpan.Zero));
    }

    [Test]
    public async Task DomDow_OrSemantics()
    {
        // dom=13 OR dow=1(Monday): the 13th of Sept 2026 is a Sunday — the dom
        // branch hits it, so the fire time is the 13th itself
        var c = CronPattern.Parse("0 9 13 * 1");
        var n = c.NextFire(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 9, 13, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Monthly_And_Yearly()
    {
        var c1 = CronPattern.Parse("0 9 1 * *");
        var n1 = c1.NextFire(Base, TimeZoneInfo.Utc);
        await Assert.That(n1).IsEqualTo(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

        var c2 = CronPattern.Parse("0 9 13 6 *");
        var n2 = c2.NextFire(Base, TimeZoneInfo.Utc);
        await Assert.That(n2).IsEqualTo(new DateTimeOffset(2027, 6, 13, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Dst_SpringForward_UtcStable()
    {
        // Europe/Berlin DST: 2026-03-29 02:00 CET → 03:00 CEST. Local 02:30 does not exist that day.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var c = CronPattern.Parse("30 2 * * *");
        var n = c.NextFire(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), tz);
        // 03-28 02:30+01 = 01:30Z; the non-existent 03-29 02:30 is skipped → 03-30 02:30+02 = 00:30Z
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task EstimateInterval_Period()
    {
        var c = CronPattern.Parse("*/5 * * * *");
        var i = c.EstimateInterval(Base, TimeZoneInfo.Utc);
        await Assert.That(i).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task NthDayOfWeek_HashSyntax()
    {
        // Second Sunday of the month: 2026-09 Sundays are 6th and 13th
        var c = CronPattern.Parse("0 9 * * 0#2");
        var n = c.NextFire(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 9, 13, 9, 0, 0, TimeSpan.Zero));

        // Fifth Monday of the month: 2026-04 has FOUR Mondays (4/6,13,20,27) and
        // 2026-05 has four too (5/4,11,18,25) → next hit is June's fifth Monday:
        // 2026-06-29 (6/1,8,15,22,29)
        var c5 = CronPattern.Parse("0 9 * * 1#5");
        var n5 = c5.NextFire(
            new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n5).IsEqualTo(new DateTimeOffset(2026, 6, 29, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task LastDayOfWeek_And_LastDayOfMonth()
    {
        // Last Friday: 2026-09 Fridays are 4,11,18,25
        var c = CronPattern.Parse("0 9 * * 5L");
        var n = c.NextFire(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));

        // Last day of month: February 2026 has 28 days (not a leap year)
        var cL = CronPattern.Parse("0 9 L * *");
        var nL = cL.NextFire(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(nL).IsEqualTo(new DateTimeOffset(2026, 2, 28, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task NearestWeekday_W()
    {
        // 15th of August 2026 is a Saturday → nearest weekday is Friday the 14th
        var c = CronPattern.Parse("0 9 15w * *");
        var n = c.NextFire(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero));

        // 15th of June 2026 is a Monday → itself
        var n2 = c.NextFire(
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n2).IsEqualTo(new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Aliases_And_AtShorthands()
    {
        // dow alias MON ≡ 1
        var c = CronPattern.Parse("0 9 * * MON");
        var n = c.NextFire(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(n).IsEqualTo(new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero));

        // month alias JUN ≡ 6
        var cm = CronPattern.Parse("0 9 13 JUN *");
        var nm = cm.NextFire(Base, TimeZoneInfo.Utc);
        await Assert.That(nm).IsEqualTo(new DateTimeOffset(2027, 6, 13, 9, 0, 0, TimeSpan.Zero));

        // @weekly ≡ "0 0 * * 0"; @monthly ≡ "0 0 1 * *"
        var cw = CronPattern.Parse("@weekly");
        var nw = cw.NextFire(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc
        );
        await Assert.That(nw).IsEqualTo(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));

        var cmm = CronPattern.Parse("@monthly");
        var nmm = cmm.NextFire(Base, TimeZoneInfo.Utc);
        await Assert.That(nmm).IsEqualTo(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
