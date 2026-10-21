using CarWise.Api.Domain;
using CarWise.Api.Administration;
using CarWise.Api.Infrastructure;
using Xunit;
namespace CarWise.Tests;
public sealed class FleetSwapTests
{
    private static readonly ReferenceData Data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
    private static readonly DateTimeOffset Now=new(2026,10,21,12,0,0,TimeSpan.Zero);
    private static readonly DateOnly Today=new(2026,10,21);
    private static FleetAssignment Assignment(Employee e,string id)=>new(id,e.Id,e.CurrentCar!,new(2025,4,21),new(2028,4,21),null,true,null,null,FleetRules.Key(Data.Cars.Single(c=>c.Id==e.CurrentCar)),Guid.NewGuid(),"test-officer",Now);
    private static (FleetDocument doc,FleetRequest left,FleetRequest right,Employee le,Employee re) Pair()
    {
        var le=Data.Employees[0] with{Children=4,CurrentCar="CAR-09"};var re=Data.Employees[1] with{Children=4,CurrentCar="CAR-19",Occupation=le.Occupation,Group=le.Group};
        var a=Assignment(le,"PHYSICAL-1");var b=Assignment(re,"PHYSICAL-2");
        var l=new FleetRequest(Guid.NewGuid(),le.Id,"family",a.Id,a.Stamp,Now,Now.AddDays(30));var r=new FleetRequest(Guid.NewGuid(),re.Id,"regular",b.Id,b.Stamp,Now,Now.AddDays(30));
        return(new(0,[a,b],[l,r],[],[]),l,r,le,re);
    }
    [Fact] public void NoPolicyOrMissingDatesCannotInventReplacementEligibility()
    {
        Assert.False(FleetRules.Replacement(new(),null,Today).CanReplace);
        Assert.Equal("unconfigured",FleetRules.Replacement(new(),null,Today).Status);
        Assert.Equal("unknown",FleetRules.Replacement(new(){Enabled=true},null,Today).Status);
    }
    [Fact] public void ReplacementUsesCalendarMonthsOrEarlierVerifiedLeaseEnd()
    {
        var a=Pair().doc.Assignments[0];var policy=new FleetPolicy{Enabled=true};
        Assert.Equal(new DateOnly(2028,4,21),FleetRules.Replacement(policy,a,Today).EligibleOn);
        Assert.False(FleetRules.Replacement(policy,a,Today).CanReplace);
        Assert.True(FleetRules.Replacement(policy,a,new(2028,4,21)).CanReplace);
        var early=a with{LeaseEndsOn=new(2027,1,1)};
        Assert.Equal(new DateOnly(2027,1,1),FleetRules.Replacement(policy,early,Today).EligibleOn);
        var lastDay=a with{AssignedOn=new(2024,1,31),LeaseEndsOn=null};
        Assert.Equal(new DateOnly(2024,2,29),FleetRules.Replacement(policy with{ReplacementMonths=1},lastDay,Today).EligibleOn);
    }
    [Fact] public void OnlyEnabledUnexpiredOfficerExceptionAllowsEarlyOrdinaryReplacement()
    {
        var a=Pair().doc.Assignments[0] with{ExceptionReason="family",ExceptionUntil=Today};var policy=new FleetPolicy{Enabled=true};
        Assert.Equal("exception",FleetRules.Replacement(policy,a,Today).Status);
        Assert.False(FleetRules.Replacement(policy,a,Today.AddDays(1)).CanReplace);
        Assert.False(FleetRules.Replacement(policy with{ExceptionReasons=[]},a,Today).CanReplace);
    }
    [Fact] public void ReciprocalRequestsWithTwoEligibleWorkersMatchBeforeContractEnd()
    {
        var p=Pair();Assert.True(FleetRules.Mutual(p.doc,p.left,p.right,new(){Enabled=true},Data,p.le,p.re,Now));
        Assert.False(FleetRules.Mutual(p.doc,p.left,p.right,new(){Enabled=true,AllowEarlySwap=false},Data,p.le,p.re,Now));
        Assert.False(FleetRules.Mutual(p.doc,p.left,p.right,new(){Enabled=false},Data,p.le,p.re,Now));
    }
    [Fact] public void UnilateralPreferenceOrIneligibleFamilyTrackNeverMatches()
    {
        var p=Pair();var policy=new FleetPolicy{Enabled=true};
        Assert.False(FleetRules.Mutual(p.doc,p.left,p.right with{Category="electric"},policy,Data,p.le,p.re,Now));
        Assert.False(FleetRules.Mutual(p.doc,p.left,p.right,policy,Data,p.le with{Children=0},p.re,Now));
        Assert.False(FleetRules.Mutual(p.doc,p.left,p.left,policy,Data,p.le,p.le,Now));
    }
    [Fact] public void ExpiredRequestsChangedAssignmentsContractsAndVehicleIdentitiesAreRejected()
    {
        var p=Pair();var policy=new FleetPolicy{Enabled=true};
        Assert.False(FleetRules.Mutual(p.doc,p.left with{ExpiresAt=Now},p.right,policy,Data,p.le,p.re,Now));
        foreach(var a in new[]{p.doc.Assignments[0] with{Stamp=Guid.NewGuid()},p.doc.Assignments[0] with{TransferAllowed=false},p.doc.Assignments[0] with{VehicleKey="changed"},p.doc.Assignments[0] with{LeaseEndsOn=Today.AddDays(-1)}})
            Assert.False(FleetRules.Mutual(p.doc with{Assignments=[a,p.doc.Assignments[1]]},p.left,p.right,policy,Data,p.le,p.re,Now));
        Assert.False(FleetRules.Mutual(p.doc,p.left,p.right,policy,Data,p.le with{CurrentCar="CAR-01"},p.re,Now));
    }
    [Fact] public void OfficerApprovedVehiclesCannotBeOfferedAgain()
    {
        var p=Pair();var d=p.doc with{Proposals=[new(Guid.NewGuid(),p.left.Id,p.right.Id,[p.le.Id,p.re.Id],"approved",Now)]};
        Assert.True(FleetRules.Reserved(d,p.le.Id));Assert.True(FleetRules.Reserved(d,p.re.Id));
        Assert.False(FleetRules.Mutual(d,p.left,p.right,new(){Enabled=true},Data,p.le,p.re,Now));
    }
    private static FleetService Service(ReferenceData data,long revision=1)
    {
        var db=new MemoryConfigurationDatabase();
        var config=new ManagedConfiguration(data.Rules,data.Cars,data.OccupationDocuments,data.Employees,new(false,new(),new())){Fleet=new(){Enabled=true}};
        return new(new(db),new(new(revision,Now,"test","test",config,[]),data),new(db));
    }
    [Fact] public void ReplacementDatesAreNotTrustedWhenTheProfileOrVehicleIdentityChanged()
    {
        var p=Pair();var service=Service(Data);
        Assert.NotNull(service.Assignment(p.doc,p.le));
        Assert.Null(service.Assignment(p.doc,p.le with{CurrentCar="CAR-01"}));
        Assert.Null(service.Assignment(p.doc with{Assignments=[p.doc.Assignments[0] with{VehicleKey="changed"}]},p.le));
    }
    [Fact] public async Task ConsentBasisDetectsPayAndPolicyChangesWithoutStoringNamesOrRawPayroll()
    {
        var p=Pair();var digest=await Service(Data).Basis(p.left,p.right,default);Assert.Equal(64,digest.Length);
        Assert.NotEqual(digest,await Service(Data,2).Basis(p.left,p.right,default));
        var payroll=ReferenceData.FromSnapshot(Data.Snapshot() with{Employees=Data.Employees.Select((e,i)=>i==0?e with{Bonus=e.Bonus+1}:e).ToList()});
        Assert.NotEqual(digest,await Service(payroll).Basis(p.left,p.right,default));
        var nameOnly=ReferenceData.FromSnapshot(Data.Snapshot() with{Employees=Data.Employees.Select(e=>e with{Name="changed",Question="private text"}).ToList()});
        Assert.Equal(digest,await Service(nameOnly).Basis(p.left,p.right,default));
    }
    [Fact] public async Task ConcurrentWritesHaveOneWinnerAndPreserveAuditWithoutLostConsent()
    {
        var store=new FleetStore(new MemoryConfigurationDatabase());var before=await store.Read(default);var p=Pair();
        var first=await store.Write(before,p.doc,0,"officer","assignment","test",default);
        Assert.Single(first.Audit);Assert.Equal(1,first.Revision);
        var wins=await Task.WhenAll(Enumerable.Range(0,10).Select(async i=>{try{await store.Write(first,first,1,"test","consent",i.ToString(),default);return 1;}catch(FleetConflictException){return 0;}}));
        Assert.Equal(1,wins.Sum());Assert.Equal(2,(await store.Read(default)).Revision);
        await Assert.ThrowsAsync<FleetConflictException>(()=>store.Write(first,first,0,"test","stale","test",default));
    }
    [Theory][InlineData(0,30)][InlineData(121,30)][InlineData(36,0)][InlineData(36,91)]
    public void PolicyBoundsAreEnforced(int months,int days)=>Assert.Throws<ArgumentException>(()=>FleetRules.Validate(new(){ReplacementMonths=months,RequestValidityDays=days}));
    [Fact] public void UnknownOrDuplicateExceptionReasonsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>FleetRules.Validate(new(){ExceptionReasons=["guess"]}));
        Assert.Throws<ArgumentException>(()=>FleetRules.Validate(new(){ExceptionReasons=["family","family"]}));
    }
    [Theory][InlineData("אני מעוניין לעבור לרכב ממסלול ברוכת ילדים, אבל נשארו לי עוד שנה וחצי לחוזה של הרכב הרגיל שלי.","family")][InlineData("רוצה להחליף לרכב חשמלי באמצע הליסינג","electric")][InlineData("רוצה לעבור לרכב רגיל","regular")]
    public void HebrewIntentSelectsOnlyAPreferenceWithoutInventingDates(string text,string category)
    {Assert.True(FleetRules.Category(text,out var actual));Assert.Equal(category,actual);}
}
