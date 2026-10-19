using System.Net.Http.Headers;
using System.Text.Json.Nodes;
namespace CarWise.Api.Infrastructure;

public sealed record Caller(string UserId,string EmployeeId,bool IsAgent,bool IsAdmin=false);
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
        return ReadCaller(user);
    }
    private static Caller ReadCaller(JsonNode user)
    {
        var id=user["id"]?.GetValue<string>();var metadata=user["app_metadata"];var role=metadata?["role"]?.GetValue<string>();
        var employee=metadata?["employee_id"]?.GetValue<string>();
        if(string.IsNullOrWhiteSpace(id)||role is not ("employee" or "agent" or "admin")||(role!="admin"&&string.IsNullOrWhiteSpace(employee)))throw new UnauthorizedAccessException();
        if(DateTimeOffset.TryParse(user["banned_until"]?.GetValue<string>(),out var banned)&&banned>DateTimeOffset.UtcNow)throw new UnauthorizedAccessException();
        return new(id,employee??"",role is "agent" or "admin",role=="admin");
    }
    public void Employee(HttpContext context,string id)
    {
        if(demoMode||context.Items["caller"] is Caller{IsAdmin:true})return;
        if(context.Items["caller"] is not Caller caller || caller.EmployeeId!=id)throw new UnauthorizedAccessException("Forbidden");
    }
    public void Agent(HttpContext context)
    {
        if(!demoMode && context.Items["caller"] is not Caller{IsAgent:true})throw new UnauthorizedAccessException("Forbidden");
    }
    public bool CanAdmin(HttpContext context) => demoMode
        ? LocalSetupAccess.Allowed(context)
        : context.Items["caller"] is Caller{IsAdmin:true};
    public void Admin(HttpContext context)
    {if(!CanAdmin(context))throw new UnauthorizedAccessException("Forbidden");}
    public async Task<object> LoginAsync(string email,string password,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(email)||string.IsNullOrWhiteSpace(password))throw new ArgumentException("יש להזין דוא״ל וסיסמה");
        using var response=await authClient.PostAsJsonAsync("auth/v1/token?grant_type=password",new{email,password},ct);
        if(!response.IsSuccessStatusCode)throw new UnauthorizedAccessException();
        var result=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var token=result["access_token"]!.GetValue<string>();
        using var userRequest=new HttpRequestMessage(HttpMethod.Get,"auth/v1/user");userRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var userResponse=await authClient.SendAsync(userRequest,ct);if(!userResponse.IsSuccessStatusCode)throw new UnauthorizedAccessException();
        var caller=ReadCaller(JsonNode.Parse(await userResponse.Content.ReadAsStringAsync(ct))!);
        return new{accessToken=token,expiresIn=result["expires_in"]!.GetValue<int>(),role=caller.IsAdmin?"admin":caller.IsAgent?"agent":"employee"};
    }
}
