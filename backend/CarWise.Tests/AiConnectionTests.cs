using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class AiConnectionTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static (MemoryConfigurationDatabase Db,SecretVault Vault) Setup()
    {
        var db=new MemoryConfigurationDatabase();
        return(db,new(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build()));
    }
    private static HttpResponseMessage Reply(string output="{\"ok\":true}",string finish="stop")=>new(HttpStatusCode.OK){Content=new StringContent(
        new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]=finish,["message"]=new JsonObject{["content"]=output}})}.ToJsonString())};
    private static AiProfile Profile=>new(){KeyReference="environment:CARWISE_AI_UNCONFIGURED"};

    [Theory][InlineData(128,5)][InlineData(8192,90)][InlineData(1200,60)]
    public async Task AllowedValuesSaveAndReachTheProviderUnchanged(int tokens,int seconds)
    {
        var (db,vault)=Setup();var profile=Profile with{MaxOutputTokens=tokens,TimeoutSeconds=seconds};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,new(false,profile,profile));
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));
        var saved=await new ConfigurationService(db,state,new(Source),vault).SaveAsync(new(0,config,"valid range regression"),"admin",default);
        Assert.Equal(tokens,saved.Configuration.Ai.Extraction.MaxOutputTokens);
        var factory=new ProviderTestFactory(request=>{var body=JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Assert.Equal(tokens,body["max_completion_tokens"]!.GetValue<int>());return Reply();});
        var result=await AiConnectionTest.RunAsync(new(profile,ApiKeys:new(){{"primary","TEST-DRAFT-PRIMARY"}}),vault,factory,null,new(),default);
        Assert.True(result.Success);Assert.DoesNotContain("TEST-DRAFT-PRIMARY",db.Dump());
    }
    [Fact] public void ValidationIdentifiesTheOtherModelWithTheInvalidLimit()
    {
        var profile=Profile;var ai=new AiConfiguration(false,profile,profile){Models=[new("visible","Visible",profile),new("other","Other model",profile with{TimeoutSeconds=91})],
            ServiceModels=new(){{"extraction","visible"},{"narration","visible"},{"payslip","visible"}}};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        var error=Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(Source).Validate(config));
        Assert.Contains("Other model",error.Message);Assert.Contains("other",error.Message);Assert.Contains("91",error.Message);
    }
    [Fact] public async Task DraftPoolUsesNewKeysInOrderWithoutSavingThemOrChangingStoredKeyHealth()
    {
        var (db,vault)=Setup();var reference=await vault.StoreAsync("TEST-STORED-PRIMARY","api.openai.com",default);
        var profile=Profile with{KeyReference=reference,BackupKeys=[new("aabbccddeeff0011","backup",Profile.KeyReference)]};
        var health=new AiKeyHealth();health.Reject(profile,reference,new(429,"rate_limit_exceeded"));var keys=new List<string>();
        var factory=new ProviderTestFactory(request=>{var key=request.Headers.Authorization!.Parameter!;keys.Add(key);
            return key=="TEST-DRAFT-PRIMARY"?new(HttpStatusCode.TooManyRequests){Content=new StringContent("""{"error":{"code":"rate_limit_exceeded"}}""")}:Reply();});
        var request=new TestAiRequest(profile,ApiKeys:new(){{"primary"," TEST-DRAFT-PRIMARY "},{"aabbccddeeff0011","TEST-DRAFT-BACKUP"}});
        var result=await AiConnectionTest.RunAsync(request,vault,factory,null,health,default);
        Assert.True(result.Success);Assert.Equal(new[]{"TEST-DRAFT-PRIMARY","TEST-DRAFT-BACKUP"},keys);
        Assert.NotNull(health.Get(profile,reference));Assert.Equal("TEST-STORED-PRIMARY",await vault.ResolveAsync(reference,default,"api.openai.com"));
        Assert.DoesNotContain("TEST-DRAFT",db.Dump());
    }
    [Fact] public async Task BlankDraftKeepsStoredKeyAndDisabledDraftKeysAreSkipped()
    {
        var (_,vault)=Setup();var profile=Profile with{PrimaryKeyEnabled=false,BackupKeys=[new("aabbccddeeff0011","backup",await vault.StoreAsync("TEST-STORED-BACKUP","api.openai.com",default))]};
        var calls=0;var factory=new ProviderTestFactory(request=>{calls++;Assert.Equal("TEST-STORED-BACKUP",request.Headers.Authorization!.Parameter);return Reply();});
        var result=await AiConnectionTest.RunAsync(new(profile,ApiKeys:new(){{"primary","TEST-DISABLED"},{"aabbccddeeff0011"," "}}),vault,factory,null,new(),default);
        Assert.True(result.Success);Assert.Equal(1,calls);
    }
    [Theory][InlineData("timeout")][InlineData("network_error")][InlineData("invalid_output")]
    public async Task FailureDiagnosticsDoNotExposeTransportOrResponseContents(string expected)
    {
        var (_,vault)=Setup();var factory=new RoutingFactory(_=>expected switch{
            "timeout"=>throw new TaskCanceledException("TEST-SECRET"),
            "network_error"=>throw new HttpRequestException("TEST-SECRET"),
            _=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("TEST-SECRET")})});
        var result=await AiConnectionTest.RunAsync(new(Profile,"TEST-KEY"),vault,factory,null,new(),default);
        Assert.False(result.Success);Assert.Equal(expected,result.Code);Assert.DoesNotContain("TEST-SECRET",result.Message);
    }
    [Theory][InlineData("{\"ok\":\"yes\"}")][InlineData("[]")][InlineData("{\"ok\":false}")]
    public async Task MalformedProbeShapeIsAnExplicitFailure(string output)
    {
        var (_,vault)=Setup();var result=await AiConnectionTest.RunAsync(new(Profile,"TEST-KEY"),vault,new ProviderTestFactory(_=>Reply(output)),null,new(),default);
        Assert.False(result.Success);Assert.Equal("invalid_output",result.Code);
    }
    [Theory][InlineData("openai")][InlineData("anthropic")][InlineData("gemini")]
    public async Task TokenExhaustionIsIdentifiedForEveryNativeProvider(string provider)
    {
        var (_,vault)=Setup();var profile=Profile with{Provider=provider,BaseUrl=provider switch{"anthropic"=>"https://api.anthropic.com/v1/","gemini"=>"https://generativelanguage.googleapis.com/v1beta/",_=>Profile.BaseUrl}};
        var factory=new ProviderTestFactory(_=>provider switch{
            "anthropic"=>new(HttpStatusCode.OK){Content=new StringContent("""{"stop_reason":"max_tokens"}""")},
            "gemini"=>new(HttpStatusCode.OK){Content=new StringContent("""{"candidates":[{"finishReason":"MAX_TOKENS"}]}""")},_=>Reply("","length")});
        var result=await AiConnectionTest.RunAsync(new(profile,"TEST-KEY"),vault,factory,null,new(),default);
        Assert.False(result.Success);Assert.Equal("output_limit",result.Code);Assert.Contains("טוקנים",result.Message);
    }
    [Theory][InlineData("max_completion_tokens",true)][InlineData("TEST-SECRET",false)]
    public async Task RejectedRequestExposesOnlyKnownParameterNames(string parameter,bool visible)
    {
        var (_,vault)=Setup();var factory=new ProviderTestFactory(_=>new(HttpStatusCode.BadRequest){Content=new StringContent(
            new JsonObject{["error"]=new JsonObject{["code"]="unsupported_parameter",["param"]=parameter,["message"]="TEST-SECRET"}}.ToJsonString())});
        var result=await AiConnectionTest.RunAsync(new(Profile,"TEST-KEY"),vault,factory,null,new(),default);
        Assert.False(result.Success);Assert.Equal(400,result.Status);Assert.Equal("unsupported_parameter",result.Code);
        Assert.DoesNotContain("TEST-SECRET",result.Message);Assert.Equal(visible,result.Message.Contains("max_completion_tokens"));
    }
    [Fact] public async Task UnknownDraftKeyIsRejectedBeforeSendingAnything()
    {
        var (_,vault)=Setup();var calls=0;var factory=new ProviderTestFactory(_=>{calls++;return Reply();});
        await Assert.ThrowsAsync<ArgumentException>(()=>AiConnectionTest.RunAsync(new(Profile,ApiKeys:new(){{"unknown","TEST-KEY"}}),vault,factory,null,new(),default));
        Assert.Equal(0,calls);
    }
}
