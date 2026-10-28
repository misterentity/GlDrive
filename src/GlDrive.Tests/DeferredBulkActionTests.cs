using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class DeferredBulkActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullUndoWindowPassesBeforeApplyingExactlyOnce()
    {
        using var pending = new DeferredBulkAction();
        var calls = 0;
        Assert.True(pending.TrySchedule("Remove selected history", Now, () => calls++));
        Assert.Equal(Now.AddSeconds(10), pending.DueAt);
        Assert.Equal("Remove selected history", pending.Description);
        Assert.False(pending.RunDue(Now.AddMilliseconds(9999)));
        Assert.Equal(0, calls);
        Assert.True(pending.RunDue(Now.AddSeconds(10)));
        Assert.False(pending.RunDue(Now.AddMinutes(1)));
        Assert.False(pending.Undo());
        Assert.Equal(1, calls);
        Assert.Null(pending.DueAt);
    }

    [Fact]
    public void UndoCancelsWithoutRestoringOrOverwritingConcurrentChanges()
    {
        using var pending = new DeferredBulkAction();
        var data = new List<string> { "selected" };
        pending.TrySchedule("Remove selected", Now, () => data.Remove("selected"));
        data.Add("new arrival");
        Assert.True(pending.Undo());
        Assert.False(pending.RunDue(Now.AddMinutes(1)));
        Assert.Equal(new[] { "selected", "new arrival" }, data);
        Assert.Null(pending.Description);
    }

    [Fact]
    public void SchedulingAnotherActionDoesNotDiscardTheFirstUndoOpportunity()
    {
        using var pending = new DeferredBulkAction();
        var first = 0;
        var second = 0;
        Assert.True(pending.TrySchedule("First", Now, () => first++));
        Assert.False(pending.TrySchedule("Second", Now, () => second++));
        Assert.Equal("First", pending.Description);
        Assert.True(pending.Undo());
        Assert.True(pending.TrySchedule("Second", Now, () => second++));
        pending.RunDue(Now.AddSeconds(10));
        Assert.Equal(0, first); Assert.Equal(1, second);
    }

    [Fact]
    public void FailedApplyIsNotRetriedAndDoesNotBlockLaterExplicitWork()
    {
        using var pending = new DeferredBulkAction();
        var attempts = 0;
        pending.TrySchedule("Failing action", Now, () => { attempts++; throw new InvalidOperationException(); });
        Assert.Throws<InvalidOperationException>(() => pending.RunDue(Now.AddSeconds(10)));
        Assert.False(pending.RunDue(Now.AddMinutes(1)));
        Assert.Equal(1, attempts);
        Assert.True(pending.TrySchedule("Next", Now, () => { }));
    }

    [Fact]
    public void ClosingTheOwnerCancelsPendingWorkAndRejectsLateSchedules()
    {
        var pending = new DeferredBulkAction();
        var calls = 0;
        pending.TrySchedule("Pending", Now, () => calls++);
        pending.Dispose(); pending.Dispose();
        Assert.False(pending.RunDue(Now.AddSeconds(10)));
        Assert.False(pending.TrySchedule("Late", Now, () => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RacingTimersCannotApplyTwice()
    {
        using var pending = new DeferredBulkAction();
        var calls = 0;
        pending.TrySchedule("Once", Now, () => Interlocked.Increment(ref calls));
        Parallel.For(0, 32, _ => pending.RunDue(Now.AddSeconds(10)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ConcurrentUndoAndExecutionHaveOneOutcome()
    {
        using var pending = new DeferredBulkAction();
        var applied = 0;
        var undone = 0;
        pending.TrySchedule("Race", Now, () => applied++);
        Parallel.Invoke(() => pending.RunDue(Now.AddSeconds(10)), () => { if (pending.Undo()) undone++; });
        Assert.Equal(1, applied + undone);
        Assert.Null(pending.Description);
    }
}
