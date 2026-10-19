using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Infrastructure;
using CarWise.Api.Administration;
using Microsoft.AspNetCore.Http;
using Xunit;
namespace CarWise.Tests;

public sealed class IdentityAndLimitsTests
{
    [Fact] public async Task AdministratorIdentityDoesNotAcquireTheFirstEmployee()
    {
        using var client=new HttpClient(new FakeHttp(_=>new(HttpStatusCode.OK){Content=new StringContent("""{"id":"admin-user","email":"manager@example.test","app_metadata":{"role":"admin"},"user_metadata":{"full_name":"Manager","employee_id":"E-001","role":"employee"}}""")})){BaseAddress=new("https://example.invalid/")};
        var context=new DefaultHttpContext();context.Request.Headers.Authorization="Bearer test-session";
        var caller=await new AccessControl(client,false).AuthenticateAsync(context,default);
        Assert.True(caller!.IsAdmin);Assert.Equal("",caller.EmployeeId);Assert.Equal("manager@example.test",caller.Email);Assert.Equal("Manager",caller.DisplayName);
    }
    [Theory][InlineData(127,35,"128")][InlineData(8193,35,"8,192")][InlineData(1200,4,"5")][InlineData(1200,91,"90")]
    public void InvalidLimitsIdentifyTheFieldAndAllowedRange(int tokens,int timeout,string expected)
    {
        var error=Assert.Throws<ArgumentException>(()=>ConfigurationValidator.ValidateAi(new(){MaxOutputTokens=tokens,TimeoutSeconds=timeout}));Assert.Contains(expected,error.Message);
    }
}
