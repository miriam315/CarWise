using System.Text.Json.Nodes;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Administration;

public sealed class ConfigurationService(IJsonDatabase database,ConfigurationState state,ConfigurationValidator validator,SecretVault vault)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public ConfigurationState State=>state;
    public async Task RefreshAsync(CancellationToken ct)
    {
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","admin-active"}},ct:ct);
        if(rows.Count==0)return;
        var document=JsonDefaults.Read<ConfigurationDocument>(rows[0]!["document"]!.ToJsonString());
        if(document.Revision<=state.Current.Document.Revision)return;
        var data=validator.Validate(document.Configuration);
        document=document with{Configuration=document.Configuration with{Employees=data.Employees}};
        state.Publish(new(document,data));
    }
    public async Task<object> ViewAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);var current=state.Current.Document;
        return new{current.Revision,current.SavedAt,current.Actor,current.Note,current.Configuration,
            history=current.History.Select(h=>new{h.Revision,h.SavedAt,h.Actor,h.Note}),
            keyStatus=new{extraction=await vault.IsAvailableAsync(current.Configuration.Ai.Extraction.KeyReference,ct,new Uri(current.Configuration.Ai.Extraction.BaseUrl).Host),narration=await vault.IsAvailableAsync(current.Configuration.Ai.Narration.KeyReference,ct,new Uri(current.Configuration.Ai.Narration.BaseUrl).Host)}};
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
            _=validator.Validate(config);
            if(request.ApiKeys is not null)
            {
                if(request.ApiKeys.Keys.Any(k=>k is not ("extraction" or "narration")))throw new ArgumentException("סוג מפתח אינו מוכר");
                foreach(var (name,value) in request.ApiKeys.Where(x=>!string.IsNullOrWhiteSpace(x.Value)))
                {
                    var host=new Uri(name=="extraction"?config.Ai.Extraction.BaseUrl:config.Ai.Narration.BaseUrl).Host;
                    var reference=await vault.StoreAsync(value,host,ct);
                    config=config with{Ai=name=="extraction"?config.Ai with{Extraction=config.Ai.Extraction with{KeyReference=reference}}:
                        config.Ai with{Narration=config.Ai.Narration with{KeyReference=reference}}};
                }
            }
            var validated=validator.Validate(config);config=config with{Employees=validated.Employees};
            if(config.Ai.Enabled && (!await vault.IsAvailableAsync(config.Ai.Extraction.KeyReference,ct,new Uri(config.Ai.Extraction.BaseUrl).Host)||!await vault.IsAvailableAsync(config.Ai.Narration.KeyReference,ct,new Uri(config.Ai.Narration.BaseUrl).Host)))
                throw new ArgumentException("הפעלת AI דורשת מפתח זמין לכל אחד משני שלבי השיחה");
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
