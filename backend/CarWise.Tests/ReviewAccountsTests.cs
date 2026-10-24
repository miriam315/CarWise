using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CarWise.Tests;
public sealed class ReviewAccountsTests
{
    private const string Password="SYNTHETIC-REVIEW-PASSWORD";
    private static AuthAccount User(string email="test@example.invalid",string role="employee",string? employee="E-001",bool disabled=false)
        =>new(Guid.NewGuid().ToString(),email,role,employee,DateTimeOffset.UtcNow,disabled);
    private static ReviewAccounts Store(MemoryConfigurationDatabase db,IDataProtectionProvider? protection=null,bool allowed=true)
        =>new(db,protection??new EphemeralDataProtectionProvider(),new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"CarWise:ReviewAccountsEnabled",allowed.ToString()}}).Build());
    private static async Task<JsonNode> Public(ReviewAccounts store)=>JsonNode.Parse(JsonDefaults.Serialize(await store.PublicViewAsync(default)))!;
    private static async Task<JsonNode> Admin(ReviewAccounts store)=>JsonNode.Parse(JsonDefaults.Serialize(await store.AdminViewAsync(default)))!;
    private static AuthAccounts Accounts(HttpClient http,MemoryConfigurationDatabase db,ReviewAccounts store)
    {
        var data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
        var config=new ManagedConfiguration(data.Rules,data.Cars,data.OccupationDocuments,data.Employees,new(false,new(),new()));
        return new(http,db,new(new(new(0,DateTimeOffset.UtcNow,"test","initial",config,[]),data)),store);
    }
    [Fact] public async Task DiscoversAllEligibleAccountsWithoutInventingOrResettingPasswords()
    {
        var store=Store(new());var users=new[]{User(),User("admin@example.invalid","admin",null),User("agent@example.invalid","agent"),User("unassigned@example.invalid","unassigned"),User("banned@example.invalid",disabled:true),User("missing@example.invalid",employee:null)};
        await store.SyncAsync(users,default);
        var view=await Public(store);Assert.Equal(3,view["items"]!.AsArray().Count);
        Assert.All(view["items"]!.AsArray(),item=>Assert.Null(item!["password"]));
    }
    [Fact] public async Task CredentialsAreEncryptedDurableAndAbsentFromAdminResponses()
    {
        var db=new MemoryConfigurationDatabase();var protection=new EphemeralDataProtectionProvider();var store=Store(db,protection);var user=User();
        await store.SyncAsync([user],default);await store.CaptureAsync(user,Password,default);
        Assert.DoesNotContain(Password,db.Dump());Assert.DoesNotContain(Password,JsonDefaults.Serialize(await store.AdminViewAsync(default)));
        Assert.True((await Admin(store))["items"]![0]!["passwordSaved"]!.GetValue<bool>());
        Assert.Equal(Password,(await Public(Store(db,protection)))["items"]![0]!["password"]!.GetValue<string>());
        Assert.Null((await Public(Store(db)))["items"]![0]!["password"]); // Missing keyring fails closed.
    }
    [Fact] public async Task HiddenAccountStaysHiddenAfterSyncAndPasswordReset()
    {
        var store=Store(new());var user=User();await store.SyncAsync([user],default);
        await store.UpdateAsync(user.Id,new(false),default);await store.CaptureAsync(user,Password,default);await store.SyncAsync([user],default);
        Assert.Empty((await Public(store))["items"]!.AsArray());
        await store.UpdateAsync(user.Id,new(true),default);Assert.Single((await Public(store))["items"]!.AsArray());
    }
    [Fact] public async Task DeletedBannedOrReidentifiedAccountsCannotRetainOldCredentials()
    {
        var store=Store(new());var user=User();await store.SyncAsync([user],default);await store.CaptureAsync(user,Password,default);
        await store.SyncAsync([user with{Email="new@example.invalid"}],default);Assert.Null((await Public(store))["items"]![0]!["password"]);
        await store.SyncAsync([user with{Disabled=true}],default);Assert.Empty((await Public(store))["items"]!.AsArray());
        await store.SyncAsync([user],default);await store.CaptureAsync(user,Password,default);await store.SyncAsync([],default);
        Assert.Empty((await Public(store))["items"]!.AsArray());
    }
    [Fact] public async Task GlobalDisableHidesAllAccountsAndDoesNotCaptureRegularPasswords()
    {
        var db=new MemoryConfigurationDatabase();var store=Store(db);var user=User();await store.SyncAsync([user],default);await store.CaptureAsync(user,Password,default);
        await store.SetEnabledAsync(false,default);await store.CaptureAsync(user,"NEW-REGULAR-PASSWORD",default);
        Assert.Empty((await Public(store))["items"]!.AsArray());Assert.DoesNotContain("NEW-REGULAR-PASSWORD",db.Dump());
        await store.SetEnabledAsync(true,default);Assert.Null((await Public(store))["items"]![0]!["password"]);
        var denied=Store(db,allowed:false);Assert.Empty((await Public(denied))["items"]!.AsArray());
        await Assert.ThrowsAsync<ArgumentException>(()=>denied.SetEnabledAsync(true,default));
    }
    [Fact] public async Task PreviouslyPublishedCredentialsMigrateOnlyForExistingUsersAndOnlyOnce()
    {
        var store=Store(new());var user=User("5678@gmail.com");await store.SyncAsync([user],default);
        Assert.NotNull((await Public(store))["items"]![0]!["password"]);
        await store.CaptureAsync(user,Password,default);await store.SyncAsync([user],default);
        Assert.Equal(Password,(await Public(store))["items"]![0]!["password"]!.GetValue<string>());
        await store.SyncAsync([],default);Assert.Empty((await Public(store))["items"]!.AsArray());
    }
    [Fact] public async Task CreationAndResetPublishTheActualNewPasswordAndNeverReturnItFromAccountApi()
    {
        var db=new MemoryConfigurationDatabase();var store=Store(db);var user=User();var created=false;
        object Wire()=>new{id=user.Id,email=user.Email,app_metadata=new{role=user.Role,employee_id=user.EmployeeId}};
        using var http=new HttpClient(new FakeHttp(request=>{
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new{users=created?new[]{Wire()}:[]})};
            if(request.Method==HttpMethod.Post){created=true;return new(HttpStatusCode.OK){Content=JsonContent.Create(Wire())};}
            Assert.Equal(HttpMethod.Put,request.Method);return new(HttpStatusCode.OK);
        })){BaseAddress=new("https://accounts.invalid/")};
        var accounts=Accounts(http,db,store);
        var returned=await accounts.CreateAsync(new(user.Email,Password,"employee","E-001"),default);
        Assert.DoesNotContain(Password,JsonDefaults.Serialize(returned));Assert.Equal(Password,(await Public(store))["items"]![0]!["password"]!.GetValue<string>());
        await accounts.ResetAsync(user.Id,"NEW-SYNTHETIC-PASSWORD",default);
        Assert.Equal("NEW-SYNTHETIC-PASSWORD",(await Public(store))["items"]![0]!["password"]!.GetValue<string>());
        Assert.DoesNotContain("NEW-SYNTHETIC-PASSWORD",db.Dump());
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task ExistingPasswordIsVerifiedWithoutResettingIt(bool valid)
    {
        var db=new MemoryConfigurationDatabase();var store=Store(db);var user=User();var writes=0;
        using var http=new HttpClient(new FakeHttp(request=>{
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new{users=new[]{new{id=user.Id,email=user.Email,app_metadata=new{role=user.Role,employee_id=user.EmployeeId}}}})};
            Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("/auth/v1/token",request.RequestUri!.AbsolutePath);writes++;
            return new(valid?HttpStatusCode.OK:HttpStatusCode.BadRequest){Content=JsonContent.Create(new{user=new{id=user.Id}})};
        })){BaseAddress=new("https://accounts.invalid/")};
        var accounts=Accounts(http,db,store);
        if(valid){await accounts.VerifyReviewPasswordAsync(user.Id,Password,default);await store.UpdateAsync(user.Id,new(true,Password),default);Assert.Equal(Password,(await Public(store))["items"]![0]!["password"]!.GetValue<string>());}
        else{await Assert.ThrowsAsync<ArgumentException>(()=>accounts.VerifyReviewPasswordAsync(user.Id,Password,default));Assert.Null((await Public(store))["items"]![0]!["password"]);}
        Assert.Equal(1,writes);
    }
    [Fact] public async Task RepeatedDiscoveryDoesNotCreateNewPolicyVersionsOrDirectoryWrites()
    {
        var db=new MemoryConfigurationDatabase();var store=Store(db);var user=User();await store.SyncAsync([user],default);var before=db.Dump();
        await store.SyncAsync([user],default);Assert.Equal(before,db.Dump());
        Assert.DoesNotContain("configuration-history",before);
    }
    [Fact] public async Task DiscoveryReadsEveryProviderPageAndExcludesBlockedUsers()
    {
        var db=new MemoryConfigurationDatabase();var store=Store(db);var calls=0;
        var users=Enumerable.Range(0,101).Select(i=>new{id=Guid.NewGuid().ToString(),email=$"page-{i}@example.invalid",
            app_metadata=new{role="employee",employee_id="E-001"},banned_until=i==100?DateTimeOffset.UtcNow.AddDays(1).ToString("O"):null}).ToArray();
        using var http=new HttpClient(new FakeHttp(request=>{
            calls++;var page=request.RequestUri!.Query.Contains("page=1&")?users.Take(100):users.Skip(100);
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{users=page})};
        })){BaseAddress=new("https://accounts.invalid/")};
        var result=await Accounts(http,db,store).ListAsync(default);
        Assert.Equal(101,result.Count);Assert.Equal(2,calls);
        Assert.Equal(100,(await Public(store))["items"]!.AsArray().Count);
    }
}
