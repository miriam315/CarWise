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
        return new{current.Revision,policyRevision=current.PolicyVersion,current.PolicySavedAt,current.PolicyActor,current.PolicyNote,current.Operations,current.SavedAt,current.Actor,current.Note,current.Configuration,
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
            if(request.Note?.Length>500)throw new ArgumentException("סיבת שינוי מוגבלת ל־500 תווים");
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
            // Readiness belongs to an activation or a changed service profile. An unchanged,
            // unavailable AI service must never block unrelated catalogue/policy saves.
            AiProfile? Profile(AiConfiguration ai,string service)=>service=="payslip"?PayslipAvailability.DocumentProfile(ai):ai.ProfileFor(service);
            bool Changed(string service,bool enabled,bool previouslyEnabled)=>enabled&&(!previouslyEnabled||
                !JsonNode.DeepEquals(JsonNode.Parse(JsonDefaults.Serialize(Profile(config.Ai,service))),JsonNode.Parse(JsonDefaults.Serialize(Profile(current.Configuration.Ai,service)))));
            var services=new List<string>();
            foreach(var service in new[]{"extraction","narration"})if(Changed(service,config.Ai.Enabled,current.Configuration.Ai.Enabled))services.Add(service);
            if(Changed("payslip",config.Ai.PayslipEnabled,current.Configuration.Ai.PayslipEnabled))services.Add("payslip");
            foreach(var service in services)
            {
                var profile=Profile(config.Ai,service);
                if(service=="payslip"&&profile is not {SupportsDocuments:true})throw new ArgumentException("יש לבחור מודל לחילוץ תלושים התומך ב־PDF ובתמונות");
                if(profile is null||!await HasKeyAsync(profile,ct))throw new ArgumentException("הפעלת שירות AI או שינוי המודל שלו דורשים מפתח זמין למודל המשויך אליו.");
            }
            var policyChanged=ConfigurationChanges.PolicyChanged(current.Configuration,config);
            if(policyChanged&&string.IsNullOrWhiteSpace(request.Note))throw new ArgumentException("לשינוי מדיניות יש לציין סיבת שינוי עד 500 תווים");
            if(JsonNode.DeepEquals(JsonNode.Parse(JsonDefaults.Serialize(current.Configuration)),JsonNode.Parse(JsonDefaults.Serialize(config))))return current;
            var now=DateTimeOffset.UtcNow;
            var history=current.History.ToList();
            if(policyChanged)
            {
                history.Insert(0,new(current.PolicyVersion,current.PolicySavedAt??current.SavedAt,current.PolicyActor??current.Actor,current.PolicyNote??current.Note,ConfigurationChanges.Policy(current.Configuration)));
                history=history.Take(10).ToList();
            }
            var employeeIds=current.Configuration.Employees.Select(e=>e.Id).Union(config.Employees.Select(e=>e.Id)).Where(id=>
                !JsonNode.DeepEquals(JsonNode.Parse(JsonDefaults.Serialize(current.Configuration.Employees.SingleOrDefault(e=>e.Id==id))),JsonNode.Parse(JsonDefaults.Serialize(config.Employees.SingleOrDefault(e=>e.Id==id))))||current.Configuration.EmployeeAccountRoles.GetValueOrDefault(id,"employee")!=config.EmployeeAccountRoles.GetValueOrDefault(id,"employee")).ToList();
            var carIds=current.Configuration.Cars.Select(c=>c.Id).Union(config.Cars.Select(c=>c.Id)).Where(id=>
                !JsonNode.DeepEquals(JsonNode.Parse(JsonDefaults.Serialize(current.Configuration.Cars.SingleOrDefault(c=>c.Id==id)?.Photos??[])),JsonNode.Parse(JsonDefaults.Serialize(config.Cars.SingleOrDefault(c=>c.Id==id)?.Photos??[])))).ToList();
            var aiChanged=!JsonNode.DeepEquals(JsonNode.Parse(JsonDefaults.Serialize(current.Configuration.Ai)),JsonNode.Parse(JsonDefaults.Serialize(config.Ai)));
            var operations=current.Operations.ToList();if(employeeIds.Count>0||carIds.Count>0||aiChanged)operations.Insert(0,new(now,actor,employeeIds,carIds){AiChanged=aiChanged});
            var next=new ConfigurationDocument(current.Revision+1,now,actor,request.Note?.Trim()??"",config,history)
            {
                PolicyRevision=current.PolicyVersion+(policyChanged?1:0),
                PolicySavedAt=policyChanged?now:current.PolicySavedAt??current.SavedAt,
                PolicyActor=policyChanged?actor:current.PolicyActor??current.Actor,
                PolicyNote=policyChanged?request.Note?.Trim():current.PolicyNote??current.Note,
                Operations=operations.Take(100).ToList()
            };
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
        var operational=current.Configuration;
        var restored=previous.Configuration with{EmployeeAccountRoles=operational.EmployeeAccountRoles,Ai=operational.Ai,Employees=operational.Employees,Cars=previous.Configuration.Cars.Select(c=>c with{Photos=operational.Cars.SingleOrDefault(p=>p.Id==c.Id)?.Photos??[]}).ToList()};
        return await SaveAsync(new(request.ExpectedRevision,restored,request.Note),actor,ct);
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
