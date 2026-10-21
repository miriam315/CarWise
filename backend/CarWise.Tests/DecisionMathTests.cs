using CarWise.Api.Domain;
using Xunit;
namespace CarWise.Tests;
public sealed class DecisionMathTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    [Fact] public void OfficialTaxYearChangeKeepsVehicleIdentityButDifferentModelOrRegistrationYearDoesNot()
    {
        var car=Data.Cars[0];Assert.True(DecisionMath.SameVehicle(car,car with{OfficialKey=["2027","2025","1",car.MakerCode,car.ModelCode]}));
        Assert.False(DecisionMath.SameVehicle(car,car with{Year=car.Year+1}));
        Assert.False(DecisionMath.SameVehicle(car,car with{ModelCode="different"}));
    }
    [Fact] public void ExactBoundaryKeepsNetAtCrossingAndRejectsTheNextCent()
    {
        var engine=new PayrollEngine(Data);var e=Data.Employees[0];
        foreach(var car in Data.Cars.Where(c=>engine.Eligibility(e,c).Eligible))
        {
            var b=DecisionMath.Boundary(engine,Data,e,car.Id);var baseline=engine.Calculate(e,Data.Cars.Single(c=>c.Id==e.CurrentCar),e.Participation);
            Assert.Equal(engine.Calculate(e,car,0).Net-baseline.Net,b.RawThreshold);
            if(b.MaximumWithoutLoss is {} maximum&&maximum<20000)
            {Assert.True(engine.Calculate(e,car,maximum).Net>=baseline.Net);Assert.True(engine.Calculate(e,car,maximum+.01m).Net<baseline.Net);}
        }
    }
    [Fact] public void UnknownParticipationDoesNotPreventAConditionalThresholdOrBecomeAnApproval()
    {
        var e=Data.Employees[0];var cars=Data.Cars.Select(c=>c.Id=="CAR-10"?c with{ParticipationKnown=false}:c).ToList();
        var data=ReferenceData.FromSnapshot(Data.Snapshot() with{Cars=cars});var engine=new PayrollEngine(data);
        var before=engine.Simulate(e).Results.Single(r=>r.Id=="CAR-10");Assert.True(before.Pending);
        Assert.Contains("לא סכום השתתפות מאושר",DecisionMath.Boundary(engine,data,e,"CAR-10").Explanation);
        Assert.True(engine.Simulate(e).Results.Single(r=>r.Id=="CAR-10").Pending);
    }
    [Fact] public void MissingBaselineAndIneligibleVehicleCannotProduceAThreshold()
    {
        var e=Data.Employees[0];var engine=new PayrollEngine(Data);
        Assert.Throws<ArgumentException>(()=>DecisionMath.Boundary(engine,Data,e with{KnownFields=[]},"CAR-10"));
        var forbidden=Data.Cars.FirstOrDefault(c=>!engine.Eligibility(e,c).Eligible);Assert.NotNull(forbidden);
        Assert.Throws<ArgumentException>(()=>DecisionMath.Boundary(engine,Data,e,forbidden.Id));
    }
    [Fact] public void FullAllowedRangeIsReportedWithoutInventingAnOutOfRangeRecommendation()
    {
        var engine=new PayrollEngine(Data);var e=Data.Employees[0] with{Participation=20000};
        var boundary=DecisionMath.Boundary(engine,Data,e,e.CurrentCar!);
        Assert.Equal("always",boundary.Outcome);Assert.Equal(20000,boundary.MaximumWithoutLoss);Assert.Equal(20000,boundary.Maximum);
    }
    [Fact] public void NegativeThresholdHasNoFalseZeroParticipationRecommendation()
    {
        var engine=new PayrollEngine(Data);var e=Data.Employees[0] with{Participation=0};
        var car=Data.Cars.First(c=>engine.Eligibility(e,c).Eligible&&engine.Calculate(e,c,0).Net<engine.Calculate(e,Data.Cars.Single(x=>x.Id==e.CurrentCar),0).Net);
        var result=DecisionMath.Boundary(engine,Data,e,car.Id);Assert.Equal("never",result.Outcome);Assert.Null(result.MaximumWithoutLoss);Assert.True(result.RawThreshold<0);
    }
}
