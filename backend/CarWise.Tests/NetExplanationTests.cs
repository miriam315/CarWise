using CarWise.Api.Domain;
using Xunit;
namespace CarWise.Tests;

public sealed class NetExplanationTests
{
    private static ReferenceData Data()=>ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    [Fact] public void ThreeDriversReconcileEveryEligibleReferenceAlternativeWithoutCountingBenefitTwice()
    {
        var data=Data();var engine=new PayrollEngine(data);
        foreach(var employee in data.Employees)
        foreach(var alternative in engine.Simulate(employee).Results.Where(r=>r.Eligible&&!r.Pending))
        {
            var explanation=Assert.IsType<NetExplanation>(alternative.Explanation);
            Assert.Equal(3,explanation.Drivers.Count);Assert.Equal(3,explanation.Drivers.Select(d=>d.Id).Distinct().Count());
            Assert.Equal(alternative.Delta,explanation.MonthlyChange);Assert.Equal(alternative.Delta*12,explanation.AnnualChange);
            Assert.InRange(Math.Abs(explanation.Drivers.Sum(d=>d.Impact)-alternative.Delta),0,.000000000000000001m);
        }
    }
    [Fact] public void SameCarAndConfirmedParticipationHaveNoChange()
    {
        var data=Data();var employee=data.Employees.First(e=>e.CurrentCar is not null);
        var result=new PayrollEngine(data).Simulate(employee,new Dictionary<string,decimal>{{employee.CurrentCar!,employee.Participation}}).Results.Single(r=>r.Id==employee.CurrentCar);
        Assert.Equal(0,result.Delta);Assert.All(result.Explanation!.Drivers,d=>Assert.Equal(0,d.Impact));
    }
    [Fact] public void NewParticipationIsADirectNetDeductionWithoutChangingTaxes()
    {
        var data=Data();var e=data.Employees[0];var engine=new PayrollEngine(data);
        var a=engine.Simulate(e,new Dictionary<string,decimal>{{"CAR-10",300}}).Results.Single(r=>r.Id=="CAR-10");
        var b=engine.Simulate(e,new Dictionary<string,decimal>{{"CAR-10",450}}).Results.Single(r=>r.Id=="CAR-10");
        Assert.Equal(-150,b.Explanation!.MonthlyChange-a.Explanation!.MonthlyChange);
        Assert.Equal(-150,b.Explanation.Drivers.Single(d=>d.Id=="participation").Impact-a.Explanation.Drivers.Single(d=>d.Id=="participation").Impact);
        Assert.Equal(a.Explanation.Drivers.Single(d=>d.Id=="taxes").Impact,b.Explanation.Drivers.Single(d=>d.Id=="taxes").Impact);
    }
    [Fact] public void WaiverIncludesTaxableAndPensionableMaintenance()
    {
        var data=Data();data.Rules["waiver"]!["maintenanceIsPensionable"]=true;
        var engine=new PayrollEngine(data);var e=data.Employees.First(e=>data.OccupationFor(e.Occupation).Waiver&&e.CurrentCar is not null);
        var simulation=engine.Simulate(e);var waiver=simulation.Results.Single(r=>r.Id=="waiver");
        var expected=waiver.Gross-simulation.Current.Gross+waiver.Maintenance-simulation.Current.Maintenance+waiver.ElectricGross-simulation.Current.ElectricGross+simulation.Current.Pension+simulation.Current.Fund-waiver.Pension-waiver.Fund;
        Assert.Equal(expected,waiver.Explanation!.Drivers.Single(d=>d.Id=="income").Impact);
        Assert.InRange(Math.Abs(waiver.Explanation.Drivers.Sum(d=>d.Impact)-waiver.Delta),0,.000000000000000001m);
    }
    [Fact] public void PendingAndIneligibleAlternativesNeverExposeExplanationMoney()
    {
        var data=Data();var e=data.Employees[0];var simulation=new PayrollEngine(data).Simulate(e,pending:new HashSet<string>{"CAR-10"});
        Assert.Null(simulation.Results.Single(r=>r.Id=="CAR-10").Explanation);
        var pending=simulation.Results.Single(r=>r.Id=="CAR-10") with{Explanation=NetExplanation.Compare(simulation.Current,simulation.Current)};
        var view=SimulationView.Complete(simulation with{Results=simulation.Results.Select(r=>r.Id==pending.Id?pending:r).ToList()});
        foreach(var row in view["results"]!.AsArray().Where(r=>r!["pending"]!.GetValue<bool>()||!r["eligible"]!.GetValue<bool>()))
        {Assert.Null(row!["net"]);Assert.Null(row["delta"]);Assert.Null(row["explanation"]);}
    }
}
