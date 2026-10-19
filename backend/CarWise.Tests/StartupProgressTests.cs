using CarWise.Api.Infrastructure;
using Xunit;
namespace CarWise.Tests;

public sealed class StartupProgressTests
{
    [Fact] public async Task SlowStartupReportsProgressAndStopsAtItsDeadline()
    {
        var messages=new List<string>();var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(()=>StartupProgress.RunAsync("database",async ct=>{
            try{await Task.Delay(Timeout.Infinite,ct);return true;}
            finally{canceled.TrySetResult();}
        },TimeSpan.FromMilliseconds(250),messages.Add,TimeSpan.FromMilliseconds(20)));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(messages,m=>m.Contains("still working"));Assert.DoesNotContain(messages,m=>m.Contains("completed"));
    }
    [Fact] public async Task DeadlineIsBoundedEvenIfAnOperationIgnoresCancellation()
    {
        var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try{await Assert.ThrowsAsync<TimeoutException>(()=>StartupProgress.RunAsync("database",_=>pending.Task,TimeSpan.FromMilliseconds(30),_=>{}));}
        finally{pending.TrySetResult(true);}
    }
    [Fact] public async Task FastStartupCompletesAndDoesNotLeaveAHeartbeatRunning()
    {
        var messages=new List<string>();Assert.Equal(42,await StartupProgress.RunAsync("database",_=>Task.FromResult(42),TimeSpan.FromSeconds(2),messages.Add));
        Assert.Equal(2,messages.Count);Assert.Contains("completed",messages[1]);
    }
}

public sealed class SupabaseStartupRetryTests
{
    [Fact] public async Task ReadTimeoutRetriesOnceAndThenReturnsTheResult()
    {
        var calls=0;using var http=new HttpClient(new FakeHttp(_=>{
            if(++calls==1)throw new TaskCanceledException("test timeout");
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent("[]")};
        })){BaseAddress=new("https://example.invalid/")};
        Assert.Empty(await new SupabaseDatabase(http).ReadAsync("carwise_official"));Assert.Equal(2,calls);
    }
    [Fact] public async Task RepeatedReadTimeoutIsBoundedAndWritesAreNeverRetried()
    {
        var calls=0;using var http=new HttpClient(new FakeHttp(_=>{calls++;throw new TaskCanceledException("test timeout");})){BaseAddress=new("https://example.invalid/")};
        var db=new SupabaseDatabase(http);
        await Assert.ThrowsAsync<TaskCanceledException>(()=>db.ReadAsync("carwise_official"));Assert.Equal(2,calls);
        calls=0;await Assert.ThrowsAsync<TaskCanceledException>(()=>db.UpsertAsync("carwise_rules",new()));Assert.Equal(1,calls);
    }
    [Fact] public async Task CallerCancellationMakesNoRequest()
    {
        var calls=0;using var http=new HttpClient(new FakeHttp(_=>{calls++;return new(System.Net.HttpStatusCode.OK);})){BaseAddress=new("https://example.invalid/")};
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new SupabaseDatabase(http).ReadAsync("carwise_official",ct:canceled.Token));Assert.Equal(0,calls);
    }
}
