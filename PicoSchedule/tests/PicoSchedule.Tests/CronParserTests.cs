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
