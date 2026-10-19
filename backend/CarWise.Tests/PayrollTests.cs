using System.Text.Json.Nodes;
using CarWise.Api.Domain;
using Xunit;
namespace CarWise.Tests;

public sealed class PayrollTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private readonly PayrollEngine engine=new(Data);
    [Fact] public void AllSixReferenceNetsAndTaxesMatch()
    {
        var rows=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"data","references.json")))!.AsArray();
        var targets=new[]{"CAR-10","CAR-19","CAR-06","CAR-11","waiver","CAR-14"};
        for(var i=0;i<6;i++)
        {
            var simulation=engine.Simulate(Data.Employees[i]);var result=simulation.Results.Single(r=>r.Id==targets[i]);var row=rows[i]!;
            Assert.Equal(row["netBefore"]!.GetValue<decimal>(),PayrollEngine.Display(simulation.Current.Net));
            Assert.Equal(row["netAfter"]!.GetValue<decimal>(),PayrollEngine.Display(result.Net));
            Assert.Equal(row["taxBefore"]!.GetValue<decimal>(),PayrollEngine.Display(simulation.Current.Tax));
            Assert.Equal(row["taxAfter"]!.GetValue<decimal>(),PayrollEngine.Display(result.Tax));
            Assert.Equal(row["insuranceAfter"]!.GetValue<decimal>(),PayrollEngine.Display(result.Insurance));
            Assert.Equal(row["delta"]!.GetValue<decimal>(),PayrollEngine.Display(result.Net)-PayrollEngine.Display(simulation.Current.Net));
        }
    }
    [Fact] public void AllOfficialKeysAndDriveTypesMatch()
    {
        Assert.Equal(77988,Data.Official.Count);
        foreach(var car in Data.Cars)
        {
            Assert.Equal(car.Value,Data.Lookup(car).Value);
            Assert.Equal(car.Drive,Data.Rules["valueOfUse"]!["driveField"]![Data.Lookup(car).Drive]!.GetValue<string>());
            Assert.Equal(5,Data.Enrich(car).OfficialKey.Length);
        }
    }
    [Fact] public void ElectricGrossUpLeavesPrecisely250WithinDecimalPrecision()
    {
        foreach(var e in Data.Employees)
        foreach(var car in Data.Cars.Where(c=>c.Drive=="חשמלי"))
            Assert.InRange(Math.Abs(engine.Calculate(e,car).ElectricNet-250m),0m,0.00000000000000000001m);
    }
    [Theory]
    [InlineData(7010)] [InlineData(7522)] [InlineData(10060)] [InlineData(16150)]
    [InlineData(22440)] [InlineData(46690)] [InlineData(50695)] [InlineData(60130)]
    public void BracketAndInsuranceBoundariesAreContinuous(decimal threshold)
    {
        var e=Data.Employees[0] with{Commission=0,Bonus=0,Pension=0,Fund=0,Credits=0};
        var maintenance=Data.OccupationFor(e.Occupation).Maintenance;
        var before=engine.Calculate(e with{Salary=threshold-maintenance-.01m},null);
        var after=engine.Calculate(e with{Salary=threshold-maintenance+.01m},null);
        Assert.InRange(after.Net-before.Net,0.00001m,.02m);
    }
    [Fact] public void FamilyEligibilityAndGapAreCatalogueSpecific()
    {
        var e=Data.Employees[0];Assert.False(engine.Eligibility(e,Data.Cars[18]).Eligible);
        e=e with{Children=4};Assert.True(engine.Eligibility(e,Data.Cars[18]).Eligible);
        Assert.Equal(750m,engine.Calculate(e,Data.Cars[18]).FamilyGap);
        Assert.Equal(550m,engine.Calculate(e,Data.Cars[19]).FamilyGap);
    }
    [Fact] public void FieldRoleCannotWaiveOrChooseHigherGroup()
    {
        Assert.False(engine.Eligibility(Data.Employees[1],null).Eligible);
        Assert.False(engine.Eligibility(Data.Employees[0],Data.Cars[13]).Eligible);
    }
    [Fact] public void ParticipationIsNetOnlyAndMaintenanceIsNotPensionable()
    {
        var e=Data.Employees[0];var car=Data.Cars[9];var a=engine.Calculate(e,car,300);var b=engine.Calculate(e,car,350);
        Assert.Equal(a.Tax,b.Tax);Assert.Equal(a.Value,b.Value);Assert.Equal(50m,a.Net-b.Net);
        Assert.Equal(a.Pension,engine.Calculate(e,null).Pension);Assert.Equal(a.Fund,engine.Calculate(e,null).Fund);
    }
    [Fact] public void UnknownOfficialKeyOrYearIsRejected()
    {
        Assert.Throws<ArgumentException>(()=>Data.Lookup(Data.Cars[0] with{ModelCode="99999"}));
        Assert.Throws<ArgumentException>(()=>Data.Lookup(Data.Cars[0] with{Year=2030}));
    }
    [Fact] public void InvalidFieldsFailInsteadOfGuessing()
    {
        var e=Data.Employees[0];
        foreach(var bad in new[]{e with{Salary=-1},e with{Children=1.5m},e with{Pension=101},e with{Occupation="unknown"},e with{CurrentCar="unknown"}})
            Assert.Throws<ArgumentException>(()=>engine.Simulate(bad));
    }
    [Theory][InlineData(2,"CAR-06",-1939)][InlineData(5,"CAR-14",722)]
    public void DifferenceIsComputedBeforeDisplayRounding(int index,string target,decimal expected)
        => Assert.Equal(expected,PayrollEngine.Display(engine.Simulate(Data.Employees[index]).Results.Single(r=>r.Id==target).Delta));
}
