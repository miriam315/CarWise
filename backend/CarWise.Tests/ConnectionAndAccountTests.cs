using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
namespace CarWise.Tests;
public sealed class ConnectionAndAccountTests
{
    private const string Key="sb_secret_TEST_ONLY_NOT_REAL_SERVER_KEY";
    [Fact] public async Task StoredConnectionIsEncryptedAndSurvivesANewProviderAndProcessLifetime()
    {
        var directory=Path.Combine(Path.GetTempPath(),"carwise-connection-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var keys=new DirectoryInfo(Path.Combine(directory,"keys"));keys.Create();var file=Path.Combine(directory,"settings","connection.json");
            var provider=DataProtectionProvider.Create(keys,c=>c.SetApplicationName("test"));
            var store=new ServerConnectionStore(file,provider);
            await store.SaveAsync(new("https://testproject123.supabase.co",Key),0,"admin",default);
            Assert.DoesNotContain(Key,await File.ReadAllTextAsync(file));
            var second=new ServerConnectionStore(file,DataProtectionProvider.Create(keys,c=>c.SetApplicationName("test")));
            var saved=await second.LoadAsync(default);Assert.Equal(Key,saved.Connection!.ServiceKey);Assert.Equal(1,saved.Metadata!.Revision);
            await Assert.ThrowsAsync<ConfigurationConflictException>(()=>second.SaveAsync(new("https://otherproject123.supabase.co",Key),0,"other",default));
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact] public async Task ExistingServerKeyCanBePersistedButNeverForwardedToADifferentProject()
    {
        var directory=Path.Combine(Path.GetTempPath(),"carwise-connection-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var requests=0;var factory=new ProviderTestFactory(req=>{
                requests++;Assert.Equal(Key,Assert.Single(req.Headers.GetValues("apikey")));Assert.Null(req.Headers.Authorization);
                return new(HttpStatusCode.OK){Content=new StringContent("""[{"id":"active"}]""")};
            });
            var store=new ServerConnectionStore(Path.Combine(directory,"connection.json"),new EphemeralDataProtectionProvider());
            var manager=new ServerConnectionManager(store,new("https://testproject123.supabase.co",Key),true,()=>factory.CreateClient("test"));
            await manager.SaveAsync(new("https://testproject123.supabase.co",null,0),"admin",default);Assert.Equal(3,requests);
            var view=JsonDefaults.Serialize(await manager.ViewAsync(default));Assert.DoesNotContain(Key,view);Assert.Contains("\"persisted\":true",view);
            await Assert.ThrowsAsync<ArgumentException>(()=>manager.SaveAsync(new("https://anotherproject123.supabase.co",null,1),"admin",default));Assert.Equal(3,requests);
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact] public async Task FailedConnectionTestPreservesThePreviousSavedConnection()
    {
        var directory=Path.Combine(Path.GetTempPath(),"carwise-connection-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var store=new ServerConnectionStore(Path.Combine(directory,"connection.json"),new EphemeralDataProtectionProvider());await store.SaveAsync(new("https://testproject123.supabase.co",Key),0,"admin",default);
            var factory=new ProviderTestFactory(_=>new(HttpStatusCode.Unauthorized){Content=new StringContent("SENSITIVE ERROR DO NOT RETURN")});
            var manager=new ServerConnectionManager(store,null,true,()=>factory.CreateClient("test"));
            var error=await Assert.ThrowsAsync<ArgumentException>(()=>manager.SaveAsync(new("https://anotherproject123.supabase.co",Key,1),"admin",default));
            Assert.DoesNotContain("SENSITIVE",error.Message);Assert.Equal("https://testproject123.supabase.co",(await store.LoadAsync(default)).Connection!.Url);
        }
        finally{Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData("http://testproject123.supabase.co")]
    [InlineData("https://evil.example")]
    [InlineData("https://testproject123.supabase.co/path")]
    [InlineData("https://user:pass@testproject123.supabase.co")]
    [InlineData("https://testproject123.supabase.co?key=secret")]
    public void ConnectionTargetsAreRestricted(string url)=>Assert.Throws<ArgumentException>(()=>ServerConnectionManager.ValidateUrl(url,false));
    [Fact] public void LocalBootstrapRejectsRemoteClientsAndDnsRebindingHosts()
    {
        var ctx=new DefaultHttpContext();ctx.Connection.RemoteIpAddress=IPAddress.Loopback;ctx.Request.Host=new("evil.example");Assert.False(LocalSetupAccess.Allowed(ctx));
        ctx.Request.Host=new("localhost",8000);Assert.True(LocalSetupAccess.Allowed(ctx));ctx.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");Assert.False(LocalSetupAccess.Allowed(ctx));
    }
    [Fact] public async Task AdminCanLoginWithoutAnEmployeeAndEmployeeCannotReadOtherProfiles()
    {
        var factory=new ProviderTestFactory(_=>new(HttpStatusCode.OK){Content=new StringContent("""{"id":"admin-user","app_metadata":{"role":"admin"}}""")});
        using var client=factory.CreateClient("test");client.BaseAddress=new("https://testproject123.supabase.co/");
        var access=new AccessControl(client,false);var ctx=new DefaultHttpContext();ctx.Request.Headers.Authorization="Bearer test-user-token";
        var caller=await access.AuthenticateAsync(ctx,default);Assert.True(caller!.IsAdmin);ctx.Items["caller"]=caller;access.Admin(ctx);access.Employee(ctx,"E-006");
        ctx.Items["caller"]=new Caller("employee","E-001",false);access.Employee(ctx,"E-001");Assert.Throws<UnauthorizedAccessException>(()=>access.Employee(ctx,"E-002"));Assert.Throws<UnauthorizedAccessException>(()=>access.Admin(ctx));
    }
    [Fact] public async Task FirstAdministratorBootstrapCannotBeRepeated()
    {
        var data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));var config=new ManagedConfiguration(data.Rules,data.Cars,data.OccupationDocuments,data.Employees,new(false,new(),new()));
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"system","initial",config,[]),data));var users=new JsonArray();
        var factory=new ProviderTestFactory(req=>{
            if(req.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new JsonObject{["users"]=users.DeepClone()})};
            var body=JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;body.AsObject().Remove("password");body["id"]=Guid.NewGuid().ToString();users.Add(body.DeepClone());
            return new(HttpStatusCode.OK){Content=JsonContent.Create(body)};
        });
        using var http=factory.CreateClient("test");http.BaseAddress=new("https://testproject123.supabase.co/");
        var db=new MemoryConfigurationDatabase();var accounts=new AuthAccounts(http,db,state);
        Assert.True(await accounts.NeedsAdminAsync(default));var created=await accounts.BootstrapAsync(new("admin@example.test","TEST-PASSWORD-1234","admin",null),default);
        Assert.Equal("admin",created.Role);Assert.False(await accounts.NeedsAdminAsync(default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>accounts.BootstrapAsync(new("other@example.test","TEST-PASSWORD-1234","admin",null),default));
        Assert.DoesNotContain("TEST-PASSWORD",db.Dump());
    }
    private sealed class DifferentOccupation(string occupation):IDataExtractionService
    {
        public string Mode=>"test";
        public Task<ExtractedData> ExtractAsync(string text,Employee known,IReadOnlyList<Message> history,CancellationToken ct)=>Task.FromResult(new ExtractedData{Occupation=occupation});
    }
    [Fact] public async Task EmployeeCannotChangeCorporateOccupationThroughAnAiMessage()
    {
        var data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));var employee=data.Employees[0];
        var other=data.OccupationDocuments.First(o=>o["occupation"].GetString()!=employee.Occupation)["occupation"].GetString()!;
        var advisor=new AdvisorService(data,new(data),new(new MemoryConfigurationDatabase()),new DifferentOccupation(other),new TemplateNarrationService());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>advisor.ChatAsync(new(employee,"change occupation",null,null),default,false));
    }

    private sealed class ProductionHost:IHostEnvironment
    {
        public string EnvironmentName {get;set;}="Production";
        public string ApplicationName {get;set;}="test";
        public string ContentRootPath {get;set;}="";
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    }
    [Fact] public void ProductionBootstrapIsClosedUnlessExplicitlyEnabled()
    {
        var previous=Environment.GetEnvironmentVariable("CARWISE_ALLOW_LOCAL_SETUP");
        using var provider=new ServiceCollection().AddSingleton<IHostEnvironment>(new ProductionHost()).BuildServiceProvider();
        var context=new DefaultHttpContext{RequestServices=provider};context.Request.Host=new("localhost",8000);context.Connection.RemoteIpAddress=IPAddress.Loopback;
        try
        {
            Environment.SetEnvironmentVariable("CARWISE_ALLOW_LOCAL_SETUP",null);Assert.False(LocalSetupAccess.Allowed(context));
            Environment.SetEnvironmentVariable("CARWISE_ALLOW_LOCAL_SETUP","true");Assert.True(LocalSetupAccess.Allowed(context));
        }
        finally{Environment.SetEnvironmentVariable("CARWISE_ALLOW_LOCAL_SETUP",previous);}
    }
    [Fact] public async Task LostEncryptionKeysPermitExplicitReplacementWithoutRevealingTheOldSecret()
    {
        var directory=Path.Combine(Path.GetTempPath(),"carwise-key-recovery-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var file=Path.Combine(directory,"connection.json");var original=new ServerConnectionStore(file,new EphemeralDataProtectionProvider());
            await original.SaveAsync(new("https://testproject123.supabase.co",Key),0,"admin",default);
            var recovery=new ServerConnectionStore(file,new EphemeralDataProtectionProvider());var unreadable=await recovery.LoadAsync(default);
            Assert.Null(unreadable.Connection);Assert.Equal(1,unreadable.Metadata!.Revision);
            var factory=new ProviderTestFactory(_=>new(HttpStatusCode.OK){Content=new StringContent("""[{"id":"active"}]""")});
            var manager=new ServerConnectionManager(recovery,null,true,()=>factory.CreateClient("test"));
            Assert.DoesNotContain(Key,JsonDefaults.Serialize(await manager.ViewAsync(default)));
            await manager.SaveAsync(new("https://testproject123.supabase.co",Key,1),"recovery-admin",default);
            Assert.Equal(Key,(await recovery.LoadAsync(default)).Connection!.ServiceKey);Assert.Equal(2,(await recovery.LoadAsync(default)).Metadata!.Revision);
        }
        finally{Directory.Delete(directory,true);}
    }

}
