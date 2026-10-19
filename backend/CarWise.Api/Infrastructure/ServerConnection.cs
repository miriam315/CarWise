using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using CarWise.Api.Domain;
namespace CarWise.Api.Infrastructure;

public sealed record ServerConnection(string Url,string ServiceKey);
public sealed record ProtectedConnection(long Revision,string Ciphertext,DateTimeOffset SavedAt,string Actor);
public sealed record SaveServerConnection(string Url,string? ServiceKey,long ExpectedRevision);
public sealed class ServerConnectionStore(string path,IDataProtectionProvider protection)
{
    private readonly IDataProtector protector=protection.CreateProtector("CarWise.ServerConnection.v1");
    private readonly SemaphoreSlim gate=new(1,1);
    public async Task<(ProtectedConnection? Metadata,ServerConnection? Connection)> LoadAsync(CancellationToken ct)
    {
        if(!File.Exists(path))return(null,null);
        var metadata=JsonDefaults.Read<ProtectedConnection>(await File.ReadAllTextAsync(path,ct));
        try{return(metadata,JsonDefaults.Read<ServerConnection>(protector.Unprotect(metadata.Ciphertext)));}
        catch(CryptographicException){return(metadata,null);}
    }
    public async Task<ProtectedConnection> SaveAsync(ServerConnection connection,long expectedRevision,string actor,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current=await LoadAsync(ct);if((current.Metadata?.Revision??0)!=expectedRevision)throw new Administration.ConfigurationConflictException();
            var next=new ProtectedConnection(expectedRevision+1,protector.Protect(JsonDefaults.Serialize(connection)),DateTimeOffset.UtcNow,actor);
            var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;
            if(OperatingSystem.IsWindows())Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var temporary=Path.Combine(directory,".connection-"+Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary,JsonDefaults.Serialize(next),ct);
                if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                File.Move(temporary,path,true);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return next;
        }
        finally{gate.Release();}
    }
}
public sealed class ServerConnectionManager(ServerConnectionStore store,ServerConnection? active,bool development,Func<HttpClient>? clientFactory=null)
{
    public static Uri ValidateUrl(string value,bool development)
    {
        if(!Uri.TryCreate(value.Trim(),UriKind.Absolute,out var uri)||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/")throw new ArgumentException("נדרשת כתובת הפרויקט בלבד, ללא נתיב, מפתח או פרמטרים");
        if(development&&uri.IsLoopback&&uri.Scheme=="http")return uri;
        if(uri.Scheme!="https"||!uri.IsDefaultPort||!System.Text.RegularExpressions.Regex.IsMatch(uri.Host,@"^[a-z0-9-]{10,63}\.supabase\.co$"))throw new ArgumentException("נדרשת כתובת HTTPS של פרויקט Supabase, למשל https://PROJECT.supabase.co");
        return uri;
    }
    public async Task<object> ViewAsync(CancellationToken ct)
    {
        var saved=await store.LoadAsync(ct);return new{url=saved.Connection?.Url??active?.Url??"",hasKey=!string.IsNullOrWhiteSpace(saved.Connection?.ServiceKey??active?.ServiceKey),
            persisted=saved.Connection is not null,revision=saved.Metadata?.Revision??0,savedAt=saved.Metadata?.SavedAt,actor=saved.Metadata?.Actor,
            needsNewKey=saved.Metadata is not null&&saved.Connection is null,source=saved.Connection is not null?"saved":active is not null?"environment":"unconfigured"};
    }
    public async Task SaveAsync(SaveServerConnection request,string actor,CancellationToken ct)
    {
        var uri=ValidateUrl(request.Url,development);var saved=await store.LoadAsync(ct);var current=saved.Connection??active;
        var key=request.ServiceKey?.Trim();
        if(string.IsNullOrWhiteSpace(key))
        {
            if(current is null||new Uri(current.Url).GetLeftPart(UriPartial.Authority)!=uri.GetLeftPart(UriPartial.Authority))throw new ArgumentException("לחיבור ראשון או לפרויקט אחר נדרש מפתח שרת חדש");
            key=current.ServiceKey;
        }
        if(key.Length is <16 or >8192||key.Any(char.IsWhiteSpace)||key.StartsWith("sb_publishable_",StringComparison.Ordinal))throw new ArgumentException("יש להזין Secret key או service_role של השרת, לא מפתח Publishable");
        if(key.Count(c=>c=='.')==2)
        {
            try
            {
                var payload=key.Split('.')[1].Replace('-','+').Replace('_','/');payload=payload.PadRight((payload.Length+3)/4*4,'=');
                var role=JsonNode.Parse(Convert.FromBase64String(payload))?["role"]?.GetValue<string>();
                if(role!="service_role")throw new ArgumentException("נדרש מפתח service_role; מפתח משתמש או anon אינו מתאים לחיבור השרת");
            }
            catch(Exception error)when(error is FormatException or System.Text.Json.JsonException){throw new ArgumentException("מבנה מפתח השרת אינו תקין");}
        }
        using var client=clientFactory?.Invoke()??new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});
        client.Timeout=TimeSpan.FromSeconds(20);client.BaseAddress=uri;client.DefaultRequestHeaders.Add("apikey",key);
        if(!key.StartsWith("sb_secret_",StringComparison.Ordinal))client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
        try
        {
            var db=new SupabaseDatabase(client);
            var rules=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","active"}},limit:1,ct:ct);
            var cars=await db.ReadAsync("carwise_cars",limit:1,ct:ct);
            if(rules.Count!=1||cars.Count==0)throw new ArgumentException("החיבור זמין, אך נתוני CarWise חסרים בפרויקט. יש להכין את הסכמה ולייבא את נתוני המערכת לפני שמירת החיבור.");
        }
        catch(Exception error)when(error is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {throw new ArgumentException("בדיקת Supabase נכשלה. בדקו כתובת, מפתח שרת, הרשאת רשת וקיום סכמת CarWise. החיבור הקודם לא הוחלף.");}
        await store.SaveAsync(new(uri.ToString().TrimEnd('/'),key),request.ExpectedRevision,actor,ct);
    }
}
public static class ServerInstance {public static readonly string Id=Guid.NewGuid().ToString("N");}
public static class LocalSetupAccess
{
    public static bool Allowed(HttpContext context)
    {
        var environment=context.RequestServices?.GetService<IHostEnvironment>();
        if(environment is not null&&!environment.IsDevelopment()&&Environment.GetEnvironmentVariable("CARWISE_ALLOW_LOCAL_SETUP")!="true")return false;
        return context.Connection.RemoteIpAddress is {} address&&IPAddress.IsLoopback(address)&&
            (context.Request.Host.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||IPAddress.TryParse(context.Request.Host.Host,out var host)&&IPAddress.IsLoopback(host));
    }
    public static void Require(HttpContext context){if(!Allowed(context))throw new UnauthorizedAccessException("Forbidden");}
    public static object RestartResult(HttpContext context,WebApplication app)
    {
        var automatic=Environment.GetEnvironmentVariable("CARWISE_MANAGED_LIFECYCLE")=="1";
        if(automatic)context.Response.OnCompleted(()=>{Environment.ExitCode=75;app.Lifetime.StopApplication();return Task.CompletedTask;});
        return new{saved=true,restartRequired=true,automaticRestart=automatic,previousInstanceId=ServerInstance.Id,message="החיבור נבדק ונשמר מוצפן בשרת. ההפעלות הבאות ישתמשו בו אוטומטית."};
    }
}
