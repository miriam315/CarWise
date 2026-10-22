using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;
public sealed class GeminiGenerationTests
{
    [Theory][InlineData("gemini-2.5-flash","thinkingBudget","0")][InlineData("gemini-3-flash-preview","thinkingLevel","minimal")]
    [InlineData("gemini-3.1-pro-preview","thinkingLevel","low")]
    public void DocumentAutoModeReducesThinkingWithoutChangingAdministratorsOutputBudget(string model,string field,string expected)
    {
        var profile=new AiProfile{Model=model,MaxOutputTokens=65536};
        var result=GeminiGeneration.Create(profile,new JsonObject(),true);
        Assert.Equal(expected,result["thinkingConfig"]![field]!.ToString());Assert.Equal(65536,result["maxOutputTokens"]!.GetValue<int>());
        Assert.Null(GeminiGeneration.Create(profile,new JsonObject(),false)["thinkingConfig"]);
    }
    [Theory][InlineData("default")][InlineData("auto")]
    public void UnknownModelsAndExplicitProviderDefaultAreNotGivenAnAssumedThinkingProtocol(string mode)
        =>Assert.Null(GeminiGeneration.Create(new(){Model="custom-model",GeminiThinking=mode},new JsonObject(),true)["thinkingConfig"]);
    [Fact] public async Task TruncatedDocumentNeverReturnsPartialJsonOrRetriesAnotherKey()
    {
        var vault=new SecretVault(new MemoryConfigurationDatabase(),new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var calls=0;JsonNode? sent=null;
        var factory=new ProviderTestFactory(request=>{calls++;sent=JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());return new(HttpStatusCode.OK){Content=new StringContent("""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"{}"}]}}],"usageMetadata":{"promptTokenCount":42,"thoughtsTokenCount":8192}}""")};});
        var profile=new AiProfile{Provider="gemini",BaseUrl="https://generativelanguage.googleapis.com/v1beta/",Model="gemini-2.5-flash",MaxOutputTokens=32768};
        var error=await Assert.ThrowsAsync<AiUnavailableException>(()=>new ConfiguredCompletionClient(profile,vault,factory,testKey:"SYNTHETIC-ONLY").CompleteDocumentAsync("extract_payslip",new JsonObject(),"synthetic",new("application/pdf",Encoding.ASCII.GetBytes("%PDF-1.7 synthetic test")),default));
        Assert.Equal("output_limit",error.Code);Assert.Equal(1,calls);
        Assert.Equal(32768,sent!["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0,sent["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }
}
