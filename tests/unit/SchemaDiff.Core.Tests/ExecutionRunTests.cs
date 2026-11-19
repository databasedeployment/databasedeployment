using SchemaDiff.Core.Execution;

namespace SchemaDiff.Core.Tests;

public class ExecutionRunTests
{
    [Fact]
    public void Messages_are_read_incrementally()
    {
        var run = new ExecutionRun();
        run.Push(ExecutionMessage.Info("one"));
        run.Push(ExecutionMessage.Info("two"));

        Assert.Equal(2, run.MessagesFrom(0).Count);
        Assert.Equal("two", Assert.Single(run.MessagesFrom(1)).Text);
        Assert.Empty(run.MessagesFrom(5));
    }

    [Fact]
    public void Progress_and_completion_are_tracked()
    {
        var run = new ExecutionRun();
        run.SetTotal(3);
        run.BatchDone();
        run.Complete(success: true);

        Assert.Equal(3, run.TotalBatches);
        Assert.Equal(1, run.DoneBatches);
        Assert.True(run.Finished);
        Assert.True(run.Success);
    }

    [Fact]
    public async Task Waiting_listeners_are_woken_by_a_new_message()
    {
        var run = new ExecutionRun();
        var changed = run.Changed;

        run.Push(ExecutionMessage.Info("hello"));

        await changed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(changed.IsCompleted);
    }

    [Fact]
    public void Cancel_signals_the_token_once_and_is_ignored_after_completion()
    {
        var run = new ExecutionRun();
        run.Cancel();
        Assert.True(run.Token.IsCancellationRequested);
        Assert.True(run.Cancelled);

        var finished = new ExecutionRun();
        finished.Complete(success: true);
        finished.Cancel();
        Assert.False(finished.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_connection_failure_is_reported_as_a_message_not_thrown()
    {
        var run = new ExecutionRun();

        await ScriptExecutor.RunAsync("Data Source=tcp:127.0.0.1,1;Connect Timeout=2;Integrated Security=false;User ID=x;Password=y;Encrypt=false",
            "SELECT 1", run, CancellationToken.None);

        Assert.True(run.Finished);
        Assert.False(run.Success);
        Assert.Contains(run.MessagesFrom(0), m => m.Kind == "error");
    }

    [Fact]
    public async Task An_empty_script_completes_without_connecting()
    {
        var run = new ExecutionRun();

        await ScriptExecutor.RunAsync("Data Source=nowhere", "  \nGO\n", run, CancellationToken.None);

        Assert.True(run.Success);
        Assert.Equal("execNoStatements", Assert.Single(run.MessagesFrom(0)).TranslationKey);
    }
}
