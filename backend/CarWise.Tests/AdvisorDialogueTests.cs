using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class AdvisorDialogueTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AiToolTurn Call(string name,JsonObject? args=null)=>new([new("call",name,JsonNode.Parse((args??new()).ToJsonString())!.AsObject())],"",new JsonObject{["role"]="assistant"});
    private static AiToolTurn Finish(string text)=>new([],text,new JsonObject{["role"]="assistant"});
    private static AdvisorService Service(ConversationDatabase db,IAiToolClient client,IResponseNarrationService? narrator=null)=>new(Data,new(Data),new(db),new LocalExtractionService(),narrator??new TemplateNarrationService(),
        tools:new(client,Data,new(Data),new(db),NullLogger<AdvisorToolConversation>.Instance,narration:narrator));
    private static async Task<JsonNode> Chat(AdvisorService service,Employee e,string text)=>JsonNode.Parse(JsonDefaults.Serialize(await service.ChatAsync(new(e,text,null,null),default)))!;
    [Fact] public async Task VerboseReadToolsBecomeOneSavedQuestionNotAMissingDataSpeech()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[]};
        var model=new ScriptedToolClient(_=>Call("find_vehicles",new(){["query"]="DOLPHIN",["drive"]="electric"}),
            _=>Call("get_my_current_car"),_=>Call("get_my_payroll"),_=>Finish("{FACT_0} {FACT_1} {FACT_2}"));
        var result=await Chat(Service(db,model,new NoNarrationDuringIntake()),employee,"אני שוקל לעבור לרכב חשמלי, כמה זה ישנה לי בנטו?");
        var text=result["reply"]!.ToString();Assert.Equal(1,text.Count(c=>c=='?'));Assert.True(text.Length<180);
        Assert.Contains("מה שכר הבסיס",text);Assert.DoesNotContain("DOLPHIN",text);Assert.DoesNotContain("טרם נמסר",text);
        Assert.DoesNotContain("מה הרכב",text);Assert.Empty(result["candidates"]!.AsArray());Assert.Equal("salary",result["awaitingField"]!.ToString());
        var saved=(await new Repository(db).ConversationAsync(employee.Id))!.Employee;
        Assert.True(saved.AdvisorState!.WantsElectric);Assert.Equal("electric",saved.AdvisorState.RequestedCategory);
        // A new request with a stale profile must continue the same intent and the next question.
        result=await Chat(Service(db,new ScriptedToolClient(),new NoNarrationDuringIntake()),employee,"14200 ש\"ח");
        Assert.Equal("commission",result["awaitingField"]!.ToString());Assert.Equal(14200,result["employee"]!["salary"]!.GetValue<decimal>());
        Assert.True(result["employee"]!["advisorState"]!["wantsElectric"]!.GetValue<bool>());
        Assert.Equal(1,result["reply"]!.ToString().Count(c=>c=='?'));
    }
    [Theory][InlineData("electric")][InlineData("family")][InlineData("regular")]
    public async Task CategoryIsRetainedByToolsWithoutRelyingOnAnExamplePhrase(string category)
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[]};
        var model=new ScriptedToolClient(_=>Call("start_comparison",new(){["category"]=category}),_=>Call("ask_missing_details"),_=>Finish("{FACT_0} {FACT_1}"));
        var result=await Chat(Service(db,model),employee,"בוא נבדוק את האפשרות שדיברנו עליה");
        Assert.Equal("salary",result["awaitingField"]!.ToString());Assert.Equal(category,result["employee"]!["advisorState"]!["requestedCategory"]!.ToString());
        result=await Chat(Service(db,new ScriptedToolClient()),employee,"14200 ש\"ח");
        Assert.Equal("commission",result["awaitingField"]!.ToString());Assert.Equal(category,result["employee"]!["advisorState"]!["requestedCategory"]!.ToString());
    }
    [Fact] public async Task SeveralProvidedFieldsAreSavedAndOnlyTheActualNextMissingFieldIsAsked()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[]};
        var fields=new JsonObject{["salary"]=14200,["commission"]=1000,["bonus"]=null,["pension"]=6,["fund"]=2.5m,["credits"]=2.25m,["children"]=null};
        var model=new ScriptedToolClient(_=>Call("set_payroll_fields",new(){["fields"]=fields}),_=>Call("get_my_payroll"),_=>Finish("{FACT_0} {FACT_1}"));
        var result=await Chat(Service(db,model),employee,"השכר 14200, עמלות 1000, פנסיה 6%, קרן השתלמות 2.5%, נקודות זיכוי 2.25");
        Assert.Equal("bonus",result["awaitingField"]!.ToString());Assert.Contains("בונוס",result["reply"]!.ToString());
        Assert.Equal(1,result["reply"]!.ToString().Count(c=>c=='?'));Assert.DoesNotContain("נקודות זיכוי:",result["reply"]!.ToString());
        var saved=(await new Repository(db).ConversationAsync(employee.Id))!.Employee;
        Assert.True(IntakeFields.Known(saved,"salary"));Assert.True(IntakeFields.Known(saved,"credits"));Assert.False(IntakeFields.Known(saved,"bonus"));Assert.False(IntakeFields.Known(saved,"children"));
    }
    [Fact] public async Task APolicyDetourDoesNotRestartIntakeOrEraseThePendingComparison()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[],AdvisorState=new("salary",null,true,RequestedCategory:"electric")};
        await new Repository(db).SaveConversationAsync(employee,null,default);
        var model=new ScriptedToolClient(_=>Call("get_my_eligibility"),_=>Finish("{FACT_0}"));
        var result=await Chat(Service(db,model),employee,"רגע, לאיזו קבוצה אני שייכת?");
        Assert.Contains(Data.OccupationFor(employee.Occupation).Group,result["reply"]!.ToString());Assert.DoesNotContain("מה שכר",result["reply"]!.ToString());
        Assert.Equal("salary",result["awaitingField"]!.ToString());Assert.True(result["employee"]!["advisorState"]!["wantsElectric"]!.GetValue<bool>());
    }
    [Fact] public async Task KnownPayrollIsNotAskedAgainAndUnknownParticipationIsNotMadeZero()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(f=>f is not ("children" or "currentCar" or "participation"))]};
        var model=new ScriptedToolClient(_=>Call("start_comparison",new(){["category"]="family"}),_=>Finish("{FACT_0}"));
        var result=await Chat(Service(db,model),employee,"בדוק לי בבקשה את המסלול המשפחתי");
        Assert.Equal("children",result["awaitingField"]!.ToString());Assert.DoesNotContain("שכר הבסיס",result["reply"]!.ToString());Assert.False(result["complete"]!.GetValue<bool>());
        var saved=(await new Repository(db).ConversationAsync(employee.Id))!.Employee;
        Assert.False(IntakeFields.Known(saved,"participation"));Assert.Equal(Data.OccupationFor(employee.Occupation).Group,saved.Group);
    }
    [Fact] public async Task EveryMissingFieldIsCollectedAndFamilyIntentSurvivesTheWholeConversation()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[]};
        var initial=new ScriptedToolClient(_=>Call("start_comparison",new(){["category"]="family"}),_=>Finish("{FACT_0}"));
        var result=await Chat(Service(db,initial),employee,"נבדוק את המסלול המתאים למשפחה שלי");
        var answers=new[]{("salary","14200 ש\"ח"),("commission","1000"),("bonus","0"),("pension","6%"),("fund","2.5%"),("credits","2.25"),("children","4")};
        foreach(var (field,text) in answers)
        {
            Assert.Equal(field,result["awaitingField"]!.ToString());Assert.Equal(1,result["reply"]!.ToString().Count(c=>c=='?'));
            result=await Chat(Service(db,new ScriptedToolClient()),employee,text);
        }
        Assert.Equal("currentCar",result["awaitingField"]!.ToString());
        result=JsonNode.Parse(JsonDefaults.Serialize(await Service(db,new ScriptedToolClient()).ChatAsync(new(employee,"זה הרכב הנוכחי","CAR-09",null),default)))!;
        Assert.Equal("participation",result["awaitingField"]!.ToString());
        result=await Chat(Service(db,new ScriptedToolClient()),employee,"350 ש\"ח");
        Assert.Empty(IntakeFields.Missing(JsonDefaults.Read<Employee>(result["employee"]!.ToJsonString())));
        Assert.True(result["complete"]!.GetValue<bool>());Assert.Equal("family",result["employee"]!["advisorState"]!["requestedCategory"]!.ToString());
        Assert.NotEmpty(result["candidates"]!.AsArray());Assert.All(result["candidates"]!.AsArray(),candidate=>Assert.Equal("משפחתי",Data.Cars.Single(c=>c.Id==candidate!.ToString()).Group));
    }
    private sealed class NoNarrationDuringIntake:IResponseNarrationService
    {
        public Task<string> NarrateAsync(Alternative a,CancellationToken ct)=>throw new Xunit.Sdk.XunitException("Intake must not narrate a calculation");
        public Task<string> ExplainFactsAsync(string[] facts,CancellationToken ct)=>throw new Xunit.Sdk.XunitException("Intake must not narrate a dump of tool facts");
    }
}
