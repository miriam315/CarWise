using CarWise.Api.AI;
using Xunit;
namespace CarWise.Tests;
public sealed class PayslipFailureTests
{
    [Theory][InlineData(401,"invalid_api_key")][InlineData(429,"insufficient_quota")][InlineData(404,"model_not_found")][InlineData(400,"unsupported_parameter")]
    public void ProviderFailuresKeepSafeActionableDetails(int status,string code)
    {
        var result=PayslipFailure.From(new AiProviderException(status,code){Parameter="model"});
        Assert.Equal(status,result.ProviderStatus);Assert.Equal(code,result.Code);Assert.Equal("model",result.Parameter);Assert.NotEmpty(result.Message);
    }
    [Fact] public void TransportDetailsAreNotReturnedAndTimeoutIsSpecificToExtraction()
    {
        var transport=PayslipFailure.From(new HttpRequestException("SECRET TOKEN AND INTERNAL HOST"));Assert.DoesNotContain("SECRET",transport.Message);Assert.Equal("provider_connection",transport.Code);
        var timeout=PayslipFailure.From(new TaskCanceledException());Assert.Equal("timeout",timeout.Code);Assert.Contains("חילוץ",timeout.Message);Assert.DoesNotContain("קטלוג",timeout.Message);
    }
}
