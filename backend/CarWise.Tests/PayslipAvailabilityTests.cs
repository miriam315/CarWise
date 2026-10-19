using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class StubLocalOcr(LocalOcrStatus status) : ILocalPayslipOcr
{
    public int Checks {get;private set;}
    public Task<LocalOcrStatus> StatusAsync(LocalPayslipSettings settings,CancellationToken ct){Checks++;return Task.FromResult(status);}
    public Task<PayslipResult> ExtractAsync(AiDocument document,LocalPayslipSettings settings,long revision,CancellationToken ct)=>throw new NotSupportedException();
}
public sealed class PayslipAvailabilityTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    [Fact] public async Task LocalPayslipsSaveWithoutChatAiKeysOrDocumentModels()
    {
        var db=new MemoryConfigurationDatabase();var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var missing=new AiProfile{KeyReference="environment:CARWISE_AI_NOT_CONFIGURED"};
        var ai=new AiConfiguration(false,missing,missing){PayslipEnabled=true};
        var config=new ManagedConfiguration(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,ai);
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),Source));
        var ocr=new StubLocalOcr(new(true,"ready","local ready"));
        var service=new ConfigurationService(db,state,new(Source),vault,localOcr:ocr);
        await service.SaveAsync(new(0,config,"enable local OCR without any API key"),"admin",default);
        Assert.False(state.Current.Document.Configuration.Ai.Enabled);
        var status=await PayslipAvailability.ReadAsync(ai,ocr,default);Assert.True(status.Enabled);Assert.Equal("ready",status.Code);
        Assert.Contains("ready",JsonDefaults.Serialize(await service.ViewAsync(default)));
        var checks=ocr.Checks;var disabled=await PayslipAvailability.ReadAsync(ai with{PayslipEnabled=false},ocr,default);
        Assert.False(disabled.Enabled);Assert.Equal("disabled",disabled.Code);Assert.Equal(checks,ocr.Checks);
    }
    [Fact] public async Task MissingToolsAreExplicitAndNeverTriggerAnAiFallback()
    {
        var profile=new AiProfile();var ai=new AiConfiguration(true,profile,profile){PayslipEnabled=true};
        var missing=new StubLocalOcr(new(false,"ocr_tools_missing","missing local tools"));
        var status=await PayslipAvailability.ReadAsync(ai,missing,default);
        Assert.False(status.Enabled);Assert.Equal("ocr_tools_missing",status.Code);
        Assert.Equal("ocr_unavailable",(await PayslipAvailability.ReadAsync(ai,null,default)).Code);
    }
}
