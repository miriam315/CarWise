namespace CarWise.Api.Administration;

public sealed record LocalPayslipSettings
{
    public string Languages {get;init;}="heb+eng";
    public int MaxPages {get;init;}=3;
    public int TimeoutSeconds {get;init;}=60;
    public int MinimumConfidence {get;init;}=75;
}
