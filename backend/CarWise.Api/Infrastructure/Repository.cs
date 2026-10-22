using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Domain;
namespace CarWise.Api.Infrastructure;

public sealed class Repository(IJsonDatabase db)
{
    public string Provider=>db.Provider;
    private static T Read<T>(JsonNode n)=>JsonDefaults.Read<T>(n.ToJsonString());
    private async Task<List<JsonNode>> AllAsync(string table,CancellationToken ct)
    {
        var all=new List<JsonNode>();
        string? after=null;
        while(true)
        {
            var rows=await db.ReadAsync(table,ct:ct,afterId:after);
            if(rows.Count==0)break;
            after=rows[^1]!["id"]!.GetValue<string>();
            all.AddRange(rows.Select(x=>x!.DeepClone()));
        }
        return all;
    }
    public async Task<bool> HasReferenceDataAsync(CancellationToken ct=default) => (await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","active"}},limit:1,ct:ct)).Count>0;
    public async Task SeedAsync(ReferenceData data,CancellationToken ct=default)
    {
        async Task Write<T>(string table,IEnumerable<T> values,Func<T,string> key)
        {
            foreach(var batch in values.Chunk(500))
            {
                var rows=new JsonArray(batch.Select(v=>(JsonNode)new JsonObject{["id"]=key(v),["document"]=JsonNode.Parse(JsonDefaults.Serialize(v))}).ToArray());
                await db.UpsertAsync(table,rows,ct);
            }
        }
        await Write("carwise_cars",data.Cars,c=>c.Id);await Write("carwise_employees",data.Employees,e=>e.Id);
        await Write("carwise_occupations",data.OccupationDocuments,o=>o["occupation"].GetString()!);
        await Write("carwise_official",data.Official.Values.Select(o=>o.Document),d=>ReferenceData.ParseOfficial(d).Key);
        // Rules are the completion marker. Failed partial imports are safe to retry.
        await db.UpsertAsync("carwise_rules",new JsonArray(new JsonObject{["id"]="active",["document"]=data.Rules.DeepClone()}),ct);
    }
    public async Task<ReferenceData> LoadAsync(CancellationToken ct=default,Action<int>? officialProgress=null)
    {
        var rules=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","active"}},ct:ct);
        if(rules.Count!=1)throw new InvalidOperationException("Reference data is not imported. Apply the Supabase migration, then run npm run db:import.");
        var cars=(await AllAsync("carwise_cars",ct)).Select(r=>Read<Car>(r["document"]!)).ToList();
        var employees=(await AllAsync("carwise_employees",ct)).Select(r=>Read<Employee>(r["document"]!)).ToList();
        var occupations=(await AllAsync("carwise_occupations",ct)).Select(r=>Read<Dictionary<string,JsonElement>>(r["document"]!)).ToList();
        var year=rules[0]!["document"]!["valueOfUse"]!["taxYear"]?.GetValue<int>()??2026;
        var official=await LoadOfficialForCarsAsync(cars,year,ct);
        officialProgress?.Invoke(official.Count);
        var total=await db.CountAsync("carwise_official",ct);
        return ReferenceData.FromSnapshot(new(rules[0]!["document"]!.DeepClone(),cars,employees,occupations,official.Values.ToList()),total);
    }
    public async Task<IReadOnlyDictionary<string,OfficialRecord>> LoadOfficialForCarsAsync(IEnumerable<Car> cars,int year,CancellationToken ct)
    {
        var records=new Dictionary<string,OfficialRecord>();
        var keys=cars.Select(c=>OfficialRecord.KeyFor(year,c.Year,"1",c.MakerCode,c.ModelCode)).Distinct().ToArray();
        foreach(var batch in keys.Chunk(40))
        {
            var rows=await db.ReadAsync("carwise_official",new Dictionary<string,string>{{"id_in",string.Join(',',batch)}},limit:batch.Length,ct:ct);
            foreach(var row in rows){var record=ReferenceData.ParseOfficial(Read<Dictionary<string,string>>(row!["document"]!));records.Add(record.Key,record);}
        }
        if(records.Count!=keys.Length)throw new InvalidDataException("Missing official records for catalogue cars.");
        return records;
    }

    private static Ticket TicketFrom(JsonNode r)=>new(Guid.Parse(r["id"]!.GetValue<string>()),r["employee_id"]!.GetValue<string>(),
        Read<Employee>(r["snapshot"]!),r["target"]!.GetValue<string>(),r["question"]!.GetValue<string>(),r["status"]!.GetValue<string>(),
        r["answer"]?.GetValue<string>(),r["participation"]?.GetValue<decimal>(),DateTimeOffset.Parse(r["created"]!.GetValue<string>(),System.Globalization.CultureInfo.InvariantCulture));
    public async Task<List<Ticket>> TicketsAsync(string? employeeId=null,CancellationToken ct=default)
    {
        var result=new List<Ticket>();
        for(var offset=0;;)
        {
            var rows=await db.ReadAsync("carwise_tickets",employeeId is null?null:new Dictionary<string,string>{{"employee_id",employeeId}},offset:offset,ct:ct);
            if(rows.Count==0)break;result.AddRange(rows.Select(r=>TicketFrom(r!)));offset+=rows.Count;
        }
        for(var offset=0;;)
        {
            var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","catalogue-ticket:"}},offset:offset,ct:ct);
            if(rows.Count==0)break;
            foreach(var row in rows){var ticket=TicketFrom(row!["document"]!);if(employeeId is null||ticket.EmployeeId==employeeId)result.Add(ticket);}
            offset+=rows.Count;
        }
        return result.OrderByDescending(t=>t.Created).ToList();
    }
    public async Task<Ticket?> TicketAsync(Guid id,CancellationToken ct=default)
    {
        var rows=await db.ReadAsync("carwise_tickets",new Dictionary<string,string>{{"id",id.ToString()}},ct:ct);
        if(rows.Count>0)return TicketFrom(rows[0]!);
        var general=await GeneralTicketDocument(id,ct);return general is null?null:TicketFrom(general);
    }
    public async Task<List<Message>> HistoryAsync(string employee,CancellationToken ct=default)
    {
        var result=new List<Message>();
        for(var offset=0;;)
        {
            var rows=await db.ReadAsync("carwise_messages",new Dictionary<string,string>{{"employee_id",employee}},offset:offset,ct:ct);
            if(rows.Count==0)break;result.AddRange(rows.Select(r=>new Message(r!["role"]!.GetValue<string>(),r["text"]!.GetValue<string>())));offset+=rows.Count;
        }
        return result;
    }
    public Task AddMessageAsync(string id,string role,string text,CancellationToken ct=default)=>db.AddMessageAsync(id,role,text,ct);
    public async Task SaveConversationAsync(Employee e,string? target,CancellationToken ct=default) =>
        await db.RpcAsync("carwise_save_conversation",new(){["p_employee"]=JsonNode.Parse(JsonDefaults.Serialize(e)),["p_target"]=target},ct);
    public async Task<Conversation?> ConversationAsync(string employee,CancellationToken ct=default)
    {
        var rows=await db.ReadAsync("carwise_conversations",new Dictionary<string,string>{{"employee_id",employee}},ct:ct);
        return rows.Count==0?null:new(Read<Employee>(rows[0]!["employee"]!),rows[0]!["status"]!.GetValue<string>(),rows[0]!["target"]?.GetValue<string>());
    }
    private Task<JsonArray> GeneralTicketRows(Guid id,CancellationToken ct)=>db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","catalogue-ticket:"+id}},ct:ct);
    private async Task<JsonNode?> GeneralTicketDocument(Guid id,CancellationToken ct)=>(await GeneralTicketRows(id,ct)).FirstOrDefault()?["document"];
    public async Task<Ticket> CreateTicketAsync(Employee e,string target,string question,CancellationToken ct=default)
    {
        if(!ParticipationRules.GeneralTicket(target))return TicketFrom(await db.RpcAsync("carwise_create_ticket",new(){["p_employee"]=JsonNode.Parse(JsonDefaults.Serialize(e)),["p_target"]=target,["p_question"]=question},ct));
        // Nonfinancial catalogue requests do not pretend to be cars or bypass the financial ticket FK.
        var id=new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e.Id+"/"+target)).AsSpan(0,16));
        var previous=await GeneralTicketDocument(id,ct);
        if(previous?["status"]?.GetValue<string>()=="pending")return TicketFrom(previous);
        var revision=previous?["revision"]?.GetValue<long>();
        var history=previous?["history"]?.DeepClone()?.AsArray()??new JsonArray();
        if(previous is not null){history.Add(new JsonObject{["answer"]=previous["answer"]?.DeepClone(),["resolved_at"]=previous["resolved_at"]?.DeepClone()});while(history.Count>20)history.RemoveAt(0);}
        var document=new JsonObject{["revision"]=(revision??0)+1,["id"]=id.ToString(),["employee_id"]=e.Id,["snapshot"]=JsonNode.Parse(JsonDefaults.Serialize(e)),["target"]=target,["question"]=question,["status"]="pending",["answer"]=null,["participation"]=null,["created"]=DateTimeOffset.UtcNow.ToString("O"),["history"]=history};
        if(!await db.TryWriteRuleAsync("catalogue-ticket:"+id,revision,document,ct))throw new CarWise.Api.Administration.ConfigurationConflictException();
        await AddMessageAsync(e.Id,"assistant","הפנייה לגבי הרכב שאינו בקטלוג הועברה לנציג. לא מוצג חישוב כספי לרכב הזה.",ct);
        await SaveConversationAsync(e,null,ct);return TicketFrom(document);
    }
    public async Task<Ticket> ResolveAsync(Guid id,decimal participation,string answer,string message,CancellationToken ct=default)
    {
        var document=await GeneralTicketDocument(id,ct);
        if(document is null)return TicketFrom(await db.RpcAsync("carwise_resolve_ticket",new(){["p_id"]=id.ToString(),["p_participation"]=participation,["p_answer"]=answer,["p_message"]=message},ct));
        if(document["status"]!.GetValue<string>()=="resolved")return TicketFrom(document);
        var revision=document["revision"]!.GetValue<long>();var next=document.DeepClone();next["revision"]=revision+1;next["status"]="resolved";next["answer"]=answer;next["participation"]=0m;next["resolved_at"]=DateTimeOffset.UtcNow.ToString("O");
        if(!await db.TryWriteRuleAsync("catalogue-ticket:"+id,revision,next,ct))throw new CarWise.Api.Administration.ConfigurationConflictException();
        await AddMessageAsync(next["employee_id"]!.GetValue<string>(),"assistant",message,ct);return TicketFrom(next);
    }
    private static string ReportedPrefix(string employee)=>"reported-participation:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(employee)))+":";
    public Task ReportParticipationAsync(string employee,string target,decimal amount,CancellationToken ct)
    {
        if(amount<0||amount>20000)throw new ArgumentException("סכום ההשתתפות חייב להיות בין 0 ל־20,000 ₪");
        var id=ReportedPrefix(employee)+target;
        return db.UpsertAsync("carwise_rules",new JsonArray(new JsonObject{["id"]=id,["document"]=new JsonObject{["target"]=target,["amount"]=amount,["source"]="employee",["at"]=DateTimeOffset.UtcNow.ToString("O")}}),ct);
    }
    public async Task<(Dictionary<string,decimal> Overrides,HashSet<string> Pending)> ParticipationAsync(string id,CancellationToken ct=default)
    {var result=await ParticipationWithSourcesAsync(id,ct);return(result.Overrides,result.Pending);}
    public async Task<(Dictionary<string,decimal> Overrides,HashSet<string> Pending,Dictionary<string,string> Sources)> ParticipationWithSourcesAsync(string id,CancellationToken ct=default)
    {
        var amounts=new Dictionary<string,decimal>();var sources=new Dictionary<string,string>();
        var reported=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix",ReportedPrefix(id)}},ct:ct);
        foreach(var row in reported)
        {
            var document=row?["document"];var target=document?["target"]?.GetValue<string>();var amount=document?["amount"]?.GetValue<decimal>();
            if(target is not null&&amount is >=0 and <=20000){amounts[target]=amount.Value;sources[target]="employee";}
        }
        var tickets=(await TicketsAsync(id,ct)).Where(t=>!ParticipationRules.GeneralTicket(t.Target)).ToList();
        foreach(var approved in tickets.Where(t=>t.Status=="resolved").GroupBy(t=>t.Target).Select(g=>g.First()))
        {amounts[approved.Target]=approved.Participation!.Value;sources[approved.Target]="representative";}
        return(amounts,tickets.Where(t=>t.Status=="pending").Select(t=>t.Target).ToHashSet(),sources);
    }
}
