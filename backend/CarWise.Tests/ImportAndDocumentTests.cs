using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace CarWise.Tests;
public sealed class ImportAndDocumentTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static ManagedConfiguration Config()=>new(Source.Rules.DeepClone(),Source.Cars,Source.OccupationDocuments,Source.Employees,new(false,new(),new()));
    private static byte[] Csv(int year,bool missing=false,bool duplicate=false,int? takeAll=null)
    {
        var docs=takeAll is null?Source.Cars.Select(c=>Source.Lookup(c).Document).DistinctBy(d=>ReferenceData.ParseOfficial(d).Key).ToList():Source.Official.Values.Take(takeAll.Value).Select(r=>r.Document).ToList();
        if(missing)docs.RemoveAt(0);if(duplicate)docs.Add(docs[0]);
        var headers=docs[0].Keys.ToArray();string Escape(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
        return Encoding.UTF8.GetBytes(string.Join(',',headers.Select(Escape))+"\r\n"+string.Join("\r\n",docs.Select(d=>string.Join(',',headers.Select(h=>Escape(h=="שנת מס"?year.ToString():h=="שווי שימוש"?(decimal.Parse(d[h],System.Globalization.CultureInfo.InvariantCulture)+100).ToString(System.Globalization.CultureInfo.InvariantCulture):d[h]))))));
    }
    [Fact] public async Task NewYearCanBeUploadedActivatedRefreshedAndRolledBackWithoutChangingCode()
    {
        var db=new MemoryConfigurationDatabase();var versions=new OfficialVersions(db,Source);var config=Config();
        var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","baseline",config,[]),Source));
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var service=new ConfigurationService(db,state,new(Source),vault,versions);
        var uploaded=await versions.UploadAsync(Csv(2027),"official-2027.csv","admin",default);
        Assert.Equal(2027,uploaded.TaxYear);Assert.Equal(2026,state.Current.Data.TaxYear);Assert.NotEmpty(uploaded.Sha256);
        var preview=await versions.PreviewAsync(uploaded.Id,config,default);Assert.Empty(preview.Preview.Missing);
        var activated=await service.SaveAsync(new(0,preview.Configuration,"activate 2027"),"admin",default);
        Assert.Equal(2027,state.Current.Data.TaxYear);Assert.Equal(Source.Cars[0].Value+100,state.Current.Data.Lookup(state.Current.Data.Cars[0]).Value);
        Assert.Contains("2027",state.Current.Data.Rules["valueOfUse"]!["source"]!.GetValue<string>());
        var freshVersions=new OfficialVersions(db,Source);var newState=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","baseline",config,[]),Source));
        var other=new ConfigurationService(db,newState,new(Source),vault,freshVersions);await other.RefreshAsync(default);
        Assert.Equal(2027,newState.Current.Data.TaxYear);Assert.Equal(uploaded.Id,newState.Current.Document.Configuration.OfficialVersionId);
        await service.RestoreAsync(new(activated.Revision,0,"rollback"),"admin",default);
        Assert.Equal(2026,state.Current.Data.TaxYear);Assert.Equal(Source.Cars[0].Value,state.Current.Data.Lookup(state.Current.Data.Cars[0]).Value);
        Assert.Equal(2,(await versions.ListAsync(default)).Count);
    }
    [Fact] public async Task IncompleteCatalogueMatchesBlockActivationAndForgedYearIsRejected()
    {
        var db=new MemoryConfigurationDatabase();var versions=new OfficialVersions(db,Source);
        var upload=await versions.UploadAsync(Csv(2027,missing:true),"incomplete.csv","admin",default);
        var preview=await versions.PreviewAsync(upload.Id,Config(),default);Assert.NotEmpty(preview.Preview.Missing);
        await Assert.ThrowsAnyAsync<ArgumentException>(()=>Task.FromResult(new ConfigurationValidator(Source).Validate(preview.Configuration,Source.Official)));
        var config=Config();config.Rules["valueOfUse"]!["taxYear"]=2027;Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(Source).Validate(config));
    }
    [Fact] public async Task MultipleChunkBatchesCanBeLoadedWithKeysetPagination()
    {
        var db=new MemoryConfigurationDatabase();var versions=new OfficialVersions(db,Source);
        var version=await versions.UploadAsync(Csv(2027,takeAll:2251),"multi.csv","admin",default);
        Assert.Equal(10,version.Chunks);Assert.Equal(2251,(await new OfficialVersions(db,Source).LoadAsync(version.Id,default)).Count);
    }
    [Fact] public async Task ExplicitReplacementCanResolveMissingCatalogueMatches()
    {
        var db=new MemoryConfigurationDatabase();var versions=new OfficialVersions(db,Source);
        var version=await versions.UploadAsync(Csv(2027,missing:true),"partial.csv","admin",default);
        var preview=await versions.PreviewAsync(version.Id,Config(),default);var missing=Assert.Single(preview.Preview.Missing);
        var candidate=(await versions.LoadAsync(version.Id,default)).Values.First();
        var mapped=await versions.PreviewAsync(version.Id,Config(),default,new(){{missing.Id,new(candidate.RegistrationYear,candidate.MakerCode,candidate.ModelCode)}});
        Assert.Empty(mapped.Preview.Missing);var validated=new ConfigurationValidator(Source).Validate(mapped.Configuration,await versions.LoadAsync(version.Id,default));
        Assert.Equal(candidate.Key,validated.Lookup(validated.Cars.Single(c=>c.Id==missing.Id)).Key);
    }
    [Fact] public void FullOriginalOfficialCsvPassesTheSameUploadValidator()
    {var rows=OfficialVersions.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"data","tax-authority-2026.csv")));Assert.Equal(77988,rows.Count);}
    [Fact] public void DuplicateOfficialKeysAndBadHeadersAreRejectedBeforePersistence()
    {
        Assert.Throws<ArgumentException>(()=>OfficialVersions.Parse(Csv(2027,duplicate:true)));
        Assert.Throws<ArgumentException>(()=>OfficialVersions.Parse("not,a,tax,csv"u8.ToArray()));
    }
    [Theory]
    [InlineData("openai","image/png","image_url")]
    [InlineData("openai","application/pdf","file_data")]
    [InlineData("compatible","image/jpeg","image_url")]
    [InlineData("anthropic","application/pdf","document")]
    [InlineData("anthropic","image/png","image")]
    [InlineData("gemini","application/pdf","inlineData")]
    public async Task DocumentsUseProviderNativePayloadsAndOnlyProviderRequestsContainBytes(string provider,string media,string marker)
    {
        var host=provider=="anthropic"?"api.anthropic.com":provider=="gemini"?"generativelanguage.googleapis.com":"api.openai.com";
        string? sent=null;
        var factory=new ProviderTestFactory(request=>{
            sent=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var response=provider=="anthropic"?"""{"stop_reason":"end_turn","content":[{"type":"text","text":"{}"}]}""":provider=="gemini"?"""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{}"}]}}]}""":"""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""";
            return new(HttpStatusCode.OK){Content=new StringContent(response)};
        });
        var vault=new SecretVault(new MemoryConfigurationDatabase(),new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var client=new ConfiguredCompletionClient(new(){Provider=provider,BaseUrl="https://"+host+"/",Model="test-model"},vault,factory,"TEST-ONLY");
        var document=new AiDocument(media,Encoding.UTF8.GetBytes("FAKE DOCUMENT BYTES - TEST ONLY"));
        Assert.Equal("{}",await client.CompleteDocumentAsync("extract_payslip",new JsonObject(),"CUSTOM DOCUMENT PROMPT",document,default));
        Assert.Contains(marker,sent);Assert.Contains(Convert.ToBase64String(document.Bytes),sent);Assert.Contains("CUSTOM DOCUMENT PROMPT",sent);Assert.DoesNotContain("TEST-ONLY",sent);
    }
    [Fact] public void PayslipFieldsNeedEvidenceAndEmployeePercentagesCannotBeInferredFromMoney()
    {
        var fields=new JsonObject();foreach(var key in PayslipExtraction.Limits.Keys)fields[key]=new JsonObject{["value"]=null,["evidence"]=null};
        fields["salary"]=new JsonObject{["value"]=18000,["evidence"]="שכר בסיס 18,000"};
        fields["pension"]=new JsonObject{["value"]=6,["evidence"]="ניכוי פנסיה 1080"};
        fields["fund"]=new JsonObject{["value"]=2.5m,["evidence"]="קרן השתלמות עובד 2.5%"};
        var result=PayslipExtraction.Validate(fields.ToJsonString(),5);
        Assert.Equal(18000,result.Fields["salary"].Value);Assert.Null(result.Fields["pension"].Value);Assert.Equal(2.5m,result.Fields["fund"].Value);Assert.Null(result.Fields["bonus"].Value);Assert.Equal(5,result.ConfigurationRevision);
    }
    [Fact] public async Task DocumentUsageIsBilledToTheConversationWithoutStoringTheDocument()
    {
        var db=new MemoryConfigurationDatabase();var profile=new AiProfile();var config=Config() with{Ai=new(true,profile,profile){Pricing=[new("openai","api.openai.com",profile.Model,1m,.5m,2m,null,"test rates")]}};
        var snapshot=new ConfigurationSnapshot(new(9,DateTimeOffset.UtcNow,"test","test",config,[]),Source);
        var factory=new ProviderTestFactory(_=>new(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}],"usage":{"prompt_tokens":125,"completion_tokens":25}}""")});
        var recorder=new UsageAccounting(db,snapshot,new(db,factory),new(){ConversationId="E-001"},NullLogger<UsageAccounting>.Instance);
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var document=new AiDocument("application/pdf","FAKE DOCUMENT TEST BYTES"u8.ToArray());
        await new ConfiguredCompletionClient(profile,vault,factory,"TEST-ONLY",recorder).CompleteDocumentAsync("extract_payslip",new JsonObject(),"extract safely",document,default);
        var saved=Assert.Single(await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","usage:"}}));
        var record=JsonDefaults.Read<AiUsageRecord>(saved!["document"]!.ToJsonString());
        Assert.Equal("extract_payslip",record.Operation);Assert.Equal("E-001",record.ConversationId);Assert.Equal(.000175m,record.EstimatedUsd);Assert.Null(record.EstimatedIls);
        Assert.DoesNotContain(Convert.ToBase64String(document.Bytes),db.Dump());Assert.DoesNotContain("FAKE DOCUMENT",db.Dump());
    }
    [Fact] public void NonDocumentUploadsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>AiDocument.Read("this is not a valid document file"u8.ToArray()));
        Assert.Equal("application/pdf",AiDocument.Read("%PDF-1.7 fake document bytes for test"u8.ToArray()).MediaType);
    }
    [Fact] public async Task SavedPromptsAreUsedInTheActualExtractionAndNarrationRequests()
    {
        var received=new List<string>();var factory=new ProviderTestFactory(req=>{
            received.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var output=received.Count==1?"{}":"{\"text\":\"{NET} {DELTA} {VALUE} {PARTICIPATION}\"}";
            return new(HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=output}}}}))};
        });
        using var http=factory.CreateClient("test");http.BaseAddress=new("https://api.openai.com/v1/");
        var gateway=new AiGateway(http,"test-model");
        await new AiExtractionService(gateway,"MANAGED EXTRACTION PROMPT").ExtractAsync("hello",Source.Employees[0],[],default);
        await new AiNarrationService(gateway,NullLogger<AiNarrationService>.Instance,"MANAGED NARRATION PROMPT").NarrateAsync(new(){Id="test"},default);
        Assert.Contains("MANAGED EXTRACTION PROMPT",received[0]);Assert.Contains("Mandatory extraction constraints",received[0]);
        Assert.Contains("MANAGED NARRATION PROMPT",received[1]);Assert.Contains("Mandatory narration constraints",received[1]);
    }
    [Fact] public void SecretsRequireHttpsInProductionAndCrossOriginWritesAreRejected()
    {
        var context=new DefaultHttpContext();context.Request.Method="POST";context.Request.Host=new("localhost",8000);context.Request.Scheme="http";
        Assert.Throws<ArgumentException>(()=>RequestProtection.Validate(context,false));RequestProtection.Validate(context,true);
        context.Request.Headers.Origin="https://evil.example";Assert.Throws<UnauthorizedAccessException>(()=>RequestProtection.Validate(context,true));
        context.Request.Scheme="https";context.Request.Headers.Origin="https://localhost:8000";RequestProtection.Validate(context,false);
    }
}
