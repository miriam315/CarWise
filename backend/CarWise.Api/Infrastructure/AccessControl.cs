using System.Net.Http.Headers;
using System.Text.Json.Nodes;
namespace CarWise.Api.Infrastructure;

public sealed record Caller(string UserId,string EmployeeId,bool IsAgent);
public sealed class AccessControl(HttpClient authClient,bool demoMode)
{
    public bool DemoMode=>demoMode;
    public async Task<Caller?> AuthenticateAsync(HttpContext context,CancellationToken ct)
    {
        if(demoMode)return null;
        var authorization=context.Request.Headers.Authorization.ToString();
        if(!authorization.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException();
        using var request=new HttpRequestMessage(HttpMethod.Get,"auth/v1/user");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",authorization[7..]);
        using var response=await authClient.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)throw new UnauthorizedAccessException();
        var user=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var id=user["id"]?.GetValue<string>();var metadata=user["app_metadata"];
        var employee=metadata?["employee_id"]?.GetValue<string>();
        // app_metadata is administrator-controlled; user_metadata is deliberately never trusted for authorization.
        if(string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(employee))throw new UnauthorizedAccessException();
        return new(id,employee,metadata?["role"]?.GetValue<string>()=="agent");
    }
    public void Employee(HttpContext context,string id)
    {
        if(demoMode)return;
        if(context.Items["caller"] is not Caller caller || caller.EmployeeId!=id)throw new UnauthorizedAccessException("Forbidden");
    }
    public void Agent(HttpContext context)
    {
        if(!demoMode && context.Items["caller"] is not Caller{IsAgent:true})throw new UnauthorizedAccessException("Forbidden");
    }
    public async Task<object> LoginAsync(string email,string password,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(email)||string.IsNullOrWhiteSpace(password))throw new ArgumentException("יש להזין דוא״ל וסיסמה");
        using var response=await authClient.PostAsJsonAsync("auth/v1/token?grant_type=password",new{email,password},ct);
        if(!response.IsSuccessStatusCode)throw new UnauthorizedAccessException();
        var result=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return new{accessToken=result["access_token"]!.GetValue<string>(),expiresIn=result["expires_in"]!.GetValue<int>()};
    }
}
