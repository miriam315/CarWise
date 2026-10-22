using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class ScriptedToolClient(params Func<IReadOnlyList<AiToolMessage>,AiToolTurn>[] turns):IAiToolClient
{
    public int Calls {get;private set;}
    public Task<AiToolTurn> CallToolsAsync(string instructions,IReadOnlyList<AiTool> tools,IReadOnlyList<AiToolMessage> messages,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();Assert.Contains("native tool-calling",instructions);return Task.FromResult(turns[Calls++](messages));}
}
public sealed class AdvisorToolTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AiToolTurn Call(string name,JsonObject? args=null,string id="call1")=>new([new(id,name,JsonNode.Parse((args??new()).ToJsonString())!.AsObject())],"",new JsonObject{["role"]="assistant"});
    private static AiToolTurn Finish(string text)=>new([],text,new JsonObject{["role"]="assistant"});
    private static AdvisorService Advisor(ConversationDatabase db,IAiToolClient client)=>new(Data,new(Data),new(db),new NoLegacyExtraction(),new TemplateNarrationService(),tools:new(client,Data,new(Data),new(db),NullLogger<AdvisorToolConversation>.Instance));
    private sealed class NoLegacyExtraction:IDataExtractionService
    {public string Mode=>"ai";public Task<ExtractedData> ExtractAsync(string t,Employee e,IReadOnlyList<Message> h,CancellationToken ct)=>throw new Xunit.Sdk.XunitException("Legacy extraction must not handle a successful native tool conversation");}
    private static async Task<JsonNode> Chat(AdvisorService advisor,Employee employee,string text)=>JsonNode.Parse(JsonDefaults.Serialize(await advisor.ChatAsync(new(employee,text,null,null),default)))!;
    [Fact] public async Task GroupBudgetAndFollowupUseToolsAuthoritativeDataAndServerHistory()
    {
        var employee=Data.Employees[0];var db=new ConversationDatabase();
        var client=new ScriptedToolClient(_=>Call("get_my_eligibility"),m=>{Assert.True(m.Last().Result!["ok"]!.GetValue<bool>());return Finish("בשמחה. {FACT_0}");},
            m=>{Assert.Contains(m,x=>x.Text?.Contains("לאיזה קבוצה")==true);Assert.DoesNotContain(employee.Id,string.Join("",m.Select(x=>x.Text)));return Call("get_policy",new(){["topic"]="budget"});},_=>Finish("{FACT_0}"));
        var advisor=Advisor(db,client);
        var first=await Chat(advisor,employee,"לאיזה קבוצה אני שייכת?");Assert.Contains(employee.Occupation,first["reply"]!.ToString());Assert.Contains("טבלת התקציבים",first["reply"]!.ToString());
        var next=await Chat(advisor,employee,"ומה התקציב?");Assert.Contains("התקציב",next["reply"]!.ToString());Assert.Equal(4,db.Messages.Count);Assert.Equal(4,client.Calls);
    }
    [Fact] public async Task SearchThenCompareUsesActualEngineAndStoresOnePairOfChatMessages()
    {
        var employee=Data.Employees[0];var db=new ConversationDatabase();
        var client=new ScriptedToolClient(_=>Call("find_vehicles",new(){["query"]="ATTO",["drive"]="electric"}),
            m=>{Assert.Contains("CAR-10",m.Last().Result!.ToJsonString());return Call("compare_vehicle",new(){["target"]="CAR-10"},"compare");},_=>Finish("{FACT_0}\n{FACT_1}"));
        var result=await Chat(Advisor(db,client),employee,"מה יקרה לנטו אם אעבור ל־ATTO 3?");
        Assert.Equal("CAR-10",result["target"]!.ToString());Assert.Contains("הנטו המשוער",result["reply"]!.ToString());Assert.Contains("מקור ההשתתפות",result["reply"]!.ToString());Assert.Equal(2,db.Messages.Count);
        Assert.DoesNotContain("מזהה לבחירה",result["reply"]!.ToString());
    }
    [Theory][InlineData("get_my_eligibility")][InlineData("delete_employee")]
    public async Task UnknownToolsAndEmployeeIdInjectionCannotAccessAnotherEmployee(string tool)
    {
        var db=new ConversationDatabase();var client=new ScriptedToolClient(_=>Call(tool,new(){["employeeId"]="E-OTHER"}),m=>{
            Assert.False(m.Last().Result!["ok"]!.GetValue<bool>());Assert.DoesNotContain("E-OTHER",m.Last().Result!.ToJsonString());return Finish("I accessed another employee");});
        var result=await Chat(Advisor(db,client),Data.Employees[0],"מה הקבוצה שלי?");Assert.DoesNotContain("another employee",result["reply"]!.ToString());Assert.Empty(db.Tickets);
    }
    [Fact] public async Task InventedPayrollAndDuplicateMutationsAreBlocked()
    {
        JsonObject Fields(int salary)=>new(){["fields"]=new JsonObject{["salary"]=salary,["commission"]=null,["bonus"]=null,["pension"]=null,["fund"]=null,["credits"]=null,["children"]=null}};
        var db=new ConversationDatabase();var client=new ScriptedToolClient(_=>Call("set_payroll_fields",Fields(99999)),m=>{Assert.False(m.Last().Result!["ok"]!.GetValue<bool>());return Call("set_payroll_fields",Fields(19000));},
            m=>{Assert.True(m.Last().Result!["ok"]!.GetValue<bool>());return Call("set_current_participation",new(){["amount"]=300});},m=>{Assert.False(m.Last().Result!["ok"]!.GetValue<bool>());return Finish("{FACT_0}");});
        var result=await Chat(Advisor(db,client),Data.Employees[0],"השכר שלי 19000 וההשתתפות שלי 300");
        Assert.Equal(19000,result["employee"]!["salary"]!.GetValue<decimal>());Assert.Equal(Data.Employees[0].Participation,result["employee"]!["participation"]!.GetValue<decimal>());Assert.Equal(2,db.Messages.Count);
    }
    [Fact] public async Task MissingPersonalParticipationCreatesRealOwnTicketAndNeverInventsNet()
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(f=>f!="participation")]};var db=new ConversationDatabase();
        var client=new ScriptedToolClient(_=>Call("ask_representative",new(){["target"]="current",["reason"]="current_participation"}),_=>Finish("{FACT_0}"));
        var response=await Chat(Advisor(db,client),employee,"לא יודעת מה ההשתתפות שלי ברכב הנוכחי, שאל נציג");
        Assert.Equal(employee.Id,Assert.Single(await new Repository(db).TicketsAsync(employee.Id)).EmployeeId);Assert.Equal("participation",response["awaitingField"]!.ToString());
        Assert.Contains("לא יוצג נטו סופי",response["reply"]!.ToString());Assert.False(response["complete"]!.GetValue<bool>());
    }
    [Fact] public async Task UnrelatedAnswerAndFabricatedFinalNumbersAreNotDisplayed()
    {
        var client=new ScriptedToolClient(_=>Call("decline_unrelated"),_=>Finish("הנה מתכון עם 99999 גרם"));
        var result=await Chat(Advisor(new(),client),Data.Employees[0],"ספר לי על כוכבים");Assert.Contains("ייעוץ",result["reply"]!.ToString());Assert.DoesNotContain("99999",result["reply"]!.ToString());Assert.DoesNotContain("מתכון",result["reply"]!.ToString());
    }
    [Fact] public async Task GuessedVehicleIdsAndUnnecessaryApprovalCannotReplaceDocumentedCatalogueData()
    {
        var db=new ConversationDatabase();var client=new ScriptedToolClient(_=>Call("compare_vehicle",new(){["target"]="CAR-10"}),
            m=>{Assert.False(m.Last().Result!["ok"]!.GetValue<bool>());return Call("find_vehicles",new(){["query"]="ATTO",["drive"]="electric"});},
            _=>Call("ask_representative",new(){["target"]="CAR-10",["reason"]="alternative_participation"}),
            m=>{Assert.False(m.Last().Result!["ok"]!.GetValue<bool>());return Finish("{FACT_0}");});
        await Chat(Advisor(db,client),Data.Employees[0],"השווה ATTO 3");Assert.Empty(await new Repository(db).TicketsAsync(Data.Employees[0].Id));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AlternativeContributionAndDisputeReachTheExistingFinancialWorkflow(bool dispute)
    {
        var db=new ConversationDatabase();var client=new ScriptedToolClient(_=>Call("find_vehicles",new(){["query"]="ATTO",["drive"]="electric"}),
            _=>dispute?Call("ask_representative",new(){["target"]="CAR-10",["reason"]="alternative_participation"}):Call("report_alternative_participation",new(){["target"]="CAR-10",["amount"]=0}),_=>Finish("{FACT_0} {FACT_1}"));
        var response=await Chat(Advisor(db,client),Data.Employees[0],dispute?"ההשתתפות ברכב החדש ATTO שונה, אני רוצה אישור נציג":"ההשתתפות שלי ברכב החדש ATTO היא 0");
        var repository=new Repository(db);var (overrides,_,sources)=await repository.ParticipationWithSourcesAsync(Data.Employees[0].Id,default);
        if(dispute){Assert.Single(await repository.TicketsAsync(Data.Employees[0].Id));Assert.Contains("ממתינה לאישור",response["reply"]!.ToString());}
        else{Assert.Equal(0,overrides["CAR-10"]);Assert.Equal("employee",sources["CAR-10"]);}
    }
    [Fact] public async Task RepeatedReadLoopStopsWithinBudgetAndReturnsVerifiedFacts()
    {
        var client=new ScriptedToolClient(Enumerable.Range(0,6).Select<int,Func<IReadOnlyList<AiToolMessage>,AiToolTurn>>(i=>_=>Call("get_my_eligibility",id:"read"+i)).ToArray());
        var result=await Chat(Advisor(new(),client),Data.Employees[0],"מה הקבוצה שלי?");Assert.Equal(6,client.Calls);Assert.Contains("קבוצת הזכאות",result["reply"]!.ToString());
    }
    [Fact] public async Task RepeatedSameWriteUsesCachedResultAndDoesNotDuplicateChatOrTicket()
    {
        var db=new ConversationDatabase();var args=new JsonObject{["target"]="general",["reason"]="general"};
        var client=new ScriptedToolClient(_=>Call("ask_representative",args),_=>Call("ask_representative",args.DeepClone().AsObject(),"retry"),_=>Finish("{FACT_0}"));
        var result=await Chat(Advisor(db,client),Data.Employees[0],"רוצה לברר עם נציג מדיניות רכב שלא ידועה");Assert.Single(await new Repository(db).TicketsAsync(Data.Employees[0].Id));Assert.Equal(2,db.Messages.Count);
        Assert.Contains("פתחתי פנייה",result["reply"]!.ToString());
    }
}
