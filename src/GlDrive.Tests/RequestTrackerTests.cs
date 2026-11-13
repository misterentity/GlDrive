using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RequestTrackerTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private const string Release = "Some.Show.S02E03.2160p.WEB.h265-GRP";
    private const string Second = "Some.Show.S02E04.2160p.WEB.h265-GRP";
    private const string Third = "Some.Show.S02E05.2160p.WEB.h265-GRP";
    private const string Fourth = "Some.Show.S02E06.2160p.WEB.h265-GRP";

    private static RequestTracker Unlimited() => new(new RequestTrackerOptions
    {
        MaxPerHour = 0,
        CooldownSeconds = 0
    });

    private static void SetState(RequestTracker tracker, string release, RequestState state)
    {
        if (state == RequestState.Closed)
            tracker.MarkFilledElsewhere(release);
        else if (state is RequestState.Filling or RequestState.Filled)
        {
            Assert.True(tracker.TryBeginFill(release, Now));
            if (state == RequestState.Filled)
                tracker.FillCompleted(release);
        }
    }

    [Fact]
    public void Options_have_the_specified_defaults()
    {
        var options = new RequestTrackerOptions();

        Assert.Equal(10, options.MaxPerHour);
        Assert.Equal(60, options.CooldownSeconds);
        Assert.Equal(3, options.MaxFillAttempts);
        Assert.Equal(TimeSpan.FromHours(48), options.MaxAge);
        Assert.Equal(TimeSpan.FromMinutes(2), options.ReconcileGrace);
        Assert.Equal(200, options.Capacity);
    }

    [Fact]
    public void Observe_adds_an_open_request_due_immediately()
    {
        var tracker = new RequestTracker();

        Assert.True(tracker.Observe(Release, 3, "#chan1", Now));
        Assert.Equal(new TrackedRequest(Release, 3, "#chan1", Now,
            RequestState.Open, 0, 0, Now), tracker.Get(Release));
        Assert.Equal(tracker.Get(Release), Assert.Single(tracker.DueForSearch(Now)));
    }

    [Theory]
    [InlineData(RequestState.Open)]
    [InlineData(RequestState.Filling)]
    public void Observe_updates_non_null_metadata_without_resetting_active_requests(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, 3, null, Now);
        tracker.SearchMissed(Release, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release)!;

        Assert.False(tracker.Observe(Release.ToLowerInvariant(), 4, "#chan1", Now.AddMinutes(1)));
        Assert.Equal(before with { Number = 4, Channel = "#chan1" }, tracker.Get(Release));
        Assert.False(tracker.Observe(Release, null, null, Now.AddMinutes(2)));
        Assert.Equal(before with { Number = 4, Channel = "#chan1" }, tracker.Get(Release));
        Assert.Single(tracker.Snapshot());
    }

    [Theory]
    [InlineData(RequestState.Closed)]
    [InlineData(RequestState.Filled)]
    public void Observe_reopens_terminal_requests_with_fresh_times_metadata_and_counters(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, 3, "#chan1", Now);
        tracker.SearchMissed(Release, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));
        if (state == RequestState.Filled)
            tracker.FillCompleted(Release);
        else
            tracker.MarkFilledElsewhere(Release);
        var later = Now.AddHours(1);

        Assert.True(tracker.Observe(Release, null, null, later));

        Assert.Equal(new TrackedRequest(Release, null, null, later,
            RequestState.Open, 0, 0, later), tracker.Get(Release));
    }

    [Theory]
    [InlineData(RequestState.Open, RequestState.Closed)]
    [InlineData(RequestState.Filling, RequestState.Closed)]
    [InlineData(RequestState.Filled, RequestState.Filled)]
    [InlineData(RequestState.Closed, RequestState.Closed)]
    public void MarkFilledElsewhere_only_closes_active_requests(RequestState initial, RequestState expected)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, 3, "#chan1", Now);
        SetState(tracker, Release, initial);
        var before = tracker.Get(Release)!;

        tracker.MarkFilledElsewhere(Release.ToLowerInvariant());

        Assert.Equal(before with { State = expected }, tracker.Get(Release));
    }

    [Fact]
    public void Reconcile_adds_unknown_names_once_and_preserves_known_metadata_and_schedule()
    {
        var tracker = Unlimited();
        tracker.Observe(Release, 3, "#chan1", Now);
        tracker.SearchMissed(Release, Now);
        var before = tracker.Get(Release);
        var later = Now.AddSeconds(10);

        tracker.Reconcile([Release.ToLowerInvariant(), Second, Second.ToLowerInvariant()], later);

        Assert.Equal(before, tracker.Get(Release));
        Assert.Equal(new TrackedRequest(Second, null, null, later,
            RequestState.Open, 0, 0, later), tracker.Get(Second));
        Assert.Equal(2, tracker.Snapshot().Count);
    }

    [Fact]
    public void Reconcile_closes_absent_open_requests_only_after_the_grace_boundary()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            ReconcileGrace = TimeSpan.FromMinutes(3)
        });
        tracker.Observe(Release, null, null, Now);
        tracker.Reconcile([], Now.AddMinutes(3));
        Assert.Equal(RequestState.Open, tracker.Get(Release)!.State);

        tracker.Reconcile([], Now.AddMinutes(3).AddTicks(1));

        Assert.Equal(RequestState.Closed, tracker.Get(Release)!.State);
    }

    [Theory]
    [InlineData(RequestState.Closed)]
    [InlineData(RequestState.Filled)]
    public void Reconcile_never_reopens_terminal_requests(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, 3, "#chan1", Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release);

        tracker.Reconcile([Release.ToLowerInvariant()], Now.AddHours(1));

        Assert.Equal(before, tracker.Get(Release));
    }

    [Theory]
    [InlineData(RequestState.Filling)]
    [InlineData(RequestState.Filled)]
    public void Reconcile_leaves_absent_filling_and_filled_requests_untouched(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release);

        tracker.Reconcile([], Now.AddHours(1));

        Assert.Equal(before, tracker.Get(Release));
    }

    [Fact]
    public void DueForSearch_returns_only_due_open_requests_ordered_by_first_seen()
    {
        var tracker = Unlimited();
        tracker.Observe(Second, null, null, Now.AddSeconds(1));
        tracker.Observe(Release, null, null, Now);
        tracker.Observe(Third, null, null, Now.AddSeconds(2));
        tracker.SearchMissed(Third, Now);
        tracker.Observe(Fourth, null, null, Now);
        Assert.True(tracker.TryBeginFill(Fourth, Now));

        Assert.Equal(new[] { Release, Second },
            tracker.DueForSearch(Now.AddSeconds(2)).Select(request => request.Release));
        Assert.Equal(new[] { Release, Second, Third },
            tracker.DueForSearch(Now.AddMinutes(1)).Select(request => request.Release));
    }

    [Fact]
    public void DueForSearch_expires_open_requests_only_after_max_age_even_when_not_due()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { MaxAge = TimeSpan.FromSeconds(30) });
        tracker.Observe(Release, null, null, Now);
        tracker.SearchMissed(Release, Now);
        Assert.Empty(tracker.DueForSearch(Now.AddSeconds(30)));
        Assert.Equal(RequestState.Open, tracker.Get(Release)!.State);

        Assert.Empty(tracker.DueForSearch(Now.AddSeconds(30).AddTicks(1)));

        Assert.Equal(RequestState.Closed, tracker.Get(Release)!.State);
    }

    [Theory]
    [InlineData(RequestState.Filling)]
    [InlineData(RequestState.Filled)]
    [InlineData(RequestState.Closed)]
    public void DueForSearch_preserves_non_open_requests_after_max_age(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release);

        Assert.Empty(tracker.DueForSearch(Now.AddHours(49)));
        Assert.Equal(before, tracker.Get(Release));
    }

    [Fact]
    public void Unknown_releases_are_ignored_without_consuming_rate_limits()
    {
        var tracker = new RequestTracker();

        tracker.MarkFilledElsewhere(Release);
        tracker.SearchMissed(Release, Now);
        tracker.FillFailed(Release, Now);
        tracker.FillCompleted(Release);
        tracker.Nudge(Release, Now);
        Assert.False(tracker.TryBeginFill(Release, Now));
        Assert.Null(tracker.Get(Release));
        Assert.Empty(tracker.Snapshot());
        Assert.Empty(tracker.DueForSearch(Now));
        tracker.Observe(Release, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 20)]
    [InlineData(6, 30)]
    [InlineData(8, 30)]
    public void SearchMissed_uses_the_search_backoff_table(int attempts, int minutes)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        var later = Now.AddHours(1);

        for (var attempt = 0; attempt < attempts; attempt++)
            tracker.SearchMissed(Release.ToLowerInvariant(), later);

        var request = tracker.Get(Release)!;
        Assert.Equal(RequestState.Open, request.State);
        Assert.Equal(attempts, request.SearchAttempts);
        Assert.Equal(0, request.FillAttempts);
        Assert.Equal(later.AddMinutes(minutes), request.NextAttemptUtc);
    }

    [Theory]
    [InlineData(RequestState.Filling)]
    [InlineData(RequestState.Filled)]
    [InlineData(RequestState.Closed)]
    public void SearchMissed_ignores_non_open_requests(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release);

        tracker.SearchMissed(Release, Now.AddMinutes(1));

        Assert.Equal(before, tracker.Get(Release));
    }

    [Fact]
    public void TryBeginFill_starts_an_open_request_even_before_its_search_due_time()
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        tracker.SearchMissed(Release, Now);

        Assert.True(tracker.TryBeginFill(Release.ToLowerInvariant(), Now));

        var request = tracker.Get(Release)!;
        Assert.Equal(RequestState.Filling, request.State);
        Assert.Equal(1, request.FillAttempts);
        Assert.Equal(1, request.SearchAttempts);
    }

    [Theory]
    [InlineData(RequestState.Filling)]
    [InlineData(RequestState.Filled)]
    [InlineData(RequestState.Closed)]
    public void TryBeginFill_ignores_non_open_requests(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release);

        Assert.False(tracker.TryBeginFill(Release, Now.AddMinutes(1)));

        Assert.Equal(before, tracker.Get(Release));
    }

    [Fact]
    public void Observing_reconciling_and_searching_do_not_consume_fill_start_limits()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { MaxPerHour = 1 });
        tracker.Observe(Release, null, null, Now);
        tracker.Reconcile([Release, Second, Third], Now);
        Assert.Equal(3, tracker.DueForSearch(Now).Count);
        Assert.Equal(3, tracker.DueForSearch(Now).Count);
        tracker.SearchMissed(Release, Now);

        Assert.True(tracker.TryBeginFill(Release, Now));
        Assert.False(tracker.TryBeginFill(Second, Now));
        Assert.Equal(RequestState.Open, tracker.Get(Second)!.State);
        Assert.Equal(Now.AddHours(1), tracker.Get(Second)!.NextAttemptUtc);
    }

    [Fact]
    public void Admission_deferral_preserves_rate_limit_and_request_age()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { MaxPerHour = 1 });
        tracker.Observe(Release, null, null, Now);
        tracker.Observe(Second, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));
        tracker.FillDeferred(Release, Now);
        Assert.Equal(0, tracker.Get(Release)!.FillAttempts);
        Assert.Equal(Now, tracker.Get(Release)!.FirstSeenUtc);
        Assert.False(tracker.TryBeginFill(Second, Now.AddMinutes(2)));
        Assert.Equal(Now.AddHours(1), tracker.Get(Second)!.NextAttemptUtc);
        Assert.Empty(tracker.DueForSearch(Now.AddHours(49)));
        Assert.Equal(RequestState.Closed, tracker.Get(Release)!.State);
    }

    [Fact]
    public void Cooldown_keeps_requests_open_until_the_exact_boundary_without_counting_rejections()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { MaxPerHour = 0, CooldownSeconds = 60 });
        tracker.Observe(Release, null, null, Now);
        tracker.Observe(Second, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));
        tracker.FillCompleted(Release);

        Assert.False(tracker.TryBeginFill(Second, Now.AddSeconds(20)));
        Assert.False(tracker.TryBeginFill(Second, Now.AddSeconds(59)));
        var held = tracker.Get(Second)!;
        Assert.Equal(RequestState.Open, held.State);
        Assert.Equal(0, held.FillAttempts);
        Assert.Equal(Now.AddMinutes(1), held.NextAttemptUtc);
        Assert.True(tracker.TryBeginFill(Second, Now.AddMinutes(1)));
    }

    [Fact]
    public void Hourly_window_slides_at_each_fill_start_and_counts_failed_and_completed_fills()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { MaxPerHour = 2, CooldownSeconds = 0 });
        foreach (var release in new[] { Release, Second, Third, Fourth })
            tracker.Observe(release, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));
        tracker.FillFailed(Release, Now);
        Assert.True(tracker.TryBeginFill(Second, Now.AddMinutes(10)));
        tracker.FillCompleted(Second);

        Assert.False(tracker.TryBeginFill(Third, Now.AddMinutes(59)));
        Assert.Equal(Now.AddHours(1), tracker.Get(Third)!.NextAttemptUtc);
        Assert.False(tracker.TryBeginFill(Third, Now.AddHours(1).AddTicks(-1)));
        Assert.True(tracker.TryBeginFill(Third, Now.AddHours(1)));
        Assert.False(tracker.TryBeginFill(Fourth, Now.AddHours(1)));
        Assert.Equal(RequestState.Open, tracker.Get(Fourth)!.State);
        Assert.Equal(Now.AddMinutes(70), tracker.Get(Fourth)!.NextAttemptUtc);
        Assert.True(tracker.TryBeginFill(Fourth, Now.AddMinutes(70)));
    }

    [Theory]
    [InlineData(1800, 60)]
    [InlineData(7200, 120)]
    public void Combined_limits_schedule_the_later_of_their_expirations(int cooldownSeconds, int expectedMinutes)
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            MaxPerHour = 1,
            CooldownSeconds = cooldownSeconds
        });
        tracker.Observe(Release, null, null, Now);
        tracker.Observe(Second, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));

        Assert.False(tracker.TryBeginFill(Second, Now.AddSeconds(1)));

        Assert.Equal(Now.AddMinutes(expectedMinutes), tracker.Get(Second)!.NextAttemptUtc);
        Assert.True(tracker.TryBeginFill(Second, Now.AddMinutes(expectedMinutes)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    public void Nonpositive_rate_limits_are_disabled(int maxPerHour, int cooldownSeconds)
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            MaxPerHour = maxPerHour,
            CooldownSeconds = cooldownSeconds
        });

        for (var episode = 0; episode < 20; episode++)
        {
            var release = $"Some.Show.S02E{episode:D2}.2160p.WEB.h265-GRP";
            tracker.Observe(release, null, null, Now);
            Assert.True(tracker.TryBeginFill(release, Now));
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 15)]
    public void FillFailed_uses_the_fill_backoff_table(int attempts, int minutes)
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            MaxPerHour = 0,
            CooldownSeconds = 0,
            MaxFillAttempts = 5
        });
        tracker.Observe(Release, null, null, Now);
        var later = Now;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            Assert.True(tracker.TryBeginFill(Release, later));
            later = later.AddSeconds(1);
            tracker.FillFailed(Release.ToLowerInvariant(), later);
            if (attempt + 1 < attempts)
                later = tracker.Get(Release)!.NextAttemptUtc;
        }

        var request = tracker.Get(Release)!;
        Assert.Equal(RequestState.Open, request.State);
        Assert.Equal(attempts, request.FillAttempts);
        Assert.Equal(0, request.SearchAttempts);
        Assert.Equal(later.AddMinutes(minutes), request.NextAttemptUtc);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FillFailed_closes_at_the_maximum_number_of_fill_attempts(int maxFillAttempts)
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            MaxPerHour = 0,
            CooldownSeconds = 0,
            MaxFillAttempts = maxFillAttempts
        });
        tracker.Observe(Release, null, null, Now);
        for (var attempt = 1; attempt <= maxFillAttempts; attempt++)
        {
            Assert.True(tracker.TryBeginFill(Release, Now));
            tracker.FillFailed(Release, Now);
            Assert.Equal(attempt == maxFillAttempts ? RequestState.Closed : RequestState.Open,
                tracker.Get(Release)!.State);
        }

        Assert.Equal(maxFillAttempts, tracker.Get(Release)!.FillAttempts);
        Assert.False(tracker.TryBeginFill(Release, Now.AddHours(1)));
    }

    [Theory]
    [InlineData(RequestState.Open)]
    [InlineData(RequestState.Filled)]
    [InlineData(RequestState.Closed)]
    public void FillFailed_ignores_requests_that_are_not_filling(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release);

        tracker.FillFailed(Release, Now.AddMinutes(1));

        Assert.Equal(before, tracker.Get(Release));
    }

    [Theory]
    [InlineData(RequestState.Open, RequestState.Open)]
    [InlineData(RequestState.Filling, RequestState.Filled)]
    [InlineData(RequestState.Filled, RequestState.Filled)]
    [InlineData(RequestState.Closed, RequestState.Closed)]
    public void FillCompleted_only_changes_filling_requests(RequestState initial, RequestState expected)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, initial);
        var before = tracker.Get(Release)!;

        tracker.FillCompleted(Release.ToLowerInvariant());

        Assert.Equal(before with { State = expected }, tracker.Get(Release));
    }

    [Theory]
    [InlineData(RequestState.Open)]
    [InlineData(RequestState.Filling)]
    [InlineData(RequestState.Filled)]
    [InlineData(RequestState.Closed)]
    public void Nudge_only_makes_open_requests_due_now(RequestState state)
    {
        var tracker = Unlimited();
        tracker.Observe(Release, null, null, Now);
        tracker.SearchMissed(Release, Now);
        SetState(tracker, Release, state);
        var before = tracker.Get(Release)!;
        var later = Now.AddSeconds(10);

        tracker.Nudge(Release.ToLowerInvariant(), later);

        Assert.Equal(state == RequestState.Open ? before with { NextAttemptUtc = later } : before,
            tracker.Get(Release));
    }

    [Theory]
    [InlineData(RequestState.Closed, RequestState.Filled)]
    [InlineData(RequestState.Filled, RequestState.Closed)]
    public void Capacity_evicts_oldest_terminal_entries_before_older_open_entries(
        RequestState olderTerminal, RequestState newerTerminal)
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            Capacity = 3,
            MaxPerHour = 0,
            CooldownSeconds = 0
        });
        tracker.Observe(Release, null, null, Now);
        tracker.Observe(Second, null, null, Now.AddSeconds(1));
        SetState(tracker, Second, olderTerminal);
        tracker.Observe(Third, null, null, Now.AddSeconds(2));
        SetState(tracker, Third, newerTerminal);

        tracker.Observe(Fourth, null, null, Now.AddSeconds(3));
        Assert.Null(tracker.Get(Second));
        Assert.NotNull(tracker.Get(Release));
        Assert.NotNull(tracker.Get(Third));

        tracker.Observe(Second, null, null, Now.AddSeconds(4));
        Assert.Null(tracker.Get(Third));
        Assert.NotNull(tracker.Get(Release));
        Assert.Equal(3, tracker.Snapshot().Count);
    }

    [Fact]
    public void Capacity_evicts_open_entries_by_first_seen_instead_of_insertion_order()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { Capacity = 2 });
        tracker.Observe(Second, null, null, Now.AddSeconds(1));
        tracker.Observe(Release, null, null, Now);

        tracker.Observe(Third, null, null, Now.AddSeconds(2));

        Assert.Null(tracker.Get(Release));
        Assert.Equal(new[] { Second, Third }, tracker.Snapshot().Select(request => request.Release));
    }

    [Fact]
    public void Capacity_never_evicts_filling_entries()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions
        {
            Capacity = 2,
            MaxPerHour = 0,
            CooldownSeconds = 0
        });
        tracker.Observe(Release, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));
        tracker.Observe(Second, null, null, Now.AddSeconds(1));

        tracker.Reconcile([Release, Second, Third], Now.AddSeconds(2));

        Assert.Equal(RequestState.Filling, tracker.Get(Release)!.State);
        Assert.Null(tracker.Get(Second));
        Assert.NotNull(tracker.Get(Third));
        Assert.Equal(2, tracker.Snapshot().Count);
    }

    [Fact]
    public void Capacity_preserves_new_requests_when_every_existing_entry_is_filling()
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { Capacity = 1 });
        tracker.Observe(Release, null, null, Now);
        Assert.True(tracker.TryBeginFill(Release, Now));

        Assert.True(tracker.Observe(Second, null, null, Now.AddSeconds(1)));

        Assert.Equal(RequestState.Filling, tracker.Get(Release)!.State);
        Assert.Equal(RequestState.Open, tracker.Get(Second)!.State);
    }

    [Theory]
    [InlineData(RequestState.Closed)]
    [InlineData(RequestState.Filled)]
    public void Reconcile_does_not_readd_terminal_entries_evicted_during_the_same_listing(RequestState state)
    {
        var tracker = new RequestTracker(new RequestTrackerOptions { Capacity = 1 });
        tracker.Observe(Release, null, null, Now);
        SetState(tracker, Release, state);

        tracker.Reconcile([Second, Release], Now.AddSeconds(1));

        Assert.Null(tracker.Get(Release));
        Assert.Equal(Second, Assert.Single(tracker.Snapshot()).Release);
    }

    [Fact]
    public void Snapshots_are_ordered_and_do_not_change_after_tracker_mutations()
    {
        var tracker = Unlimited();
        tracker.Observe(Second, null, null, Now.AddSeconds(1));
        tracker.Observe(Release, null, null, Now);
        var snapshot = tracker.Snapshot();
        var due = tracker.DueForSearch(Now.AddSeconds(1));
        var before = tracker.Get(Release)!;

        tracker.MarkFilledElsewhere(Release);
        tracker.Observe(Third, null, null, Now.AddSeconds(2));

        Assert.Equal(new[] { Release, Second }, snapshot.Select(request => request.Release));
        Assert.Equal(RequestState.Open, snapshot[0].State);
        Assert.Equal(before, due[0]);
        Assert.Equal(2, due.Count);
        Assert.Equal(RequestState.Open, before.State);
        Assert.Equal(RequestState.Closed, tracker.Get(Release)!.State);
    }

    [Fact]
    public void Concurrent_observations_fill_starts_and_snapshots_are_safe()
    {
        var tracker = Unlimited();
        var observations = 0;
        var fillStarts = 0;

        Parallel.For(0, 512, iteration =>
        {
            var release = $"Some.Show.S02E{iteration % 16:D2}.2160p.WEB.h265-GRP";
            if (iteration / 16 % 2 == 0)
                release = release.ToLowerInvariant();
            if (tracker.Observe(release, iteration, "#chan1", Now))
                Interlocked.Increment(ref observations);
            if (tracker.TryBeginFill(release, Now))
                Interlocked.Increment(ref fillStarts);
            Assert.InRange(tracker.Snapshot().Count, 1, 16);
        });

        Assert.Equal(16, observations);
        Assert.Equal(16, fillStarts);
        Assert.Equal(16, tracker.Snapshot().Count);
        Assert.All(tracker.Snapshot(), request =>
        {
            Assert.Equal(RequestState.Filling, request.State);
            Assert.Equal(1, request.FillAttempts);
        });
    }
}
