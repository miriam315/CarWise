using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Accounting;

public sealed class AiOperationContext
{
    // There is one durable, ongoing conversation per employee in the current application.
    public string? ConversationId {get;set;}
    public string ActionId {get;}=Guid.NewGuid().ToString("N");
}
public sealed record TokenUsage(long Input,long CachedInput,long CacheWrite,long Output)
{
    public static TokenUsage? Parse(string provider,JsonNode? body)
    {
        try
        {
            var u=body?[provider=="gemini"?"usageMetadata":"usage"];if(u is null)return null;
            long Read(string field,bool required=false)=>u[field]?.GetValue<long>()??(required?throw new InvalidDataException():0);
            TokenUsage usage;
            if(provider=="gemini")usage=new(Read("promptTokenCount",true),Read("cachedContentTokenCount"),0,Read("candidatesTokenCount",true)+Read("thoughtsTokenCount"));
            else if(provider=="anthropic")
            {
                var cached=Read("cache_read_input_tokens");var write=Read("cache_creation_input_tokens");
                usage=new(Read("input_tokens",true)+cached+write,cached,write,Read("output_tokens",true));
            }
            else usage=new(Read("prompt_tokens",true),u["prompt_tokens_details"]?["cached_tokens"]?.GetValue<long>()??0,0,Read("completion_tokens",true));
            return usage.Input>=0&&usage.CachedInput>=0&&usage.CacheWrite>=0&&usage.Output>=0&&usage.Input>=usage.CachedInput+usage.CacheWrite?usage:null;
        }
        catch(Exception error)when(error is InvalidDataException or InvalidOperationException or FormatException or OverflowException){return null;}
    }
    public decimal? Cost(AiModelPrice? price)=>price is null||(CacheWrite>0&&price.CacheWriteUsdPerMillion is null)?null:
        ((Input-CachedInput-CacheWrite)*price.InputUsdPerMillion+CachedInput*price.CachedInputUsdPerMillion+
         CacheWrite*(price.CacheWriteUsdPerMillion??0)+Output*price.OutputUsdPerMillion)/1000000m;
}
public sealed record AiUsageRecord(string Id,DateTimeOffset StartedAt,DateTimeOffset? CompletedAt,string? ConversationId,string ActionId,
    string Operation,string Provider,string Host,string Model,long ConfigurationRevision,AiModelPrice? Pricing,ExchangeRate? ExchangeRate,
    string Status,TokenUsage? Tokens,decimal? EstimatedUsd,decimal? EstimatedIls,string? ErrorCode)
{
    public string CredentialId {get;init;}="primary";
    public string CredentialName {get;init;}="מפתח ראשי";
    public int Attempt {get;init;}=1;
    public string? InvocationId {get;init;}
}
public sealed class UsageAccounting(IJsonDatabase database,ConfigurationSnapshot snapshot,ExchangeRates rates,AiOperationContext context,
    ILogger<UsageAccounting> logger)
{
    public async Task<AiUsageRecord> StartAsync(AiProfile profile,string operation,CancellationToken ct,string credentialId="primary",string credentialName="מפתח ראשי",int attempt=1,string? invocationId=null)
    {
        var host=new Uri(profile.BaseUrl).Host;
        var price=snapshot.Document.Configuration.Ai.Pricing.SingleOrDefault(p=>p.Provider==profile.Provider&&p.Host==host&&p.Model==profile.Model);
        var record=new AiUsageRecord("usage:"+DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff")+":"+Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,null,context.ConversationId,context.ActionId,operation,profile.Provider,host,profile.Model,snapshot.Document.PolicyVersion,
            price,rates.Current,"pending",null,null,null,null){CredentialId=credentialId,CredentialName=credentialName,Attempt=attempt,InvocationId=invocationId};
        // Reserve durably before contacting a paid API. A crash leaves an explicit unknown/pending record.
        await StoreAsync(record,ct);return record;
    }
    public async Task FinishAsync(AiUsageRecord record,JsonNode? body,Exception? error)
    {
        var tokens=TokenUsage.Parse(record.Provider,body);var usd=tokens?.Cost(record.Pricing);
        var next=record with{CompletedAt=DateTimeOffset.UtcNow,Status=error is null?"completed":"failed",Tokens=tokens,
            EstimatedUsd=usd,EstimatedIls=usd*record.ExchangeRate?.UsdIls,ErrorCode=error is AI.AiUnavailableException ai?ai.Code:error is null?null:"request_failed"};
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await StoreAsync(next,timeout.Token);
        }
        catch(Exception){logger.LogError("AI usage completion could not be saved for {UsageId}; its pending record requires reconciliation.",record.Id);}
    }
    private Task StoreAsync(AiUsageRecord record,CancellationToken ct)=>database.UpsertAsync("carwise_rules",new JsonArray(new JsonObject{
        ["id"]=record.Id,["document"]=JsonNode.Parse(JsonDefaults.Serialize(record))}),ct);
}
public sealed class UsageReports(IJsonDatabase database)
{
    public async Task<object> ConversationAsync(string id,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>100)throw new ArgumentException("מזהה שיחה לא תקין");
        long calls=0,unknownUsd=0,unknownIls=0;decimal usd=0,ils=0;string? after=null;bool more;
        do
        {
            var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","usage:"},{"document->>conversationId",id}},limit:1000,ct:ct,afterId:after);
            foreach(var row in rows)
            {
                var r=JsonDefaults.Read<AiUsageRecord>(row!["document"]!.ToJsonString());calls++;usd+=r.EstimatedUsd??0;ils+=r.EstimatedIls??0;
                if(r.EstimatedUsd is null)unknownUsd++;if(r.EstimatedIls is null)unknownIls++;after=r.Id;
            }
            more=rows.Count==1000;
        }while(more&&calls<100000);
        return new{conversationId=id,calls,knownUsd=usd,knownIls=ils,unknownUsd,unknownIls,truncated=more};
    }
    public async Task<object> ReadAsync(string? after,int? limit,CancellationToken ct)
    {
        var count=limit??100;if(count is <1 or >500||after is {Length:>100}||after is not null&&!after.StartsWith("usage:",StringComparison.Ordinal))throw new ArgumentException("עמוד שימוש לא תקין");
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","usage:"}},limit:count,ct:ct,afterId:after);
        var records=rows.Select(r=>JsonDefaults.Read<AiUsageRecord>(r!["document"]!.ToJsonString())).ToArray();
        return new{items=records,nextCursor=records.Length==count?records[^1].Id:null,scope="page",
            conversations=records.GroupBy(r=>r.ConversationId).Select(g=>new{conversationId=g.Key,calls=g.Count(),
                knownUsd=g.Sum(r=>r.EstimatedUsd??0),knownIls=g.Sum(r=>r.EstimatedIls??0),unknownUsd=g.Count(r=>r.EstimatedUsd is null),unknownIls=g.Count(r=>r.EstimatedIls is null)})};
    }
}
