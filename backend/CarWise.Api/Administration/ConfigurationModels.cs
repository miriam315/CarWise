using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CarWise.Api.Domain;
namespace CarWise.Api.Administration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiProfile
{
    public string Provider {get;init;}="openai";
    public bool CustomProvider {get;init;}=false;
    public bool CustomEndpointApproved {get;init;}=false;
    public string DisplayName {get;init;}="";
    public string Model {get;init;}="gpt-4.1-mini";
    public string BaseUrl {get;init;}="https://api.openai.com/v1/";
    public string KeyReference {get;init;}="environment:CARWISE_AI_API_KEY";
    public string GeminiThinking {get;init;}="auto";
    public bool SupportsDocuments {get;init;}=false;
    public bool PrimaryKeyEnabled {get;init;}=true;
    public List<AiBackupKey> BackupKeys {get;init;}=[];
    public IEnumerable<AiBackupKey> Keys()=>new[]{new AiBackupKey("primary","מפתח ראשי",KeyReference,PrimaryKeyEnabled)}.Concat(BackupKeys);
    public int MaxOutputTokens {get;init;}=1200;
    public int TimeoutSeconds {get;init;}=35;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiBackupKey(string Id,string Name,string KeyReference,bool Enabled=true);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiModelPrice(string Provider,string Host,string Model,decimal InputUsdPerMillion,
    decimal CachedInputUsdPerMillion,decimal OutputUsdPerMillion,decimal? CacheWriteUsdPerMillion,string Source);
public sealed record AiRegisteredModel(string Id,string Name,AiProfile Profile);
public sealed record AiConfiguration(bool Enabled,AiProfile Extraction,AiProfile Narration)
{
    public List<AiRegisteredModel> Models {get;init;}=[];
    public Dictionary<string,string> ServiceModels {get;init;}=[];
    public AiProfile ProfileFor(string service)=>Models.Count==0?(service=="narration"?Narration:Extraction):
        Models.Single(m=>m.Id==ServiceModels[service]).Profile;
    public IEnumerable<AiRegisteredModel> RegisteredModels()=>Models.Count==0?
        [new("extraction","הבנת השיחה",Extraction),new("narration","ניסוח תשובה",Narration)]:Models;
    public AiConfiguration WithProfile(string id,AiProfile profile)=>Models.Count==0?
        id=="extraction"?this with{Extraction=profile}:this with{Narration=profile}:
        this with{Models=Models.Select(m=>m.Id==id?m with{Profile=profile}:m).ToList()};
    public string ModelIdFor(string service)=>Models.Count==0?(service=="narration"?"narration":"extraction"):ServiceModels[service];
    public string ExtractionPrompt {get;init;}="חלץ רק נתונים שנמסרו במפורש. אל תסיק נתונים חסרים.";
    public string NarrationPrompt {get;init;}="כתוב בעברית ברורה, חמה וקצרה, והסבר לעובד את החלופה.";
    public string PayslipPrompt {get;init;}="חלץ רכיבי שכר חודשיים בלבד. הבחֵן בין שכר בסיס לברוטו ובין הפרשת עובד להפרשת מעסיק. אל תסיק אחוזים מסכומי ניכוי.";
    public bool PayslipEnabled {get;init;}=false;
    public LocalPayslipSettings PayslipOcr {get;init;}=new();
    public List<AiModelPrice> Pricing {get;init;}=[];
    public bool DailyExchangeRateEnabled {get;init;}=true;
    public int ExchangeRateUpdateHourUtc {get;init;}=17;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManagedConfiguration(JsonNode Rules,List<Car> Cars,List<Dictionary<string,JsonElement>> Occupations,
    List<Employee> Employees,AiConfiguration Ai)
{
    public string OfficialVersionId {get;init;}="baseline";
    public Dictionary<string,string> EmployeeAccountRoles {get;init;}=[];
    public FleetPolicy Fleet {get;init;}=new();
    public IntentPolicy IntentAnalytics {get;init;}=new();
}
public sealed record ConfigurationVersion(long Revision,DateTimeOffset SavedAt,string Actor,string Note,ManagedConfiguration Configuration);
public sealed record OperationalChange(DateTimeOffset At,string Actor,List<string> EmployeeIds,List<string> CarIds)
{
    public bool AiChanged {get;init;}
}
public sealed record ConfigurationDocument(long Revision,DateTimeOffset SavedAt,string Actor,string Note,
    ManagedConfiguration Configuration,List<ConfigurationVersion> History)
{
    public long? PolicyRevision {get;init;}
    [JsonIgnore] public long PolicyVersion=>PolicyRevision??Revision;
    public DateTimeOffset? PolicySavedAt {get;init;}
    public string? PolicyActor {get;init;}
    public string? PolicyNote {get;init;}
    public List<OperationalChange> Operations {get;init;}=[];
}
public static class ConfigurationChanges
{
    // Keep the legacy snapshot shape, but exclude operational AI settings from policy comparison/history.
    // Restoring policy always merges the current AI configuration.
    public static ManagedConfiguration Policy(ManagedConfiguration config)=>config with{Employees=[],EmployeeAccountRoles=[],Cars=config.Cars.Select(c=>c with{Photos=[]}).ToList(),Ai=new(false,new(),new())};
    public static bool PolicyChanged(ManagedConfiguration before,ManagedConfiguration after)=>!JsonNode.DeepEquals(
        JsonNode.Parse(JsonSerializer.Serialize(Policy(before))),JsonNode.Parse(JsonSerializer.Serialize(Policy(after))));
}
public sealed record SaveConfigurationRequest(long ExpectedRevision,ManagedConfiguration Configuration,string Note,
    Dictionary<string,string>? ApiKeys=null,Dictionary<string,Dictionary<string,string>>? BackupApiKeys=null);
public sealed record RestoreConfigurationRequest(long ExpectedRevision,long Revision,string Note);
public sealed record TestAiRequest(AiProfile Profile,string? ApiKey=null,string? KeyId=null,Dictionary<string,string>? ApiKeys=null);
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
