using System.Diagnostics;
namespace CarWise.Api.Infrastructure;

public static class StartupProgress
{
    public static async Task<T> RunAsync<T>(string phase,Func<CancellationToken,Task<T>> operation,TimeSpan timeout,
        Action<string>? report=null,TimeSpan? heartbeat=null)
    {
        report??=Console.WriteLine;var elapsed=Stopwatch.StartNew();
        report($"[CarWise] {phase} (maximum wait: {timeout.TotalSeconds:0}s)");
        using var deadline=new CancellationTokenSource(timeout);
        async Task ProgressAsync()
        {
            using var timer=new PeriodicTimer(heartbeat??TimeSpan.FromSeconds(10));
            try{while(await timer.WaitForNextTickAsync(deadline.Token))report($"[CarWise] {phase}: still working, {elapsed.Elapsed.TotalSeconds:0}s elapsed...");}
            catch(OperationCanceledException)when(deadline.IsCancellationRequested){ }
        }
        var progress=ProgressAsync();
        try
        {
            var value=await operation(deadline.Token).WaitAsync(deadline.Token);
            report($"[CarWise] {phase}: completed in {elapsed.Elapsed.TotalSeconds:0.0}s");return value;
        }
        catch(OperationCanceledException)when(deadline.IsCancellationRequested)
        {throw new TimeoutException($"{phase} exceeded {timeout.TotalSeconds:0}s. Check the database connection and network, then restart CarWise.");}
        finally{await deadline.CancelAsync();await progress;}
    }
}
