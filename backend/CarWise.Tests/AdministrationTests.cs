using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class MemoryConfigurationDatabase : IJsonDatabase
{
    private readonly Dictionary<string,JsonNode> rows=[];
    private readonly object gate=new();
    public string Provider=>"test";
    public Task<JsonArray> ReadAsync(string table,IReadOnlyDictionary<string,string>? filters=null,int offset=0,int limit=1000,CancellationToken ct=default,string? afterId=null)
    {
        lock(gate)
        {
            var query=rows.Where(r=>(filters is null || filters.All(f=>f.Key switch{
                "id"=>r.Key==f.Value,"id_in"=>f.Value.Split(',').Contains(r.Key),"id_prefix"=>r.Key.StartsWith(f.Value,StringComparison.Ordinal),
                "document->>conversationId"=>r.Value["conversationId"]?.GetValue<string>()==f.Value,_=>false}))
                &&(afterId is null||string.CompareOrdinal(r.Key,afterId)>0)).OrderBy(r=>r.Key);
            return Task.FromResult(new JsonArray(query.Skip(offset).Take(limit).Select(r=>(JsonNode)new JsonObject{["id"]=r.Key,["document"]=r.Value.DeepClone()}).ToArray()));
        }
    }
    public Task UpsertAsync(string table,JsonArray values,CancellationToken ct=default)
    {lock(gate)foreach(var row in values)rows[row!["id"]!.GetValue<string>()]=row["document"]!.DeepClone();return Task.CompletedTask;}
    public Task<bool> TryWriteRuleAsync(string id,long? expectedRevision,JsonNode document,CancellationToken ct=default)
    {
        lock(gate)
        {
            var exists=rows.TryGetValue(id,out var current);
            if(expectedRevision is null?exists:!exists||current!["revision"]!.GetValue<long>()!=expectedRevision)return Task.FromResult(false);
            rows[id]=document.DeepClone();return Task.FromResult(true);
        }
    }
    public string Dump(){lock(gate)return JsonDefaults.Serialize(rows);}
    public Task<JsonNode> RpcAsync(string name,JsonObject parameters,CancellationToken ct=default)=>throw new NotSupportedException();
    public Task AddMessageAsync(string employee,string role,string text,CancellationToken ct=default)=>throw new NotSupportedException();
}
public sealed class ProviderTestFactory(Func<HttpRequestMessage,HttpResponseMessage> response) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)=>new(new FakeHttp(response));
}
public sealed class AdministrationTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static ManagedConfiguration Default()=>new(Source.Rules.DeepClone(),Source.Cars.ToList(),Source.OccupationDocuments.ToList(),Source.Employees.ToList(),
        new(false,new(){KeyReference="environment:CARWISE_AI_TEST_MISSING"},new(){KeyReference="environment:CARWISE_AI_TEST_MISSING"}));
    private static ManagedConfiguration Copy(ManagedConfiguration config)=>JsonDefaults.Read<ManagedConfiguration>(JsonDefaults.Serialize(config));
    private static (ConfigurationService Service,MemoryConfigurationDatabase Db,SecretVault Vault) Setup(MemoryConfigurationDatabase? db=null)
    {
        db??=new();var config=Default();var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","initial",config,[]),Source));
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        return(new(db,state,new(Source),vault),db,vault);
    }
    [Fact] public async Task SavingChangesTheLiveEngineAndKeepsOldRequestSnapshotStable()
    {
        var (service,_,_)=Setup();var previous=service.State.Current;var config=Copy(previous.Document.Configuration);
        config.Rules["electric"]!["netBonus"]=600m;
        var next=await service.SaveAsync(new(0,config,"update electric policy"),"admin-user",default);
        Assert.Equal(1,next.Revision);Assert.Equal("admin-user",next.Actor);
        Assert.InRange(new PayrollEngine(service.State.Current.Data).Calculate(Source.Employees[0],Source.Cars[9]).ElectricNet,599.9999999999m,600.0000000001m);
        Assert.InRange(new PayrollEngine(previous.Data).Calculate(Source.Employees[0],Source.Cars[9]).ElectricNet,249.9999999999m,250.0000000001m);
        Assert.Equal(0,previous.Document.Revision);
    }
    [Fact] public async Task RevisionConflictDoesNotOverwriteAnotherAdministrator()
    {
        var (service,db,_)=Setup();await service.SaveAsync(new(0,Default(),"first edit"),"admin-a",default);
        var other=Setup(db).Service;
        await Assert.ThrowsAsync<ConfigurationConflictException>(()=>other.SaveAsync(new(0,Default(),"stale edit"),"admin-b",default));
        Assert.Contains("first edit",db.Dump());Assert.DoesNotContain("stale edit",db.Dump());
    }
    [Fact] public async Task AtomicStorageAllowsOnlyOneCompetingInitialRevision()
    {
        var db=new MemoryConfigurationDatabase();
        var document=JsonNode.Parse("""{"revision":1}""")!;
        var attempts=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>db.TryWriteRuleAsync("admin-active",null,document))));
        Assert.Single(attempts,x=>x);
    }
    [Fact] public async Task RollbackCreatesANewVersionAndRetainsAuditHistory()
    {
        var (service,_,_)=Setup();var config=Copy(Default());config.Rules["budgets"]!["ג"]=5000m;
        await service.SaveAsync(new(0,config,"raise budget"),"admin",default);
        var restored=await service.RestoreAsync(new(1,0,"restore baseline"),"admin",default);
        Assert.Equal(2,restored.Revision);Assert.Equal(4500m,restored.Configuration.Rules["budgets"]!["ג"]!.GetValue<decimal>());
        Assert.Contains(restored.History,h=>h.Revision==1&&h.Note=="raise budget");
    }
    [Fact] public async Task ASecondServerLoadsPersistedConfiguration()
    {
        var (service,db,_)=Setup();var config=Copy(Default());config.Rules["credit"]="unused metadata";
        await service.SaveAsync(new(0,config,"persistent change"),"admin",default);
        var second=Setup(db).Service;await second.RefreshAsync(default);
        Assert.Equal(1,second.State.Current.Document.Revision);Assert.Equal("persistent change",second.State.Current.Document.Note);
    }
    [Fact] public async Task KeysAreEncryptedAndNeverReturnedOrIncludedInAuditSnapshots()
    {
        var (service,db,vault)=Setup();var config=Default() with{Ai=Default().Ai with{Enabled=true}};
        const string key="TEST-ONLY-NOT-A-REAL-KEY";
        var saved=await service.SaveAsync(new(0,config,"set fake test keys",new(){{"extraction",key},{"narration",key}}),"admin",default);
        Assert.DoesNotContain(key,db.Dump());Assert.DoesNotContain(key,JsonDefaults.Serialize(await service.ViewAsync(default)));
        Assert.Equal(key,await vault.ResolveAsync(saved.Configuration.Ai.Extraction.KeyReference,default,"api.openai.com"));
        Assert.Null(await vault.ResolveAsync(saved.Configuration.Ai.Extraction.KeyReference,default,"api.anthropic.com"));
    }
    [Fact] public async Task AStoredKeyCannotBeForwardedToAnotherProvider()
    {
        var (_,_,vault)=Setup();var reference=await vault.StoreAsync("TEST-ONLY", "api.openai.com",default);var calls=0;
        var profile=new AiProfile{Provider="anthropic",BaseUrl="https://api.anthropic.com/v1/",Model="test",KeyReference=reference};
        var client=new ConfiguredCompletionClient(profile,vault,new ProviderTestFactory(_=>{calls++;return new(HttpStatusCode.OK);}));
        await Assert.ThrowsAsync<AiUnavailableException>(()=>client.CompleteAsync("test",new JsonObject(),"test","test",default));Assert.Equal(0,calls);
    }
    [Theory][InlineData("openai")][InlineData("anthropic")][InlineData("gemini")][InlineData("compatible")]
    public async Task NativeAdaptersUseTheExpectedProtocol(string provider)
    {
        var (_,_,vault)=Setup();var url=provider switch{"anthropic"=>"https://api.anthropic.com/v1/","gemini"=>"https://generativelanguage.googleapis.com/v1beta/",_=>"https://api.openai.com/v1/"};
        var reference=await vault.StoreAsync("TEST-ONLY",new Uri(url).Host,default);
        var factory=new ProviderTestFactory(request=>{
            var body=JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            string response;
            if(provider=="anthropic")
            {
                Assert.EndsWith("/messages",request.RequestUri!.AbsolutePath);Assert.True(request.Headers.Contains("x-api-key"));Assert.Null(request.Headers.Authorization);
                Assert.NotNull(body["output_config"]?["format"]?["schema"]);
                response="""{"stop_reason":"end_turn","content":[{"type":"text","text":"{\"ok\":true}"}]}""";
            }
            else if(provider=="gemini")
            {
                Assert.EndsWith(":generateContent",request.RequestUri!.AbsolutePath);Assert.True(request.Headers.Contains("x-goog-api-key"));Assert.Null(request.Headers.Authorization);
                Assert.NotNull(body["generationConfig"]?["responseJsonSchema"]);
                response="""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"ok\":true}"}]}}]}""";
            }
            else
            {
                Assert.EndsWith("/chat/completions",request.RequestUri!.AbsolutePath);Assert.Equal("Bearer",request.Headers.Authorization?.Scheme);
                Assert.NotNull(body["response_format"]?["json_schema"]);
                response="""{"choices":[{"finish_reason":"stop","message":{"content":"{\"ok\":true}"}}]}""";
            }
            return new(HttpStatusCode.OK){Content=new StringContent(response,Encoding.UTF8,"application/json")};
        });
        var result=await new ConfiguredCompletionClient(new(){Provider=provider,BaseUrl=url,Model="test",KeyReference=reference},vault,factory)
            .CompleteAsync("probe",JsonNode.Parse("""{"type":"object"}""")!,"test","test",default);
        Assert.True(JsonNode.Parse(result)!["ok"]!.GetValue<bool>());
    }
    [Fact] public async Task ProviderBillingDiagnosticsDoNotExposeSecrets()
    {
        var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent("""{"error":{"code":"credit_balance_exhausted","message":"TEST-SECRET-MUST-NOT-LEAK"}}""")};
        var error=await AiProviderException.FromResponseAsync(response,default);
        Assert.Equal("credit_balance_exhausted",error.Code);Assert.DoesNotContain("TEST-SECRET",error.Message);
    }
    [Fact] public void AgentsAndEmployeesCannotUseAdministration()
    {
        var access=new AccessControl(new HttpClient(),false);var context=new DefaultHttpContext();
        context.Items["caller"]=new Caller("user","E-001",true);
        Assert.Throws<UnauthorizedAccessException>(()=>access.Admin(context));
        context.Items["caller"]=new Caller("admin","E-001",true,true);access.Admin(context);
        var demo=new AccessControl(new HttpClient(),true);context.Request.Host=new HostString("localhost",8000);context.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");
        Assert.Throws<UnauthorizedAccessException>(()=>demo.Admin(context));context.Connection.RemoteIpAddress=IPAddress.Loopback;demo.Admin(context);
    }
    [Fact] public void InvalidTaxAndForgedOfficialValueAreRejected()
    {
        var validator=new ConfigurationValidator(Source);var config=Copy(Default());config.Rules["netMethod"]!["brackets"]![1]![0]=1m;
        Assert.Throws<ArgumentException>(()=>validator.Validate(config));
        config=Copy(Default());config.Cars[0]=config.Cars[0] with{Value=1};Assert.Throws<ArgumentException>(()=>validator.Validate(config));
    }
    [Theory][InlineData("http://api.openai.com/v1/")][InlineData("https://127.0.0.1/v1/")][InlineData("https://api.openai.com.evil.example/v1/")][InlineData("https://user:pass@api.openai.com/v1/")]
    public void UnsafeProviderDestinationsAreRejected(string url)=>Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(new(){BaseUrl=url}));
    [Fact] public void ChangedOccupationNormalizesEmployeeEligibilityGroup()
    {
        var config=Copy(Default());var occupation=config.Occupations.Single(o=>o["occupation"].GetString()==Source.Employees[0].Occupation);
        occupation["group"]=System.Text.Json.JsonSerializer.SerializeToElement("ב");
        Assert.Equal("ב",new ConfigurationValidator(Source).Validate(config).Employees[0].Group);
    }
    [Fact] public void BaseSalaryContributionsAndTaxFreeMaintenanceAffectCalculation()
    {
        var config=Copy(Default());config.Rules["netMethod"]!["contributionsBase"]="base";config.Rules["waiver"]!["maintenanceIsTaxable"]=false;
        var engine=new PayrollEngine(new ConfigurationValidator(Source).Validate(config));var e=Source.Employees[1];
        Assert.Equal(e.Salary*e.Pension/100m,engine.Calculate(e,Source.Cars[4]).Pension);
        var waiver=engine.Calculate(Source.Employees[0],null);Assert.Equal(Source.Employees[0].Salary,waiver.Taxable);
    }
    [Fact] public void ADelayedRefreshCannotDowngradeAnAppliedRevision()
    {
        var (service,_,_)=Setup();var original=service.State.Current;
        service.State.Publish(original with{Document=original.Document with{Revision=5}});
        service.State.Publish(original with{Document=original.Document with{Revision=2}});
        Assert.Equal(5,service.State.Current.Document.Revision);
    }
}
