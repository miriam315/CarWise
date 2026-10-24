using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace CarWise.Tests;

public sealed class ChatHistoryTests
{
    private sealed class HistoryDatabase:IJsonDatabase
    {
        public string Provider=>"test";public int Reads;public int Writes;
        private readonly Employee employee=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data")).Employees[0] with{ChatHistoryStart=60};
        public string EmployeeId=>employee.Id;
        public Task<JsonArray> ReadAsync(string table,IReadOnlyDictionary<string,string>? filters=null,int offset=0,int limit=1000,CancellationToken ct=default,string? afterId=null)
        {
            Reads++;Assert.Equal(employee.Id,filters!["employee_id"]);
            if(table=="carwise_conversations")return Task.FromResult(new JsonArray(new JsonObject{["employee"]=JsonNode.Parse(JsonDefaults.Serialize(employee)),["status"]="Active"}));
            Assert.Equal("carwise_messages",table);Assert.InRange(limit,1,1000);
            return Task.FromResult(new JsonArray(Enumerable.Range(offset,Math.Min(limit,63-offset)).Select(index=>(JsonNode)new JsonObject{
                ["role"]=index%2==0?"user":"assistant",["text"]="message "+index,["created"]=index==59?null:index<50?"2026-10-23T20:59:59Z":"2026-10-23T21:00:00Z"}).ToArray()));
        }
        public Task UpsertAsync(string table,JsonArray rows,CancellationToken ct=default){Writes++;throw new NotSupportedException();}
        public Task<JsonNode> RpcAsync(string name,JsonObject parameters,CancellationToken ct=default){Writes++;throw new NotSupportedException();}
        public Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default){Writes++;throw new NotSupportedException();}
        public Task<bool> TryWriteRuleAsync(string id,long? revision,JsonNode document,CancellationToken ct=default){Writes++;throw new NotSupportedException();}
    }
    [Fact] public async Task OldMessagesArePagedWithoutCurrentConversationWritesOrUnboundedReads()
    {
        var db=new HistoryDatabase();var repository=new Repository(db);
        var latest=await repository.ArchivedHistoryAsync(db.EmployeeId,60,null,50,default);
        Assert.Equal(50,latest.Items.Length);Assert.Equal(10,latest.Items[0].Index);Assert.Equal(59,latest.Items[^1].Index);Assert.Equal(10,latest.NextBefore);Assert.Equal(60,latest.Total);
        Assert.All(latest.Items[..^1],item=>Assert.NotNull(item.At));Assert.Null(latest.Items[^1].At);
        Assert.Equal(DateTimeOffset.Parse("2026-10-23T20:59:59Z"),latest.Items[0].At);
        Assert.Equal(DateTimeOffset.Parse("2026-10-23T21:00:00Z"),latest.Items[^2].At);
        var older=await repository.ArchivedHistoryAsync(db.EmployeeId,60,latest.NextBefore,50,default);
        Assert.Equal(10,older.Items.Length);Assert.Equal(0,older.Items[0].Index);Assert.Equal(9,older.Items[^1].Index);Assert.Null(older.NextBefore);
        Assert.Empty((await repository.ArchivedHistoryAsync(db.EmployeeId,0,null,50,default)).Items);Assert.Equal(2,db.Reads);Assert.Equal(0,db.Writes);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.ArchivedHistoryAsync(db.EmployeeId,60,null,101,default));
    }
    [Fact] public async Task ActiveHistoryKeepsDatabaseTimestampsAndDoesNotInventMissingDates()
    {
        var db=new HistoryDatabase();var messages=await new Repository(db).HistoryAsync(db.EmployeeId,default,skip:58);
        Assert.Equal(5,messages.Count);Assert.Equal(DateTimeOffset.Parse("2026-10-23T21:00:00Z"),messages[0].At);
        Assert.Null(messages[1].At);Assert.NotNull(messages[2].At);
        var json=JsonNode.Parse(JsonDefaults.Serialize(messages))!.AsArray();Assert.NotNull(json[0]!["at"]);Assert.Null(json[1]!["at"]);
    }
    [Fact] public async Task ActualArchiveEndpointRequiresEmployeeOwnershipAndDoesNotExposeAnotherEmployeeToAgent()
    {
        var db=new HistoryDatabase();var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new Repository(db));using var auth=new HttpClient();builder.Services.AddSingleton(new AccessControl(auth,false));
        await using var app=builder.Build();
        app.Use(async(context,next)=>{
            var caller=context.Request.Headers["X-Test-Employee"].ToString();
            if(caller.Length>0)context.Items["caller"]=new Caller("test",caller,true);
            try{await next(context);}catch(UnauthorizedAccessException){context.Response.StatusCode=403;}catch(ArgumentException){context.Response.StatusCode=400;}
        });app.MapChatHistory();await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
        using var anonymous=await client.GetAsync("/api/history/"+db.EmployeeId+"/archive");Assert.Equal(HttpStatusCode.Forbidden,anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Employee","OTHER-EMPLOYEE");using var other=await client.GetAsync("/api/history/"+db.EmployeeId+"/archive");Assert.Equal(HttpStatusCode.Forbidden,other.StatusCode);Assert.Equal(0,db.Reads);
        client.DefaultRequestHeaders.Remove("X-Test-Employee");client.DefaultRequestHeaders.Add("X-Test-Employee",db.EmployeeId);
        using var allowed=await client.GetAsync("/api/history/"+db.EmployeeId+"/archive?limit=10");Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        var page=JsonNode.Parse(await allowed.Content.ReadAsStringAsync())!;Assert.Equal(10,page["items"]!.AsArray().Count);Assert.Equal(50,page["nextBefore"]!.GetValue<int>());
        using var invalid=await client.GetAsync("/api/history/"+db.EmployeeId+"/archive?before=-1");Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);Assert.Equal(0,db.Writes);
        await app.StopAsync();
    }
}
