using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Xunit;

namespace CarWise.Tests;

public sealed class AccountLifecycleTests
{
    private const string Password = "SYNTHETIC-PASSWORD-ONLY";
    private static AuthAccounts Accounts(HttpClient http,string? assignedRole=null,string? assignedEmployee=null)
    {
        var data = ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory, "data"));
        var configuration = new ManagedConfiguration(data.Rules, data.Cars, data.OccupationDocuments, data.Employees, new(false, new(), new()));
        if(assignedRole is not null&&assignedEmployee is not null)configuration=configuration with{EmployeeAccountRoles=new(){{assignedEmployee,assignedRole}}};
        var state = new ConfigurationState(new(new(0, DateTimeOffset.UtcNow, "test", "initial", configuration, []), data));
        return new(http, new MemoryConfigurationDatabase(), state);
    }

    [Theory]
    [InlineData("employee", null)]
    [InlineData("agent", null)]
    [InlineData("employee", "UNSAVED-PROFILE")]
    [InlineData("admin", "UNSAVED-PROFILE")]
    [InlineData("superadmin", "E-001")]
    public async Task InvalidProfileOrRoleNeverCreatesAnAuthenticationUser(string role, string? employeeId)
    {
        var calls = 0;
        using var http = new HttpClient(new FakeHttp(_ => { calls++; return new(HttpStatusCode.OK); })) { BaseAddress = new("https://accounts.invalid/") };
        await Assert.ThrowsAsync<ArgumentException>(() => Accounts(http).CreateAsync(new("worker@example.test", Password, role, employeeId), default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("employee", "E-001")]
    [InlineData("agent", "E-002")]
    [InlineData("admin", null)]
    public async Task NewAccountKeepsItsExplicitProfileAndRoleWithoutReturningPassword(string role, string? employeeId)
    {
        var posts = 0;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { users = Array.Empty<object>() }) };
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Assert.Equal(Password, body["password"]!.GetValue<string>());
            Assert.Equal(role, body["app_metadata"]!["role"]!.GetValue<string>());
            Assert.Equal(employeeId, body["app_metadata"]!["employee_id"]?.GetValue<string>());
            posts++;
            body["id"] = Guid.NewGuid().ToString();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        })) { BaseAddress = new("https://accounts.invalid/") };
        var account = await Accounts(http,role,employeeId).CreateAsync(new("worker@example.test", Password, role, employeeId), default);
        Assert.Equal(role, account.Role);
        Assert.Equal(employeeId, account.EmployeeId);
        Assert.DoesNotContain(Password, JsonDefaults.Serialize(account));
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("WORKER@example.test", "E-002", "employee")]
    [InlineData("other@example.test", "E-001", "employee")]
    [InlineData("other@example.test", "E-001", "agent")]
    public async Task DuplicateEmailOrEmployeeMappingCannotCreateAnotherAccount(string email, string employeeId, string role)
    {
        var posts = 0;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            if (request.Method != HttpMethod.Get) posts++;
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"users":[{"id":"existing","email":"worker@example.test","app_metadata":{"role":"employee","employee_id":"E-001"}}]}""") };
        })) { BaseAddress = new("https://accounts.invalid/") };
        await Assert.ThrowsAsync<ArgumentException>(() => Accounts(http,role,employeeId).CreateAsync(new(email, Password, role, employeeId), default));
        Assert.Equal(0, posts);
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task PasswordResetEnforcesBoundsBeforeContactingProvider(int length, bool valid)
    {
        var calls = 0;
        var id = Guid.NewGuid().ToString();
        using var http = new HttpClient(new FakeHttp(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith("/auth/v1/admin/users/" + id, request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            Assert.Single(body);
            Assert.Equal(new string('x', length), body["password"]!.GetValue<string>());
            return new(HttpStatusCode.OK);
        })) { BaseAddress = new("https://accounts.invalid/") };
        var accounts = Accounts(http);
        if (valid) await accounts.ResetAsync(id, new string('x', length), default);
        else await Assert.ThrowsAsync<ArgumentException>(() => accounts.ResetAsync(id, new string('x', length), default));
        Assert.Equal(valid ? 1 : 0, calls);
    }

    [Fact]
    public async Task ProviderFailureDoesNotExposePasswordOrRawResponse()
    {
        using var http = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("INTERNAL-DETAILS " + Password) })) { BaseAddress = new("https://accounts.invalid/") };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Accounts(http).ResetAsync(Guid.NewGuid().ToString(), Password, default));
        Assert.DoesNotContain(Password, error.Message);
        Assert.DoesNotContain("INTERNAL-DETAILS", error.Message);
    }
}
