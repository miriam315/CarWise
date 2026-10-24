using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Accounting;

public sealed record OpenRouterRate(decimal? InputUsdPerMillion,decimal? CachedInputUsdPerMillion,
    decimal? OutputUsdPerMillion,decimal? CacheWriteUsdPerMillion,string? UnavailableReason);
public sealed record OpenRouterCatalog(DateTimeOffset RetrievedAt,Dictionary<string,OpenRouterRate> Models);

/// <summary>Public reference token tariffs. Fetching pricing never changes inference routing.</summary>
public sealed class OpenRouterPricing(IJsonDatabase database,IHttpClientFactory clients)
{
    public const string Source="https://openrouter.ai/api/v1/models";
    private const string RuleId="pricing:openrouter";
    private readonly SemaphoreSlim gate=new(1,1);
    private OpenRouterCatalog? current;
    public OpenRouterCatalog? Current=>Volatile.Read(ref current);
    public DateTimeOffset? LastAttempt {get;private set;}
    public string? LastError {get;private set;}
    public static bool Supports(AiProfile profile)=>profile.Provider is "compatible" or "openai"&&
        Uri.TryCreate(profile.BaseUrl,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="openrouter.ai"&&
        uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath.TrimEnd('/')=="/api/v1";
    public static string Key(AiProfile profile)=>profile.Provider+"/"+new Uri(profile.BaseUrl).Host+"/"+profile.Model;
    public static string? SourceModel(AiProfile profile,AiConfiguration settings)
    {
        if(settings.OpenRouterPriceMappings.TryGetValue(Key(profile),out var mapped))return mapped;
        if(Supports(profile))return profile.Model;
        // Only exact, namespaced IDs on known direct endpoints. No fuzzy matching or version stripping.
        var host=new Uri(profile.BaseUrl).Host;
        return (profile.Provider,host) switch
        {
            ("openai","api.openai.com")=>"openai/"+profile.Model,
            ("gemini","generativelanguage.googleapis.com")=>"google/"+profile.Model,
            ("anthropic","api.anthropic.com")=>"anthropic/"+profile.Model,
            _=>null
        };
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",RuleId}},ct:ct);
        if(rows.Count==0)return;
        var saved=JsonDefaults.Read<OpenRouterCatalog>(rows[0]!["document"]!.ToJsonString());
        if(saved.Models is null||saved.Models.Count>10000||saved.RetrievedAt>DateTimeOffset.UtcNow.AddMinutes(5))throw new InvalidDataException();
        await gate.WaitAsync(ct);
        try{if(Current is null||saved.RetrievedAt>Current.RetrievedAt)Volatile.Write(ref current,saved);}
        finally{gate.Release();}
    }
    public static OpenRouterCatalog Parse(JsonNode body,DateTimeOffset now)
    {
        if(body["data"] is not JsonArray rows||rows.Count is <1 or >10000)throw new InvalidDataException();
        var models=new Dictionary<string,OpenRouterRate>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            if(row is not JsonObject item||item["id"] is not JsonValue idValue||!idValue.TryGetValue<string>(out var id)||
                id.Length is <1 or >160||!Regex.IsMatch(id,@"^[A-Za-z0-9_./:@-]+$"))continue;
            var pricing=item["pricing"] as JsonObject;
            decimal? Rate(string name)
            {
                var node=pricing?[name];if(node is null)return null;
                var text=node is JsonValue value&&value.TryGetValue<string>(out var s)?s:node.ToJsonString();
                return decimal.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var rate)&&rate>=0&&rate<=.1m?rate*1000000m:null;
            }
            var input=Rate("prompt");var output=Rate("completion");var cached=Rate("input_cache_read");var write=Rate("input_cache_write");
            string? reason=input is null||output is null?"לא פורסם תעריף קלט ופלט תקין למודל.":null;
            if(cached>input){cached=null;reason="תעריף המטמון במקור אינו תקין.";}
            // A single flat rate cannot represent context tiers or time-dependent overrides.
            // Display the base prices, but require an explicit manual tariff for an estimate.
            if(pricing?["overrides"] is JsonNode overrides&&overrides.ToJsonString() is not ("[]" or "{}" or "null"))
                reason="למודל יש מדרגות או תנאי תמחור נוספים. נדרש תעריף ידני מתאים לחישוב העלות.";
            var reasoning=Rate("internal_reasoning");
            if(reasoning is not null&&reasoning!=output)reason="תעריף החשיבה שונה מתעריף הפלט. נדרש תעריף ידני מתאים.";
            if(pricing?["input_cache_write_1h"] is not null&&Rate("input_cache_write_1h")!=write)write=null;
            if(!models.TryAdd(id,new(input,cached,output,write,reason)))throw new InvalidDataException();
        }
        if(!models.Values.Any(rate=>rate.InputUsdPerMillion is not null&&rate.OutputUsdPerMillion is not null))throw new InvalidDataException();
        return new(now,models);
    }
    public AiModelPrice? PriceFor(AiProfile profile,AiConfiguration settings)
    {
        var catalog=Current;
        var sourceModel=SourceModel(profile,settings);
        if(sourceModel is null||catalog is null||!catalog.Models.TryGetValue(sourceModel,out var rate)||rate.UnavailableReason is not null||rate.InputUsdPerMillion is null||rate.OutputUsdPerMillion is null)return null;
        return new(profile.Provider,new Uri(profile.BaseUrl).Host,profile.Model,rate.InputUsdPerMillion.Value,rate.CachedInputUsdPerMillion,
            rate.OutputUsdPerMillion.Value,rate.CacheWriteUsdPerMillion,Source){RetrievedAt=catalog.RetrievedAt,SourceModel=sourceModel,ReferenceOnly=!Supports(profile)};
    }
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            LastAttempt=DateTimeOffset.UtcNow;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(25));var token=deadline.Token;
            using var client=clients.CreateClient("OpenRouterPricing");
            using var response=await client.GetAsync(Source,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            const int limit=8*1024*1024;
            if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException();
            await using var stream=await response.Content.ReadAsStreamAsync(token);using var body=new MemoryStream();var buffer=new byte[16384];int count;
            while((count=await stream.ReadAsync(buffer,token))>0){if(body.Length+count>limit)throw new InvalidDataException();body.Write(buffer,0,count);}
            var next=Parse(JsonNode.Parse(body.ToArray())??throw new InvalidDataException(),DateTimeOffset.UtcNow);
            await database.UpsertAsync("carwise_rules",new JsonArray(new JsonObject{["id"]=RuleId,["document"]=JsonNode.Parse(JsonDefaults.Serialize(next))}),token);
            Volatile.Write(ref current,next);LastError=null;return true;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception error)when(error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or FormatException or IOException or OverflowException or System.Data.Common.DbException)
        {LastError="עדכון מחירון OpenRouter נכשל. המחירון האחרון שנשמר נשאר פעיל; בדקו רשת וזמינות המקור.";return false;}
        finally{gate.Release();}
    }
    public object View()=>new{source=Source,catalog=Current,lastAttempt=LastAttempt,error=LastError,
        stale=Current is null||Current.RetrievedAt<DateTimeOffset.UtcNow.AddHours(-36)};
    public static bool ShouldRefresh(AiConfiguration settings,OpenRouterCatalog? catalog,DateTimeOffset? lastAttempt,DateTimeOffset now)=>
        settings.OpenRouterPricingEnabled&&settings.RegisteredModels().Any(model=>SourceModel(model.Profile,settings) is not null)&&
        (lastAttempt is null||now-lastAttempt>=TimeSpan.FromHours(1))&&
        (catalog is null||catalog.RetrievedAt<now.AddDays(-1)||(now.Hour>=settings.OpenRouterPricingUpdateHourUtc&&catalog.RetrievedAt.UtcDateTime.Date<now.UtcDateTime.Date));
}
public sealed class OpenRouterPricingWorker(OpenRouterPricing pricing,ConfigurationState configuration,ILogger<OpenRouterPricingWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                try{await pricing.LoadAsync(stoppingToken);}catch(Exception)when(!stoppingToken.IsCancellationRequested){logger.LogWarning("OpenRouter saved pricing unavailable.");}
                if(OpenRouterPricing.ShouldRefresh(configuration.Current.Document.Configuration.Ai,pricing.Current,pricing.LastAttempt,DateTimeOffset.UtcNow))
                {
                    var ok=await pricing.RefreshAsync(stoppingToken);
                    logger.Log(ok?LogLevel.Information:LogLevel.Warning,"OpenRouter pricing refresh: {Code}",ok?"ok":"pricing_refresh_failed");
                }
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception){logger.LogWarning("OpenRouter pricing update unavailable; retaining the last saved catalogue.");}
        }while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
