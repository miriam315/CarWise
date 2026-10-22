using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class CustomAiProviderTests
{
    private static AiProfile Profile=>new(){CustomProvider=true,CustomEndpointApproved=true,DisplayName="Custom supplier",Provider="compatible",BaseUrl="https://api.custom-supplier.com/v1/"};
    [Fact] public void CustomDestinationRequiresExplicitApprovalAndAName()
    {
        ConfigurationValidator.ValidateAi(Profile);
        Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(Profile with{CustomEndpointApproved=false}));
        Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(Profile with{DisplayName=""}));
        Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(Profile with{CustomProvider=false}));
        Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(Profile with{Provider="invented-protocol"}));
    }
    [Theory]
    [InlineData("https://127.0.0.1/")][InlineData("https://[::1]/")][InlineData("https://server.internal/")]
    [InlineData("https://server.local/")][InlineData("https://localhost/")][InlineData("https://api.example.com:8443/")]
    [InlineData("http://api.custom-supplier.com/")][InlineData("https://user:password@api.custom-supplier.com/")]
    public void NonPublicOrUnsafeDestinationsAreRejected(string url)=>Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(Profile with{BaseUrl=url}));
    [Theory]
    [InlineData("127.0.0.1")][InlineData("10.1.2.3")][InlineData("172.16.2.1")][InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")][InlineData("100.64.0.1")][InlineData("::1")][InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")][InlineData("2001:db8::1")][InlineData("2002:7f00:1::")]
    public void PrivateAddressesAndMixedDnsAnswersAreRejected(string address)
    {
        var ip=IPAddress.Parse(address);Assert.False(PublicAiConnection.IsPublic(ip));
        Assert.Throws<HttpRequestException>(()=>PublicAiConnection.ValidateAddresses([IPAddress.Parse("8.8.8.8"),ip]));
    }
    [Theory][InlineData("8.8.8.8")][InlineData("1.1.1.1")][InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAllowed(string address)=>Assert.True(PublicAiConnection.IsPublic(IPAddress.Parse(address)));
    [Theory][InlineData("compatible")][InlineData("anthropic")][InlineData("gemini")]
    public async Task CustomSupplierUsesSelectedAdapterAndGuardedClient(string protocol)
    {
        var vault=new SecretVault(new MemoryConfigurationDatabase(),new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var factory=new CustomFactory(protocol);
        var profile=Profile with{Provider=protocol};
        Assert.Equal("{}",await new ConfiguredCompletionClient(profile,vault,factory,testKey:"SYNTHETIC-KEY").CompleteAsync("test",new JsonObject(),"test","test",default));
        Assert.Equal("AiCustomProviders",factory.ClientName);
    }
    private sealed class CustomFactory(string protocol):IHttpClientFactory
    {
        public string? ClientName;
        public HttpClient CreateClient(string name){ClientName=name;return new(new Handler(protocol));}
    }
    private sealed class Handler(string protocol):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("api.custom-supplier.com",request.RequestUri!.Host);
            Assert.EndsWith(protocol switch{"anthropic"=>"messages","gemini"=>":generateContent",_=>"chat/completions"},request.RequestUri.AbsolutePath);
            var content=protocol switch{
                "anthropic"=>"""{"stop_reason":"end_turn","content":[{"type":"text","text":"{}"}]}""",
                "gemini"=>"""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{}"}]}}]}""",
                _=>"""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}"""};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(content)});
        }
    }
}
