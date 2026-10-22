using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Xunit;
using Xunit.Abstractions;
namespace CarWise.Tests;

// Explicitly opted in; never reads application secrets, production profiles or employee data.
public sealed class LiveConversationFactAttribute : FactAttribute
{
    public LiveConversationFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CARWISE_EVAL_AI_KEY")))
            Skip="Set CARWISE_EVAL_AI_KEY explicitly to run the paid synthetic conversation evaluation.";
    }
}
public sealed class LiveConversationTests(ITestOutputHelper output)
{
    [LiveConversationFact]
    public async Task LiveModelHandlesAConversationWithDetoursAndChangingItsMind()
    {
        var provider=Environment.GetEnvironmentVariable("CARWISE_EVAL_AI_PROVIDER")??"gemini";
        var endpoint=provider switch{"gemini"=>"https://generativelanguage.googleapis.com/v1beta/","openai"=>"https://api.openai.com/v1/","anthropic"=>"https://api.anthropic.com/v1/",_=>throw new ArgumentException("Use gemini, openai or anthropic")};
        var model=Environment.GetEnvironmentVariable("CARWISE_EVAL_AI_MODEL")??(provider switch{"gemini"=>"gemini-2.5-flash","openai"=>"gpt-4.1-mini",_=>"claude-sonnet-4-20250514"});
        using var http=new HttpClient{BaseAddress=new(endpoint),Timeout=TimeSpan.FromSeconds(45)};
        var client=new LiveClient(http,new(){Provider=provider,BaseUrl=endpoint,Model=model,MaxOutputTokens=8192},Environment.GetEnvironmentVariable("CARWISE_EVAL_AI_KEY")!);
        var db=new ConversationDatabase();
        var employee=ConversationWorkflowTests.Data.Employees[0] with{Id="synthetic-eval",Name="עובד בדיקה"};
        var service=ConversationWorkflowTests.Service(db,client);
        using var budget=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        async Task<JsonNode> Say(string text)
        {
            var reply=JsonNode.Parse(JsonDefaults.Serialize(await service.ChatAsync(new(employee,text,null,null),budget.Token)))!;
            output.WriteLine("Employee: "+text+"\nAdvisor: "+reply["reply"]);
            Assert.False(string.IsNullOrWhiteSpace(reply["reply"]!.ToString()));
            var updated=JsonDefaults.Read<Employee>(reply["employee"]!.ToJsonString());
            Assert.Equal(employee.Salary,updated.Salary);Assert.Equal(employee.Participation,updated.Participation);
            return reply;
        }
        var first=await Say("בא לי לבדוק רכב חשמלי. מה האפשרויות שלי?");
        Assert.NotEmpty(first["candidates"]!.AsArray());
        var offered=first["candidates"]!.AsArray().Select(id=>id!.ToString()).ToArray();
        var car=ConversationWorkflowTests.Data.Cars.Single(car=>car.Id==offered[0]);
        await Say("רגע לפני שאני בוחר, לאיזו קבוצת זכאות אני שייך?");
        var chosen=await Say("נחזור לרכבים, אני רוצה את "+car.Model);
        Assert.Equal(car.Id,chosen["target"]!.ToString());
        var other=await Say("בעצם אולי משהו אחר, תראה לי אפשרויות נוספות באותו כיוון");
        Assert.DoesNotContain(other["candidates"]!.AsArray(),id=>id!.ToString()==car.Id);
        var restored=await Say("התחרטתי. תחזיר אותי לאפשרות שבדקנו לפני כן");
        Assert.Equal(car.Id,restored["target"]!.ToString());
        await Say("נניח לרכבים רגע, איזה מחשב כדאי לקנות?");
        Assert.Empty(db.Tickets);
        output.WriteLine($"Provider requests: {client.Calls}; no production configuration or employee data used.");
    }
    private sealed class LiveClient(HttpClient http,AiProfile profile,string key):IAiToolClient
    {
        public int Calls {get;private set;}
        public async Task<AiToolTurn> CallToolsAsync(string instructions,IReadOnlyList<AiTool> tools,IReadOnlyList<AiToolMessage> messages,CancellationToken ct)
        {
            if(++Calls>24)throw new InvalidOperationException("Synthetic evaluation request budget exceeded");
            var raw=await NativeTools.SendAsync(http,profile,key,instructions,tools,messages,_=>{},ct);
            return JsonDefaults.Read<AiToolTurn>(raw);
        }
    }
}
