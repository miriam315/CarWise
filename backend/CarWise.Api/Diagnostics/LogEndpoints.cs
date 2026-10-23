using System.Text;
using CarWise.Api.Domain;
namespace CarWise.Api.Diagnostics;

public static class LogEndpoints
{
    public static void MapApplicationLogs(this RouteGroupBuilder admin)
    {
        admin.MapGet("/logs",(string? level,string? category,string? q,DateTimeOffset? from,DateTimeOffset? to,long? before,int? limit,ApplicationLogStore store)=>
            store.Read(new(level,category,q,from,to,before,limit??100)));
        admin.MapGet("/logs/export",(string? level,string? category,string? q,DateTimeOffset? from,DateTimeOffset? to,ApplicationLogStore store)=>{
            var page=store.Read(new(level,category,q,from,to,Limit:ApplicationLogStore.Capacity),export:true);
            return Results.File(Encoding.UTF8.GetBytes(string.Join('\n',page.Items.Select(JsonDefaults.Serialize))),"application/x-ndjson","CarWise-logs-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+".jsonl");
        });
    }
}
