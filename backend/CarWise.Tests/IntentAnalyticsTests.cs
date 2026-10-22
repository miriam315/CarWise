using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class IntentTestDatabase:IJsonDatabase
{
    public readonly MemoryConfigurationDatabase Rules=new();
    public string Provider=>"test";
    public Task<JsonArray> ReadAsync(string table,IReadOnlyDictionary<string,string>? filters=null,int offset=0,int limit=1000,CancellationToken ct=default,string? afterId=null)=>
        table=="carwise_rules"?Rules.ReadAsync(table,filters,offset,limit,ct,afterId):Task.FromResult(new JsonArray());
    public Task UpsertAsync(string table,JsonArray values,CancellationToken ct=default)=>Rules.UpsertAsync(table,values,ct);
    public Task<bool> TryWriteRuleAsync(string id,long? expectedRevision,JsonNode document,CancellationToken ct=default)=>Rules.TryWriteRuleAsync(id,expectedRevision,document,ct);
    public Task<JsonNode> RpcAsync(string name,JsonObject parameters,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default)=>throw new NotSupportedException();
}
public sealed class IntentAnalyticsTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static readonly DateOnly Today=new(2026,10,22);
    private static IntentMember Member(int i,List<IntentEvent> events,bool consent=true)=>new("E-"+i,new(1,consent,events,DateTimeOffset.UtcNow));
    private static IntentEvent Event(string kind="explore",string bucket="loss",int daysAgo=8,string? reason=null)=>new(Today.AddDays(-daysAgo),kind,"CAR-10","electric",bucket,reason,1);
    private static IntentAnalytics Service(IntentTestDatabase db,IntentPolicy? policy=null)=>new(db,new(new(1,DateTimeOffset.UtcNow,"test","test",new(Data.Rules,Data.Cars,Data.OccupationDocuments,Data.Employees,new(false,new(),new())){IntentAnalytics=policy??new(){Enabled=true}},[]),Data),new(db),NullLogger<IntentAnalytics>.Instance);
    [Theory][InlineData(1,10,null)][InlineData(4,10,null)][InlineData(5,10,5)][InlineData(6,10,null)][InlineData(10,10,10)][InlineData(0,10,0)]
    public void SmallGroupsAndSmallComplementsAreSuppressed(int value,int total,int? expected)=>Assert.Equal(expected,IntentRules.Mask(value,total,5));
    [Fact] public void RepeatedSimulationsDoNotBecomeMultiplePeopleOrObservedAbandonment()
    {
        var members=Enumerable.Range(0,10).Select(i=>Member(i,Enumerable.Repeat(Event(),20).ToList())).ToList();
        var c=IntentRules.Summarize(members,Today,30,5).Single(c=>c.Category=="electric");
        Assert.Equal(10,c.Explored);Assert.Equal(10,c.UnselectedAfterSevenDays);Assert.Equal(0,c.SavedChoice);Assert.Equal(10,c.NegativeNet);
        Assert.DoesNotContain(IntentRules.Summarize(members,Today,30,5).Single(c=>c.Category=="electric").Feedback,f=>f.Value>0);
        Assert.All(IntentAnalytics.Insights([c]),i=>Assert.DoesNotContain("בגלל",i.Text));
    }
    [Fact] public void ExplicitFeedbackIsNotInferredFromNegativeFinancialResults()
    {
        var people=Enumerable.Range(0,12).Select(i=>Member(i,i<6?[Event(),Event("feedback",reason:"charging")]:[Event()])).ToList();
        var c=IntentRules.Summarize(people,Today,30,5).Single(c=>c.Category=="electric");
        Assert.Equal(12,c.NegativeNet);Assert.Equal(0,c.Feedback["net"]);Assert.Equal(6,c.Feedback["charging"]);
    }
    [Fact] public void ConsentThresholdAndSevenDayGraceAreEnforced()
    {
        var people=Enumerable.Range(0,10).Select(i=>Member(i,[Event(daysAgo:1)],i<4)).ToList();
        Assert.True(IntentRules.Summarize(people,Today,30,5)[0].Suppressed);
        Assert.Null(IntentRules.Summarize(people,Today,30,5)[0].Explored);
        people=Enumerable.Range(0,10).Select(i=>Member(i,[Event(daysAgo:1)])).ToList();
        Assert.Equal(0,IntentRules.Summarize(people,Today,30,5)[0].UnselectedAfterSevenDays);
    }
    [Fact] public void SavedChoiceIsDistinctFromOrderAndSmallSelectedCountsAreHidden()
    {
        var people=Enumerable.Range(0,10).Select(i=>Member(i,i<2?[Event(),Event("choice_saved",daysAgo:1)]:[Event()])).ToList();
        var c=IntentRules.Summarize(people,Today,30,5)[0];Assert.Null(c.SavedChoice);Assert.Null(c.UnselectedAfterSevenDays);
        Assert.DoesNotContain("order",JsonDefaults.Serialize(c),StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void GrowthRequiresEnoughUniquePeopleInBothEqualWindows()
    {
        var people=Enumerable.Range(0,20).Select(i=>Member(i,i<10?[Event(),Event(daysAgo:40)]:[Event()])).ToList();
        Assert.Equal(100,IntentRules.Summarize(people,Today,30,5)[0].TrendPercent);
        Assert.Null(IntentRules.Summarize(people.Take(3),Today,30,5)[0].TrendPercent);
        Assert.Throws<ArgumentException>(()=>IntentRules.Summarize(people,Today,1,5));
    }
    [Fact] public void RetentionExcludesOldAndFutureEventsAndBoundsStorage()
    {
        var events=new[]{Event(daysAgo:100),Event(daysAgo:-1),Event(daysAgo:29),Event(daysAgo:30)};
        Assert.Single(IntentRules.Prune(events,Today,30));Assert.Equal(300,IntentRules.Prune(Enumerable.Repeat(Event(),500),Today,90).Count);
    }
    [Fact] public void ScenarioChangesOnlyClonedPolicyAndUsesTheAuthoritativeDecimalEngine()
    {
        var before=JsonDefaults.Serialize(Data.Snapshot());var e=Data.Employees[0];var engine=new PayrollEngine(Data);
        var after=IntentRules.ScenarioData(Data,"electric",200);var changed=new PayrollEngine(after);var target=Data.Cars.Single(c=>c.Id=="CAR-10");
        Assert.InRange(changed.Calculate(e,target,target.Participation).Net-engine.Calculate(e,target,target.Participation).Net,199.999999m,200.000001m);
        Assert.Equal(before,JsonDefaults.Serialize(Data.Snapshot()));
        var waiver=IntentRules.ScenarioData(Data,"waiver",200);Assert.Equal(Data.OccupationFor(e.Occupation).Maintenance+200,waiver.OccupationFor(e.Occupation).Maintenance);
        Assert.Throws<ArgumentException>(()=>IntentRules.ScenarioData(Data,"electric",5000));Assert.Throws<ArgumentException>(()=>IntentRules.ScenarioData(Data,"family",200));
    }
    [Fact] public void BudgetHasNoImplicitAdoptionPredictionAndRequiresAllExplicitAssumptions()
    {
        Assert.Null(IntentRules.Plan(new("electric",200),20,5));
        var input=new IntentScenario("electric",200,AssumedAdoptionPercent:50,MonthlyOperatingSaving:900,MonthlyGrantCost:500,OneOffCost:1000);
        var p=JsonNode.Parse(JsonDefaults.Serialize(IntentRules.Plan(input,40,5)))!;
        Assert.Equal(20,p["assumedAdopters"]!.GetValue<int>());Assert.Equal(76000,p["annualSampleNet"]!.GetValue<decimal>());Assert.Equal(2.5m,p["paybackMonths"]!.GetValue<decimal>());
        Assert.True(JsonNode.Parse(JsonDefaults.Serialize(IntentRules.Plan(input,10,6)))!["suppressed"]!.GetValue<bool>());
        Assert.Throws<ArgumentException>(()=>IntentRules.Plan(input with{MonthlyGrantCost=0},40,5));
    }
    [Theory][InlineData(4,90)][InlineData(101,90)][InlineData(5,29)][InlineData(5,366)]
    public void PrivacyPolicyCannotDisableThresholdOrAllowUnlimitedRetention(int cohort,int retention)=>Assert.Throws<ArgumentException>(()=>IntentRules.Validate(new(){MinimumCohort=cohort,RetentionDays=retention}));
    [Fact] public void InsightsBindingMustExistWhileHistoricalUnusedPayslipBindingsRemainCompatible()
    {
        var profile=new AiProfile();var ai=new AiConfiguration(false,profile,profile){Models=[new("text","text",profile)],ServiceModels=new(){{"extraction","text"},{"narration","text"},{"payslip","retired"},{"insights","text"}}};
        var configuration=new ManagedConfiguration(Data.Rules,Data.Cars,Data.OccupationDocuments,Data.Employees,ai);
        new ConfigurationValidator(Data).Validate(configuration);
        Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(Data).Validate(configuration with{Ai=ai with{ServiceModels=new(ai.ServiceModels){["insights"]="missing"}}}));
    }
    [Fact] public async Task CollectionRequiresExplicitConsentDeduplicatesAndOptOutClearsHistory()
    {
        var db=new IntentTestDatabase();var service=Service(db);var id=Data.Employees[0].Id;
        await service.Capture(id,"CAR-10","explore",null,default);Assert.Empty((await service.Read(id,default)).Events);
        await service.Preference(id,true,default);
        for(var i=0;i<10;i++)await service.Capture(id,"CAR-10","explore",null,default);
        Assert.Single((await service.Read(id,default)).Events);
        await service.Capture(id,"CAR-10","feedback","net",default);await service.Capture(id,"CAR-10","choice_saved",null,default);
        var doc=await service.Read(id,default);Assert.Equal(3,doc.Events.Count);
        var payload=JsonDefaults.Serialize(doc);Assert.DoesNotContain("salary",payload);Assert.DoesNotContain(Data.Employees[0].Name,payload);Assert.DoesNotContain("text",payload);
        await service.Preference(id,false,default);Assert.Empty((await service.Read(id,default)).Events);Assert.False((await service.Read(id,default)).Consented);
    }
    [Fact] public async Task PurgeRunsEvenWhileCollectionIsDisabledAndCannotExposeExpiredEvents()
    {
        var db=new IntentTestDatabase();var id=Data.Employees[0].Id;var old=new IntentDocument(1,true,[new(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100),"explore","CAR-10","electric","loss",null,1)],DateTimeOffset.UtcNow);
        await db.TryWriteRuleAsync("intent:"+id,null,JsonNode.Parse(JsonDefaults.Serialize(old))!);
        var service=Service(db,new(){Enabled=false,RetentionDays=30});await service.Purge(default);
        Assert.Empty((await service.Read(id,default)).Events);
    }
    [Fact] public async Task SyntheticJudgeDemoDoesNotWriteEmployeeProfilesEventsOrChoices()
    {
        var db=new IntentTestDatabase();var service=Service(db);var before=db.Rules.Dump();
        var demo=JsonNode.Parse(JsonDefaults.Serialize(service.Demo(new("electric",400,AssumedAdoptionPercent:50,MonthlyOperatingSaving:900,MonthlyGrantCost:500,OneOffCost:1000))))!;
        Assert.True(demo["synthetic"]!.GetValue<bool>());Assert.False(demo["scenario"]!["suppressed"]!.GetValue<bool>());Assert.Equal(before,db.Rules.Dump());Assert.Empty(await service.Members(default));
        Assert.DoesNotContain(Data.Employees[0].Name,demo.ToJsonString());Assert.DoesNotContain("salary",demo.ToJsonString());
    }
    private sealed class Gateway(string response):IAiCompletionClient
    {
        public string Input="";
        public Task<string> CompleteAsync(string name,JsonNode schema,string instructions,string text,CancellationToken ct){Input=text;return Task.FromResult(response);}
    }
    [Fact] public async Task AiCanOnlyPrioritizeAllowedAggregateCardsAndCannotSupplyNumbersOrFreeText()
    {
        var card=new IntentInsight("safe","title","10 employees tested; no causal conclusion","aggregate");var good=new Gateway("{\"insightId\":\"safe\"}");
        Assert.True((await IntentNarration.Select(good,[card],"",default)).usedAi);
        foreach(var response in new[]{"{\"insightId\":\"invented\"}","{\"insightId\":\"safe\",\"forecast\":35}","{\"text\":\"Save 1800000\"}","not json"})
        {var result=await IntentNarration.Select(new Gateway(response),[card],"",default);Assert.False(result.usedAi);Assert.Equal(card,result.insight);}
        Assert.DoesNotContain("salary",good.Input);
    }
}
