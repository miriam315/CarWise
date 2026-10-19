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
    public async Task<bool> HasReferenceDataAsync(CancellationToken ct=default) => (await db.ReadAsync("carwise_rules",limit:1,ct:ct)).Count>0;
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
    public async Task<ReferenceData> LoadAsync(CancellationToken ct=default)
    {
        var rules=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","active"}},ct:ct);
        if(rules.Count!=1)throw new InvalidOperationException("Reference data is not imported. Apply the Supabase migration, then run npm run db:import.");
        var cars=(await AllAsync("carwise_cars",ct)).Select(r=>Read<Car>(r["document"]!)).ToList();
        var employees=(await AllAsync("carwise_employees",ct)).Select(r=>Read<Employee>(r["document"]!)).ToList();
        var occupations=(await AllAsync("carwise_occupations",ct)).Select(r=>Read<Dictionary<string,JsonElement>>(r["document"]!)).ToList();
        var official=new List<OfficialRecord>();
        string? after=null;
        while(true)
        {
            var rows=await db.ReadAsync("carwise_official",ct:ct,afterId:after);
            if(rows.Count==0)break;
            after=rows[^1]!["id"]!.GetValue<string>();
            official.AddRange(rows.Select(r=>ReferenceData.ParseOfficial(Read<Dictionary<string,string>>(r!["document"]!))));
        }
        return ReferenceData.FromSnapshot(new(rules[0]!["document"]!.DeepClone(),cars,employees,occupations,official));
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
        return result.OrderByDescending(t=>t.Created).ToList();
    }
    public async Task<Ticket?> TicketAsync(Guid id,CancellationToken ct=default)
    {
        var rows=await db.ReadAsync("carwise_tickets",new Dictionary<string,string>{{"id",id.ToString()}},ct:ct);
        return rows.Count==0?null:TicketFrom(rows[0]!);
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
    public async Task<Ticket> CreateTicketAsync(Employee e,string target,string question,CancellationToken ct=default)=>TicketFrom(
        await db.RpcAsync("carwise_create_ticket",new(){["p_employee"]=JsonNode.Parse(JsonDefaults.Serialize(e)),["p_target"]=target,["p_question"]=question},ct));
    public async Task<Ticket> ResolveAsync(Guid id,decimal participation,string answer,string message,CancellationToken ct=default)=>TicketFrom(
        await db.RpcAsync("carwise_resolve_ticket",new(){["p_id"]=id.ToString(),["p_participation"]=participation,["p_answer"]=answer,["p_message"]=message},ct));
    public async Task<(Dictionary<string,decimal> Overrides,HashSet<string> Pending)> ParticipationAsync(string id,CancellationToken ct=default)
    {
        var tickets=await TicketsAsync(id,ct);
        var approved=tickets.Where(t=>t.Status=="resolved").GroupBy(t=>t.Target).ToDictionary(g=>g.Key,g=>g.First().Participation!.Value);
        return (approved,tickets.Where(t=>t.Status=="pending").Select(t=>t.Target).ToHashSet());
    }
}
