using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Xunit;
namespace CarWise.Tests;

public sealed class ConversationDatabase:IJsonDatabase
{
    public Dictionary<string,JsonNode> Conversations {get;}=[];
    public Dictionary<string,JsonNode> Rules {get;}=[];
    public List<JsonNode> Tickets {get;}=[];
    public List<(string Employee,string Role,string Text)> Messages {get;}=[];
    public TimeSpan ReadDelay {get;set;}
    public string Provider=>"conversation-test";
    public async Task<JsonArray> ReadAsync(string table,IReadOnlyDictionary<string,string>? filters=null,int offset=0,int limit=1000,CancellationToken ct=default,string? afterId=null)
    {
        if(ReadDelay>TimeSpan.Zero)await Task.Delay(ReadDelay,ct);
        IEnumerable<JsonNode> rows=table switch{
            "carwise_conversations"=>Conversations.Values,
            "carwise_tickets"=>Tickets,
            "carwise_rules"=>Rules.Values,
            "carwise_messages"=>Messages.Select((m,i)=>(JsonNode)new JsonObject{["id"]=i+1,["employee_id"]=m.Employee,["role"]=m.Role,["text"]=m.Text}),
            _=>[]};
        return new JsonArray(rows.Where(row=>filters is null||filters.All(filter=>(filter.Key=="id_prefix"?row["id"]!.ToString().StartsWith(filter.Value,StringComparison.Ordinal):row[filter.Key]?.ToString()==filter.Value))).Skip(offset).Take(limit).Select(row=>row.DeepClone()).ToArray());
    }
    public Task<JsonNode> RpcAsync(string name,JsonObject parameters,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(name=="carwise_save_conversation")
        {
            var id=parameters["p_employee"]!["id"]!.GetValue<string>();
            Conversations[id]=new JsonObject{["employee_id"]=id,["employee"]=parameters["p_employee"]!.DeepClone(),["target"]=parameters["p_target"]?.DeepClone(),["status"]="Active"};
            return Task.FromResult<JsonNode>(new JsonObject());
        }
        if(name=="carwise_create_ticket")
        {
            var id=parameters["p_employee"]!["id"]!.GetValue<string>();var target=parameters["p_target"]!.GetValue<string>();
            var ticket=Tickets.FirstOrDefault(t=>t["employee_id"]!.ToString()==id&&t["target"]!.ToString()==target&&t["status"]!.ToString()=="pending");
            if(ticket is null){ticket=new JsonObject{["id"]=Guid.NewGuid().ToString(),["employee_id"]=id,["snapshot"]=parameters["p_employee"]!.DeepClone(),["target"]=target,["question"]=parameters["p_question"]!.DeepClone(),["status"]="pending",["answer"]=null,["participation"]=null,["created"]=DateTimeOffset.UtcNow.ToString("O")};Tickets.Add(ticket);}
            return Task.FromResult(ticket.DeepClone());
        }
        throw new NotSupportedException(name);
    }
    public Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Messages.Add((employee,role,text));return Task.CompletedTask;}
    public Task UpsertAsync(string table,JsonArray rows,CancellationToken ct=default){if(table!="carwise_rules")throw new NotSupportedException();foreach(var row in rows)Rules[row!["id"]!.ToString()]=row.DeepClone();return Task.CompletedTask;}
    public Task<bool> TryWriteRuleAsync(string id,long? expectedRevision,JsonNode document,CancellationToken ct=default)
    {var current=Rules.GetValueOrDefault(id)?["document"]?["revision"]?.GetValue<long>();if(current!=expectedRevision)return Task.FromResult(false);Rules[id]=new JsonObject{["id"]=id,["document"]=document.DeepClone()};return Task.FromResult(true);}
}
public sealed class AdvisorIntakeTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AdvisorService Service(ConversationDatabase db,ReferenceData? data=null,IDataExtractionService? extraction=null,IResponseNarrationService? narration=null,AdvisorTiming? timing=null)
    {data??=Data;return new(data,new(data),new(db),extraction??new LocalExtractionService(),narration??new TemplateNarrationService(),timing);}
    private static async Task<JsonNode> Chat(AdvisorService service,Employee employee,string text,string? target=null)=>JsonNode.Parse(JsonDefaults.Serialize(await service.ChatAsync(new(employee,text,target,null),default)))!;
    private static Employee Returned(JsonNode result)=>JsonDefaults.Read<Employee>(result["employee"]!.ToJsonString());
    [Fact] public void MissingIsDistinctFromConfirmedZeroAndCannotBeCalculated()
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field!="bonus")]};
        Assert.Contains("bonus",IntakeFields.Missing(employee));Assert.Throws<ArgumentException>(()=>new PayrollEngine(Data).Calculate(employee,Data.Cars[0]));
        var confirmed=IntakeFields.Answer(employee,"bonus",0);Assert.Empty(IntakeFields.Missing(confirmed));Assert.Equal(0,confirmed.Bonus);
        var json=SimulationView.Incomplete(employee);Assert.Null(json["current"]);Assert.Empty(json["results"]!.AsArray());
    }
    [Fact] public void OmittedJsonNumericFieldsAreRejectedRatherThanDefaultedToZero()
    {
        var json=JsonNode.Parse(JsonDefaults.Serialize(Data.Employees[0]))!;json.AsObject().Remove("salary");
        Assert.Throws<JsonException>(()=>JsonDefaults.Read<Employee>(json.ToJsonString()));
    }
    [Fact] public async Task ClientCannotInventTheSavedQuestionAndNoCarHasNoParticipation()
    {
        var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field!="salary")],AdvisorState=new("salary","CAR-10")};
        var result=await Chat(Service(new()),employee,"19000");
        Assert.False(IntakeFields.Known(Returned(result),"salary"));Assert.Equal("salary",result["awaitingField"]!.ToString());
        var noCar=Data.Employees[0] with{CurrentCar=null,KnownFields=[..IntakeFields.All.Where(field=>field!="participation")]};
        var normalized=await Service(new()).RefreshKnownParticipationAsync(noCar,default);
        Assert.Equal(0,normalized.Participation);Assert.Empty(IntakeFields.Missing(normalized));
    }
    [Fact] public void OmittedCatalogueParticipationIsNotAnImplicitFreeCar()
    {
        var node=JsonNode.Parse(JsonDefaults.Serialize(Data.Cars[0]))!;node.AsObject().Remove("participation");
        Assert.Throws<JsonException>(()=>JsonDefaults.Read<Car>(node.ToJsonString()));
    }
    [Theory][InlineData("6%","pension",true)][InlineData("6 ₪","pension",false)][InlineData("2.5%","credits",false)]
    [InlineData("30.5","children",false)][InlineData("-1","salary",false)][InlineData("200001","salary",false)][InlineData("1 2","bonus",false)]
    public void ShortAnswersRequireCorrectUnitsAndRange(string text,string field,bool expected)=>Assert.Equal(expected,IntakeFields.TryNumber(text,field,out _));
    [Fact] public async Task SequentialAnswersSurviveNewServiceInstancesAndAskOnlyMissingFields()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[]};
        var response=await Chat(Service(db),employee,"השווה לרכב שבחרתי","CAR-10");
        var sequence=new[]{("salary","19000.25"),("commission","0"),("bonus","0"),("pension","6%"),("fund","2.5%"),("credits","2.25"),("children","4")};
        foreach(var (field,answer) in sequence)
        {
            Assert.Equal(field,response["awaitingField"]!.GetValue<string>());
            // Deliberately send the stale original profile: server-persisted context wins.
            response=await Chat(Service(db),employee,answer);
        }
        Assert.Equal("currentCar",response["awaitingField"]!.GetValue<string>());
        response=await Chat(Service(db),employee,"זה הרכב הנוכחי","CAR-09");
        Assert.Equal("participation",response["awaitingField"]!.GetValue<string>());
        response=await Chat(Service(db),employee,"350");Assert.True(response["complete"]!.GetValue<bool>());
        Assert.Equal("CAR-10",response["target"]!.GetValue<string>());Assert.Contains("הנטו המשוער",response["reply"]!.ToString());
        var saved=Returned(response);Assert.Equal(19000.25m,saved.Salary);Assert.Equal(0,saved.Bonus);Assert.Empty(IntakeFields.Missing(saved));
    }
    [Fact] public async Task KnownProfilesDoNotRepeatOnboardingAndFreeTextConfirmsOnlyExplicitFields()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[]};
        var response=await Chat(Service(db),employee,"שכר בסיס 19000 עמלות 0");var saved=Returned(response);
        Assert.Equal(new[]{"salary","commission"},saved.KnownFields);Assert.Equal("bonus",response["awaitingField"]!.ToString());
        response=await Chat(Service(new()),Data.Employees[0],"בחרתי","CAR-10");Assert.Null(response["awaitingField"]);Assert.True(response["complete"]!.GetValue<bool>());
    }
    [Fact] public async Task UnknownNewParticipationCreatesOneTicketAndNoFinalMoney()
    {
        var snapshot=Data.Snapshot();var data=ReferenceData.FromSnapshot(snapshot with{Cars=snapshot.Cars.Select(car=>car.Id=="CAR-10"?car with{ParticipationKnown=false}:car).ToList()});
        var db=new ConversationDatabase();var employee=data.Employees[0];var service=Service(db,data);
        var first=await Chat(service,employee,"בחרתי","CAR-10");await Chat(service,employee,"בחרתי שוב","CAR-10");
        Assert.Single(db.Tickets);Assert.Contains("שלחתי שאלה",first["reply"]!.ToString());Assert.DoesNotContain("הנטו המשוער",first["reply"]!.ToString());
        var view=SimulationView.Complete(await service.SimulateAsync(employee,default));var pending=view["results"]!.AsArray().Single(r=>r!["id"]!.ToString()=="CAR-10")!;
        Assert.True(pending["pending"]!.GetValue<bool>());Assert.Null(pending["net"]);Assert.Null(pending["delta"]);
        db.Tickets[0]["status"]="resolved";db.Tickets[0]["answer"]="מאושר";db.Tickets[0]["participation"]=350m;
        var resumed=await Chat(Service(db,data),employee,"בחרתי","CAR-10");Assert.Contains("הנטו המשוער",resumed["reply"]!.ToString());
        Assert.False((await Service(db,data).SimulateAsync(employee,default)).Results.Single(r=>r.Id=="CAR-10").Pending);
    }
    [Fact] public async Task UnknownCurrentParticipationIsEscalatedAndRecoveredFromApprovedTicket()
    {
        var db=new ConversationDatabase();var employee=Data.Employees[0] with{KnownFields=[..IntakeFields.All.Where(field=>field!="participation")]};
        await Chat(Service(db),employee,"בחרתי","CAR-10");var waiting=await Chat(Service(db),employee,"לא יודע");
        Assert.Single(db.Tickets);Assert.Equal(employee.CurrentCar,db.Tickets[0]["target"]!.ToString());Assert.Contains("לא אציג נטו",waiting["reply"]!.ToString());
        db.Tickets[0]["status"]="resolved";db.Tickets[0]["answer"]="אישור";db.Tickets[0]["participation"]=0m;
        var resumed=await Chat(Service(db),employee,"נמשיך");Assert.True(resumed["complete"]!.GetValue<bool>());Assert.Equal(0,Returned(resumed).Participation);
    }
    [Fact] public async Task CatalogueDefaultsAreExplainedWithoutChangingCurrentPersonalParticipation()
    {
        var db=new ConversationDatabase();var e=Data.Employees[0];var reply=await Chat(Service(db),e,"בחרתי","CAR-10");
        Assert.Contains("ברירת מחדל בקטלוג",reply["reply"]!.ToString());Assert.Empty(db.Tickets);
        var simulation=await Service(db).SimulateAsync(e,default);
        Assert.Equal(e.Participation,simulation.Current.Participation);
        Assert.Equal("catalogue",simulation.Results.Single(r=>r.Id=="CAR-10").ParticipationSource);
    }
    [Fact] public async Task PersonalAlternativeAmountAndRepresentativeConfirmationHaveDistinctSources()
    {
        var db=new ConversationDatabase();var service=Service(db);var e=Data.Employees[0];
        await Chat(service,e,"בחרתי","CAR-10");
        var reported=await Chat(service,e,"השתתפות ברכב החדש 777","CAR-10");
        var result=(await service.SimulateAsync(e,default)).Results.Single(r=>r.Id=="CAR-10");
        Assert.Equal(777,result.Participation);Assert.Equal("employee",result.ParticipationSource);Assert.Empty(db.Tickets);
        var dispute=await Chat(service,e,"אצלי סוכם אחרת ולא יודע כמה, אבקש אישור נציג","CAR-10");
        Assert.Equal("targetParticipation",dispute["awaitingField"]!.ToString());Assert.Single(db.Tickets);
        Assert.True((await service.SimulateAsync(e,default)).Results.Single(r=>r.Id=="CAR-10").Pending);
        db.Tickets[0]["status"]="resolved";db.Tickets[0]["participation"]=444m;
        result=(await Service(db).SimulateAsync(e,default)).Results.Single(r=>r.Id=="CAR-10");
        Assert.Equal(444,result.Participation);Assert.Equal("representative",result.ParticipationSource);
        Assert.Equal(e.Participation,(await service.SimulateAsync(e,default)).Current.Participation);
    }
    [Fact] public async Task AlternativeReportNeverFillsAnUnknownCurrentPersonalContribution()
    {
        var db=new ConversationDatabase();var e=Data.Employees[0];await new Repository(db).ReportParticipationAsync(e.Id,e.CurrentCar!,999,default);
        var unknown=e with{KnownFields=[..IntakeFields.All.Where(f=>f!="participation")]};
        Assert.False(IntakeFields.Known(await Service(db).RefreshKnownParticipationAsync(unknown,default),"participation"));
    }
    [Fact] public async Task UnknownCatalogueVehicleCreatesNonFinancialReferralWithoutInventingNet()
    {
        var db=new ConversationDatabase();var e=Data.Employees[0];var reply=await Chat(Service(db),e,"רכב בדיקה XYZ לא מופיע בקטלוג");
        Assert.Contains("לא אנחש",reply["reply"]!.ToString());Assert.Null(reply["target"]);
        var ticket=Assert.Single(await new Repository(db).TicketsAsync(e.Id));Assert.True(ParticipationRules.GeneralTicket(ticket.Target));
        await new Repository(db).ResolveAsync(ticket.Id,0,"synthetic answer","synthetic answer",default);
        Assert.Empty((await new Repository(db).ParticipationAsync(e.Id)).Overrides);
    }
    private sealed class SlowExtraction:IDataExtractionService
    {public string Mode=>"ai";public async Task<ExtractedData> ExtractAsync(string text,Employee known,IReadOnlyList<Message> history,CancellationToken ct){await Task.Delay(Timeout.InfiniteTimeSpan,ct);return new();}}
    private sealed class SlowNarration:IResponseNarrationService
    {public async Task<string> NarrateAsync(Alternative result,CancellationToken ct){await Task.Delay(Timeout.InfiniteTimeSpan,ct);return "never";}}
    [Fact] public async Task AiDeadlinesShareABudgetAndFallBackToVerifiedCode()
    {
        var clock=Stopwatch.StartNew();var service=Service(new(),extraction:new SlowExtraction(),narration:new SlowNarration(),timing:new(TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(25),TimeSpan.FromMilliseconds(25)));
        var response=await Chat(service,Data.Employees[0],"שכר בסיס 19500 השווה ATTO 3");
        Assert.Equal(19500,Returned(response).Salary);Assert.Contains("CAR-10",response["candidates"]!.AsArray().Select(node=>node!.ToString()));
        response=await Chat(service,Data.Employees[0],"אישור בחירה","CAR-10");
        Assert.Contains("הנטו המשוער",response["reply"]!.ToString());Assert.True(clock.Elapsed<TimeSpan.FromSeconds(2));
    }
    [Fact] public async Task TotalDeadlineReturnsNoUnverifiedNumbersAndCallerCancellationIsPreserved()
    {
        var service=Service(new(){ReadDelay=TimeSpan.FromSeconds(1)},timing:new(TimeSpan.FromMilliseconds(40),TimeSpan.FromMilliseconds(20),TimeSpan.FromMilliseconds(20)));
        var response=await Chat(service,Data.Employees[0],"שלום");Assert.Equal("timeout",response["status"]!.ToString());Assert.False(response["complete"]!.GetValue<bool>());
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ChatAsync(new(Data.Employees[0],"שלום",null,null),cancelled.Token));
    }
}
