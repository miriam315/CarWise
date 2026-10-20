namespace CarWise.Api.AI;

public sealed record PayslipField(decimal? Value,string? Evidence);
public sealed record PayslipResult(Dictionary<string,PayslipField> Fields,List<string> Warnings,long ConfigurationRevision)
{public string Source {get;init;}="local";}
public static class PayslipFields
{
    public static readonly IReadOnlyDictionary<string,decimal> Limits=new Dictionary<string,decimal>{["salary"]=200000,["commission"]=200000,["bonus"]=200000,["pension"]=20,["fund"]=20,["credits"]=30,["participation"]=20000};
}
