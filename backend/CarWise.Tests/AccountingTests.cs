using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class AccountingTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AiModelPrice Price=>new("openai","api.openai.com","gpt-4.1-mini",2m,0.5m,8m,3m,"test rate; not a live quotation");
    [Theory]
    [InlineData("openai","""{"usage":{"prompt_tokens":1000,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":200}}}""",1000,200,0,100)]
    [InlineData("anthropic","""{"usage":{"input_tokens":600,"cache_read_input_tokens":200,"cache_creation_input_tokens":200,"output_tokens":100}}""",1000,200,200,100)]
    [InlineData("gemini","""{"usageMetadata":{"promptTokenCount":1000,"cachedContentTokenCount":200,"candidatesTokenCount":80,"thoughtsTokenCount":20}}""",1000,200,0,100)]
    public void ProviderUsageSeparatesCacheAndIncludesReasoning(string provider,string body,long input,long cached,long write,long output)
    {Assert.Equal(new(input,cached,write,output),TokenUsage.Parse(provider,JsonNode.Parse(body)));}
    [Fact] public void DecimalCostsDoNotDoubleChargeCachedTokens()
    {
        var usage=new TokenUsage(1000,200,100,100);
        Assert.Equal(.0026m,usage.Cost(Price));
        Assert.Null(usage.Cost(null));Assert.Null(usage.Cost(Price with{CacheWriteUsdPerMillion=null}));
        Assert.Equal(0m,new TokenUsage(0,0,0,0).Cost(Price));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"usage":{"prompt_tokens":-1,"completion_tokens":5}}""")]
    [InlineData("""{"usage":{"prompt_tokens":1,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":2}}}""")]
    public void MissingOrInvalidUsageIsUnknown(string json)=>Assert.Null(TokenUsage.Parse("openai",JsonNode.Parse(json)));
    [Fact] public void BankIsraelParserKeepsPublicationDateAndRejectsImpossibleRate()
    {
        var now=DateTimeOffset.Parse("2026-10-19T17:00:00Z");
        var body=JsonNode.Parse("""{"exchangeRates":[{"key":"USD","currentExchangeRate":3.7,"unit":1,"lastUpdate":"2026-10-19T15:00:00+03:00"}]}""")!;
        var rate=ExchangeRates.Parse(body,now);Assert.Equal(3.7m,rate.UsdIls);Assert.Equal(new DateOnly(2026,10,19),rate.PublishedDate);
        body["exchangeRates"]![0]!["currentExchangeRate"]=0m;
        Assert.Throws<InvalidDataException>(()=>ExchangeRates.Parse(body,now));
    }
    [Fact] public async Task CostsAndExchangeRateArePersistedWithoutPromptOrKeyAndRemainUnchanged()
    {
        var db=new MemoryConfigurationDatabase();var profile=new AiProfile();
        var config=new ManagedConfiguration(Data.Rules,Data.Cars,Data.OccupationDocuments,Data.Employees,new(true,profile,profile){Pricing=[Price]});
        var snapshot=new ConfigurationSnapshot(new(12,DateTimeOffset.UtcNow,"admin","prices",config,[]),Data);
        var factory=new ProviderTestFactory(_=>new(HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Serialize(new{exchangeRates=new[]{new{key="USD",currentExchangeRate=3.7m,unit=1,lastUpdate=DateTimeOffset.UtcNow.ToString("O")}}}))});
        var fx=new ExchangeRates(db,factory);Assert.True(await fx.RefreshAsync(default));
        var recorder=new UsageAccounting(db,snapshot,fx,new(){ConversationId="E-001"},NullLogger<UsageAccounting>.Instance);
        var pending=await recorder.StartAsync(profile,"extract_employee_intent",default);
        Assert.Contains("pending",db.Dump());
        await recorder.FinishAsync(pending,JsonNode.Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":200}}}"""),null);
        var saved=(await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",pending.Id}}))[0]!["document"]!;
        var record=JsonDefaults.Read<AiUsageRecord>(saved.ToJsonString());
        Assert.Equal(.0025m,record.EstimatedUsd);Assert.Equal(.00925m,record.EstimatedIls);Assert.Equal(12,record.ConfigurationRevision);
        Assert.Equal(Price,record.Pricing);Assert.Equal("E-001",record.ConversationId);Assert.Equal("completed",record.Status);
        var failure=await recorder.StartAsync(profile,"narrate_comparison",default);await recorder.FinishAsync(failure,null,new AiProviderException(429,"insufficient_quota"));
        var failed=JsonDefaults.Read<AiUsageRecord>((await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",failure.Id}}))[0]!["document"]!.ToJsonString());
        Assert.Null(failed.EstimatedUsd);Assert.Equal("failed",failed.Status);Assert.Equal(pending.ActionId,failed.ActionId);
        Assert.DoesNotContain("messages",db.Dump());Assert.DoesNotContain("apiKey",db.Dump());
    }
    [Fact] public async Task ProviderResponseIsAccountedEvenWhenJsonOutputFails()
    {
        var db=new MemoryConfigurationDatabase();var profile=new AiProfile();
        var config=new ManagedConfiguration(Data.Rules,Data.Cars,Data.OccupationDocuments,Data.Employees,new(true,profile,profile){Pricing=[Price]});
        var snapshot=new ConfigurationSnapshot(new(1,DateTimeOffset.UtcNow,"admin","pricing",config,[]),Data);
        var factory=new ProviderTestFactory(_=>new(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"length","message":{"content":"incomplete"}}],"usage":{"prompt_tokens":1000,"completion_tokens":100}}""")});
        var recorder=new UsageAccounting(db,snapshot,new(db,factory),new(),NullLogger<UsageAccounting>.Instance);
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var client=new ConfiguredCompletionClient(profile,vault,factory,"TEST-ONLY",recorder);
        await Assert.ThrowsAsync<AiUnavailableException>(()=>client.CompleteAsync("test",new JsonObject(),"private instructions","PRIVATE PROMPT",default));
        var row=(await db.ReadAsync("carwise_rules"))[0]!["document"]!;var usage=JsonDefaults.Read<AiUsageRecord>(row.ToJsonString());
        Assert.Equal("failed",usage.Status);Assert.Equal(.0028m,usage.EstimatedUsd);Assert.Null(usage.EstimatedIls);Assert.DoesNotContain("PRIVATE PROMPT",db.Dump());Assert.DoesNotContain("TEST-ONLY",db.Dump());
    }
}
