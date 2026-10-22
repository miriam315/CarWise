using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Accounting;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;
public sealed class NativeToolTests
{
    [Theory][InlineData("openai")][InlineData("compatible")][InlineData("gemini")][InlineData("anthropic")]
    public async Task NativeFunctionCallAndResultRoundTripUseSelectedProviderAndPreserveOpaqueContent(string provider)
    {
        var requests=new List<JsonNode>();var invocation=0;
        var factory=new ProviderTestFactory(request=>{
            var payload=JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;requests.Add(payload);
            var first=invocation++==0;
            var json=provider switch{
                "gemini"=>first?"""{"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[{"thoughtSignature":"OPAQUE-SIGNATURE","functionCall":{"name":"get_my_eligibility","args":{}}}]}}]}""":"""{"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[{"text":"{FACT_0}"}]}}]}""",
                "anthropic"=>first?"""{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"tool1","name":"get_my_eligibility","input":{}}]}""":"""{"stop_reason":"end_turn","content":[{"type":"text","text":"{FACT_0}"}]}""",
                _=>first?"""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"tool1","type":"function","function":{"name":"get_my_eligibility","arguments":"{}"}}]}}]}""":"""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{FACT_0}"}}]}"""};
            return new(HttpStatusCode.OK){Content=new StringContent(json)};
        });
        var vault=new SecretVault(new MemoryConfigurationDatabase(),new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var profile=new AiProfile{Provider=provider,BaseUrl=provider switch{"gemini"=>"https://generativelanguage.googleapis.com/v1beta/","anthropic"=>"https://api.anthropic.com/v1/",_=>"https://api.openai.com/v1/"},Model="SELECTED-MODEL",MaxOutputTokens=100000};
        var client=new ConfiguredCompletionClient(profile,vault,factory,testKey:"SYNTHETIC-ONLY");
        var messages=new List<AiToolMessage>{new("user","לאיזה קבוצה אני שייכת?")};
        var firstTurn=await client.CallToolsAsync("test",AdvisorToolConversation.Tools,messages,default);
        var call=Assert.Single(firstTurn.Calls);Assert.Equal("get_my_eligibility",call.Name);
        messages.Add(new("assistant",Native:firstTurn.Native));messages.Add(new("tool",CallId:call.Id,Name:call.Name,Result:new(){["fact"]="SERVER-FACT"}));
        var final=await client.CallToolsAsync("test",AdvisorToolConversation.Tools,messages,default);
        Assert.Empty(final.Calls);Assert.Equal("{FACT_0}",final.Text);Assert.Equal(2,requests.Count);
        Assert.Contains("SERVER-FACT",requests[1].ToJsonString());Assert.Contains("get_my_eligibility",requests[0].ToJsonString());
        Assert.DoesNotContain("response_format",requests[0].ToJsonString());
        if(provider=="gemini")
        {
            Assert.Contains("OPAQUE-SIGNATURE",requests[1].ToJsonString());Assert.NotNull(requests[1]["contents"]![2]!["parts"]![0]!["functionResponse"]);
            Assert.Null(requests[0]["generationConfig"]!["responseJsonSchema"]);Assert.Equal(100000,requests[0]["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        }
        else Assert.Equal("SELECTED-MODEL",requests[0]["model"]!.ToString());
    }
    [Fact] public async Task AdminProbeActuallyExecutesAFullSyntheticToolRoundTrip()
    {
        var calls=0;var factory=new ProviderTestFactory(_=>new(HttpStatusCode.OK){Content=new StringContent(calls++==0?
            """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[{"id":"probe","type":"function","function":{"name":"carwise_connection_probe","arguments":"{\"nonce\":\"carwise\"}"}}]}}]}""":
            """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"CARWISE_TOOL_OK"}}]}""")});
        var vault=new SecretVault(new MemoryConfigurationDatabase(),new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var result=await AiConnectionTest.RunAsync(new(new(),"SYNTHETIC-ONLY",ToolCalling:true),vault,factory,null,new(),default);
        Assert.True(result.Success);Assert.Equal("tools_ok",result.Code);Assert.Equal(2,calls);
    }
    [Fact] public async Task NativeToolsKeepKeyFailoverAndDurablePerAttemptTokenAccounting()
    {
        var data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));var db=new MemoryConfigurationDatabase();
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var profile=new AiProfile{KeyReference=await vault.StoreAsync("SYNTHETIC-MAIN","api.openai.com",default),BackupKeys=[new("aabbccddeeff0011","backup",await vault.StoreAsync("SYNTHETIC-BACKUP","api.openai.com",default))]};
        var price=new AiModelPrice("openai","api.openai.com",profile.Model,2,1,8,null,"synthetic test price");
        var config=new ManagedConfiguration(data.Rules,data.Cars,data.OccupationDocuments,data.Employees,new(true,profile,profile){Pricing=[price]});
        var snapshot=new ConfigurationSnapshot(new(1,DateTimeOffset.UtcNow,"admin","test",config,[]),data);
        var keys=new List<string>();var factory=new ProviderTestFactory(request=>{
            var key=request.Headers.Authorization!.Parameter!;keys.Add(key);
            return key=="SYNTHETIC-MAIN"?new(HttpStatusCode.TooManyRequests){Content=new StringContent("""{"error":{"code":"rate_limit_exceeded"}}""")}:
                new(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[{"id":"tool1","type":"function","function":{"name":"get_my_eligibility","arguments":"{}"}}]}}],"usage":{"prompt_tokens":100,"completion_tokens":20}}""")};
        });
        var accounting=new UsageAccounting(db,snapshot,new ExchangeRates(db,factory),new(){ConversationId="SYNTHETIC-EMPLOYEE"},NullLogger<UsageAccounting>.Instance);
        var result=await new ConfiguredCompletionClient(profile,vault,factory,accounting:accounting,health:new()).CallToolsAsync("PRIVATE-PROMPT",AdvisorToolConversation.Tools,[new("user","PRIVATE-TEXT")],default);
        Assert.Single(result.Calls);Assert.Equal(new[]{"SYNTHETIC-MAIN","SYNTHETIC-BACKUP"},keys);
        var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","usage:"}});
        var records=rows.Select(r=>JsonDefaults.Read<AiUsageRecord>(r!["document"]!.ToJsonString())).ToArray();Assert.Equal(2,records.Length);
        Assert.All(records,r=>Assert.Equal("advisor_tool_turn",r.Operation));Assert.Single(records,r=>r.Status=="failed");
        var successful=Assert.Single(records,r=>r.Status=="completed");Assert.Equal(100,successful.Tokens!.Input);Assert.Equal(.00036m,successful.EstimatedUsd);
        Assert.DoesNotContain("PRIVATE-PROMPT",db.Dump());Assert.DoesNotContain("PRIVATE-TEXT",db.Dump());Assert.DoesNotContain("SYNTHETIC-BACKUP",db.Dump());
    }
    [Theory][InlineData("openai")][InlineData("gemini")][InlineData("anthropic")]
    public async Task TruncatedToolCallsNeverExecuteAndDoNotRetryAnotherKey(string provider)
    {
        var calls=0;var factory=new ProviderTestFactory(_=>{calls++;return new(HttpStatusCode.OK){Content=new StringContent(provider switch{
            "gemini"=>"""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"role":"model","parts":[]}}]}""",
            "anthropic"=>"""{"stop_reason":"max_tokens","content":[]}""",_=>"""{"choices":[{"finish_reason":"length","message":{"role":"assistant","content":""}}]}"""})};});
        var vault=new SecretVault(new MemoryConfigurationDatabase(),new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var profile=new AiProfile{Provider=provider,BaseUrl=provider switch{"gemini"=>"https://generativelanguage.googleapis.com/v1beta/","anthropic"=>"https://api.anthropic.com/v1/",_=>"https://api.openai.com/v1/"}};
        var error=await Assert.ThrowsAsync<AiUnavailableException>(()=>new ConfiguredCompletionClient(profile,vault,factory,testKey:"SYNTHETIC-ONLY").CallToolsAsync("test",AdvisorToolConversation.Tools,[new("user","test")],default));
        Assert.Equal("output_limit",error.Code);Assert.Equal(1,calls);
    }
}
