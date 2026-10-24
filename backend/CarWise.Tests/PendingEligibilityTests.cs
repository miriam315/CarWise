using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;

public sealed class PendingEligibilityTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static Employee Pending=>Source.Employees[0] with{Id="E-pending",Name="Pending employee",Occupation="",Group="",KnownFields=null};
    private static ManagedConfiguration Config()=>new(Source.Rules.DeepClone(),Source.Cars.ToList(),Source.OccupationDocuments.ToList(),Source.Employees.Append(Pending).ToList(),new(false,new(),new()));
    [Fact] public async Task EmployeeWithoutOccupationOrGroupCanBeSavedAndLoadedWithoutPolicyChange()
    {
        var db=new MemoryConfigurationDatabase();var original=Config() with{Employees=Source.Employees.ToList()};
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","",original,[]),Source));
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var service=new ConfigurationService(db,state,new(Source),vault);var document=await service.SaveAsync(new(0,Config(),""),"admin",default);
        var employee=Assert.Single(document.Configuration.Employees,e=>e.Id==Pending.Id);Assert.Equal("",employee.Occupation);Assert.Equal("",employee.Group);
        Assert.Equal(0,document.PolicyVersion);Assert.Empty(document.History);
        var other=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","",original,[]),Source));
        await new ConfigurationService(db,other,new(Source),vault).RefreshAsync(default);Assert.Equal("",Assert.Single(other.Current.Data.Employees,e=>e.Id==Pending.Id).Group);
    }
    [Fact] public void PendingOccupationAllowsItsNameToBeSavedButNeverGrantsAnyVehicleOrWaiver()
    {
        var config=Config();var role=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>("""{"occupation":"Unclassified role","group":"","mandatory":false,"waiver":true,"maintenance":1000}""")!;
        config=config with{Occupations=[..config.Occupations,role],Employees=config.Employees.Select(e=>e.Id==Pending.Id?e with{Occupation="Unclassified role",Group="ד",Children=10}:e).ToList()};
        var data=new ConfigurationValidator(Source).Validate(config);var engine=new PayrollEngine(data);var employee=engine.Validate(data.Employees.Single(e=>e.Id==Pending.Id));
        Assert.Equal("",employee.Group);Assert.Equal(new[]{"eligibility"},IntakeFields.Missing(employee));
        foreach(var car in data.Cars.Cast<Car?>().Append(null))Assert.False(engine.Eligibility(employee,car).Eligible);
        Assert.Throws<ArgumentException>(()=>engine.Calculate(employee with{Group="ד"},data.Cars[0]));
        Assert.Throws<ArgumentException>(()=>engine.Simulate(employee with{Group="ד"}));
        var response=SimulationView.Incomplete(employee);Assert.Null(response["current"]);Assert.Empty(response["results"]!.AsArray());
        Assert.Contains("טרם נקבעה",AdvisorKnowledge.Answer(["group","budget","waiver_rules"],employee,data,engine));
    }
    [Fact] public async Task ChatExplainsPendingEligibilityWithoutInventingNumbersOrCallingTheModelAndCanResumeAfterAssignment()
    {
        var data=new ConfigurationValidator(Source).Validate(Config());var employee=data.Employees.Single(e=>e.Id==Pending.Id);var db=new ConversationDatabase();var tools=new ScriptedToolClient();
        AdvisorService Service(ReferenceData reference)=>new(reference,new(reference),new(db),new LocalExtractionService(),new TemplateNarrationService(),
            tools:new(tools,reference,new(reference),new(db),NullLogger<AdvisorToolConversation>.Instance));
        foreach(var text in new[]{"לאיזה קבוצה אני שייכת?","אני בקבוצה ד תחשב לי נטו","יש לי נתונים חסרים?"})
        {
            var response=JsonNode.Parse(JsonDefaults.Serialize(await Service(data).ChatAsync(new(employee,text,null,null),default,false)))!;
            Assert.False(response["complete"]!.GetValue<bool>());Assert.Equal("eligibility",response["awaitingField"]!.GetValue<string>());Assert.Empty(response["candidates"]!.AsArray());Assert.Contains("טרם נקבעה",response["reply"]!.ToString());
            Assert.DoesNotContain("₪",response["reply"]!.ToString());
        }
        Assert.Equal(0,tools.Calls);
        var assigned=Config() with{Employees=Config().Employees.Select(e=>e.Id==Pending.Id?e with{Occupation=Source.Employees[0].Occupation}:e).ToList()};
        var confirmed=new ConfigurationValidator(Source).Validate(assigned);var canonical=new PayrollEngine(confirmed).Validate(employee);
        Assert.Equal(Source.Employees[0].Group,canonical.Group);Assert.Equal(Source.Employees[0].Occupation,canonical.Occupation);
        var workflow=new AdvisorWorkflow(new("eligibility"));workflow.ResolveInputs(canonical);Assert.Null(workflow.State.AwaitingField);
        Assert.NotNull(new PayrollEngine(confirmed).Simulate(canonical).Current);
    }
}
