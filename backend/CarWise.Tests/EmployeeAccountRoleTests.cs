using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Xunit;
namespace CarWise.Tests;
public sealed class EmployeeAccountRoleTests
{
    [Theory][InlineData("employee")][InlineData("agent")][InlineData("admin")]
    public async Task AccountCreationUsesOnlyTheRoleAssignedByAnAdministrator(string role)
    {
        var source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));var employee=source.Employees[0];
        var config=new ManagedConfiguration(source.Rules,source.Cars,source.OccupationDocuments,source.Employees,new(false,new(),new())){EmployeeAccountRoles=new(){{employee.Id,role}}};
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),source));
        JsonNode? written=null;
        var factory=new ProviderTestFactory(request=>{
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new{users=Array.Empty<object>()})};
            written=JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;written["id"]="synthetic-account";
            return new(HttpStatusCode.OK){Content=JsonContent.Create(written)};
        });
        using var http=factory.CreateClient("test");http.BaseAddress=new("https://project.supabase.co/");
        var accounts=new AuthAccounts(http,new MemoryConfigurationDatabase(),state);
        foreach(var other in new[]{"employee","agent","admin"}.Where(r=>r!=role))
            Assert.Throws<ArgumentException>(()=>accounts.Validate(new("synthetic@example.test","SYNTHETIC-PASSWORD-123",other,employee.Id)));
        Assert.Null(written);
        var created=await accounts.CreateAsync(new("synthetic@example.test","SYNTHETIC-PASSWORD-123",role,employee.Id),default);
        Assert.Equal(role,created.Role);Assert.Equal(role,written!["app_metadata"]!["role"]!.GetValue<string>());
        Assert.Equal(employee.Id,created.EmployeeId);
    }
    [Fact] public void LegacyEmployeeDefaultsToEmployeeWithoutInferringRoleFromOccupation()
    {
        var source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
        var config=new ManagedConfiguration(source.Rules,source.Cars,source.OccupationDocuments,source.Employees,new(false,new(),new()));
        using var http=new HttpClient();var accounts=new AuthAccounts(http,new MemoryConfigurationDatabase(),new(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),source)));
        var employee=source.Employees[0];
        accounts.Validate(new("synthetic@example.test","SYNTHETIC-PASSWORD-123","employee",employee.Id));
        Assert.Throws<ArgumentException>(()=>accounts.Validate(new("synthetic@example.test","SYNTHETIC-PASSWORD-123","agent",employee.Id)));
        accounts.Validate(new("admin@example.test","SYNTHETIC-PASSWORD-123","admin",null));
    }
}
