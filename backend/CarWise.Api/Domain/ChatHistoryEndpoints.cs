using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public static class ChatHistoryEndpoints
{
    public static void MapChatHistory(this WebApplication app)=>app.MapGet("/api/history/{employee}/archive",
        async(string employee,int? before,int? limit,HttpContext context,AccessControl access,Repository repository,CancellationToken ct)=>{
            access.Employee(context,employee);
            var saved=await repository.ConversationAsync(employee,ct);
            return await repository.ArchivedHistoryAsync(employee,saved?.Employee.ChatHistoryStart??0,before,limit??50,ct);
        });
}
public sealed record ArchivedMessage(int Index,string Role,string Text,DateTimeOffset? At);
public sealed record ArchivedHistoryPage(ArchivedMessage[] Items,int? NextBefore,int Total);
