using System.Threading.RateLimiting;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Npgsql;

var builder=WebApplication.CreateBuilder(args);
var root=Environment.GetEnvironmentVariable("CARWISE_ROOT")??FindRoot();
builder.Configuration.AddJsonFile(Path.Combine(root,".local","appsettings.Development.json"),optional:true,reloadOnChange:false);
// Environment variables override ignored local settings; secrets never enter tracked configuration.
builder.Configuration.AddEnvironmentVariables();
builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"]??"http://0.0.0.0:8000");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=100_000);
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase);
var demo=builder.Environment.IsDevelopment() && builder.Configuration.GetValue("CarWise:DemoMode",true);
var supabaseUrl=builder.Configuration["SUPABASE_URL"]??builder.Configuration["Supabase:Url"];
var supabaseKey=builder.Configuration["CARWISE_SUPABASE_KEY"]??builder.Configuration["Supabase:ServiceRoleKey"];
if(!demo && (string.IsNullOrWhiteSpace(supabaseUrl)||string.IsNullOrWhiteSpace(supabaseKey)))
    throw new InvalidOperationException("Production requires Supabase configuration and authentication. Configure SUPABASE_URL and CARWISE_SUPABASE_KEY securely.");
HttpClient SupabaseClient()
{
    var uri=new Uri((supabaseUrl??"https://unconfigured.invalid").TrimEnd('/')+"/");
    if(uri.Scheme!="https" && !(builder.Environment.IsDevelopment() && uri.IsLoopback))
        throw new InvalidOperationException("Supabase requires HTTPS.");
    var client=new HttpClient{BaseAddress=uri,Timeout=TimeSpan.FromSeconds(30)};
    if(!string.IsNullOrWhiteSpace(supabaseKey))
    {
        client.DefaultRequestHeaders.Add("apikey",supabaseKey);
        client.DefaultRequestHeaders.Authorization=new("Bearer",supabaseKey);
    }
    return client;
}
IJsonDatabase database;
if(!string.IsNullOrWhiteSpace(supabaseUrl))
{
    if(string.IsNullOrWhiteSpace(supabaseKey))throw new InvalidOperationException("Missing Supabase server key.");
    database=new SupabaseDatabase(SupabaseClient());
}
else
{
    var connection=builder.Configuration.GetConnectionString("CarWise");
    if(string.IsNullOrWhiteSpace(connection))throw new InvalidOperationException("No PostgreSQL connection. Run npm run db:start, or configure ConnectionStrings__CarWise.");
    var source=NpgsqlDataSource.Create(connection);
    builder.Services.AddSingleton(source);
    var pg=new PostgresDatabase(source);
    var migration=Path.Combine(root,"supabase","migrations","202610190001_carwise.sql");
    if(!File.Exists(migration))migration=Path.Combine(AppContext.BaseDirectory,"migrations","202610190001_carwise.sql");
    await pg.MigrateAsync(migration);database=pg;
}
var repository=new Repository(database);
var dataPath=Path.Combine(root,"data");
if(!Directory.Exists(dataPath))dataPath=Path.Combine(AppContext.BaseDirectory,"data");
if(args.Contains("--import-data"))
{
    await repository.SeedAsync(ReferenceData.FromFiles(dataPath));
    Console.WriteLine("Imported official vehicle data, catalogue, employees, occupations and active rules into "+repository.Provider);
    return;
}
if(database is PostgresDatabase && !await repository.HasReferenceDataAsync())await repository.SeedAsync(ReferenceData.FromFiles(dataPath));
var reference=await repository.LoadAsync();
builder.Services.AddSingleton(reference);builder.Services.AddSingleton<PayrollEngine>();
builder.Services.AddSingleton(repository);builder.Services.AddSingleton(database);
var aiKey=builder.Configuration["CARWISE_AI_API_KEY"]??builder.Configuration["Ai:ApiKey"];
if(!string.IsNullOrWhiteSpace(aiKey))
{
    var aiUri=new Uri((builder.Configuration["Ai:BaseUrl"]??"https://api.openai.com/v1/").TrimEnd('/')+"/");
    if(aiUri.Scheme!="https")throw new InvalidOperationException("AI provider requires HTTPS.");
    var client=new HttpClient{BaseAddress=aiUri,Timeout=TimeSpan.FromSeconds(35)};
    client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",aiKey);
    builder.Services.AddSingleton(new AiGateway(client,builder.Configuration["Ai:Model"]??"gpt-4.1-mini"));
    builder.Services.AddSingleton<IDataExtractionService,AiExtractionService>();
    builder.Services.AddSingleton<IResponseNarrationService,AiNarrationService>();
}
else
{
    builder.Services.AddSingleton<IDataExtractionService,LocalExtractionService>();
    builder.Services.AddSingleton<IResponseNarrationService,TemplateNarrationService>();
}
builder.Services.AddSingleton(new AccessControl(SupabaseClient(),demo));
builder.Services.AddSingleton<AdvisorService>();
builder.Services.AddRateLimiter(options=>{
    options.RejectionStatusCode=429;
    options.AddPolicy("login",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=8,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
var app=builder.Build();
app.UseRateLimiter();
app.Use(async(context,next)=>
{
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    context.Response.Headers["Referrer-Policy"]="no-referrer";
    if(context.Request.Path.StartsWithSegments("/api"))context.Response.Headers.CacheControl="no-store";
    try
    {
        var path=context.Request.Path.Value??"";
        if(path.StartsWith("/api/") && path is not ("/api/health" or "/api/runtime" or "/api/auth/login"))
            context.Items["caller"]=await app.Services.GetRequiredService<AccessControl>().AuthenticateAsync(context,context.RequestAborted);
        await next(context);
    }
    catch(UnauthorizedAccessException error)
    {
        context.Response.StatusCode=error.Message=="Forbidden"?403:401;
        await context.Response.WriteAsJsonAsync(new{error="נדרשת התחברות או שאין הרשאה לפעולה זו"});
    }
    catch(Exception error) when(error is ArgumentException or JsonException or BadHttpRequestException)
    {
        context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error=error is ArgumentException?error.Message:"בקשה לא תקינה"});
    }
    catch(Exception error) when(error is AiUnavailableException or HttpRequestException or TaskCanceledException)
    {
        context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="שירות ה־AI אינו זמין. אפשר לבחור רכב ישירות מהקטלוג ולעדכן נתונים בטופס."});
    }
    catch(Exception error)
    {
        app.Logger.LogError("Request failed with {ErrorType}",error.GetType().Name);
        context.Response.StatusCode=500;await context.Response.WriteAsJsonAsync(new{error="אירעה שגיאת שרת. פרטי התקלה נשמרו בשרת ללא חשיפת נתונים."});
    }
});
app.MapGet("/api/runtime",(IDataExtractionService extraction)=>new{server="ASP.NET Core 10",database=repository.Provider,demoMode=demo,aiMode=extraction.Mode,authenticationRequired=!demo});
app.MapGet("/api/health",async(CancellationToken ct)=>
{
    await repository.HasReferenceDataAsync(ct);
    return Results.Ok(new{status="ok",records=reference.Official.Count,method="C# decimal / monthly 2025",database=repository.Provider});
});
app.MapPost("/api/auth/login",async(LoginRequest request,AccessControl access,CancellationToken ct)=>
{
    if(demo)return Results.BadRequest(new{error="מצב הדגמה אינו דורש התחברות"});
    return Results.Ok(await access.LoginAsync(request.Email,request.Password,ct));
}).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
app.MapGet("/api/data",(HttpContext context)=>new{
    cars=reference.Cars.Select(reference.Enrich),employees=reference.Employees.Where(e=>demo || context.Items["caller"] is Caller c && e.Id==c.EmployeeId),
    occupations=reference.OccupationDocuments,rules=reference.Rules,officialCount=reference.Official.Count});
void AuthorizeEmployee(HttpContext context,AccessControl access,Employee employee)
{
    access.Employee(context,employee.Id);
    if(!demo && reference.Employees.SingleOrDefault(e=>e.Id==employee.Id)?.Occupation!=employee.Occupation)
        throw new UnauthorizedAccessException("Forbidden");
}
app.MapPost("/api/profile",async(SimulateRequest request,HttpContext context,AccessControl access,PayrollEngine engine,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);
    var employee=engine.Validate(request.Employee);await repository.SaveConversationAsync(employee,null,ct);return employee;
});
app.MapPost("/api/simulate",async(SimulateRequest request,HttpContext context,AccessControl access,AdvisorService advisor,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);return await advisor.SimulateAsync(request.Employee,ct);
});
app.MapGet("/api/tickets",async(HttpContext context,CancellationToken ct)=>
{
    var caller=context.Items["caller"] as Caller;
    var list=await repository.TicketsAsync(demo||caller?.IsAgent==true?null:caller!.EmployeeId,ct);
    return list.Select(TicketView);
});
app.MapGet("/api/history/{employee}",async(string employee,HttpContext context,AccessControl access,CancellationToken ct)=>
{
    access.Employee(context,employee);return await repository.HistoryAsync(employee,ct);
});
app.MapGet("/api/conversation/{employee}",async(string employee,HttpContext context,AccessControl access,CancellationToken ct)=>
{
    access.Employee(context,employee);return await repository.ConversationAsync(employee,ct);
});
app.MapPost("/api/chat",async(ChatRequest request,HttpContext context,AccessControl access,AdvisorService advisor,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);return await advisor.ChatAsync(request,ct);
});
app.MapPost("/api/tickets",async(TicketRequest request,HttpContext context,AccessControl access,PayrollEngine engine,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);var e=engine.Validate(request.Employee);
    var car=reference.Cars.SingleOrDefault(c=>c.Id==request.Target)??throw new ArgumentException("בחרו רכב מתוך הקטלוג");
    if(!engine.Eligibility(e,car).Eligible)throw new ArgumentException("אין זכאות לרכב המבוקש");
    var question=request.Question?.Trim()??"מהי השתתפות העובד המאושרת ברכב החדש?";
    if(question.Length is 0 or >2000)throw new ArgumentException("יש להזין שאלה עד 2,000 תווים");
    var t=await repository.CreateTicketAsync(e,car.Id,question,ct);return new{id=t.Id,status=t.Status};
});
app.MapPost("/api/tickets/{id:guid}",async(Guid id,AnswerRequest request,HttpContext context,AccessControl access,AdvisorService advisor,CancellationToken ct)=>
{
    access.Agent(context);
    if(request.Participation<0 || request.Participation>20000 || string.IsNullOrWhiteSpace(request.Answer) || request.Answer.Length>2000)
        throw new ArgumentException("נדרשים סכום השתתפות תקין ותשובה עד 2,000 תווים");
    var ticket=await repository.TicketAsync(id,ct);
    if(ticket is null)return Results.NotFound(new{error="הפנייה לא נמצאה"});
    var conversation=await repository.ConversationAsync(ticket.EmployeeId,ct);
    var e=conversation?.Employee??ticket.Snapshot;
    var engine=app.Services.GetRequiredService<PayrollEngine>();
    var current=engine.Simulate(e,new Dictionary<string,decimal>{{ticket.Target,request.Participation}});
    var r=current.Results.Single(r=>r.Id==ticket.Target);
    if(!r.Eligible)throw new ArgumentException("הזכאות השתנתה; יש לעדכן את פרטי העובד לפני אישור ההשתתפות");
    var text=$"הנציג ענה: {request.Answer.Trim()}\nהשתתפות מאושרת: {FinancialText.Number(request.Participation)} ₪. "+
        $"חישבתי מחדש עבור {r.Car!.Model}: נטו {FinancialText.Number(r.Net)} ₪; הפרש {FinancialText.Signed(r.Delta)} ₪ בחודש.";
    await repository.ResolveAsync(id,request.Participation,request.Answer.Trim(),text,ct);return Results.Ok(new{status="resolved"});
});
var dist=Path.Combine(root,"dist");
if(!Directory.Exists(dist))dist=Path.Combine(AppContext.BaseDirectory,"wwwroot");
if(Directory.Exists(dist))
{
    app.UseStaticFiles(new StaticFileOptions{FileProvider=new PhysicalFileProvider(dist)});
    app.MapFallback((HttpContext context)=>context.Request.Path.StartsWithSegments("/api")?Results.NotFound():Results.File(Path.Combine(dist,"index.html"),"text/html; charset=utf-8"));
}
app.Logger.LogInformation("CarWise ASP.NET Core ready: {Provider}, {Records} official records, demo={Demo}",repository.Provider,reference.Official.Count,demo);
await app.RunAsync();

static string FindRoot()
{
    var current=new DirectoryInfo(Directory.GetCurrentDirectory());
    while(current is not null){if(File.Exists(Path.Combine(current.FullName,"package.json"))&&Directory.Exists(Path.Combine(current.FullName,"data")))return current.FullName;current=current.Parent;}
    return AppContext.BaseDirectory;
}
static object TicketView(Ticket t)=>new{id=t.Id,employee=JsonDefaults.Serialize(t.Snapshot),target=t.Target,question=t.Question,status=t.Status,answer=t.Answer,participation=t.Participation,created=t.Created};
public sealed record LoginRequest(string Email,string Password);
public partial class Program;
