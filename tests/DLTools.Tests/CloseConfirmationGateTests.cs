using Xunit;
using 烧录上位机程序.Services;

namespace DLTools.Tests;

public sealed class CloseConfirmationGateTests
{
    [Fact]
    public async Task PendingCloseQuestion_HoldsPackagePromptUntilUserDeclinesExit()
    {
        var gate = new CloseConfirmationGate();
        Assert.True(await gate.WaitAsync());
        Assert.True(gate.TryBegin());
        var prompt = gate.WaitAsync();
        Assert.True(gate.IsPending);
        Assert.False(prompt.IsCompleted);
        Assert.False(gate.TryBegin());
        gate.Complete(allowFurtherPrompts: true);
        Assert.True(await prompt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.IsPending);
        Assert.True(gate.TryBegin());
        gate.Complete(true);
    }

    [Fact]
    public async Task AcceptedExit_RejectsWaitingAndFuturePackagePrompts()
    {
        var gate = new CloseConfirmationGate();
        Assert.True(gate.TryBegin());
        var prompt = gate.WaitAsync();
        gate.Complete(allowFurtherPrompts: false);
        Assert.False(await prompt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await gate.WaitAsync());
        Assert.False(gate.TryBegin());
    }

    [Fact]
    public async Task CancelledWait_DoesNotCancelCloseDecisionForOtherWaiters()
    {
        var gate = new CloseConfirmationGate();
        gate.TryBegin();
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cancellation.Token);
        var other = gate.WaitAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(other.IsCompleted);
        gate.Complete(true);
        Assert.True(await other);
    }

    [Fact]
    public async Task CancellationBeforeExitDecision_UnblocksPromptWithoutDeadlockingShutdown()
    {
        var gate = new CloseConfirmationGate();
        gate.TryBegin();
        using var cancellation = new CancellationTokenSource();
        var prompt = gate.WaitAsync(cancellation.Token);
        cancellation.Cancel(); // MainWindow requests cancellation before releasing the decision gate.
        gate.Complete(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt);
        Assert.False(await gate.WaitAsync());
    }
}
