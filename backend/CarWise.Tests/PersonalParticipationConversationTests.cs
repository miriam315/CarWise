using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Xunit;
namespace CarWise.Tests;

public sealed class PersonalParticipationConversationTests
{
    private static readonly ReferenceData Data=ConversationWorkflowTests.Data;
    private static AiToolTurn Call(string name,JsonObject args)=>new([new(Guid.NewGuid().ToString("N"),name,JsonNode.Parse(args.ToJsonString())!.AsObject())],"",new JsonObject());
    private static async Task<(ConversationDatabase Db,Employee Employee)> Compared()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0];var flow=new AdvisorWorkflow(null);flow.Select("CAR-06");flow.Calculated("CAR-06");
        await new Repository(db).SaveConversationAsync(flow.Attach(employee),null);return(db,employee);
    }
    private static Task<JsonNode> Chat(ConversationDatabase db,Employee employee,string text,params Func<IReadOnlyList<AiToolMessage>,AiToolTurn>[] calls)=>
        ConversationWorkflowTests.Chat(ConversationWorkflowTests.Service(db,new ScriptedToolClient(calls)),employee,text);

    [Theory]
    [InlineData("סכמו איתי על השתתפות עצמית נמוכה יותר")]
    [InlineData("אחזור שוב, דיברו איתי על השתתפות נמוכה יותר בחודש")]
    [InlineData("ההשתתפות שלי שונה מהקטלוג")]
    public async Task DifferentAgreementAsksOnlyForAmountAndPreservesTheCar(string text)
    {
        var (db,employee)=await Compared();var reply=await Chat(db,employee,text);
        Assert.Equal("CAR-06",reply["target"]!.ToString());Assert.Equal("alternativeParticipation",reply["awaitingField"]!.ToString());
        Assert.Contains("מה סכום ההשתתפות",reply["reply"]!.ToString());Assert.DoesNotContain("נעצור",reply["reply"]!.ToString());
        Assert.DoesNotContain("הנטו המשוער",reply["reply"]!.ToString());Assert.Empty(db.Tickets);
        var next=await Chat(db,employee,"200");
        var result=new PayrollEngine(Data).Simulate(employee,new Dictionary<string,decimal>{{"CAR-06",200}}).Results.Single(row=>row.Id=="CAR-06");
        Assert.Contains(FinancialText.Number(result.Net),next["reply"]!.ToString());Assert.Contains("דיווח העובד",next["reply"]!.ToString());
        Assert.Equal(employee.Participation,next["employee"]!["participation"]!.GetValue<decimal>());
        Assert.Equal(200,(await new Repository(db).ParticipationWithSourcesAsync(employee.Id)).Overrides["CAR-06"]);
    }
    [Fact] public async Task ExistingIncorrectlyPausedConversationRecoversItsLastComparedVehicleForThePriceQuestion()
    {
        var (db,employee)=await Compared();var repo=new Repository(db);var saved=(await repo.ConversationAsync(employee.Id))!.Employee;
        var flow=new AdvisorWorkflow(saved.AdvisorState);flow.Pause();await repo.SaveConversationAsync(flow.Attach(saved),null);
        var reply=await Chat(db,employee,"דיברו איתי על השתתפות נמוכה יותר בחודש");
        Assert.Equal("CAR-06",reply["target"]!.ToString());Assert.Equal("alternativeParticipation",reply["awaitingField"]!.ToString());
        Assert.DoesNotContain("נעצור",reply["reply"]!.ToString());Assert.Empty(db.Tickets);
    }
    [Fact] public async Task IncorrectPauseToolIsRejectedAndModelCanClarifyAnUnspecifiedPersonalPrice()
    {
        var (db,employee)=await Compared();
        var reply=await Chat(db,employee,"מחיר הליסינג האישי שסגרו לי צריך להיות זול יותר",
            _=>Call("manage_conversation",new(){["action"]="pause"}),messages=>
            {
                Assert.False(messages.Last().Result!["ok"]!.GetValue<bool>());
                return Call("discuss_alternative_participation",new(){["target"]="CAR-06"});
            });
        Assert.Equal("CAR-06",reply["target"]!.ToString());
        var memory=JsonDefaults.Read<Employee>(reply["employee"]!.ToJsonString()).AdvisorState!.Memory!;
        Assert.DoesNotContain(memory.Transitions!,step=>step.Action=="pause");Assert.Equal("compare",memory.Goal);
    }
    [Fact] public async Task UnknownAgreedAmountOffersThenOpensTheCorrectVehicleTicketOnConsent()
    {
        var (db,employee)=await Compared();await Chat(db,employee,"השתתפות נמוכה יותר");
        var ask=await Chat(db,employee,"לא יודע");Assert.Contains("לפתוח פנייה?",ask["reply"]!.ToString());Assert.Empty(db.Tickets);
        var pending=await Chat(db,employee,"כן");var ticket=Assert.Single(await new Repository(db).TicketsAsync(employee.Id));Assert.Equal("CAR-06",ticket.Target);
        Assert.Equal("targetParticipation",pending["awaitingField"]!.ToString());Assert.DoesNotContain("הנטו המשוער",pending["reply"]!.ToString());
        var row=db.Tickets.Single();row["status"]="resolved";row["participation"]=125m;row["answer"]="הסכום האישי הוא 125";
        var reply=await Chat(db,employee,"אפשר להמשיך עם מה שאושר?",_=>Call("compare_vehicle",new(){["target"]="CAR-06"}));
        Assert.Contains("אישור נציג",reply["reply"]!.ToString());Assert.Contains("125",reply["reply"]!.ToString());
    }
    [Theory][InlineData("למה לעצור?")][InlineData("לא ביקשתי לעצור")][InlineData("סוכם על סכום אחר")]
    public void PausingRequiresAnActualRequest(string text)=>Assert.False(AdvisorDialogue.ExplicitPauseRequest(text));
    [Theory][InlineData("נעצור כאן")][InlineData("נניח לזה בינתיים")][InlineData("אני רוצה להפסיק")]
    public void ExplicitPauseRequestsRemainAvailable(string text)=>Assert.True(AdvisorDialogue.ExplicitPauseRequest(text));
    [Fact] public async Task ComparisonIsConciseAndDetailedDriversAreAvailableOnlyWhenRequested()
    {
        var (db,employee)=await Compared();var summary=await Chat(db,employee,"ארצה להשוות את MG4");
        var text=summary["reply"]!.ToString();Assert.True(text.Length<350);Assert.DoesNotContain("שווי השימוש:",text);Assert.DoesNotContain("הפירוט זמין",text);
        var details=await Chat(db,employee,"ממה נובע ההפרש במס?",_=>Call("explain_comparison",new(){["component"]="taxes"}),_=>new([],"{FACT_0}",new JsonObject()));
        Assert.Contains("מסים ושווי שימוש",details["reply"]!.ToString());Assert.DoesNotContain("החזרים והפרשות",details["reply"]!.ToString());
    }
    [Fact] public async Task SmallRelatedGroupAcceptsSeveralExplicitFieldsWithoutRepeatedQuestions()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=["salary"],AdvisorState=new("commission",RequestedCategory:"electric")};
        await new Repository(db).SaveConversationAsync(employee,null);
        var question=await Chat(db,employee,"נמשיך עם שאר רכיבי השכר",_=>Call("ask_payroll_group",new(){["group"]="income"}));
        Assert.Contains("עמלות",question["reply"]!.ToString());Assert.Contains("בונוס",question["reply"]!.ToString());
        var reply=await Chat(db,employee,"עמלות 1800 ובונוס 500, פנסיה 6 וקרן השתלמות 2.5, נקודות זיכוי 2.75",_=>Call("set_payroll_fields",new(){["fields"]=new JsonObject{
            ["salary"]=null,["commission"]=1800,["bonus"]=500,["pension"]=6,["fund"]=2.5,["credits"]=2.75,["children"]=null}}),
            _=>Call("ask_next_question",new(){["field"]="children",["question"]="כמה ילדים עד גיל {CHILD_AGE} יש לך?"}));
        Assert.Equal("children",reply["awaitingField"]!.ToString());Assert.Equal(1800,reply["employee"]!["commission"]!.GetValue<decimal>());
        Assert.Equal(2.75m,reply["employee"]!["credits"]!.GetValue<decimal>());Assert.DoesNotContain("פנסיה",reply["reply"]!.ToString());
    }
    [Fact] public async Task GenderedGreetingFromNarrationCannotReachTheEmployee()
    {
        using var http=new HttpClient(new FakeHttp(_=>new(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=JsonDefaults.Serialize(new{text="שלום. תרצי פירוט? {NET} {DELTA}"})}}}}))})){BaseAddress=new("https://example.invalid/v1/")};
        var narrator=new AiNarrationService(new AiGateway(http,"test"),Microsoft.Extensions.Logging.Abstractions.NullLogger<AiNarrationService>.Instance);
        var alternative=new PayrollEngine(Data).Simulate(Data.Employees[0]).Results.Single(row=>row.Id=="CAR-06");
        var text=await narrator.NarrateAsync(alternative,default);
        Assert.Equal(FinancialText.Template(alternative),text);Assert.DoesNotContain("תרצי",text);Assert.DoesNotContain("שלום",text);Assert.True(text.Length<350);
    }
    [Fact] public async Task GroupQuestionDoesNotApplyOneAmbiguousNumberToMultipleComponents()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=["salary"]};var flow=new AdvisorWorkflow(null);flow.AskGroup(["commission","bonus"]);
        await new Repository(db).SaveConversationAsync(flow.Attach(employee),null);
        var reply=await Chat(db,employee,"1800",_=>Call("set_payroll_fields",new(){["fields"]=new JsonObject{
            ["salary"]=null,["commission"]=1800,["bonus"]=1800,["pension"]=null,["fund"]=null,["credits"]=null,["children"]=null}}),messages=>
            {
                Assert.False(messages.Last().Result!["ok"]!.GetValue<bool>());
                return Call("ask_clarification",new(){["question"]="לאיזה רכיב התכוונת בסכום הזה?"});
            });
        Assert.DoesNotContain("commission",JsonDefaults.Read<Employee>(reply["employee"]!.ToJsonString()).KnownFields!);
        var specific=await Chat(db,employee,"עמלות 1800",_=>Call("set_payroll_fields",new(){["fields"]=new JsonObject{
            ["salary"]=null,["commission"]=1800,["bonus"]=null,["pension"]=null,["fund"]=null,["credits"]=null,["children"]=null}}),
            _=>Call("ask_next_question",new(){["field"]="bonus",["question"]="מה סכום הבונוס החודשי שלך?"}));
        Assert.Contains("commission",JsonDefaults.Read<Employee>(specific["employee"]!.ToJsonString()).KnownFields!);
        Assert.Equal("bonus",specific["awaitingField"]!.ToString());
    }

}
