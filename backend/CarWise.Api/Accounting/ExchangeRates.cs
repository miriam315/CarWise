using System.Globalization;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Accounting;

public sealed record ExchangeRate(decimal UsdIls,DateOnly PublishedDate,DateTimeOffset RetrievedAt,string Source);
public sealed class ExchangeRates(IJsonDatabase database,IHttpClientFactory clients)
{
    public const string Source="https://www.boi.org.il/PublicApi/GetExchangeRates?asXml=false";
    private readonly SemaphoreSlim gate=new(1,1);
    public ExchangeRate? Current {get;private set;}
    public string? LastError {get;private set;}
    public DateTimeOffset? LastAttempt {get;private set;}
    public async Task LoadAsync(CancellationToken ct)
    {
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","fx:usd-ils"}},ct:ct);
        if(rows.Count>0)Current=JsonDefaults.Read<ExchangeRate>(rows[0]!["document"]!.ToJsonString());
    }
    public static ExchangeRate Parse(JsonNode body,DateTimeOffset now)
    {
        var usd=body["exchangeRates"]?.AsArray().SingleOrDefault(r=>r?["key"]?.GetValue<string>()=="USD")??throw new InvalidDataException("Missing USD");
        var rate=usd["currentExchangeRate"]!.GetValue<decimal>();var unit=usd["unit"]?.GetValue<int>()??1;
        var published=DateTimeOffset.Parse(usd["lastUpdate"]!.GetValue<string>(),CultureInfo.InvariantCulture);
        var day=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(published,"Asia/Jerusalem").DateTime);
        if(unit!=1||rate is <0.1m or >100m||published>now.AddDays(1)||day<DateOnly.FromDateTime(now.UtcDateTime.AddDays(-14)))throw new InvalidDataException("Invalid exchange rate");
        return new(rate,day,now,Source);
    }
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            LastAttempt=DateTimeOffset.UtcNow;
            using var client=clients.CreateClient("ExchangeRates");
            var body=await client.GetFromJsonAsync<JsonNode>(Source,ct)??throw new InvalidDataException();
            var next=Parse(body,DateTimeOffset.UtcNow);
            if(Current is not null&&next.PublishedDate<Current.PublishedDate)throw new InvalidDataException();
            var document=JsonNode.Parse(JsonDefaults.Serialize(next))!;
            await database.UpsertAsync("carwise_rules",new JsonArray(new JsonObject{["id"]="fx:usd-ils",["document"]=document},
                new JsonObject{["id"]="fx:usd-ils:"+next.PublishedDate.ToString("yyyy-MM-dd"),["document"]=document.DeepClone()}),ct);
            Current=next;LastError=null;return true;
        }
        catch(Exception error)when(error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or FormatException or InvalidOperationException)
        {LastError="עדכון בנק ישראל נכשל. השער הקודם נשמר; בדקו הרשאת רשת וזמינות המקור.";return false;}
        finally{gate.Release();}
    }
    public object View()=>new{rate=Current,lastAttempt=LastAttempt,error=LastError,stale=Current is null||Current.PublishedDate<DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-4)),source=Source};
}
public sealed class ExchangeRateWorker(ExchangeRates rates,ConfigurationState configuration,ILogger<ExchangeRateWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try{await rates.LoadAsync(stoppingToken);}catch(Exception){logger.LogWarning("Saved exchange rate unavailable.");}
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                var settings=configuration.Current.Document.Configuration.Ai;var now=DateTimeOffset.UtcNow;
                if(settings.DailyExchangeRateEnabled&&(rates.Current is null||now.Hour>=settings.ExchangeRateUpdateHourUtc)
                    &&(rates.LastAttempt is null||rates.LastAttempt.Value.UtcDateTime.Date<now.UtcDateTime.Date))
                    await rates.RefreshAsync(stoppingToken);
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception){logger.LogWarning("Exchange rate update failed; retaining the last published rate.");}
        }while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
