using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class PayslipAvailabilityTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    [Fact] public async Task DocumentServiceWorksWithChatAiOffAndUsesOnlyItsOwnKey()
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var missing=new AiProfile{KeyReference="environment:CARWISE_AI_NOT_CONFIGURED"};var document=missing with{SupportsDocuments=true};
        var ai=new AiConfiguration(false,missing,missing){Models=[new("text","text",missing),new("pdf","pdf",document)],ServiceModels=new(){{"extraction","text"},{"narration","text"},{"payslip","pdf"}},PayslipEnabled=true};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));
        var service=new ConfigurationService(db,state,new(Source),vault);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveAsync(new(0,config,"enable PDFs without a key"),"admin",default));
        await service.SaveAsync(new(0,config,"enable only the document service",new(){{"pdf","TEST-ONLY-DOCUMENT-KEY"}}),"admin",default);
        var active=state.Current.Document.Configuration.Ai;Assert.False(active.Enabled);Assert.True(active.PayslipEnabled);
        var status=await PayslipAvailability.ReadAsync(active,vault,default);Assert.True(status.Enabled);Assert.Equal("ready",status.Code);
        var view=JsonDefaults.Serialize(await service.ViewAsync(default));Assert.Contains("ready",view);Assert.DoesNotContain("TEST-ONLY-DOCUMENT-KEY",view);
        var disabled=await PayslipAvailability.ReadAsync(active with{PayslipEnabled=false},vault,default);Assert.False(disabled.Enabled);Assert.Equal("disabled",disabled.Code);
    }
    [Fact] public async Task MissingDisabledWrongHostKeysAndUnsupportedModelsHaveExplicitReasons()
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var profile=new AiProfile{SupportsDocuments=true,KeyReference="environment:CARWISE_AI_NOT_CONFIGURED"};
        var ai=new AiConfiguration(false,profile,profile){Models=[new("pdf","pdf",profile)],ServiceModels=new(){{"extraction","pdf"},{"narration","pdf"},{"payslip","pdf"}},PayslipEnabled=true};
        Assert.Equal("missing_key",(await PayslipAvailability.ReadAsync(ai,vault,default)).Code);
        var wrong=await vault.StoreAsync("TEST-ONLY-WRONG-HOST","api.anthropic.com",default);
        Assert.Equal("missing_key",(await PayslipAvailability.ReadAsync(ai.WithProfile("pdf",profile with{KeyReference=wrong}),vault,default)).Code);
        var valid=await vault.StoreAsync("TEST-ONLY-BACKUP","api.openai.com",default);
        profile=profile with{BackupKeys=[new("aabbccddeeff0011","backup",valid)]};
        Assert.True((await PayslipAvailability.ReadAsync(ai.WithProfile("pdf",profile),vault,default)).Enabled);
        Assert.Equal("missing_key",(await PayslipAvailability.ReadAsync(ai.WithProfile("pdf",profile with{BackupKeys=[]}),vault,default)).Code);
        Assert.Equal("documents_unsupported",(await PayslipAvailability.ReadAsync(ai.WithProfile("pdf",profile with{SupportsDocuments=false}),vault,default)).Code);
    }
}
