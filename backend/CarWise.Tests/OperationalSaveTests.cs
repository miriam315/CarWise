using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;
public sealed class OperationalSaveTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static (ConfigurationService Service,MemoryConfigurationDatabase Db,SecretVault Vault,ConfigurationSnapshot Initial) Setup()
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars.ToList(),Source.OccupationDocuments,Source.Employees.ToList(),new(false,new(),new()));
        var initial=new ConfigurationSnapshot(new(0,DateTimeOffset.UtcNow,"system","initial",config,[]),Source);
        return(new(db,new(initial),new(Source),vault),db,vault,initial);
    }
    [Fact] public async Task EmployeesAndMediaSaveWithoutPolicyVersionOrReasonButKeepAuditAndConcurrencyProtection()
    {
        var (service,db,vault,initial)=Setup();var config=initial.Document.Configuration;
        var employee=config.Employees[0] with{Id="E-OPERATIONS-TEST",Name="Synthetic added employee"};
        config=config with{Employees=[..config.Employees,employee],Cars=config.Cars.Select((c,i)=>i==0?c with{Photos=[new(new string('a',64),"exterior","Synthetic credit")]}:c).ToList()};
        var saved=await service.SaveAsync(new(0,config,""),"operator",default);
        Assert.Equal(0,saved.PolicyVersion);Assert.Equal(1,saved.Revision);Assert.Empty(saved.History);
        var audit=Assert.Single(saved.Operations);Assert.Equal("operator",audit.Actor);Assert.Contains(employee.Id,audit.EmployeeIds);Assert.Contains(config.Cars[0].Id,audit.CarIds);
        Assert.DoesNotContain(employee.Name,CarWise.Api.Domain.JsonDefaults.Serialize(audit));
        var second=new ConfigurationService(db,new(initial),new(Source),vault);await second.RefreshAsync(default);
        Assert.Contains(second.State.Current.Data.Employees,e=>e.Id==employee.Id);Assert.Single(second.State.Current.Data.Cars[0].Photos);
        await Assert.ThrowsAsync<ConfigurationConflictException>(()=>second.SaveAsync(new(0,initial.Document.Configuration,"stale operational save"),"other",default));
    }
    [Fact] public async Task PolicyRestorePreservesLatestEmployeeRecordsAndCarMedia()
    {
        var (service,_,_,initial)=Setup();var config=initial.Document.Configuration;
        var added=config.Employees[0] with{Id="E-OPERATIONS-TEST",Name="Synthetic latest name"};
        config=config with{Employees=[..config.Employees,added],Cars=config.Cars.Select((c,i)=>i==0?c with{Photos=[new(new string('a',64),"exterior","Synthetic credit")]}:c).ToList()};
        var operational=await service.SaveAsync(new(0,config,""),"operator",default);
        var policy=JsonDefaults.Read<ManagedConfiguration>(JsonDefaults.Serialize(operational.Configuration));policy.Rules["electric"]!["netBonus"]=500;
        var changed=await service.SaveAsync(new(operational.Revision,policy,"electric policy"),"policy-admin",default);
        Assert.Equal(1,changed.PolicyVersion);Assert.Single(changed.History);Assert.Empty(changed.History[0].Configuration.Employees);Assert.Empty(changed.History[0].Configuration.Cars[0].Photos);
        var updated=changed.Configuration with{Employees=changed.Configuration.Employees.Select(e=>e.Id==added.Id?e with{Name="Updated operational name"}:e).ToList()};
        var latest=await service.SaveAsync(new(changed.Revision,updated,""),"operator",default);
        Assert.Equal(1,latest.PolicyVersion);Assert.Single(latest.History);Assert.Equal(changed.PolicySavedAt,latest.PolicySavedAt);
        var restored=await service.RestoreAsync(new(latest.Revision,0,"restore policy only"),"policy-admin",default);
        Assert.Equal(2,restored.PolicyVersion);Assert.Equal(250,restored.Configuration.Rules["electric"]!["netBonus"]!.GetValue<int>());
        Assert.Contains(restored.Configuration.Employees,e=>e.Id==added.Id&&e.Name=="Updated operational name");Assert.Single(restored.Configuration.Cars[0].Photos);
    }
    [Fact] public async Task SavingWithoutAnyChangesCreatesNeitherVersionNorOperationalEvent()
    {
        var (service,db,_,initial)=Setup();var saved=await service.SaveAsync(new(0,initial.Document.Configuration,""),"operator",default);
        Assert.Equal(0,saved.PolicyVersion);Assert.Equal(0,saved.Revision);Assert.Empty(saved.Operations);Assert.Empty(saved.History);
        Assert.Empty(await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","admin-active"}}));
    }
}
