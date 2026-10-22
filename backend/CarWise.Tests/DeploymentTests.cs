using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;
namespace CarWise.Tests;

public sealed class DeploymentTests
{
    [Theory][InlineData("GET")][InlineData("HEAD")]
    public void InternalHealthProbeCanUseHttp(string method)
    {
        var context=new DefaultHttpContext();context.Request.Method=method;context.Request.Path="/api/health";
        RequestProtection.Validate(context,false);
    }
    [Theory][InlineData("GET","/api/runtime")][InlineData("POST","/api/auth/login")]
    [InlineData("POST","/api/health")][InlineData("GET","/api/health/extra")]
    public void OtherProductionRequestsStillRequireHttps(string method,string path)
    {
        var context=new DefaultHttpContext();context.Request.Method=method;context.Request.Path=path;
        Assert.Throws<ArgumentException>(()=>RequestProtection.Validate(context,false));
        context.Request.Scheme="https";RequestProtection.Validate(context,false);
    }
}
