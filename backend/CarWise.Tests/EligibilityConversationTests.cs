using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class EligibilityConversationTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static Employee Pending=>Source.Employees[0] with{Id="E-intake-eligibility",Occupation="",Group="",KnownFields=[]};
    private static ReferenceData Data(Employee employee)=>new ConfigurationValidator(Source).Validate(new(Source.Rules,Source.Cars,Source.OccupationDocuments,[..Source.Employees,employee],new(false,new(),new())));
    private static async Task<JsonNode> Chat(AdvisorService service,Employee employee,string text)=>JsonNode.Parse(JsonDefaults.Serialize(await service.ChatAsync(new(employee,text,null,null),default,false)))!;
    private static Employee Returned(JsonNode result)=>JsonDefaults.Read<Employee>(result["employee"]!.ToJsonString());
    [Theory][InlineData("ג",true)][InlineData("קבוצה ג",false)][InlineData("אני בקבוצה ג, ויש לי בונוס",false)][InlineData("קבוצת הזכאות שלי היא ג",false)][InlineData("קבוצת זכאות ג׳",false)]
    public void ExplicitGroupAnswersUseRuntimePolicyLabels(string text,bool shortAnswer)=>Assert.Equal("ג",EligibilityInputs.Group(text,Source,shortAnswer));
    [Theory][InlineData("אני לא יודע")][InlineData("הרכב מקבוצה ג")][InlineData("בקבוצה ג או קבוצה ד")][InlineData("14200")]
    [InlineData("אני לא בקבוצה ג")][InlineData("האם אני בקבוצה ג?")][InlineData("אני בקבוצה ג?")][InlineData("אני חושבת שאני בקבוצה ג")]
    public void GroupCannotBeInferredFromUnrelatedOrAmbiguousText(string text)=>Assert.Null(EligibilityInputs.Group(text,Source));
    [Fact] public async Task IntakeStartsWithSalaryCollectsOnlyMissingValuesAndSavesReportedGroupBeforeComparison()
    {
        var pending=Pending;var data=Data(pending);var db=new ConversationDatabase();var repository=new Repository(db);var engine=new PayrollEngine(data);
        var service=new AdvisorService(data,engine,repository,new LocalExtractionService(),new TemplateNarrationService());
        var reply=await Chat(service,pending,"אני שוקל לעבור ל-ATTO 3 החשמלי, כמה זה ישנה לי בנטו?");
        Assert.Equal("salary",reply["awaitingField"]!.ToString());
        foreach(var (answer,next) in new[]{("14200","commission"),("אין","bonus"),("אין","pension"),("6","fund"),("2.5","credits"),("2.75","children"),("אין ילדים","eligibility")})
        {
            reply=await Chat(service,pending,answer);Assert.Equal(next,reply["awaitingField"]!.ToString());
        }
        reply=await Chat(service,pending,"ג");Assert.Equal("ג",reply["employee"]!["group"]!.ToString());Assert.Equal("employee",reply["employee"]!["eligibilitySource"]!.ToString());Assert.Equal("currentCar",reply["awaitingField"]!.ToString());
        reply=await Chat(service,pending,"קורולה קרוס");Assert.Equal("participation",reply["awaitingField"]!.ToString());
        reply=await Chat(service,pending,"300");Assert.Contains("הנטו",reply["reply"]!.ToString());Assert.Equal("CAR-10",reply["target"]!.ToString());
        var restored=engine.Validate((await new Repository(db).ConversationAsync(pending.Id))!.Employee);
        Assert.Equal("ג",restored.Group);Assert.Equal("employee",restored.EligibilitySource);Assert.Equal(14200,restored.Salary);Assert.Empty(IntakeFields.Missing(restored));
        Assert.NotNull(engine.Simulate(restored).Current);
        var forged=JsonDefaults.Read<Employee>("""{"id":"E-intake-eligibility","name":"Fake","occupation":"","group":"ד","salary":14200,"commission":0,"bonus":0,"pension":6,"fund":2.5,"credits":2.75,"children":0,"currentCar":"CAR-09","participation":300,"resolvedEligibility":{"group":"ד","source":"representative"}}""");
        Assert.Equal("",engine.Validate(forged).Group);
    }
    [Fact] public async Task UnknownGroupCreatesOneRealTicketAndRepresentativeAnswerHasPrecedenceWithoutLosingTheGoal()
    {
        var pending=Pending with{KnownFields=null};var data=Data(pending);var db=new ConversationDatabase();var repository=new Repository(db);var engine=new PayrollEngine(data);
        var service=new AdvisorService(data,engine,repository,new LocalExtractionService(),new TemplateNarrationService());
        var reply=await Chat(service,pending,"ארצה להשוות את ATTO 3");Assert.Equal("eligibility",reply["awaitingField"]!.ToString());
        reply=await Chat(service,pending,"לא יודעת");Assert.Contains("שלחתי שאלה",reply["reply"]!.ToString());
        await Chat(service,pending,"לא יודע");var ticket=Assert.Single(await repository.TicketsAsync(pending.Id));Assert.Equal("eligibility:group",ticket.Target);
        var current=(await repository.ConversationAsync(pending.Id))!.Employee;Assert.Equal("CAR-10",current.AdvisorState!.Target);
        var reported=await repository.ReportEligibilityAsync(current,"ב",null,"employee",data,default);Assert.Equal("employee",reported.EligibilitySource);
        var approved=await repository.ReportEligibilityAsync(reported,"ג",null,"representative",data,default);Assert.Equal("representative",approved.EligibilitySource);
        var next=await service.ContinueAfterEligibilityAsync(approved,default);Assert.NotNull(next.Calculation);Assert.Equal("CAR-10",next.Calculation.Id);Assert.DoesNotContain("שכר הבסיס",next.Reply);
        await Assert.ThrowsAsync<ArgumentException>(()=>repository.ReportEligibilityAsync(approved,"ד",null,"employee",data,default));
        var restored=engine.Validate(await repository.ResolveEligibilityAsync(pending,default));Assert.Equal("ג",restored.Group);
    }
    [Fact] public async Task GroupAloneDoesNotInventWaiverRightsOrMaintenanceAndCompanyMappingStaysAuthoritative()
    {
        var pending=Pending with{KnownFields=null,CurrentCar=null,Participation=0};var data=Data(pending);var repository=new Repository(new ConversationDatabase());var engine=new PayrollEngine(data);
        var reported=await repository.ReportEligibilityAsync(pending,"ג",null,"employee",data,default);
        Assert.Contains("occupation",IntakeFields.Missing(reported));Assert.False(engine.Eligibility(reported,null).Eligible);
        Assert.Throws<ArgumentException>(()=>engine.Calculate(reported,data.Cars[0]));
        var role=data.OccupationDocuments.Select(o=>data.OccupationFor(o["occupation"].GetString()!)).First(o=>o.Waiver);
        var approved=await repository.ReportEligibilityAsync(reported,role.Group,role.OccupationName,"representative",data,default);
        Assert.True(approved.WaiverPolicyKnown);Assert.Equal(role.Maintenance,engine.Calculate(approved,null).Maintenance);
        var known=Source.Employees[0];await Assert.ThrowsAsync<ArgumentException>(()=>repository.ReportEligibilityAsync(known,known.Group=="ד"?"א":"ד",null,"employee",Source,default));
        Assert.Equal(known.Group,new PayrollEngine(Source).Validate(known with{Group="ד"}).Group);
    }
    [Fact] public async Task NativeToolCanSaveExplicitEligibilityWithSourceAndCannotUseClientSideEvidence()
    {
        var pending=Pending with{KnownFields=null,AdvisorState=new("eligibility")};var data=Data(pending);var db=new ConversationDatabase();var repository=new Repository(db);var engine=new PayrollEngine(data);
        pending=engine.Validate(pending);
        var model=new ScriptedToolClient(_=>new([new("eligibility","report_employee_eligibility",new(){["group"]="ג",["occupation"]=""})],"",new JsonObject()),
            messages=>{Assert.True(messages.Last().Result!["ok"]!.GetValue<bool>());return new([],"{FACT_0}",new JsonObject());});
        var result=await new AdvisorToolConversation(model,data,engine,repository,NullLogger<AdvisorToolConversation>.Instance).RunAsync(new(pending,"אני בקבוצה ג",null,null),pending,[],default);
        Assert.Equal("ג",result.Employee.Group);Assert.Equal("employee",result.Employee.EligibilitySource);Assert.Equal(2,model.Calls);
        Assert.Contains("הדיווח שלך",result.Reply);Assert.Equal("ג",engine.Validate(await new Repository(db).ResolveEligibilityAsync(Pending,default)).Group);
    }
}
