using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using CarWise.Api.AI;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class ComplexPayrollIntegrationTests
{
    [ComplexPayrollFact] public async Task AllOriginalDensePayslipsMatchTheirIndependentExpectedValuesInsideActualSandbox()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"examples/payslips/complex/CarWise-complex-demo-payslips.zip")))directory=directory.Parent;
        Assert.NotNull(directory);var root=directory.FullName;
        using var archive=ZipFile.OpenRead(Path.Combine(root,"examples/payslips/complex/CarWise-complex-demo-payslips.zip"));
        var expectedEntry=Assert.Single(archive.Entries,e=>e.Name=="expected-values.json");
        using var expectedStream=expectedEntry.Open();
        var expected=await JsonSerializer.DeserializeAsync<Dictionary<string,Dictionary<string,decimal>>>(expectedStream);Assert.NotNull(expected);
        var service=new SandboxedPayslipOcr(new DockerOcrRuntime(new ConfigurationBuilder().Build()));
        var results=new List<object>();var errors=new List<string>();var count=0;
        foreach(var entry in archive.Entries.Where(e=>new[]{".pdf",".png",".jpg"}.Contains(Path.GetExtension(e.Name))).OrderBy(e=>e.Name))
        {
            var caseId=Path.GetFileNameWithoutExtension(entry.Name).Replace("-scanned","",StringComparison.Ordinal);
            Assert.True(expected.ContainsKey(caseId),entry.Name);
            using var stream=entry.Open();using var buffer=new MemoryStream();await stream.CopyToAsync(buffer);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(70));var watch=Stopwatch.StartNew();
            var result=await service.ExtractAsync(AiDocument.Read(buffer.ToArray()),new(){TimeoutSeconds=60},1,deadline.Token);
            var values=result.Fields.ToDictionary(f=>f.Key,f=>f.Value.Value);
            var missing=expected[caseId].Keys.Where(k=>values[k] is null).ToArray();
            var wrong=expected[caseId].Where(p=>values[p.Key] is {} actual&&actual!=p.Value).Select(p=>p.Key).ToArray();
            count+=expected[caseId].Count;
            results.Add(new{file=entry.Name,source=result.Source,seconds=Math.Round(watch.Elapsed.TotalSeconds,3),expected=expected[caseId],actual=values,missing,wrong});
            if(missing.Length>0||wrong.Length>0)errors.Add(entry.Name+": missing="+string.Join(',',missing)+" wrong="+string.Join(',',wrong));
        }
        Assert.Equal(84,count);Assert.Equal(12,results.Count);
        Directory.CreateDirectory(Path.Combine(root,".local"));
        await File.WriteAllTextAsync(Path.Combine(root,".local/complex-ocr-verification.json"),JsonSerializer.Serialize(new{
            suite="original-12-dense-synthetic-payslips",minimumConfidence=75,timeoutSeconds=60,documents=results.Count,expectedFields=count,passed=errors.Count==0,results
        },new JsonSerializerOptions{WriteIndented=true}));
        Assert.True(errors.Count==0,string.Join("\n",errors));
    }
}
public sealed class ComplexPayrollFactAttribute:FactAttribute
{public ComplexPayrollFactAttribute(){if(Environment.GetEnvironmentVariable("CARWISE_VERIFY_COMPLEX_OCR")!="true")Skip="Run npm run ocr:verify:complex after building the updated isolated worker.";}}
