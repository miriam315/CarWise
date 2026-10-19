using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;

public sealed class FakeHttp(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request));
}
public sealed class AiTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static HttpResponseMessage Completion(object content)=>new(HttpStatusCode.OK){Content=new StringContent(
        JsonDefaults.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=JsonDefaults.Serialize(content)}}}}),Encoding.UTF8,"application/json")};
    [Fact] public async Task ExtractionUsesTypedProviderAndCannotInventAValue()
    {
        using var http=new HttpClient(new FakeHttp(_=>Completion(new ExtractedData{Salary=99999m}))){BaseAddress=new("https://example.invalid/v1/")};
        var service=new AiExtractionService(new(http,"test-model"));
        await Assert.ThrowsAsync<AiUnavailableException>(()=>service.ExtractAsync("שלום",Data.Employees[0],[],default));
    }
    [Fact] public async Task ExplicitFieldsAreExtractedAsDecimals()
    {
        using var http=new HttpClient(new FakeHttp(_=>Completion(new ExtractedData{Salary=19500m,Pension=6.5m}))){BaseAddress=new("https://example.invalid/v1/")};
        var result=await new AiExtractionService(new(http,"test-model")).ExtractAsync("שכר 19500 פנסיה 6.5",Data.Employees[0],[],default);
        Assert.Equal(19500m,result.Salary);Assert.Equal(6.5m,result.Pension);
    }
    [Fact] public async Task LocalModeDoesNotInferUnstatedFields()
    {
        var service=new LocalExtractionService();var e=Data.Employees[0];
        var result=await service.ExtractAsync("שכר בסיס 18,500 עמלות 1000 פנסיה 6 קרן השתלמות 2.5 נקודות זיכוי 2.75 ויש לי 4 ילדים",e,[],default);
        Assert.Equal(18500m,result.Salary);Assert.Equal(4m,result.Children);Assert.Null(result.Bonus);
        Assert.False((await service.ExtractAsync("יש לי 5",e,[],default)).HasUpdates);
    }
    [Theory]
    [InlineData("נטו {NET}, הפרש {DELTA}, שווי {VALUE}, השתתפות {PARTICIPATION}",true)]
    [InlineData("נטו 999999",false)]
    [InlineData("{NET} {NET} {DELTA} {VALUE} {PARTICIPATION}",false)]
    [InlineData("{NET} {DELTA} {VALUE} {PARTICIPATION} {FAKE}",false)]
    public void NumericalNarrationIsRejected(string text,bool valid)=>Assert.Equal(valid,AiNarrationService.ValidTemplate(text));
    [Fact] public async Task ModelCannotOverrideNetNumbers()
    {
        using var http=new HttpClient(new FakeHttp(_=>Completion(new{text="תקבלי 999999 ₪"}))){BaseAddress=new("https://example.invalid/v1/")};
        var service=new AiNarrationService(new(http,"test"),NullLogger<AiNarrationService>.Instance);
        var alternative=new PayrollEngine(Data).Simulate(Data.Employees[0]).Results.Single(r=>r.Id=="CAR-10");
        Assert.Equal(FinancialText.Template(alternative),await service.NarrateAsync(alternative,default));
    }
    [Fact] public async Task ProviderFailureProducesExplicitUnavailableResult()
    {
        using var http=new HttpClient(new FakeHttp(_=>new(HttpStatusCode.Unauthorized))){BaseAddress=new("https://example.invalid/v1/")};
        await Assert.ThrowsAsync<AiUnavailableException>(()=>new AiExtractionService(new(http,"test")).ExtractAsync("שלום",Data.Employees[0],[],default));
    }
    [Fact] public async Task AuthDoesNotTrustUserEditableRole()
    {
        using var http=new HttpClient(new FakeHttp(_=>new(HttpStatusCode.OK){Content=new StringContent("""{"id":"user","app_metadata":{"employee_id":"E-001","role":"employee"},"user_metadata":{"role":"agent"}}""")})){BaseAddress=new("https://example.invalid/")};
        var access=new AccessControl(http,false);var context=new DefaultHttpContext();context.Request.Headers.Authorization="Bearer user-token";
        var caller=await access.AuthenticateAsync(context,default);context.Items["caller"]=caller;
        Assert.False(caller!.IsAgent);Assert.Throws<UnauthorizedAccessException>(()=>access.Agent(context));
        Assert.Throws<UnauthorizedAccessException>(()=>access.Employee(context,"E-002"));
        access.Employee(context,"E-001");
    }
}
