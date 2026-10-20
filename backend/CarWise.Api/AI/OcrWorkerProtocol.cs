using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record OcrWorkerRequest(int Version,int Length,long Revision,LocalPayslipSettings Settings);
public sealed record OcrWorkerResponse(int Version,bool Success,Dictionary<string,decimal?>? Fields=null,string? Source=null,string? Error=null);
