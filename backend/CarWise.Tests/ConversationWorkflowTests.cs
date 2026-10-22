using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;

public sealed class ConversationWorkflowTests
{
    internal static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AiToolCall Command(string name,JsonObject? args=null)=>new(Guid.NewGuid().ToString("N"),name,JsonNode.Parse((args??new()).ToJsonString())!.AsObject());
    private static AiToolTurn Batch(params AiToolCall[] calls)=>new(calls.ToList(),"",new JsonObject());
    private static AiToolTurn End(string text="{FACT_0}")=>new([],text,new JsonObject());
    internal static AdvisorService Service(ConversationDatabase db,IAiToolClient client)=>new(Data,new(Data),new(db),new LocalExtractionService(),new TemplateNarrationService(),
        tools:new(client,Data,new(Data),new(db),NullLogger<AdvisorToolConversation>.Instance));
    internal static async Task<JsonNode> Chat(AdvisorService service,Employee employee,string text)=>JsonNode.Parse(JsonDefaults.Serialize(await service.ChatAsync(new(employee,text,null,null),default)))!;
    private static AdvisorMemory Memory(JsonNode reply)=>JsonDefaults.Read<Employee>(reply["employee"]!.ToJsonString()).AdvisorState!.Memory!;
    [Fact] public async Task WholeJourneySurvivesPolicyDetoursDeclineCategoryChangesRestorePauseResumeAndUnknownAttributes()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0];
        async Task<JsonNode> Run(string text,params Func<IReadOnlyList<AiToolMessage>,AiToolTurn>[] calls)=>await Chat(Service(db,new ScriptedToolClient(calls)),employee,text);
        var reply=await Run("בא לי לבדוק כיוון חדש",_=>Batch(Command("start_comparison",new(){["category"]="electric"})),_=>End());
        var offered=reply["candidates"]!.AsArray().Select(id=>id!.ToString()).ToArray();Assert.True(offered.Length>1);
        var selected=offered[1];
        reply=await Run("רגע, מה אומרת המדיניות?",_=>Batch(Command("manage_conversation",new(){["action"]="answer_question"}),Command("get_my_eligibility")),messages=>
        {
            Assert.Equal("compare",messages.Last().Result!["conversation"]!["goal"]!.ToString());
            Assert.Equal(offered.Length,messages.Last().Result!["conversation"]!["offered"]!.AsArray().Count);return End("{FACT_1}");
        });
        Assert.Contains("קבוצת הזכאות",reply["reply"]!.ToString());
        reply=await Run("השנייה נראית לי",_=>Batch(Command("compare_vehicle",new(){["target"]=selected})));
        Assert.Equal(selected,reply["target"]!.ToString());Assert.Equal("compared",Memory(reply).Phase);
        reply=await Run("אולי נסתכל בעוד אפשרויות?",_=>Batch(Command("offer_next_action",new(){["action"]="alternatives",["target"]="",["field"]=""})));
        Assert.Equal("alternatives",Memory(reply).Pending!.Kind);Assert.False(reply["complete"]!.GetValue<bool>());
        reply=await Run("לא");Assert.Null(Memory(reply).Pending);Assert.Equal(selected,reply["target"]!.ToString());
        reply=await Run("עזבי חשמל, נבדוק כיוון רגיל",_=>Batch(Command("start_comparison",new(){["category"]="regular"})),_=>End());
        Assert.All(reply["candidates"]!.AsArray(),id=>Assert.NotEqual("חשמלי",Data.Cars.Single(car=>car.Id==id!.ToString()).Drive));
        reply=await Run("בעצם תחזירי אותי לאפשרות שהייתה לפני כן",_=>Batch(Command("manage_conversation",new(){["action"]="restore"})));
        Assert.Equal(selected,reply["target"]!.ToString());Assert.Contains(Memory(reply).Transitions!,step=>step.Action=="restore");
        reply=await Run("נניח לזה בינתיים",_=>Batch(Command("manage_conversation",new(){["action"]="pause"})));
        Assert.Equal("paused",Memory(reply).Goal);Assert.Empty(reply["candidates"]!.AsArray());
        reply=await Run("אפשר להמשיך מאיפה שעצרנו?",_=>Batch(Command("manage_conversation",new(){["action"]="resume"})));
        Assert.Equal(selected,reply["target"]!.ToString());Assert.Equal("compared",Memory(reply).Phase);
        reply=await Run("יותר חשוב לי שיהיה מקום לעגלה ולציוד",_=>Batch(Command("set_vehicle_preference",new(){["preference"]="more_space"})));
        Assert.Contains("מידות מרווח",reply["reply"]!.ToString());Assert.Equal("representative",Memory(reply).Pending!.Kind);Assert.Empty(await new Repository(db).TicketsAsync(employee.Id));
        reply=await Run("ומה התקציב שלי?",_=>Batch(Command("manage_conversation",new(){["action"]="answer_question"}),Command("get_policy",new(){["topic"]="budget"})),_=>End("{FACT_1}"));
        Assert.Equal("representative",Memory(reply).Pending!.Kind);
        reply=await Run("כן בבקשה");var ticket=Assert.Single(await new Repository(db).TicketsAsync(employee.Id));Assert.Contains("מרווח",ticket.Question);
        Assert.Equal("waiting_representative",Memory(reply).Phase);Assert.Equal(employee.Salary,reply["employee"]!["salary"]!.GetValue<decimal>());
        Assert.True(Memory(reply).Transitions!.Length<=24);Assert.True(Memory(reply).Backstack!.Length<=8);
    }
    [Theory][InlineData("לא ממש הבנתי, תחזור לרכב שדיברנו עליו")][InlineData("רגע, נחזור לאפשרות הקודמת")][InlineData("בעצם הקודם היה עדיף לי")]
    public async Task RestoreUsesRecordedOptionsRatherThanRecognizingASpecificSentence(string wording)
    {
        var employee=Data.Employees[0];var flow=new AdvisorWorkflow(null);flow.Begin("electric");flow.Select("CAR-03");flow.Calculated("CAR-03");flow.Begin("regular");
        var db=new ConversationDatabase();await new Repository(db).SaveConversationAsync(flow.Attach(employee),null);
        var reply=await Chat(Service(db,new ScriptedToolClient(_=>Batch(Command("manage_conversation",new(){["action"]="restore"})))),employee,wording);
        Assert.Equal("CAR-03",reply["target"]!.ToString());Assert.Contains("הנטו המשוער",reply["reply"]!.ToString());
    }
    [Fact] public async Task ModelChosenMissingInputQuestionSavesItsFieldBeforeAShortNumericReply()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field!="salary")]};
        var first=await Chat(Service(db,new ScriptedToolClient(_=>Batch(Command("get_missing_fields")),_=>End())),employee,"רוצה לוודא שאנחנו מוכנים להתקדם");
        Assert.Equal("salary",first["awaitingField"]!.ToString());
        var next=await Chat(Service(db,new ScriptedToolClient()),employee,"14200");
        Assert.Equal(14200,next["employee"]!["salary"]!.GetValue<decimal>());
        Assert.Contains("salary",JsonDefaults.Read<Employee>(next["employee"]!.ToJsonString()).KnownFields!);
        Assert.NotEqual("salary",next["awaitingField"]?.ToString());
    }
    [Fact] public async Task YesWithoutAPendingActionClarifiesAndNeverWritesATicketOrPayroll()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0];
        var reply=await Chat(Service(db,new ScriptedToolClient(_=>Batch(Command("manage_conversation",new(){["action"]="accept"})))),employee,"כן");
        Assert.Contains("איזו פעולה",reply["reply"]!.ToString());Assert.False(reply["complete"]!.GetValue<bool>());Assert.Empty(db.Tickets);Assert.Equal(employee.Salary,reply["employee"]!["salary"]!.GetValue<decimal>());
    }
    [Fact] public async Task DecliningAReferralIsNotAZeroCommissionAnswer()
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field!="commission")]};
        var flow=new AdvisorWorkflow(new("commission"));flow.OfferAction(new("representative",Field:"commission"));
        var db=new ConversationDatabase();await new Repository(db).SaveConversationAsync(flow.Attach(employee),null);
        var reply=await Chat(Service(db,new ScriptedToolClient()),employee,"לא");
        Assert.Contains("commission",reply["missingFields"]!.AsArray().Select(field=>field!.ToString()));Assert.Null(Memory(reply).Pending);Assert.Empty(db.Tickets);
    }
    [Fact] public async Task PolicyQuestionCanOverrideAConservativeComparisonHintWithoutDroppingIntake()
    {
        var employee=Data.Employees[0] with{KnownFields=[],AdvisorState=new("salary",null,true,RequestedCategory:"electric")};
        var db=new ConversationDatabase();await new Repository(db).SaveConversationAsync(employee,null);
        var reply=await Chat(Service(db,new ScriptedToolClient(_=>Batch(Command("manage_conversation",new(){["action"]="answer_question"}),Command("get_my_eligibility")),_=>End("{FACT_1}"))),employee,"אני רוצה להחליף רכב, אבל קודם מה קבוצת הזכאות שלי?");
        Assert.Contains("קבוצת הזכאות",reply["reply"]!.ToString());Assert.DoesNotContain("מה שכר",reply["reply"]!.ToString());Assert.Equal("salary",reply["awaitingField"]!.ToString());
    }
    [Fact] public async Task ActionResultsExposePendingMoneyAsNullAndNeverUseTheCatalogueForCurrentContribution()
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field!="participation")]};
        var db=new ConversationDatabase();var result=await new AdvisorOperations(Data,new(Data),new(db)).CompareAsync(employee,"CAR-10",default);
        Assert.Equal("needs_input",result.Status);Assert.Null(result.Calculation);Assert.False(IntakeFields.Known(result.Employee,"participation"));Assert.Empty(db.Tickets);
    }
}
