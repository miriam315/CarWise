using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class LazyReferenceTests
{
    private static readonly ReferenceData Source=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    [Fact] public async Task StartupAndCatalogueEditsFetchOnlyRequiredOfficialRowsAndKeepTheTotalCount()
    {
        var officialQueries=0;var counts=0;
        HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonDefaults.Serialize(value))};
        using var client=new HttpClient(new FakeHttp(request=>{
            var path=request.RequestUri!.AbsolutePath;var query=Uri.UnescapeDataString(request.RequestUri.Query);
            if(path.EndsWith("carwise_rules"))return Json(new[]{new{id="active",document=Source.Rules}});
            if(query.Contains("id=gt."))return Json(Array.Empty<object>());
            if(path.EndsWith("carwise_cars"))return Json(Source.Cars.Select(c=>new{id=c.Id,document=c}));
            if(path.EndsWith("carwise_employees"))return Json(Source.Employees.Select(e=>new{id=e.Id,document=e}));
            if(path.EndsWith("carwise_occupations"))return Json(Source.OccupationDocuments.Select(o=>new{id=o["occupation"].GetString(),document=o}));
            Assert.EndsWith("carwise_official",path);
            if(query.Contains("select=id"))
            {
                counts++;Assert.Equal("count=exact",Assert.Single(request.Headers.GetValues("Prefer")));
                var response=Json(new[]{new{id="sample"}});response.Content.Headers.TryAddWithoutValidation("Content-Range","0-0/77988");return response;
            }
            Assert.Contains("id=in.(",query);officialQueries++;
            return Json(Source.Official.Values.Where(r=>query.Contains(r.Key)).Select(r=>new{id=r.Key,document=r.Document}));
        })){BaseAddress=new("https://example.invalid/")};
        var database=new SupabaseDatabase(client);var baseline=await new Repository(database).LoadAsync();
        Assert.Equal(1,officialQueries);Assert.Equal(1,counts);Assert.Equal(77988,baseline.OfficialRowCount);Assert.InRange(baseline.Official.Count,1,20);
        Assert.Equal(11693m,decimal.Round(new PayrollEngine(baseline).Simulate(baseline.Employees[0]).Results.Single(r=>r.Id=="CAR-10").Net));
        var row=Source.Official.Values.First(r=>r.TaxYear==Source.TaxYear&&r.VehicleType=="1"&&!baseline.Official.ContainsKey(r.Key));
        var car=baseline.Cars[0] with{Id="new-catalogue-car",Year=row.RegistrationYear,MakerCode=row.MakerCode,ModelCode=row.ModelCode,Value=row.Value,Drive=baseline.Rules["valueOfUse"]!["driveField"]![row.Drive]!.GetValue<string>()};
        var versions=new OfficialVersions(database,baseline);
        var config=new ManagedConfiguration(baseline.Rules,baseline.Cars,baseline.OccupationDocuments,baseline.Employees,new(false,new(),new()));
        var memory=new MemoryConfigurationDatabase();var state=new ConfigurationState(new(new(0,DateTimeOffset.UtcNow,"test","test",config,[]),baseline));
        var vault=new SecretVault(memory,new EphemeralDataProtectionProvider(),new ConfigurationBuilder().Build());
        var service=new ConfigurationService(memory,state,new(baseline),vault,versions);
        await service.SaveAsync(new(0,config with{Cars=[..config.Cars,car]},"add an official car without downloading the entire database"),"admin",default);
        Assert.Equal(row.Value,state.Current.Data.Lookup(car).Value);Assert.Equal(77988,state.Current.Data.OfficialRowCount);Assert.Equal(1,counts);
        Assert.InRange(state.Current.Data.Official.Count,1,21);
    }
}
