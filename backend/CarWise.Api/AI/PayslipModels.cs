namespace CarWise.Api.AI;

public sealed record PayslipBox(int Page,double X,double Y,double Width,double Height,string Kind);
public sealed record PayslipField(decimal? Value,string? Evidence);
public sealed record PayslipResult(Dictionary<string,PayslipField> Fields,List<string> Warnings,long ConfigurationRevision)
{public string Source {get;init;}="local";public Dictionary<string,PayslipBox[]>? Regions {get;init;}}
public static class PayslipFields
{
    public static readonly IReadOnlyDictionary<string,decimal> Limits=new Dictionary<string,decimal>{["salary"]=200000,["commission"]=200000,["bonus"]=200000,["pension"]=20,["fund"]=20,["credits"]=30,["participation"]=20000};
}
