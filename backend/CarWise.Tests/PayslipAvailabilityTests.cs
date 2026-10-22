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
    [Fact] public async Task DocumentExtractionNeedsDocumentModelAndKeyButNotChatAiOrDocker()
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var missing=new AiProfile{KeyReference="environment:CARWISE_AI_NOT_CONFIGURED"};
        var ai=new AiConfiguration(false,missing,missing){PayslipEnabled=true};
        Assert.Equal("document_model_required",(await PayslipAvailability.ReadAsync(ai,vault,default)).Code);
        ai=ai with{Extraction=missing with{SupportsDocuments=true}};
        Assert.Equal("key_required",(await PayslipAvailability.ReadAsync(ai,vault,default)).Code);
        ai=ai with{Extraction=ai.Extraction with{KeyReference=await vault.StoreAsync("TEST-DOCUMENT-KEY","api.openai.com",default)}};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));
        var service=new ConfigurationService(db,state,new(Source),vault);
        await service.SaveAsync(new(0,config,"document AI without chat AI or local OCR"),"admin",default);
        Assert.False(state.Current.Document.Configuration.Ai.Enabled);
        Assert.True((await PayslipAvailability.ReadAsync(ai,vault,default)).Enabled);
        Assert.Equal("disabled",(await PayslipAvailability.ReadAsync(ai with{PayslipEnabled=false},vault,default)).Code);
        Assert.DoesNotContain("TEST-DOCUMENT-KEY",JsonDefaults.Serialize(await service.ViewAsync(default)));
    }
    [Fact] public async Task LegacyLocalOcrSettingsDoNotEnableExternalCallsOrBreakConfigurationLoading()
    {
        var profile=new AiProfile();var ai=new AiConfiguration(false,profile,profile){PayslipEnabled=true,
            Models=[new("text","text",profile)],ServiceModels=new(){{"extraction","text"},{"narration","text"},{"payslip","retired"}}};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        new ConfigurationValidator(Source).Validate(config);
        Assert.False((await PayslipAvailability.ReadAsync(ai,null,default)).Enabled);
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));
        var service=new ConfigurationService(db,state,new(Source),vault);
        var cars=config.Cars.Select((c,i)=>i==0?c with{Photos=[new(new string('a',64),"exterior","Test credit")]}:c).ToList();
        var saved=await service.SaveAsync(new(0,config with{Cars=cars},"save photo while legacy payslip service is unavailable"),"admin",default);
        Assert.Single(saved.Configuration.Cars[0].Photos);Assert.False((await PayslipAvailability.ReadAsync(saved.Configuration.Ai,vault,default)).Enabled);
        var disabled=await service.SaveAsync(new(saved.Revision,saved.Configuration with{Ai=ai with{PayslipEnabled=false}},"disable unavailable payslip service"),"admin",default);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveAsync(new(disabled.Revision,disabled.Configuration with{Ai=ai},"explicit reactivation still needs a document model"),"admin",default));
    }
    [Fact] public async Task CatalogueSaveDoesNotRecheckAnUnchangedDocumentModelWhoseKeyIsUnavailable()
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var missing=new AiProfile{SupportsDocuments=true,KeyReference="environment:CARWISE_AI_NOT_CONFIGURED"};
        var ai=new AiConfiguration(false,missing,missing){PayslipEnabled=true};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));var service=new ConfigurationService(db,state,new(Source),vault);
        var cars=config.Cars.Select((c,i)=>i==0?c with{Photos=[new(new string('b',64),"exterior","Test credit")]}:c).ToList();
        var saved=await service.SaveAsync(new(0,config with{Cars=cars},"vehicle photo is independent of missing document key"),"admin",default);
        Assert.Single(saved.Configuration.Cars[0].Photos);
        var status=await PayslipAvailability.ReadAsync(saved.Configuration.Ai,vault,default);Assert.False(status.Enabled);Assert.Equal("key_required",status.Code);
        var changed=ai with{Extraction=missing with{Model="another-document-model"}};
        await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveAsync(new(saved.Revision,saved.Configuration with{Ai=changed},"changing an active model still requires a key"),"admin",default));
    }

}
