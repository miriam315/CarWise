using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;

public sealed class RoutingClock : TimeProvider
{
    public DateTimeOffset Now {get;set;}=DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow()=>Now;
}
public sealed class AsyncRoutingHandler(Func<CancellationToken,Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>response(ct);
}
public sealed class RoutingFactory(Func<CancellationToken,Task<HttpResponseMessage>> response) : IHttpClientFactory
{public HttpClient CreateClient(string name)=>new(new AsyncRoutingHandler(response));}
public sealed class AiRoutingTests
{
    private const string Main="TEST-ONLY-MAIN",Backup="TEST-ONLY-BACKUP";
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static HttpResponseMessage Ok(string provider="openai")=>new(HttpStatusCode.OK){Content=new StringContent(provider switch{
        "anthropic"=>"""{"stop_reason":"end_turn","content":[{"type":"text","text":"{}"}],"usage":{"input_tokens":20,"output_tokens":10}}""",
        "gemini"=>"""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{}"}]}}],"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":10}}""",
        _=>"""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}],"usage":{"prompt_tokens":20,"completion_tokens":10}}"""})};
    private static HttpResponseMessage Error(int status,string code="rate_limit_exceeded")=>new((HttpStatusCode)status){Content=new StringContent("{\"error\":{\"code\":\""+code+"\",\"message\":\"TEST-SECRET-DO-NOT-STORE\"}}")};
    private static async Task<(MemoryConfigurationDatabase Db,SecretVault Vault,AiProfile Profile)> Setup(string provider="openai")
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var host=provider switch{"anthropic"=>"api.anthropic.com","gemini"=>"generativelanguage.googleapis.com",_=>"api.openai.com"};
        return(db,vault,new(){Provider=provider,BaseUrl="https://"+host+"/",KeyReference=await vault.StoreAsync(Main,host,default),
            BackupKeys=[new("aabbccddeeff0011","חשבון גיבוי",await vault.StoreAsync(Backup,host,default))]});
    }
    [Theory][InlineData(401)][InlineData(402)][InlineData(403)][InlineData(429)][InlineData(503)][InlineData(529)]
    public async Task RejectedKeyFallsBackOnceToTheNextKey(int status)
    {
        var (_,vault,profile)=await Setup();var keys=new List<string>();
        var factory=new ProviderTestFactory(req=>{var key=req.Headers.Authorization!.Parameter!;keys.Add(key);return key==Main?Error(status):Ok();});
        Assert.Equal("{}",await new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",default));
        Assert.Equal(new[]{Main,Backup},keys);
    }
    [Theory][InlineData(400)][InlineData(404)][InlineData(413)][InlineData(422)][InlineData(500)]
    public async Task UnsupportedOrAmbiguousRequestsAreNotMultiplied(int status)
    {
        var (_,vault,profile)=await Setup();var calls=0;
        var factory=new ProviderTestFactory(_=>{calls++;return Error(status);});
        var error=await Assert.ThrowsAsync<AiProviderException>(()=>new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",default));
        Assert.Equal(status,error.Status);Assert.Equal(1,calls);
    }
    [Theory][InlineData("openai")][InlineData("anthropic")][InlineData("gemini")]
    public async Task NativeProtocolsAndDocumentFailoverUseOnlyTheirOwnHeader(string provider)
    {
        var (_,vault,profile)=await Setup(provider);var keys=new List<string>();var bodies=new List<string>();
        var factory=new ProviderTestFactory(req=>{
            var key=provider=="openai"?req.Headers.Authorization!.Parameter!:req.Headers.GetValues(provider=="gemini"?"x-goog-api-key":"x-api-key").Single();
            keys.Add(key);bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if(provider!="openai")Assert.Null(req.Headers.Authorization);
            return key==Main?Error(429):Ok(provider);
        });
        var document=new AiDocument("application/pdf","FAKE PAYSLIP TEST BYTES"u8.ToArray());
        Assert.Equal("{}",await new ConfiguredCompletionClient(profile,vault,factory).CompleteDocumentAsync("extract_payslip",new JsonObject(),"extract",document,default));
        Assert.Equal(new[]{Main,Backup},keys);Assert.Equal(bodies[0],bodies[1]);Assert.Contains(Convert.ToBase64String(document.Bytes),bodies[1]);
    }
    [Fact] public async Task CooldownIsSharedAcrossRequestsAndPrimaryRecoversAfterRetryAfter()
    {
        var (_,vault,profile)=await Setup();var clock=new RoutingClock();var health=new AiKeyHealth(clock);var calls=new List<string>();var primaryCalls=0;
        var factory=new ProviderTestFactory(req=>{var key=req.Headers.Authorization!.Parameter!;calls.Add(key);
            if(key==Main&&primaryCalls++==0){var error=Error(429);error.Headers.RetryAfter=new(TimeSpan.FromSeconds(120));return error;}return Ok();});
        Task<string> Call()=>new ConfiguredCompletionClient(profile,vault,factory,health:health).CompleteAsync("probe",new JsonObject(),"test","test",default);
        await Call();Assert.Equal(clock.Now.AddSeconds(120),health.Get(profile,profile.KeyReference)!.RetryAt);
        await Call();Assert.Equal(new[]{Main,Backup,Backup},calls);
        clock.Now=clock.Now.AddSeconds(121);await Call();Assert.Equal(Main,calls.Last());Assert.Null(health.Get(profile,profile.KeyReference));
    }
    [Fact] public async Task AllExhaustedKeysProduceBoundedFailureAndNoRequestsWhileCooling()
    {
        var (_,vault,profile)=await Setup();var calls=0;var health=new AiKeyHealth();var factory=new ProviderTestFactory(_=>{calls++;return Error(429,"insufficient_quota");});
        Task<string> Call()=>new ConfiguredCompletionClient(profile,vault,factory,health:health).CompleteAsync("probe",new JsonObject(),"test","test",default);
        await Assert.ThrowsAsync<AiProviderException>(Call);Assert.Equal(2,calls);
        await Assert.ThrowsAsync<AiUnavailableException>(Call);Assert.Equal(2,calls);
    }
    [Fact] public async Task DisabledMissingAndWrongHostKeysAreSkippedWithoutSendingSecrets()
    {
        var (_,vault,profile)=await Setup();profile=profile with{PrimaryKeyEnabled=false,BackupKeys=[
            new("1111111111111111","wrong host",await vault.StoreAsync("NEVER-SEND-THIS","api.anthropic.com",default)),
            new("2222222222222222","missing","vault:"+new string('0',32)),profile.BackupKeys[0]]};
        var calls=0;var factory=new ProviderTestFactory(req=>{calls++;Assert.Equal(Backup,req.Headers.Authorization!.Parameter);return Ok();});
        await new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",default);Assert.Equal(1,calls);
    }
    [Fact] public async Task DuplicateKeyMaterialCannotMultiplyRejectedRequests()
    {
        var (_,vault,profile)=await Setup();profile=profile with{BackupKeys=[profile.BackupKeys[0] with{KeyReference=await vault.StoreAsync(Main,"api.openai.com",default)}]};var calls=0;
        var factory=new ProviderTestFactory(_=>{calls++;return Error(429);});
        await Assert.ThrowsAsync<AiProviderException>(()=>new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",default));Assert.Equal(1,calls);
    }
    [Fact] public async Task CancellationNetworkFailureAndMalformedSuccessNeverTryBackup()
    {
        var (_,vault,profile)=await Setup();var calls=0;
        var factory=new RoutingFactory(_=>{calls++;throw new HttpRequestException("ambiguous transport failure");});
        await Assert.ThrowsAsync<HttpRequestException>(()=>new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",default));Assert.Equal(1,calls);
        using var canceled=new CancellationTokenSource();canceled.Cancel();calls=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",canceled.Token));Assert.Equal(0,calls);
        var malformed=new ProviderTestFactory(_=>{calls++;return new(HttpStatusCode.OK){Content=new StringContent("not json")};});
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(()=>new ConfiguredCompletionClient(profile,vault,malformed).CompleteAsync("probe",new JsonObject(),"test","test",default));Assert.Equal(1,calls);
    }
    [Fact] public async Task ATimeoutDoesNotFailOverAndSingleBackupProbeKeepsItsIdentity()
    {
        var (db,vault,profile)=await Setup();var calls=0;
        var timedOut=new RoutingFactory(_=>{calls++;throw new TaskCanceledException("provider outcome unknown");});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ConfiguredCompletionClient(profile,vault,timedOut).CompleteAsync("probe",new JsonObject(),"test","test",default));Assert.Equal(1,calls);
        var config=new ManagedConfiguration(Source.Rules,Source.Cars,Source.OccupationDocuments,Source.Employees,new(true,profile,profile));
        var snapshot=new ConfigurationSnapshot(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source);
        var factory=new ProviderTestFactory(req=>{Assert.Equal(Backup,req.Headers.Authorization!.Parameter);return Ok();});
        var accounting=new UsageAccounting(db,snapshot,new(db,factory),new(),NullLogger<UsageAccounting>.Instance);
        var probe=profile.BackupKeys[0] with{Enabled=false};
        await new ConfiguredCompletionClient(profile,vault,factory,accounting:accounting,keyToTest:probe).CompleteAsync("connection_test",new JsonObject(),"test","test",default);
        var row=Assert.Single(await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","usage:"}}));
        Assert.Equal(probe.Id,row!["document"]!["credentialId"]!.GetValue<string>());
    }
    [Fact] public void KeyPoolConfigurationRejectsDuplicatesAndTooManyKeys()
    {
        var slot=new AiBackupKey("aabbccddeeff0011","backup","environment:CARWISE_AI_UNCONFIGURED");
        ConfigurationValidator.ValidateAi(new(){PrimaryKeyEnabled=false});
        Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(new(){BackupKeys=[slot,slot]}));
        Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(new(){BackupKeys=Enumerable.Repeat(slot,8).ToList()}));
    }
    [Fact] public async Task AllDisabledKeysMakeNoProviderRequestAndCannotEnableAnActiveService()
    {
        var (db,vault,profile)=await Setup();profile=profile with{PrimaryKeyEnabled=false,BackupKeys=[]};var calls=0;
        var factory=new ProviderTestFactory(_=>{calls++;return Ok();});
        await Assert.ThrowsAsync<AiUnavailableException>(()=>new ConfiguredCompletionClient(profile,vault,factory).CompleteAsync("probe",new JsonObject(),"test","test",default));Assert.Equal(0,calls);
        var config=new ManagedConfiguration(Source.Rules,Source.Cars,Source.OccupationDocuments,Source.Employees,new(true,profile,profile));
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));
        await Assert.ThrowsAsync<ArgumentException>(()=>new ConfigurationService(db,state,new(Source),vault).SaveAsync(new(0,config,"cannot enable without a usable key"),"admin",default));
        Assert.Empty(await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","admin-active"}}));
    }
    [Fact] public async Task EveryAttemptHasItsOwnLedgerEntryAndSharesOneInvocation()
    {
        var (db,vault,profile)=await Setup();var config=new ManagedConfiguration(Source.Rules,Source.Cars,Source.OccupationDocuments,Source.Employees,new(true,profile,profile){Pricing=[new("openai","api.openai.com",profile.Model,1,.5m,2,null,"test")]});
        var snapshot=new ConfigurationSnapshot(new(1,DateTimeOffset.UtcNow,"admin","test",config,[]),Source);
        var factory=new ProviderTestFactory(req=>req.Headers.Authorization!.Parameter==Main?Error(429):Ok());
        var accounting=new UsageAccounting(db,snapshot,new(db,factory),new(){ConversationId="E-001"},NullLogger<UsageAccounting>.Instance);
        await new ConfiguredCompletionClient(profile,vault,factory,accounting:accounting).CompleteAsync("extract_payslip",new JsonObject(),"test","test",default);
        var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","usage:"}});
        var records=rows.Select(r=>JsonDefaults.Read<AiUsageRecord>(r!["document"]!.ToJsonString())).OrderBy(r=>r.Attempt).ToArray();
        Assert.Equal(2,records.Length);Assert.Equal(records[0].InvocationId,records[1].InvocationId);Assert.Equal(records[0].ActionId,records[1].ActionId);
        Assert.Equal("primary",records[0].CredentialId);Assert.Equal("failed",records[0].Status);Assert.Null(records[0].EstimatedUsd);
        Assert.Equal("aabbccddeeff0011",records[1].CredentialId);Assert.Equal("completed",records[1].Status);Assert.Equal(.00004m,records[1].EstimatedUsd);
        Assert.DoesNotContain(Main,db.Dump());Assert.DoesNotContain(Backup,db.Dump());Assert.DoesNotContain("TEST-SECRET-DO-NOT-STORE",db.Dump());
    }
    [Fact] public async Task RegistryBindingsAndEncryptedPoolsPersistAndCanBeSharedAcrossServices()
    {
        var (db,vault,profile)=await Setup();var ai=new AiConfiguration(true,profile,profile){Models=[new("text-model","מודל שיחה",profile),new("pdf-model","מודל PDF",profile with{Model="document-model",SupportsDocuments=true})],
            ServiceModels=new(){{"extraction","text-model"},{"narration","text-model"},{"payslip","pdf-model"}},PayslipEnabled=true};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        var snapshot=new ConfigurationSnapshot(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source);var state=new ConfigurationState(snapshot);
        var service=new ConfigurationService(db,state,new(Source),vault);
        var saved=await service.SaveAsync(new(0,config,"separate PDF model",new(){{"pdf-model","TEST-ONLY-PDF-MAIN"}},new(){{"pdf-model",new(){{"aabbccddeeff0011","TEST-ONLY-PDF-BACKUP"}}}}),"admin",default);
        var second=new ConfigurationService(db,new(snapshot),new(Source),vault);await second.RefreshAsync(default);var active=second.State.Current.Document.Configuration.Ai;
        Assert.Equal("document-model",active.ProfileFor("payslip").Model);Assert.Equal(profile.Model,active.ProfileFor("extraction").Model);
        Assert.Equal(active.ProfileFor("extraction"),active.ProfileFor("narration"));
        Assert.Equal("TEST-ONLY-PDF-MAIN",await vault.ResolveAsync(active.ProfileFor("payslip").KeyReference,default,"api.openai.com"));
        Assert.Equal("TEST-ONLY-PDF-BACKUP",await vault.ResolveAsync(active.ProfileFor("payslip").BackupKeys[0].KeyReference,default,"api.openai.com"));
        Assert.DoesNotContain("TEST-ONLY-PDF",JsonDefaults.Serialize(await second.ViewAsync(default)));Assert.DoesNotContain("TEST-ONLY-PDF",db.Dump());
        var sentModels=new List<string>();var factory=new ProviderTestFactory(req=>{sentModels.Add(JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["model"]!.GetValue<string>());return Ok();});
        foreach(var operation in new[]{"extraction","narration","payslip"})await new ConfiguredCompletionClient(active.ProfileFor(operation),vault,factory).CompleteAsync(operation,new JsonObject(),"test","test",default);
        Assert.Equal(new[]{profile.Model,profile.Model,"document-model"},sentModels);
        var broken=ai with{ServiceModels=new(ai.ServiceModels){{"unknown-service","pdf-model"}}};
        Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(Source).Validate(config with{Ai=broken}));
        broken=ai with{Models=[ai.Models[0]],ServiceModels=ai.ServiceModels};Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(Source).Validate(config with{Ai=broken}));
        broken=ai with{Models=ai.Models.Select(m=>m with{Profile=m.Profile with{SupportsDocuments=false}}).ToList()};Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(Source).Validate(config with{Ai=broken}));
    }
}
