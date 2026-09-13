namespace PicoSchedule.Tests;

public sealed class CronParserTests
{
    [Test]
    public async Task Parse_5And6Field_Ok()
    {
        await Assert.That(CronPattern.Parse("*/5 9 * * 1-5")).IsNotNull();
        await Assert.That(CronPattern.Parse("0 */5 9 * * 1-5")).IsNotNull();
    }

    [Test]
    public async Task Parse_FieldSyntax_Expands()
    {
        var c = CronPattern.Parse("5,10 9 */2 * *");

        // minutes {5,10} — a non-member minute does not match
        await Assert
            .That(c.Matches(new DateTimeOffset(2026, 9, 1, 9, 7, 0, TimeSpan.Zero)))
            .IsFalse();
        await Assert
            .That(c.Matches(new DateTimeOffset(2026, 9, 1, 9, 5, 0, TimeSpan.Zero)))
            .IsTrue();

        // hour 9 only
        await Assert
            .That(c.Matches(new DateTimeOffset(2026, 9, 1, 10, 5, 0, TimeSpan.Zero)))
            .IsFalse();

        // dom */2 — every other day; Sept 1 is odd, Sept 2 even
        await Assert
            .That(c.Matches(new DateTimeOffset(2026, 9, 1, 9, 5, 0, TimeSpan.Zero)))
            .IsTrue();
        await Assert
            .That(c.Matches(new DateTimeOffset(2026, 9, 2, 9, 5, 0, TimeSpan.Zero)))
            .IsFalse();
        await Assert
            .That(c.Matches(new DateTimeOffset(2026, 9, 3, 9, 5, 0, TimeSpan.Zero)))
            .IsTrue();
    }

    [Test]
    public async Task Parse_DowAlias_ComposesWithNthAndLast()
    {
        // `FRI#3` — name alias + `#` modifier (third Friday)
        var third = CronPattern.Parse("0 12 * * FRI#3");
        await Assert
            .That(third.Matches(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero)))
            .IsTrue(); // third Friday
        await Assert
            .That(third.Matches(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero)))
            .IsFalse(); // second Friday

        // `FRIL` — name alias + `L` modifier (last Friday)
        var last = CronPattern.Parse("0 12 * * FRIL");
        await Assert
            .That(last.Matches(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)))
            .IsTrue(); // last Friday
        await Assert
            .That(last.Matches(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero)))
            .IsFalse(); // third Friday
    }

    [Test]
    public async Task Parse_Alias_ComposesWithRangeAndList()
    {
        // `MON-FRI` — name aliases as range endpoints
        var weekdays = CronPattern.Parse("0 12 * * MON-FRI");
        await Assert
            .That(weekdays.Matches(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)))
            .IsTrue(); // Monday
        await Assert
            .That(weekdays.Matches(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero)))
            .IsTrue(); // Friday
        await Assert
            .That(weekdays.Matches(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)))
            .IsFalse(); // Saturday

        // `JAN-MAR` — month aliases as range endpoints
        var q1 = CronPattern.Parse("0 12 1 JAN-MAR *");
        await Assert
            .That(q1.Matches(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)))
            .IsTrue();
        await Assert
            .That(q1.Matches(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero)))
            .IsFalse();

        // `mon,wed,fri` — lowercase aliases in a list
        var mwf = CronPattern.Parse("0 12 * * mon,wed,fri");
        await Assert
            .That(mwf.Matches(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)))
            .IsTrue(); // Monday
        await Assert
            .That(mwf.Matches(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)))
            .IsFalse(); // Tuesday
    }

    [Test]
    public async Task Parse_Invalid_Throws()
    {
        await Assert.That(() => CronPattern.Parse("not a cron")).Throws<ArgumentException>();
        await Assert.That(() => CronPattern.Parse("61 * * * *")).Throws<ArgumentException>(); // minute 61
        await Assert.That(() => CronPattern.Parse("1 2 3")).Throws<ArgumentException>(); // 3 fields
        await Assert.That(() => CronPattern.Parse("90 * * * * *")).Throws<ArgumentException>(); // second 90
        await Assert.That(() => CronPattern.Parse("5-1 * * * *")).Throws<ArgumentException>(); // descending range
        await Assert.That(() => CronPattern.Parse("*/0 * * * *")).Throws<ArgumentException>(); // zero step
    }
}
