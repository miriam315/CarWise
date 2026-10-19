using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CarWise.Api.Domain;
namespace CarWise.Api.Administration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiProfile
{
    public string Provider {get;init;}="openai";
    public string DisplayName {get;init;}="";
    public string Model {get;init;}="gpt-4.1-mini";
    public string BaseUrl {get;init;}="https://api.openai.com/v1/";
    public string KeyReference {get;init;}="environment:CARWISE_AI_API_KEY";
    public int MaxOutputTokens {get;init;}=1200;
    public int TimeoutSeconds {get;init;}=35;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiModelPrice(string Provider,string Host,string Model,decimal InputUsdPerMillion,
    decimal CachedInputUsdPerMillion,decimal OutputUsdPerMillion,decimal? CacheWriteUsdPerMillion,string Source);
public sealed record AiConfiguration(bool Enabled,AiProfile Extraction,AiProfile Narration)
{
    public string ExtractionPrompt {get;init;}="חלץ רק נתונים שנמסרו במפורש. אל תסיק נתונים חסרים.";
    public string NarrationPrompt {get;init;}="כתוב בעברית ברורה, חמה וקצרה, והסבר לעובד את החלופה.";
    public string PayslipPrompt {get;init;}="חלץ רכיבי שכר חודשיים בלבד. הבחֵן בין שכר בסיס לברוטו ובין הפרשת עובד להפרשת מעסיק. אל תסיק אחוזים מסכומי ניכוי.";
    public bool PayslipEnabled {get;init;}=false;
    public List<AiModelPrice> Pricing {get;init;}=[];
    public bool DailyExchangeRateEnabled {get;init;}=true;
    public int ExchangeRateUpdateHourUtc {get;init;}=17;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManagedConfiguration(JsonNode Rules,List<Car> Cars,List<Dictionary<string,JsonElement>> Occupations,
    List<Employee> Employees,AiConfiguration Ai)
{
    public string OfficialVersionId {get;init;}="baseline";
}
public sealed record ConfigurationVersion(long Revision,DateTimeOffset SavedAt,string Actor,string Note,ManagedConfiguration Configuration);
public sealed record ConfigurationDocument(long Revision,DateTimeOffset SavedAt,string Actor,string Note,
    ManagedConfiguration Configuration,List<ConfigurationVersion> History);
public sealed record SaveConfigurationRequest(long ExpectedRevision,ManagedConfiguration Configuration,string Note,
    Dictionary<string,string>? ApiKeys=null);
public sealed record RestoreConfigurationRequest(long ExpectedRevision,long Revision,string Note);
public sealed record TestAiRequest(AiProfile Profile,string? ApiKey=null);
public sealed record ConfigurationSnapshot(ConfigurationDocument Document,ReferenceData Data);
public sealed class ConfigurationConflictException : Exception;
public sealed class ConfigurationState(ConfigurationSnapshot initial)
{
    private ConfigurationSnapshot current=initial;
    public ConfigurationSnapshot Current=>Volatile.Read(ref current);
    public void Publish(ConfigurationSnapshot snapshot)
    {
        while(true)
        {
            var observed=Current;if(snapshot.Document.Revision<=observed.Document.Revision)return;
            if(ReferenceEquals(Interlocked.CompareExchange(ref current,snapshot,observed),observed))return;
        }
    }
}
