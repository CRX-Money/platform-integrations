using CrxMoney.Integrations.Abstractions.Sync;
using CrxMoney.Integrations.Providers.Br.Inter.Sync;

namespace CrxMoney.Integrations.Providers.Br.Inter.Tests;

public sealed class InterStatementPlanTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private static readonly DateOnly FullHistoryFrom = new(2025, 1, 1);
    private static readonly InterIntegrationOptions Options = new();

    private static IntegrationSyncRequest Request(DateTimeOffset? start, DateTimeOffset? end, string? checkpoint = null) =>
        new(1, InterIntegrationPlugin.Id, start, end,
            PreviousCheckpoints: checkpoint is null ? null : [new IntegrationCheckpoint(InterStatementPlan.CheckpointResource, checkpoint)]);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 12) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Full_history_starts_at_the_backfill_date_and_splits_into_90_day_windows()
    {
        var plan = InterStatementPlan.Create(Request(null, null), Today, FullHistoryFrom, Options);

        Assert.Equal(FullHistoryFrom, plan.From);
        Assert.Equal(Today, plan.To);
        Assert.All(plan.Windows, w => Assert.InRange(w.To.DayNumber - w.From.DayNumber + 1, 1, 90));
        Assert.Equal(FullHistoryFrom, plan.Windows[0].From);
        Assert.Equal(Today, plan.Windows[^1].To);
        for (var i = 1; i < plan.Windows.Count; i++)
            Assert.Equal(plan.Windows[i - 1].To.AddDays(1), plan.Windows[i].From);
    }

    [Fact]
    public void Window_bounds_are_brazilian_calendar_dates()
    {
        // 02:00 UTC on 1 March is still 28 February in São Paulo.
        var plan = InterStatementPlan.Create(Request(Utc(2026, 3, 1, 2), Utc(2026, 3, 31, 2)), Today, FullHistoryFrom, Options);

        Assert.Equal([(new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 30))], plan.Windows);
    }

    [Fact]
    public void Checkpoint_resumes_a_few_days_before_the_last_synced_date()
    {
        var plan = InterStatementPlan.Create(Request(Utc(2026, 10, 15), Utc(2026, 10, 16), "2026-10-15"), Today, FullHistoryFrom, Options);

        Assert.Equal(new DateOnly(2026, 10, 12), plan.From);
    }

    [Fact]
    public void Replaying_an_old_window_keeps_the_later_checkpoint()
    {
        var plan = InterStatementPlan.Create(Request(Utc(2026, 1, 10), Utc(2026, 1, 31), "2026-10-15"), Today, FullHistoryFrom, Options);

        Assert.Equal(new DateOnly(2026, 1, 31), plan.To);
        Assert.Equal(new DateOnly(2026, 10, 15), plan.CheckpointDate);
    }

    [Fact]
    public void Full_history_says_it_starts_at_the_backfill_anchor()
    {
        Assert.True(InterStatementPlan.Create(Request(null, null), Today, FullHistoryFrom, Options).StartsAtBackfillAnchor);
        Assert.False(InterStatementPlan.Create(Request(Utc(2026, 1, 10), null), Today, FullHistoryFrom, Options).StartsAtBackfillAnchor);
    }

    [Fact]
    public void An_earlier_host_window_wins_over_the_checkpoint()
    {
        var plan = InterStatementPlan.Create(Request(Utc(2026, 1, 10), null, "2026-10-15"), Today, FullHistoryFrom, Options);

        Assert.Equal(new DateOnly(2026, 1, 10), plan.From);
    }

    [Fact]
    public void The_end_never_passes_today_in_brazil()
    {
        var plan = InterStatementPlan.Create(Request(Utc(2026, 10, 1), Utc(2026, 12, 1)), Today, FullHistoryFrom, Options);

        Assert.Equal(Today, plan.To);
    }

    [Fact]
    public void A_start_after_the_end_reads_nothing()
    {
        var plan = InterStatementPlan.Create(Request(Utc(2026, 10, 20), Utc(2026, 10, 25)), Today, FullHistoryFrom, Options);

        Assert.True(plan.IsEmpty);
    }

    [Theory]
    [InlineData("2026-03-10", 2026, 3, 10)]
    [InlineData("2026-03-10T00:00:00.0000000+00:00", 2026, 3, 10)]
    public void Reads_the_checkpoint_as_a_date(string cursor, int year, int month, int day)
    {
        var request = Request(null, null, cursor);

        Assert.Equal(new DateOnly(year, month, day), InterStatementPlan.ReadCheckpoint(request));
    }

    [Fact]
    public void A_foreign_checkpoint_fails_instead_of_restarting_from_scratch()
    {
        Assert.Throws<FormatException>(() => InterStatementPlan.ReadCheckpoint(Request(null, null, "page:7")));
    }

    [Fact]
    public void Checkpoint_round_trips()
    {
        var checkpoint = InterStatementPlan.CheckpointFor(new DateOnly(2026, 3, 31));
        var request = new IntegrationSyncRequest(1, InterIntegrationPlugin.Id, null, null, PreviousCheckpoints: [checkpoint]);

        Assert.Equal(new IntegrationCheckpoint("statement", "2026-03-31"), checkpoint);
        Assert.Equal(new DateOnly(2026, 3, 31), InterStatementPlan.ReadCheckpoint(request));
    }
}
