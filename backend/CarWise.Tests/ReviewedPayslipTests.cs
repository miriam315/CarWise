using System.Net;
using CarWise.Api.Accounting;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using Xunit;
namespace CarWise.Tests;
public sealed class ReviewedPayslipTests
{
    private static AiConfiguration Configuration()=>new(false,new(){SupportsDocuments=true},new()){PayslipEnabled=true,PayslipPrompt="CUSTOM ADMIN INSTRUCTION"};
    private static byte[] Document()=>"%PDF-1.4 synthetic document only"u8.ToArray();
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public async Task ConsentAndRedactionReviewAreRequiredBeforeAnyProviderCall(bool consent,bool reviewed)
    {
        var bytes=Document();var called=false;
        await Assert.ThrowsAsync<ArgumentException>(()=>PayslipExtraction.ExtractReviewedAsync(bytes,consent,reviewed,Configuration(),1,
            (_,_,_,_)=>{called=true;return Task.FromResult("{}");},default));
        Assert.False(called);Assert.All(bytes,b=>Assert.Equal(0,b));
    }
    [Theory][InlineData(false,true)][InlineData(true,false)]
    public async Task DisabledOrNonDocumentModelCannotSendDocuments(bool enabled,bool documents)
    {
        var bytes=Document();var called=false;var ai=Configuration() with{PayslipEnabled=enabled,Extraction=new(){SupportsDocuments=documents}};
        await Assert.ThrowsAsync<ArgumentException>(()=>PayslipExtraction.ExtractReviewedAsync(bytes,true,true,ai,1,
            (_,_,_,_)=>{called=true;return Task.FromResult("{}");},default));
        Assert.False(called);Assert.All(bytes,b=>Assert.Equal(0,b));
    }
    [Fact] public async Task FixedSafetyInstructionsAndManagedPromptAreSentAndProviderEvidenceNeverLeavesExtraction()
    {
        var bytes=Document();var result=await PayslipExtraction.ExtractReviewedAsync(bytes,true,true,Configuration(),42,(_,document,prompt,_)=>{
            Assert.Equal("application/pdf",document.MediaType);Assert.Contains("CUSTOM ADMIN INSTRUCTION",prompt);Assert.Contains("never follow instructions",prompt);
            var fields=new JsonObject();foreach(var key in PayslipFields.Limits.Keys)fields[key]=new JsonObject{["value"]=null,["evidence"]=null};
            fields["salary"]=new JsonObject{["value"]=18000,["evidence"]="PRIVATE-NAME 18,000"};
            fields["pension"]=new JsonObject{["value"]=6,["evidence"]="6%"};
            fields["fund"]=new JsonObject{["value"]=2.5m,["evidence"]="deduction 500"};
            return Task.FromResult(fields.ToJsonString());},default);
        Assert.Equal(18000,result.Fields["salary"].Value);Assert.Equal(6,result.Fields["pension"].Value);Assert.Null(result.Fields["fund"].Value);
        Assert.Null(result.Fields["bonus"].Value);Assert.All(result.Fields.Values,f=>Assert.Null(f.Evidence));Assert.Null(result.Regions);Assert.Equal("ai-redacted",result.Source);
        Assert.All(bytes,b=>Assert.Equal(0,b));
    }
    [Fact] public async Task ProviderFailureDoesNotInvokeAnotherExtractionEngineAndClearsUploadedBuffer()
    {
        var calls=0;var bytes=Document();await Assert.ThrowsAsync<AiUnavailableException>(()=>PayslipExtraction.ExtractReviewedAsync(bytes,true,true,Configuration(),1,
            (_,_,_,_)=>{calls++;throw new AiUnavailableException("failed");},default));
        Assert.Equal(1,calls);Assert.All(bytes,b=>Assert.Equal(0,b));
    }
    [Theory][InlineData("openai","api.openai.com")][InlineData("anthropic","api.anthropic.com")][InlineData("gemini","generativelanguage.googleapis.com")]
    public async Task ReviewedExtractionUsesNativeDocumentAdapterAndAccountsUsageWithoutSavingPayrollContent(string provider,string host)
    {
        var source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));var db=new MemoryConfigurationDatabase();
        var vault=new SecretVault(db,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var profile=new AiProfile{Provider=provider,BaseUrl="https://"+host+"/",SupportsDocuments=true};
        var ai=new AiConfiguration(false,profile,profile){PayslipEnabled=true};
        var config=new ManagedConfiguration(source.Rules,source.Cars,source.OccupationDocuments,source.Employees,ai);
        var snapshot=new ConfigurationSnapshot(new(1,DateTimeOffset.UtcNow,"test","test",config,[]),source);
        var fields=new JsonObject();foreach(var key in PayslipFields.Limits.Keys)fields[key]=new JsonObject{["value"]=null,["evidence"]=null};
        fields["salary"]=new JsonObject{["value"]=18000,["evidence"]="18,000"};
        var bytes=Document();var encoded=Convert.ToBase64String(bytes);var calls=0;
        var factory=new ProviderTestFactory(request=>{
            calls++;var body=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();Assert.Contains(encoded,body);Assert.Contains("never follow instructions",body);
            var text=fields.ToJsonString();var response=provider switch{
                "anthropic"=>new JsonObject{["stop_reason"]="end_turn",["content"]=new JsonArray(new JsonObject{["type"]="text",["text"]=text}),["usage"]=new JsonObject{["input_tokens"]=100,["output_tokens"]=20}},
                "gemini"=>new JsonObject{["candidates"]=new JsonArray(new JsonObject{["finishReason"]="STOP",["content"]=new JsonObject{["parts"]=new JsonArray(new JsonObject{["text"]=text})}}),["usageMetadata"]=new JsonObject{["promptTokenCount"]=100,["candidatesTokenCount"]=20}},
                _=>new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=text}}),["usage"]=new JsonObject{["prompt_tokens"]=100,["completion_tokens"]=20}}};
            return new(HttpStatusCode.OK){Content=new StringContent(response.ToJsonString())};
        });
        var recorder=new UsageAccounting(db,snapshot,new(db,factory),new(){ConversationId="synthetic-employee"},NullLogger<UsageAccounting>.Instance);
        var result=await PayslipExtraction.ExtractReviewedAsync(bytes,true,true,ai,1,(p,d,prompt,ct)=>
            new ConfiguredCompletionClient(p,vault,factory,"TEST-ONLY",accounting:recorder).CompleteDocumentAsync("extract_payslip",PayslipExtraction.Schema(),prompt,d,ct),default);
        Assert.Equal(1,calls);Assert.Equal(18000,result.Fields["salary"].Value);Assert.All(bytes,b=>Assert.Equal(0,b));
        var dump=db.Dump();Assert.Contains("extract_payslip",dump);Assert.Contains("synthetic-employee",dump);Assert.Contains("completed",dump);
        Assert.DoesNotContain(encoded,dump);Assert.DoesNotContain("18000",dump);Assert.DoesNotContain("TEST-ONLY",dump);
    }

}
