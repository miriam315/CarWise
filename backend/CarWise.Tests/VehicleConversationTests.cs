using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;

public sealed class VehicleConversationTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AdvisorService Service(ConversationDatabase db,IAiToolClient client)=>new(Data,new(Data),new(db),new LocalExtractionService(),new TemplateNarrationService(),tools:new(client,Data,new(Data),new(db),NullLogger<AdvisorToolConversation>.Instance));
    private static async Task<JsonNode> Chat(AdvisorService service,Employee employee,string text)=>JsonNode.Parse(JsonDefaults.Serialize(await service.ChatAsync(new(employee,text,null,null),default)))!;
    [Theory][InlineData("BYD ATTO 3")][InlineData("BYD Atto3")][InlineData("ATTO-3")][InlineData("אטו 3")][InlineData("BYD אטו3")]
    public void BrandSpacingAndHebrewNamesResolveOnlyToCatalogueRows(string name)=>Assert.Equal("CAR-10",Assert.Single(VehicleSearch.Find(name,Data.Cars)).Id);
    [Fact] public void FromToSeparatesCurrentAndTargetAndDoesNotConfuseCorollaWithCross()
    {
        var (targets,sources)=VehicleSearch.Resolve(Data.Employees[0].Question,Data.Cars);
        Assert.Equal("CAR-10",Assert.Single(targets).Id);Assert.Equal("CAR-09",Assert.Single(sources).Id);
        Assert.Equal(2,VehicleSearch.Find("COROLLA מול COROLLA CROSS",Data.Cars).Count);
    }
    [Theory][InlineData("אם אעבור מהקורולה קרוס ל-BYD Atto 3 החשמלי - כמה זה ישנה לי בנטו?")]
    [InlineData("ארצה להשוות את ATTO3")][InlineData("אני רוצה להשוות את אטו 3")]
    public async Task ExplicitUniqueModelIsComparedWithoutAButtonOrPlannerGuess(string text)
    {
        var db=new ConversationDatabase();var client=new ScriptedToolClient();var employee=Data.Employees[0];
        var result=await Chat(Service(db,client),employee,text);
        Assert.Equal("CAR-10",result["target"]!.ToString());Assert.Equal(0,client.Calls);Assert.Contains("הנטו המשוער",result["reply"]!.ToString());
        var alternative=new PayrollEngine(Data).Simulate(employee,new Dictionary<string,decimal>(),new HashSet<string>()).Results.Single(r=>r.Id=="CAR-10");
        Assert.Contains(FinancialText.Number(alternative.Net),result["reply"]!.ToString());Assert.Contains("ברירת מחדל בקטלוג",result["reply"]!.ToString());
        Assert.Equal(2,db.Messages.Count);
    }
    [Fact] public void MissingOrAmbiguousYearIsNotGuessed()
    {
        var original=Data.Cars.Single(car=>car.Id=="CAR-10");var cars=new[]{original,original with{Id="ANOTHER-YEAR",Year=2026}};
        Assert.Equal(2,VehicleSearch.Find("BYD ATTO3",cars).Count);
        Assert.Equal("ANOTHER-YEAR",Assert.Single(VehicleSearch.Find("BYD ATTO 3 2026",cars)).Id);
        Assert.Empty(VehicleSearch.Find("BYD ATTO 3 2027",cars));Assert.Empty(VehicleSearch.Find("Synthetic Unknown Car",cars));
    }
    [Fact] public async Task AContradictoryCurrentCarDoesNotSilentlyChangeTheBaseline()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{CurrentCar="CAR-01"};
        var result=await Chat(Service(db,new ScriptedToolClient()),employee,Data.Employees[0].Question);
        Assert.Null(result["target"]);Assert.Contains("שונה מהרכב",result["reply"]!.ToString());Assert.Equal("CAR-01",result["employee"]!["currentCar"]!.ToString());Assert.DoesNotContain("הנטו המשוער",result["reply"]!.ToString());
    }
    [Fact] public async Task CurrentCarCanAlsoBeAnsweredByNameWithoutAButton()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field is not ("currentCar" or "participation"))],AdvisorState=new("currentCar","CAR-10",true,RequestedCategory:"electric")};
        await new Repository(db).SaveConversationAsync(employee,"CAR-10");
        var result=await Chat(Service(db,new ScriptedToolClient()),employee,"קורולה קרוס");
        Assert.Equal("CAR-09",result["employee"]!["currentCar"]!.ToString());Assert.Equal("participation",result["awaitingField"]!.ToString());Assert.DoesNotContain("הנטו המשוער",result["reply"]!.ToString());
    }
    [Fact] public async Task AUniqueNameStillCannotBypassEligibility()
    {
        var employee=Data.Employees[0] with{Occupation="מפקח/ת איכות במפעל",CurrentCar="CAR-01"};
        var result=await Chat(Service(new(),new ScriptedToolClient()),employee,"ארצה להשוות את BYD ATTO3");
        Assert.Equal("not_eligible",result["status"]!.ToString());Assert.DoesNotContain("הנטו המשוער",result["reply"]!.ToString());
    }
    [Theory][InlineData("תודה")][InlineData("תודה רבה!")][InlineData("מעולה תודה")]
    public async Task ThanksDoesNotTriggerSearchOrEraseTheConversation(string text)
    {
        var db=new ConversationDatabase();var client=new ScriptedToolClient();var employee=Data.Employees[0];var service=Service(db,client);
        await Chat(service,employee,"ארצה להשוות את ATTO 3");
        var result=await Chat(service,employee,text);
        Assert.Equal(0,client.Calls);Assert.Contains("בשמחה",result["reply"]!.ToString());Assert.Empty(result["candidates"]!.AsArray());Assert.Equal("CAR-10",result["target"]!.ToString());Assert.Empty(db.Tickets);
    }
    [Fact] public async Task NewChatPreservesApprovedPayrollAndTicketsButExcludesOldDialogueFromTheModel()
    {
        var db=new ConversationDatabase();var repository=new Repository(db);var employee=Data.Employees[0] with{Salary=19999,AdvisorState=new("targetParticipation","CAR-10",true,["CAR-10"],"electric")};
        await repository.SaveConversationAsync(employee,"CAR-10");await repository.AddMessageAsync(employee.Id,"user","old private context");
        await repository.CreateTicketAsync(employee,"CAR-10","אישור השתתפות");
        var client=new ScriptedToolClient(messages=>{Assert.DoesNotContain(messages,message=>message.Text?.Contains("old private context")==true);return new([new("read","get_my_eligibility",new())],"",new JsonObject());},_=>new([],"{FACT_0}",new JsonObject()));
        var service=Service(db,client);var fresh=await service.StartNewChatAsync(Data.Employees[0],default);
        Assert.Equal(19999,fresh.Salary);Assert.Null(fresh.AdvisorState);Assert.Empty(await repository.HistoryAsync(employee.Id,default,fresh.ChatHistoryStart));Assert.Single(await repository.TicketsAsync(employee.Id));
        Assert.NotEmpty(await repository.HistoryAsync(employee.Id));
        await Chat(service,Data.Employees[0],"לאיזו קבוצה אני שייך?");
        var saved=(await repository.ConversationAsync(employee.Id))!.Employee;Assert.Equal(fresh.ChatHistoryStart,saved.ChatHistoryStart);
        var again=await service.StartNewChatAsync(employee,default);Assert.True(again.ChatHistoryStart>fresh.ChatHistoryStart);Assert.Empty(await repository.HistoryAsync(employee.Id,default,again.ChatHistoryStart));
    }
}
