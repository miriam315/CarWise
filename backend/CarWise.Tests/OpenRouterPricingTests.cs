using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;

public sealed class OpenRouterPricingTests
{
    private const string Catalog="""{"data":[{"id":"openai/gpt-4.1-mini","pricing":{"prompt":"0.0000004","completion":"0.0000016","input_cache_read":"0.0000001"}},{"id":"google/gemini-2.5-flash","pricing":{"prompt":"3e-7","completion":"0.0000025"}},{"id":"test/free","pricing":{"prompt":"0","completion":"0"}},{"id":"test/tiered","pricing":{"prompt":"0.000001","completion":"0.000002","overrides":[{"min_prompt_tokens":100000,"prompt":"0.000003"}]}},{"id":"test/invalid","pricing":{"prompt":"-1","completion":"unknown"}}]}""";
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static AiConfiguration Settings(AiProfile? profile=null)=>new(false,profile??new(),profile??new());
    private static ProviderTestFactory Factory(Func<HttpRequestMessage,HttpResponseMessage>? reply=null)=>new(reply??(_=>new(HttpStatusCode.OK){Content=new StringContent(Catalog)}));
    [Fact] public void PricesConvertFromPerTokenUsingDecimalAndMissingCacheIsNotInvented()
    {
        var catalog=OpenRouterPricing.Parse(JsonNode.Parse(Catalog)!,DateTimeOffset.UtcNow);
        Assert.Equal(.4m,catalog.Models["openai/gpt-4.1-mini"].InputUsdPerMillion);
        Assert.Equal(1.6m,catalog.Models["openai/gpt-4.1-mini"].OutputUsdPerMillion);
        Assert.Equal(.1m,catalog.Models["openai/gpt-4.1-mini"].CachedInputUsdPerMillion);
        Assert.Equal(.3m,catalog.Models["google/gemini-2.5-flash"].InputUsdPerMillion);
        Assert.Null(catalog.Models["google/gemini-2.5-flash"].CachedInputUsdPerMillion);
        Assert.Equal(0m,catalog.Models["test/free"].InputUsdPerMillion);
        Assert.NotNull(catalog.Models["test/tiered"].UnavailableReason);Assert.NotNull(catalog.Models["test/invalid"].UnavailableReason);
    }
    [Theory][InlineData("{}")] [InlineData("{\"data\":[]}")] [InlineData("{\"data\":[{\"id\":\"test/model\",\"pricing\":{\"prompt\":\"NaN\",\"completion\":\"0\"}}]}")]
    public void InvalidCataloguesCannotReplaceSavedPrices(string body)=>Assert.Throws<InvalidDataException>(()=>OpenRouterPricing.Parse(JsonNode.Parse(body)!,DateTimeOffset.UtcNow));
    [Fact] public async Task PublicFetchIsDurablePreservesLastGoodSnapshotAndNeverSendsCredentials()
    {
        var db=new MemoryConfigurationDatabase();var fail=false;var calls=0;
        var pricing=new OpenRouterPricing(db,Factory(request=>{
            calls++;Assert.Equal(OpenRouterPricing.Source,request.RequestUri!.ToString());Assert.Equal(HttpMethod.Get,request.Method);
            Assert.Null(request.Content);Assert.Null(request.Headers.Authorization);Assert.DoesNotContain(request.Headers,h=>h.Key.Contains("key",StringComparison.OrdinalIgnoreCase));
            return new(fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent(Catalog)};
        }));
        Assert.True(await pricing.RefreshAsync(default));var first=pricing.Current;Assert.NotNull(first);
        fail=true;Assert.False(await pricing.RefreshAsync(default));Assert.Same(first,pricing.Current);Assert.NotNull(pricing.LastError);
        var restored=new OpenRouterPricing(db,Factory());await restored.LoadAsync(default);Assert.Equal(first.RetrievedAt,restored.Current!.RetrievedAt);
        Assert.Equal(first.Models["openai/gpt-4.1-mini"],restored.Current.Models["openai/gpt-4.1-mini"]);Assert.Equal(2,calls);
        Assert.DoesNotContain("admin-active",db.Dump());
    }
    [Fact] public async Task DirectProvidersUseExplicitlyLabelledReferencePricesWithoutChangingTheirRoute()
    {
        var pricing=new OpenRouterPricing(new MemoryConfigurationDatabase(),Factory());await pricing.RefreshAsync(default);
        var profile=new AiProfile();var before=JsonDefaults.Serialize(profile);var price=pricing.PriceFor(profile,Settings());
        Assert.NotNull(price);Assert.True(price.ReferenceOnly);Assert.Equal("api.openai.com",price.Host);Assert.Equal("gpt-4.1-mini",price.Model);
        Assert.Equal("openai/gpt-4.1-mini",price.SourceModel);Assert.Equal(OpenRouterPricing.Source,price.Source);Assert.Equal(before,JsonDefaults.Serialize(profile));
        var routed=profile with{Provider="compatible",BaseUrl="https://openrouter.ai/api/v1/",Model="openai/gpt-4.1-mini"};
        Assert.False(pricing.PriceFor(routed,Settings(routed))!.ReferenceOnly);
        Assert.Null(pricing.PriceFor(profile with{Model="gpt-4.1-mini-unknown-version"},Settings()));
        Assert.Null(pricing.PriceFor(profile with{Provider="compatible",BaseUrl="https://api.groq.com/openai/v1/"},Settings()));
        var settings=Settings() with{OpenRouterPriceMappings=new(){{OpenRouterPricing.Key(profile),"test/free"}}};
        Assert.Equal(0m,pricing.PriceFor(profile,settings)!.InputUsdPerMillion);
        settings=settings with{OpenRouterPriceMappings=new(){{OpenRouterPricing.Key(profile),"test/tiered"}}};
        Assert.Null(pricing.PriceFor(profile,settings));
    }
    [Fact] public async Task AccountingUsesManualFirstThenAutomaticAndSnapshotsTheOriginalReferencePrice()
    {
        var db=new MemoryConfigurationDatabase();var json=Catalog;var pricing=new OpenRouterPricing(db,Factory(_=>new(HttpStatusCode.OK){Content=new StringContent(json)}));
        await pricing.RefreshAsync(default);var profile=new AiProfile();
        ConfigurationSnapshot Snapshot(AiConfiguration ai)=>new(new(4,DateTimeOffset.UtcNow,"admin","",new(Data.Rules,Data.Cars,Data.OccupationDocuments,Data.Employees,ai),[]),Data);
        UsageAccounting Recorder(AiConfiguration ai)=>new(db,Snapshot(ai),new(db,Factory()),new(),NullLogger<UsageAccounting>.Instance,pricing);
        var recorder=Recorder(Settings());var record=await recorder.StartAsync(profile,"test",default);
        Assert.True(record.Pricing!.ReferenceOnly);Assert.Equal(.4m,record.Pricing.InputUsdPerMillion);
        json=Catalog.Replace("0.0000004","0.0000008");await pricing.RefreshAsync(default);
        await recorder.FinishAsync(record,JsonNode.Parse("""{"usage":{"prompt_tokens":1000,"completion_tokens":100}}"""),null);
        var saved=JsonDefaults.Read<AiUsageRecord>((await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",record.Id}}))[0]!["document"]!.ToJsonString());
        Assert.Equal(.00056m,saved.EstimatedUsd);Assert.Equal(.4m,saved.Pricing!.InputUsdPerMillion);Assert.NotNull(saved.Pricing.RetrievedAt);
        var next=await recorder.StartAsync(profile,"next",default);Assert.Equal(.8m,next.Pricing!.InputUsdPerMillion);
        var manual=new AiModelPrice("openai","api.openai.com",profile.Model,9m,null,10m,null,"Account contract");
        Assert.Equal(manual,(await Recorder(Settings() with{Pricing=[manual]}).StartAsync(profile,"manual",default)).Pricing);
        Assert.Null((await Recorder(Settings() with{OpenRouterPricingEnabled=false}).StartAsync(profile,"disabled",default)).Pricing);
        Assert.Null(new TokenUsage(1000,100,0,10).Cost(manual));Assert.NotNull(new TokenUsage(1000,0,0,10).Cost(manual));
    }
    [Fact] public void SchedulerHonorsDisableDailyHourRestartFreshnessAndRetryBackoff()
    {
        var now=DateTimeOffset.Parse("2026-10-24T10:00:00Z");var old=new OpenRouterCatalog(now.AddDays(-2),[]);var fresh=old with{RetrievedAt=now};var yesterday=old with{RetrievedAt=now.AddHours(-15)};
        Assert.True(OpenRouterPricing.ShouldRefresh(Settings(),null,null,now));
        Assert.False(OpenRouterPricing.ShouldRefresh(Settings() with{OpenRouterPricingEnabled=false},old,null,now));
        Assert.False(OpenRouterPricing.ShouldRefresh(Settings(),fresh,null,now));
        Assert.False(OpenRouterPricing.ShouldRefresh(Settings(),old,now.AddMinutes(-30),now));
        Assert.True(OpenRouterPricing.ShouldRefresh(Settings(),old,now.AddHours(-1),now));
        Assert.False(OpenRouterPricing.ShouldRefresh(Settings() with{OpenRouterPricingUpdateHourUtc=17},yesterday,null,now));
        Assert.True(OpenRouterPricing.ShouldRefresh(Settings() with{OpenRouterPricingUpdateHourUtc=3},yesterday,null,now));
    }
    [Fact] public void PricingSettingsAreOperationalAndValidationRejectsBadMappingsAndHours()
    {
        var config=new ManagedConfiguration(Data.Rules,Data.Cars,Data.OccupationDocuments,Data.Employees,Settings());var validator=new ConfigurationValidator(Data);
        var next=config with{Ai=Settings() with{OpenRouterPricingUpdateHourUtc=8,OpenRouterPriceMappings=new(){{OpenRouterPricing.Key(new()),"openai/gpt-4.1-mini"}}}};
        Assert.False(ConfigurationChanges.PolicyChanged(config,next));validator.Validate(next);
        Assert.Throws<ArgumentException>(()=>validator.Validate(next with{Ai=next.Ai with{OpenRouterPricingUpdateHourUtc=24}}));
        Assert.Throws<ArgumentException>(()=>validator.Validate(next with{Ai=next.Ai with{OpenRouterPriceMappings=new(){{"invalid", "https://example.com?secret=yes"}}}}));
    }
}
