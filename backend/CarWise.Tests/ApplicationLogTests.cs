using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Diagnostics;
using CarWise.Api.Domain;
using Microsoft.Extensions.Logging;
using Xunit;
namespace CarWise.Tests;

public sealed class ApplicationLogTests
{
    private static string DirectoryForTest()=>Path.Combine(Path.GetTempPath(),"carwise-log-test-"+Guid.NewGuid().ToString("N"));
    [Fact] public async Task StructuredDiagnosticsSurviveRestartWithoutCredentialsPersonalValuesBodiesOrExceptionMessages()
    {
        var path=DirectoryForTest();
        try
        {
            var store=new ApplicationLogStore(path);using var factory=LoggerFactory.Create(logging=>logging.AddProvider(new ApplicationLogProvider(store)));var logger=factory.CreateLogger("CarWise.AI");
            using var activity=new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
            logger.LogWarning(new InvalidOperationException("PRIVATE-DOCUMENT-TEXT sk-secret123456789"),"AI {Operation} {Model} {Code}; {ApiKey} {EmployeeName} {Salary} {Prompt} {Body}",
                "extract_payslip","gemini-test","output_limit","sk-secret123456789","Private Person",98765,"PRIVATE-PROMPT","PRIVATE-DOCUMENT-TEXT");
            logger.LogInformation("Sending HTTP request {HttpMethod} {Uri}","GET","https://person:password@example.test/path?key=PRIVATE-QUERY&employee=PrivatePerson");
            logger.LogInformation("PRIVATE RAW TEXT Private Person 98765");
            var page=store.Read(new());Assert.Equal(3,page.Items.Length);
            var warning=page.Items.Single(entry=>entry.Level=="Warning");Assert.Contains("output_limit",warning.Message);Assert.Contains("gemini-test",warning.Message);Assert.Equal(activity.TraceId.ToString(),warning.TraceId);Assert.Equal("InvalidOperationException",warning.ExceptionType);
            await store.PersistAsync(default);
            var disk=await File.ReadAllTextAsync(Path.Combine(path,"system-logs.json"));
            foreach(var secret in new[]{"sk-secret123456789","Private Person","98765","PRIVATE-PROMPT","PRIVATE-DOCUMENT-TEXT","PRIVATE-QUERY","person:password","PRIVATE RAW TEXT"})Assert.DoesNotContain(secret,disk);
            var restored=new ApplicationLogStore(path).Read(new());Assert.Equal(page.Items.Select(entry=>entry.Sequence),restored.Items.Select(entry=>entry.Sequence));Assert.Contains(restored.Items,entry=>entry.Message.Contains("output_limit"));
            if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(Path.Combine(path,"system-logs.json")));
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact] public void PaginationIsStableWhileNewEventsArriveAndFiltersDoNotAlterTheStore()
    {
        var store=new ApplicationLogStore(DirectoryForTest());
        for(var i=0;i<10;i++)store.Add(i%2==0?LogLevel.Warning:LogLevel.Information,"CarWise.Test",i,"operation "+i);
        var first=store.Read(new(Limit:3));Assert.Equal(new long[]{10,9,8},first.Items.Select(entry=>entry.Sequence));Assert.True(first.HasMore);
        store.Add(LogLevel.Error,"CarWise.Other",55,"another failure");
        var second=store.Read(new(Before:first.NextBefore,Limit:3));Assert.Equal(new long[]{7,6,5},second.Items.Select(entry=>entry.Sequence));
        var filtered=store.Read(new(Level:"Warning",Category:"CarWise.Test",Search:"operation"));Assert.Equal(5,filtered.Items.Length);Assert.All(filtered.Items,entry=>Assert.Equal("Warning",entry.Level));Assert.Equal(11,filtered.StoredCount);
        Assert.Empty(store.Read(new(From:DateTimeOffset.UtcNow.AddMinutes(1))).Items);
    }
    [Fact] public async Task RetentionAndCapacityAreBoundedAndAtomicSnapshotsRemainReadableDuringConcurrentLogging()
    {
        var path=DirectoryForTest();
        try
        {
            var store=new ApplicationLogStore(path);
            await Task.WhenAll(Enumerable.Range(0,8).Select(worker=>Task.Run(()=>{for(var i=0;i<700;i++)store.Add(LogLevel.Information,"Concurrent",i,"worker "+worker);})));
            await store.PersistAsync(default);var result=store.Read(new(Limit:500));Assert.Equal(ApplicationLogStore.Capacity,result.StoredCount);Assert.Equal(500,result.Items.Length);Assert.Equal(5600,result.Items[0].Sequence);
            var restored=new ApplicationLogStore(path);Assert.Equal(ApplicationLogStore.Capacity,restored.Read(new()).StoredCount);restored.Add(LogLevel.Error,"AfterRestart",0,"new record");Assert.Equal(5601,restored.Read(new()).Items[0].Sequence);
            var old=new ApplicationLogEntry(1,DateTimeOffset.UtcNow.AddDays(-8),"old","Warning","Old",0,"old record",null,null);
            await File.WriteAllTextAsync(Path.Combine(path,"system-logs.json"),JsonDefaults.Serialize(new[]{old,old with{Sequence=2,At=DateTimeOffset.UtcNow,Level="made-up"},old with{Sequence=3,At=DateTimeOffset.UtcNow,Message="recent"}}));
            Assert.Single(new ApplicationLogStore(path).Read(new()).Items);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact] public async Task StorageFailureDoesNotBreakLoggingAndRecoveryPersistsTheBufferedEvents()
    {
        var path=DirectoryForTest();await File.WriteAllTextAsync(path,"not a directory");
        try
        {
            var store=new ApplicationLogStore(path);store.Add(LogLevel.Error,"CarWise.Test",0,"operation failed");await store.PersistAsync(default);
            Assert.Equal("memory-only",store.Read(new()).StorageState);Assert.Single(store.Read(new()).Items);
            File.Delete(path);await store.PersistAsync(default);Assert.Equal("persistent",store.Read(new()).StorageState);Assert.Single(new ApplicationLogStore(path).Read(new()).Items);
        }
        finally{if(File.Exists(path))File.Delete(path);if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Theory][InlineData("Bearer abcdefghijklmnop")][InlineData("sk-proj-PRIVATE-KEY-123456")][InlineData("AIzaPRIVATE-KEY-123456")][InlineData("sb_secret_PRIVATE-KEY-123456")][InlineData("password=PRIVATE")][InlineData("private@example.test")]
    public void SensitivePatternsAreRedacted(string secret)=>Assert.DoesNotContain(secret,LogPrivacy.Clean(secret));
    [Theory][InlineData(0)][InlineData(501)][InlineData(-1)]
    public void InvalidPageSizesAreRejected(int limit)=>Assert.Throws<ArgumentException>(()=>new ApplicationLogStore(DirectoryForTest()).Read(new(Limit:limit)));
    [Fact] public void InvalidFiltersAreRejected()
    {
        var store=new ApplicationLogStore(DirectoryForTest());Assert.Throws<ArgumentException>(()=>store.Read(new(Level:"not-a-level")));Assert.Throws<ArgumentException>(()=>store.Read(new(Search:new string('a',201))));Assert.Throws<ArgumentException>(()=>store.Read(new(From:DateTimeOffset.UtcNow,To:DateTimeOffset.UtcNow.AddDays(-1))));
    }
}
