using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Net.Mail;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
namespace CarWise.Api.Infrastructure;
public sealed record CreateAccount(string Email,string Password,string Role,string? EmployeeId);
public sealed record ResetAccountPassword(string Password);
public sealed record AuthAccount(string Id,string Email,string Role,string? EmployeeId,DateTimeOffset? CreatedAt);
public sealed class AuthAccounts(HttpClient client,IJsonDatabase database,ConfigurationState state)
{
    public async Task<List<AuthAccount>> ListAsync(CancellationToken ct)
    {
        var result=new List<AuthAccount>();
        for(var page=1;page<=1000;page++)
        {
            using var response=await client.GetAsync("auth/v1/admin/users?page="+page+"&per_page=100",ct);
            if(!response.IsSuccessStatusCode)throw new ArgumentException("לא ניתן לקרוא את חשבונות Supabase Auth. בדקו שהוגדר מפתח שרת בעל הרשאות ניהול.");
            var body=await response.Content.ReadFromJsonAsync<JsonNode>(ct);var users=body?["users"]?.AsArray()??throw new ArgumentException("Supabase Auth החזיר מבנה לא תקין");
            result.AddRange(users.Select(Read));if(users.Count<100)return result;
        }
        throw new ArgumentException("מספר החשבונות גדול ממגבלת הניהול");
    }
    private static AuthAccount Read(JsonNode? user)=>new(user!["id"]!.GetValue<string>(),user["email"]?.GetValue<string>()??"",
        user["app_metadata"]?["role"]?.GetValue<string>()??"unassigned",user["app_metadata"]?["employee_id"]?.GetValue<string>(),
        DateTimeOffset.TryParse(user["created_at"]?.GetValue<string>(),out var created)?created:null);
    public static void Password(string password)
    {if(string.IsNullOrEmpty(password)||password.Length is <12 or >128)throw new ArgumentException("יש לבחור סיסמה באורך 12–128 תווים");}
    public void Validate(CreateAccount request)
    {
        if(!MailAddress.TryCreate(request.Email,out var email)||email.Address!=request.Email||request.Email.Length>254)throw new ArgumentException("כתובת הדוא״ל אינה תקינה");
        Password(request.Password);
        if(request.Role is not ("employee" or "agent" or "admin"))throw new ArgumentException("תפקיד חשבון לא נתמך");
        if(request.Role!="admin"&&string.IsNullOrWhiteSpace(request.EmployeeId))throw new ArgumentException("יש לשייך חשבון עובד לפרופיל עובד");
        if(!string.IsNullOrWhiteSpace(request.EmployeeId)&&!state.Current.Data.Employees.Any(e=>e.Id==request.EmployeeId))throw new ArgumentException("פרופיל העובד אינו קיים במערכת");
    }
    public async Task<AuthAccount> CreateAsync(CreateAccount request,CancellationToken ct)
    {
        Validate(request);
        var users=await ListAsync(ct);
        if(users.Any(u=>u.Email.Equals(request.Email,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("כבר קיים חשבון לכתובת הזו. ניתן לאפס את סיסמתו בניהול.");
        if(request.Role!="admin"&&users.Any(u=>u.EmployeeId==request.EmployeeId&&u.Role is "employee" or "agent"))throw new ArgumentException("כבר קיים חשבון לפרופיל העובד הזה");
        using var response=await client.PostAsJsonAsync("auth/v1/admin/users",new{email=request.Email,password=request.Password,email_confirm=true,
            app_metadata=new{role=request.Role,employee_id=string.IsNullOrWhiteSpace(request.EmployeeId)?null:request.EmployeeId}},ct);
        if(!response.IsSuccessStatusCode)throw new ArgumentException("יצירת החשבון נכשלה. בדקו שהכתובת פנויה ושהסיסמה עומדת בדרישות Supabase.");
        return Read(await response.Content.ReadFromJsonAsync<JsonNode>(ct));
    }
    public async Task<bool> NeedsAdminAsync(CancellationToken ct)=>(await ListAsync(ct)).All(u=>u.Role!="admin");
    public async Task<AuthAccount> BootstrapAsync(CreateAccount request,CancellationToken ct)
    {
        if(request.Role!="admin")throw new ArgumentException("החשבון הראשון חייב להיות מנהל");Validate(request);
        if(!await NeedsAdminAsync(ct))throw new UnauthorizedAccessException("Forbidden");
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","auth-bootstrap"}},ct:ct);
        var previous=rows.FirstOrDefault()?["document"];var revision=previous?["revision"]?.GetValue<long>();
        if(previous?["status"]?.GetValue<string>()=="pending"&&DateTimeOffset.Parse(previous["startedAt"]!.GetValue<string>())>DateTimeOffset.UtcNow.AddMinutes(-5))throw new ConfigurationConflictException();
        var marker=new JsonObject{["revision"]=(revision??0)+1,["status"]="pending",["startedAt"]=DateTimeOffset.UtcNow.ToString("O")};
        if(!await database.TryWriteRuleAsync("auth-bootstrap",revision,marker,ct))throw new ConfigurationConflictException();
        try
        {
            // Check again after obtaining the database-backed bootstrap lease.
            if(!await NeedsAdminAsync(ct))throw new UnauthorizedAccessException("Forbidden");
            var account=await CreateAsync(request,ct);marker["status"]="completed";marker["userId"]=account.Id;
            await database.TryWriteRuleAsync("auth-bootstrap",(revision??0)+1,Updated(marker),ct);return account;
        }
        catch
        {
            marker["status"]="failed";
            try{await database.TryWriteRuleAsync("auth-bootstrap",(revision??0)+1,Updated(marker),CancellationToken.None);}catch(Exception){ }
            throw;
        }
    }
    private static JsonNode Updated(JsonObject marker){var copy=marker.DeepClone();copy["revision"]=marker["revision"]!.GetValue<long>()+1;return copy;}
    public async Task ResetAsync(string id,string password,CancellationToken ct)
    {
        Password(password);if(!Guid.TryParse(id,out _))throw new ArgumentException("מזהה חשבון לא תקין");
        using var response=await client.PutAsJsonAsync("auth/v1/admin/users/"+id,new{password},ct);
        if(!response.IsSuccessStatusCode)throw new ArgumentException("איפוס הסיסמה נכשל. בדקו שהחשבון קיים ושהסיסמה תקינה.");
    }
}
