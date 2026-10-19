using System.Collections.Concurrent;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record AiKeyFailure(string Code,DateTimeOffset RetryAt);
/// <summary>Process-local circuit breakers; no key material is retained here.</summary>
public sealed class AiKeyHealth(TimeProvider? clock=null)
{
    private readonly TimeProvider time=clock??TimeProvider.System;
    private readonly ConcurrentDictionary<string,AiKeyFailure> failures=new();
    private static string Id(AiProfile profile,string reference)=>new Uri(profile.BaseUrl).Host+"/"+profile.Model+"/"+reference;
    public AiKeyFailure? Get(AiProfile profile,string reference)
    {
        var id=Id(profile,reference);
        if(!failures.TryGetValue(id,out var failure))return null;
        return failure.RetryAt>time.GetUtcNow()?failure:null;
    }
    public void Reject(AiProfile profile,string reference,AiProviderException error)
    {
        // Expired entries are removed during writes, keeping churn from growing the map indefinitely.
        foreach(var pair in failures.Where(p=>p.Value.RetryAt<=time.GetUtcNow()).ToArray())failures.TryRemove(pair.Key,out _);
        var seconds=error.RetryAfterSeconds??(error.Status is 401 or 403 or 402||error.Code is "credit_balance_exhausted" or "insufficient_quota"?3600:60);
        failures[Id(profile,reference)]=new(error.Code,time.GetUtcNow().AddSeconds(Math.Clamp(seconds,1,86400)));
    }
    public void Success(AiProfile profile,string reference)=>failures.TryRemove(Id(profile,reference),out _);
}
