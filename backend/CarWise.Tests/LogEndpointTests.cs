using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Diagnostics;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace CarWise.Tests;

public sealed class LogEndpointTests
{
    [Fact] public async Task BothLogReadingAndExportEnforceTheRealAdministratorGateAndValidateFilters()
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
        var store=new ApplicationLogStore(Path.Combine(Path.GetTempPath(),"unused-log-endpoint-"+Guid.NewGuid().ToString("N")));
        store.Add(LogLevel.Warning,"CarWise.Test",17,"safe diagnostic");builder.Services.AddSingleton(store);
        using var auth=new HttpClient(new FakeHttp(_=>throw new InvalidOperationException("Authentication network calls are not needed in this gate test")));
        builder.Services.AddSingleton(new AccessControl(auth,false));await using var app=builder.Build();
        app.Use(async(context,next)=>{
            var role=context.Request.Headers["X-Test-Role"].ToString();
            if(role.Length>0)context.Items["caller"]=new Caller("test","E-001",role=="agent",role=="admin");
            try{await next(context);}catch(UnauthorizedAccessException){context.Response.StatusCode=403;}catch(ArgumentException){context.Response.StatusCode=400;}
        });
        var admin=app.MapGroup("/api/admin");admin.AddEndpointFilter(AdminEndpoints.AuthorizeAdministrator);admin.MapApplicationLogs();
        await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
        foreach(var role in new[]{"","employee","agent"})foreach(var path in new[]{"/api/admin/logs","/api/admin/logs/export"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,path);if(role.Length>0)request.Headers.Add("X-Test-Role",role);
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.DoesNotContain("safe diagnostic",await response.Content.ReadAsStringAsync());
        }
        client.DefaultRequestHeaders.Add("X-Test-Role","admin");
        using var allowed=await client.GetAsync("/api/admin/logs?level=Warning&limit=1");Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        var page=JsonNode.Parse(await allowed.Content.ReadAsStringAsync())!;Assert.Equal("safe diagnostic",page["items"]![0]!["message"]!.ToString());
        using var exported=await client.GetAsync("/api/admin/logs/export?q=diagnostic");Assert.Equal(HttpStatusCode.OK,exported.StatusCode);Assert.Equal("application/x-ndjson",exported.Content.Headers.ContentType!.MediaType);
        Assert.Contains("safe diagnostic",await exported.Content.ReadAsStringAsync());Assert.Contains("CarWise-logs-",exported.Content.Headers.ContentDisposition!.FileName);
        using var invalid=await client.GetAsync("/api/admin/logs?limit=501");Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        using var invalidLevel=await client.GetAsync("/api/admin/logs?level=made-up");Assert.Equal(HttpStatusCode.BadRequest,invalidLevel.StatusCode);
        await app.StopAsync();
    }
}
