using System.Diagnostics;
using System.Text.Json;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Diagnostics;

public sealed record ApplicationLogEntry(long Sequence,DateTimeOffset At,string InstanceId,string Level,string Category,int EventId,string Message,string? ExceptionType,string? TraceId);
public sealed record LogSelection(string? Level=null,string? Category=null,string? Search=null,DateTimeOffset? From=null,DateTimeOffset? To=null,long? Before=null,int Limit=100);
public sealed record ApplicationLogPage(ApplicationLogEntry[] Items,string[] Categories,bool HasMore,long? NextBefore,int StoredCount,int MaxEntries,int RetentionDays,string StorageState,string? StorageError,DateTimeOffset? LastPersistedAt,string InstanceId);

/// <summary>Bounded per-server diagnostics. Memory reads and logger writes never wait for disk or DB.
/// A background snapshot uses the existing private persistent volume, not a new database schema.</summary>
public sealed class ApplicationLogStore
{
    public const int Capacity=5000, RetentionDays=7;
    private readonly object gate=new();private readonly Queue<ApplicationLogEntry> entries=new();
    private readonly SemaphoreSlim disk=new(1,1);private readonly string path;
    private long sequence,revision,persistedRevision;private string? storageError;private DateTimeOffset? lastPersisted;
    public ApplicationLogStore(string directory)
    {
        path=Path.Combine(directory,"system-logs.json");
        try
        {
            if(!File.Exists(path))return;
            if(new FileInfo(path).Length>32_000_000)throw new InvalidDataException();
            var saved=JsonDefaults.Read<ApplicationLogEntry[]>(File.ReadAllText(path));
            foreach(var entry in saved.OrderBy(entry=>entry.Sequence).TakeLast(Capacity))
            {
                if(entry.Sequence<1||entry.Category is null||entry.Message is null||entry.InstanceId is null||entry.Level is null||!Enum.TryParse<LogLevel>(entry.Level,out var level)||!Enum.IsDefined(level)||level==LogLevel.None)continue;
                sequence=Math.Max(sequence,entry.Sequence);
                if(entry.At<DateTimeOffset.UtcNow.AddDays(-RetentionDays)){revision++;continue;}
                entries.Enqueue(entry with{Message=LogPrivacy.Clean(entry.Message),Category=LogPrivacy.Clip(entry.Category,180),ExceptionType=entry.ExceptionType is null?null:LogPrivacy.Clip(entry.ExceptionType,120),TraceId=SafeTrace(entry.TraceId)});
            }
            lastPersisted=File.GetLastWriteTimeUtc(path);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {storageError=error.GetType().Name;}
    }
    public static string? SafeTrace(string? value)=>value is {Length:32}&&value.All(Uri.IsHexDigit)?value:null;
    public void Add(LogLevel level,string category,int eventId,string message,string? exceptionType=null,string? traceId=null)
    {
        if(level==LogLevel.None)return;
        var now=DateTimeOffset.UtcNow;
        lock(gate)
        {
            while(entries.TryPeek(out var old)&&(entries.Count>=Capacity||old.At<now.AddDays(-RetentionDays)))entries.Dequeue();
            entries.Enqueue(new(++sequence,now,ServerInstance.Id,level.ToString(),LogPrivacy.Clip(category,180),eventId,LogPrivacy.Clean(message),exceptionType,SafeTrace(traceId)));revision++;
        }
    }
    public ApplicationLogPage Read(LogSelection query,bool export=false)
    {
        if(query.Limit<1||query.Limit>(export?Capacity:500)||query.Search?.Length>200||query.Category?.Length>180||query.Before<1||query.From>query.To)
            throw new ArgumentException("מסנני הלוגים אינם תקינים. ניתן להציג עד 500 רשומות בעמוד.");
        var minimum=LogLevel.Trace;
        if(!string.IsNullOrEmpty(query.Level)&&(!Enum.TryParse(query.Level,true,out minimum)||!Enum.IsDefined(minimum)||minimum==LogLevel.None))throw new ArgumentException("רמת לוג אינה תקינה");
        ApplicationLogEntry[] all;string? failure;DateTimeOffset? persisted;
        lock(gate){all=entries.Where(entry=>entry.At>=DateTimeOffset.UtcNow.AddDays(-RetentionDays)).ToArray();failure=storageError;persisted=lastPersisted;}
        var matches=all.Reverse().Where(entry=>(int)Enum.Parse<LogLevel>(entry.Level)>=(int)minimum&&
            (string.IsNullOrEmpty(query.Category)||entry.Category.Equals(query.Category,StringComparison.Ordinal))&&
            (query.From is null||entry.At>=query.From)&&(query.To is null||entry.At<=query.To)&&(query.Before is null||entry.Sequence<query.Before)&&
            (string.IsNullOrWhiteSpace(query.Search)||string.Join(' ',entry.Message,entry.Category,entry.ExceptionType,entry.TraceId).Contains(query.Search.Trim(),StringComparison.OrdinalIgnoreCase)))
            .Take(query.Limit+1).ToArray();
        var more=matches.Length>query.Limit;var page=matches.Take(query.Limit).ToArray();
        return new(page,all.Select(entry=>entry.Category).Distinct().Order().ToArray(),more,more?page[^1].Sequence:null,all.Length,Capacity,RetentionDays,
            failure is not null?"memory-only":"persistent",failure,persisted,ServerInstance.Id);
    }
    public async Task PersistAsync(CancellationToken ct)
    {
        await disk.WaitAsync(ct);
        try
        {
            ApplicationLogEntry[] snapshot;long version;
            lock(gate)
            {
                while(entries.TryPeek(out var old)&&old.At<DateTimeOffset.UtcNow.AddDays(-RetentionDays)){entries.Dequeue();revision++;}
                if(revision==persistedRevision&&storageError is null)return;snapshot=entries.ToArray();version=revision;
            }
            var directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var temporary=path+".tmp";
            await using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous))
            {
                if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream,snapshot,JsonDefaults.Options,ct);await stream.FlushAsync(ct);
            }
            File.Move(temporary,path,true);
            lock(gate){persistedRevision=version;storageError=null;lastPersisted=DateTimeOffset.UtcNow;}
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {lock(gate)storageError=error.GetType().Name;}
        finally{disk.Release();}
    }
}
public sealed class ApplicationLogProvider(ApplicationLogStore store):ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)=>new JournalLogger(store,categoryName);
    public void Dispose(){}
    private sealed class JournalLogger(ApplicationLogStore store,string category):ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level!=LogLevel.None;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if(!IsEnabled(level))return;
            try{store.Add(level,category,eventId.Id,LogPrivacy.Message(state),exception?.GetType().Name,Activity.Current?.TraceId.ToString());}
            catch(Exception error)when(error is not OutOfMemoryException){/* Diagnostics must not crash the operation or recurse into logging. */}
        }
    }
}
public sealed class ApplicationLogWriter(ApplicationLogStore store):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        try{while(await timer.WaitForNextTickAsync(stoppingToken))await store.PersistAsync(stoppingToken);}
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await store.PersistAsync(cancellationToken);
    }
}
