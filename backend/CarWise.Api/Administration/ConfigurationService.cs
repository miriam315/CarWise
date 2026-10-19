using System.Text.Json.Nodes;
using CarWise.Api.Domain;
using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Administration;

public sealed class ConfigurationService(IJsonDatabase database,ConfigurationState state,ConfigurationValidator validator,SecretVault vault,OfficialVersions? versions=null,AiKeyHealth? keyHealth=null)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public ConfigurationState State=>state;
    private Task<IReadOnlyDictionary<string,OfficialRecord>?> OfficialAsync(ManagedConfiguration config,CancellationToken ct)
    {
        if(config.OfficialVersionId=="baseline"&&versions is null)return Task.FromResult<IReadOnlyDictionary<string,OfficialRecord>?>(null);
        return Load();
        async Task<IReadOnlyDictionary<string,OfficialRecord>?> Load()=>await (versions??throw new ArgumentException("גרסאות מאגר אינן זמינות")).LoadForCarsAsync(config.OfficialVersionId,config.Cars,ct);
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","admin-active"}},ct:ct);
        if(rows.Count==0)return;
        var document=JsonDefaults.Read<ConfigurationDocument>(rows[0]!["document"]!.ToJsonString());
        if(document.Revision<=state.Current.Document.Revision)return;
        var data=validator.Validate(document.Configuration,await OfficialAsync(document.Configuration,ct));
        document=document with{Configuration=document.Configuration with{Employees=data.Employees}};
        state.Publish(new(document,data));
    }
    public async Task<object> ViewAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);var current=state.Current.Document;
        var pools=new Dictionary<string,object[]>();
        foreach(var model in current.Configuration.Ai.RegisteredModels())pools[model.Id]=await PoolAsync(model.Profile,ct);
        return new{current.Revision,current.SavedAt,current.Actor,current.Note,current.Configuration,
            history=current.History.Select(h=>new{h.Revision,h.SavedAt,h.Actor,h.Note}),
            keyStatus=new{extraction=await HasKeyAsync(current.Configuration.Ai.ProfileFor("extraction"),ct),narration=await HasKeyAsync(current.Configuration.Ai.ProfileFor("narration"),ct)},
            keyPools=pools,payslipStatus=await PayslipAvailability.ReadAsync(current.Configuration.Ai,vault,ct)};
    }
    private async Task<bool> HasKeyAsync(AiProfile profile,CancellationToken ct)
    {
        foreach(var key in profile.Keys().Where(k=>k.Enabled))
            if(await vault.IsAvailableAsync(key.KeyReference,ct,new Uri(profile.BaseUrl).Host))return true;
        return false;
    }
    private async Task<object[]> PoolAsync(AiProfile profile,CancellationToken ct)
    {
        var list=new List<object>();foreach(var key in profile.Keys())
        {
            var failure=keyHealth?.Get(profile,key.KeyReference);
            list.Add(new{key.Id,key.Name,key.Enabled,host=new Uri(profile.BaseUrl).Host,configured=await vault.IsAvailableAsync(key.KeyReference,ct,new Uri(profile.BaseUrl).Host),failure?.RetryAt,lastError=failure?.Code});
        }
        return list.ToArray();
    }

    public async Task<ConfigurationDocument> SaveAsync(SaveConfigurationRequest request,string actor,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await RefreshAsync(ct);var current=state.Current.Document;
            if(request.ExpectedRevision!=current.Revision)throw new ConfigurationConflictException();
            if(string.IsNullOrWhiteSpace(request.Note)||request.Note.Length>500)throw new ArgumentException("יש לציין סיבת שינוי עד 500 תווים");
            if(request.Configuration is null)throw new ArgumentException("חסרות הגדרות");
            var config=JsonDefaults.Read<ManagedConfiguration>(JsonDefaults.Serialize(request.Configuration));
            _=validator.Validate(config,await OfficialAsync(config,ct));
            if(request.ApiKeys is not null)
            {
                if(request.ApiKeys.Keys.Any(id=>!config.Ai.RegisteredModels().Any(m=>m.Id==id)))throw new ArgumentException("סוג מפתח אינו מוכר");
                foreach(var (name,value) in request.ApiKeys.Where(x=>!string.IsNullOrWhiteSpace(x.Value)))
                {
                    var profile=config.Ai.RegisteredModels().Single(m=>m.Id==name).Profile;
                    var host=new Uri(profile.BaseUrl).Host;
                    var reference=await vault.StoreAsync(value,host,ct);
                    config=config with{Ai=config.Ai.WithProfile(name,profile with{KeyReference=reference})};
                }
            }
            if(request.BackupApiKeys is not null)
            {
                if(request.BackupApiKeys.Keys.Any(id=>!config.Ai.RegisteredModels().Any(m=>m.Id==id)))throw new ArgumentException("סוג מפתח אינו מוכר");
                foreach(var (role,values) in request.BackupApiKeys)
                {
                    var profile=config.Ai.RegisteredModels().Single(m=>m.Id==role).Profile;
                    if(values is null||values.Keys.Any(id=>!profile.BackupKeys.Any(k=>k.Id==id)))throw new ArgumentException("מזהה מפתח גיבוי אינו קיים בטיוטה");
                    var backups=profile.BackupKeys.ToList();
                    foreach(var (id,value) in values.Where(x=>!string.IsNullOrWhiteSpace(x.Value)))
                    {
                        var reference=await vault.StoreAsync(value,new Uri(profile.BaseUrl).Host,ct);
                        var index=backups.FindIndex(k=>k.Id==id);backups[index]=backups[index] with{KeyReference=reference};
                    }
                    profile=profile with{BackupKeys=backups};
                    config=config with{Ai=config.Ai.WithProfile(role,profile)};
                }
            }
            var validated=validator.Validate(config,await OfficialAsync(config,ct));config=config with{Employees=validated.Employees};
            var services=new List<string>();if(config.Ai.Enabled)services.AddRange(["extraction","narration"]);if(config.Ai.PayslipEnabled)services.Add("payslip");
            foreach(var service in services)
                if(!await HasKeyAsync(config.Ai.ProfileFor(service),ct))throw new ArgumentException(service=="payslip"?
                    "להפעלת חילוץ תלושים יש להגדיר ולשמור מפתח פעיל למודל קריאת התלושים בניהול ← ספקי AI.":"הפעלת AI בשיחה דורשת מפתח זמין לכל אחד משירותי השיחה.");
            var history=new List<ConfigurationVersion>{new(current.Revision,current.SavedAt,current.Actor,current.Note,current.Configuration)};
            history.AddRange(current.History);history=history.Take(10).ToList();
            var next=new ConfigurationDocument(current.Revision+1,DateTimeOffset.UtcNow,actor,request.Note.Trim(),config,history);
            var saved=await database.TryWriteRuleAsync("admin-active",current.Revision==0?null:current.Revision,JsonNode.Parse(JsonDefaults.Serialize(next))!,ct);
            if(!saved)throw new ConfigurationConflictException();
            state.Publish(new(next,validated));return next;
        }
        finally{gate.Release();}
    }
    public async Task<ConfigurationDocument> RestoreAsync(RestoreConfigurationRequest request,string actor,CancellationToken ct)
    {
        await RefreshAsync(ct);var current=state.Current.Document;
        var previous=current.History.SingleOrDefault(h=>h.Revision==request.Revision)??throw new ArgumentException("הגרסה אינה בהיסטוריה הזמינה");
        return await SaveAsync(new(request.ExpectedRevision,previous.Configuration,request.Note),actor,ct);
    }
}
public sealed class ConfigurationRefreshWorker(ConfigurationService settings,ILogger<ConfigurationRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try{await settings.RefreshAsync(stoppingToken);}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception){logger.LogWarning("Could not refresh administration settings; retaining the last validated snapshot.");}
        }
    }
}
