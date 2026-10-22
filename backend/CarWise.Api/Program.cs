using CarWise.Api.Accounting;
using Microsoft.AspNetCore.HttpOverrides;
using CarWise.Api.Administration;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Npgsql;

Console.WriteLine("[CarWise] Server process started. Reading saved connection settings...");
var builder=WebApplication.CreateBuilder(args);
var root=Environment.GetEnvironmentVariable("CARWISE_ROOT")??FindRoot();
builder.Configuration.AddJsonFile(Path.Combine(root,".local","appsettings.Development.json"),optional:true,reloadOnChange:false);
// Environment variables override ignored local settings; secrets never enter tracked configuration.
builder.Configuration.AddEnvironmentVariables();
builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"]??"http://0.0.0.0:8000");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=2_000_000);
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase);
builder.Services.Configure<ForwardedHeadersOptions>(options=>{
    options.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
    foreach(var address in (Environment.GetEnvironmentVariable("CARWISE_TRUSTED_PROXIES")??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        options.KnownProxies.Add(System.Net.IPAddress.TryParse(address,out var ip)?ip:throw new InvalidOperationException("Invalid trusted proxy IP"));
});
var keyDirectory=Environment.GetEnvironmentVariable("CARWISE_KEYRING_DIR")??Path.Combine(root,".local","admin-keys");
Directory.CreateDirectory(keyDirectory);
if(!OperatingSystem.IsWindows())File.SetUnixFileMode(keyDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
var protection=builder.Services.AddDataProtection().SetApplicationName("CarWise.Administration").PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
if(OperatingSystem.IsWindows())protection.ProtectKeysWithDpapi();
var connectionProtection=DataProtectionProvider.Create(new DirectoryInfo(keyDirectory),options=>{options.SetApplicationName("CarWise.Administration");if(OperatingSystem.IsWindows())options.ProtectKeysWithDpapi();});
var connectionStore=new ServerConnectionStore(Environment.GetEnvironmentVariable("CARWISE_SERVER_SETTINGS_PATH")??Path.Combine(root,".local","server-settings","supabase.protected.json"),connectionProtection);
var savedConnection=Environment.GetEnvironmentVariable("CARWISE_IGNORE_SAVED_CONNECTION")=="true"?null:(await connectionStore.LoadAsync(CancellationToken.None)).Connection;
var demo=builder.Environment.IsDevelopment() && builder.Configuration.GetValue("CarWise:DemoMode",false);
var supabaseUrl=savedConnection?.Url??builder.Configuration["SUPABASE_URL"]??builder.Configuration["Supabase:Url"];
var supabaseKey=savedConnection?.ServiceKey??builder.Configuration["CARWISE_SUPABASE_KEY"]??builder.Configuration["Supabase:ServiceRoleKey"];
var connectionManager=new ServerConnectionManager(connectionStore,string.IsNullOrWhiteSpace(supabaseUrl)||string.IsNullOrWhiteSpace(supabaseKey)?null:new(supabaseUrl,supabaseKey),builder.Environment.IsDevelopment());
builder.Services.AddSingleton(connectionManager);
if((string.IsNullOrWhiteSpace(supabaseUrl)&&string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("CarWise")))||(!demo&&string.IsNullOrWhiteSpace(supabaseUrl))||(!string.IsNullOrWhiteSpace(supabaseUrl)&&string.IsNullOrWhiteSpace(supabaseKey)))
{
    if(args.Contains("--import-data"))throw new InvalidOperationException("Configure a database before importing data.");
    await InitialSetup.RunAsync(builder,root,connectionManager);return;
}
HttpClient SupabaseClient()
{
    var uri=new Uri((supabaseUrl??"https://unconfigured.invalid").TrimEnd('/')+"/");
    if(uri.Scheme!="https" && !(builder.Environment.IsDevelopment() && uri.IsLoopback))
        throw new InvalidOperationException("Supabase requires HTTPS.");
    var client=new HttpClient{BaseAddress=uri,Timeout=TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("CarWise:SupabaseRequestTimeoutSeconds",30),1,120))};
    if(!string.IsNullOrWhiteSpace(supabaseKey))
    {
        client.DefaultRequestHeaders.Add("apikey",supabaseKey);
        if(!supabaseKey.StartsWith("sb_secret_",StringComparison.Ordinal))client.DefaultRequestHeaders.Authorization=new("Bearer",supabaseKey);
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
var startupTimeout=TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("CarWise:StartupTimeoutSeconds",180),30,900));
ReferenceData reference;
try
{
    if(database is PostgresDatabase && !await repository.HasReferenceDataAsync())await repository.SeedAsync(ReferenceData.FromFiles(dataPath));
    reference=await StartupProgress.RunAsync("Loading reference data from "+repository.Provider,ct=>repository.LoadAsync(ct,count=>Console.WriteLine($"[CarWise] Catalogue official records loaded: {count:N0} (bulk database download is skipped)")),startupTimeout);
}
catch(Exception error)when(database is SupabaseDatabase)
{
    Console.Error.WriteLine(error is TimeoutException or OperationCanceledException?"[CarWise] Supabase startup timed out. Check the connection/network in the local setup page.":"[CarWise] Supabase reference data could not be loaded. Open localhost to check or replace the saved connection.");
    var reason=error is SupabaseRequestException {Status:401 or 403}?
        "Supabase דחה את המפתח או את הרשאותיו. יש לבדוק שהמפתח שייך לפרויקט הנכון ושהוא מפתח שרת בעל הרשאות לנתוני CarWise.":
        error is SupabaseRequestException {Status:404}?
        "טבלאות CarWise לא נמצאו בפרויקט Supabase. יש להחיל את מיגרציית המסד ולייבא את נתוני המערכת.":
        error is OperationCanceledException or TimeoutException or HttpRequestException?
        "החיבור ל־Supabase לא השלים את הבקשה בזמן. המפתח השמור לא נמחק; בדקו שהפרויקט פעיל ושהרשת מאפשרת גישה אליו, ואז נסו שוב ללא הזנה חוזרת של המפתח.":
        "לא ניתן לטעון את נתוני CarWise. בדקו שהסכמה ונתוני המקור יובאו ושרכבי הקטלוג קיימים במאגר הרשמי. החיבור השמור לא נמחק.";
    await InitialSetup.RunAsync(builder,root,connectionManager,reason);return;
}
var defaultProfile=new AiProfile{Model=builder.Configuration["Ai:Model"]??"gpt-4.1-mini",BaseUrl=builder.Configuration["Ai:BaseUrl"]??"https://api.openai.com/v1/"};
var defaultAi=new AiConfiguration(!string.IsNullOrWhiteSpace(builder.Configuration["CARWISE_AI_API_KEY"]??builder.Configuration["Ai:ApiKey"]),defaultProfile,defaultProfile);
var defaultConfig=new ManagedConfiguration(reference.Rules.DeepClone(),reference.Cars,reference.OccupationDocuments,reference.Employees,defaultAi);
var initial=new ConfigurationDocument(0,DateTimeOffset.UtcNow,"system","הגדרות ראשוניות",defaultConfig,[]);
var state=new ConfigurationState(new(initial,reference));
builder.Services.AddSingleton(new OfficialVersions(database,reference));
builder.Services.AddSingleton(state);builder.Services.AddSingleton(new ConfigurationValidator(reference));
builder.Services.AddScoped(sp=>sp.GetRequiredService<ConfigurationState>().Current);
builder.Services.AddScoped(sp=>sp.GetRequiredService<ConfigurationSnapshot>().Data);
builder.Services.AddScoped<PayrollEngine>();
builder.Services.AddSingleton(repository);builder.Services.AddSingleton(database);
builder.Services.AddSingleton<CarImages>();
builder.Services.AddHttpClient<CommonsCarPhotos>(client=>{
    client.Timeout=TimeSpan.FromSeconds(20);client.DefaultRequestHeaders.UserAgent.ParseAdd("CarWise/1.0 (vehicle image import; https://github.com/miriam315/CarWise)");
}).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
builder.Services.AddScoped<DecisionStore>();
builder.Services.AddScoped<FleetStore>();
builder.Services.AddScoped<FleetService>();
builder.Services.AddScoped<IntentAnalytics>();
builder.Services.AddHostedService<IntentRetentionWorker>();
builder.Services.AddSingleton<AiKeyHealth>();
builder.Services.AddSingleton<SecretVault>();builder.Services.AddSingleton<ConfigurationService>();
builder.Services.AddHostedService<ConfigurationRefreshWorker>();
builder.Services.AddSingleton<ExchangeRates>();builder.Services.AddSingleton<UsageReports>();
builder.Services.AddScoped<AiOperationContext>();builder.Services.AddScoped<UsageAccounting>();
builder.Services.AddHttpClient("ExchangeRates",client=>client.Timeout=TimeSpan.FromSeconds(20)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});
builder.Services.AddHostedService<ExchangeRateWorker>();
builder.Services.AddHttpClient("AiCustomProviders").ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,ConnectCallback=PublicAiConnection.ConnectAsync});
builder.Services.AddHttpClient("AiProviders").ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false});
builder.Services.AddScoped<IDataExtractionService>(sp=>{
    var ai=sp.GetRequiredService<ConfigurationSnapshot>().Document.Configuration.Ai;
    return ai.Enabled?new AiExtractionService(new ConfiguredCompletionClient(ai.ProfileFor("extraction"),sp.GetRequiredService<SecretVault>(),sp.GetRequiredService<IHttpClientFactory>(),accounting:sp.GetRequiredService<UsageAccounting>(),health:sp.GetRequiredService<AiKeyHealth>()),ai.ExtractionPrompt):new LocalExtractionService();
});
builder.Services.AddScoped<IResponseNarrationService>(sp=>{
    var ai=sp.GetRequiredService<ConfigurationSnapshot>().Document.Configuration.Ai;
    return ai.Enabled?new AiNarrationService(new ConfiguredCompletionClient(ai.ProfileFor("narration"),sp.GetRequiredService<SecretVault>(),sp.GetRequiredService<IHttpClientFactory>(),accounting:sp.GetRequiredService<UsageAccounting>(),health:sp.GetRequiredService<AiKeyHealth>()),sp.GetRequiredService<ILogger<AiNarrationService>>(),ai.NarrationPrompt):new TemplateNarrationService();
});
builder.Services.AddSingleton(new AccessControl(SupabaseClient(),demo));
builder.Services.AddSingleton(new AuthAccounts(SupabaseClient(),database,state));
builder.Services.AddScoped<AdvisorToolConversation>(sp=>{
    var ai=sp.GetRequiredService<ConfigurationSnapshot>().Document.Configuration.Ai;
    if(!ai.Enabled)return null!;
    var client=new ConfiguredCompletionClient(ai.ProfileFor("extraction"),sp.GetRequiredService<SecretVault>(),sp.GetRequiredService<IHttpClientFactory>(),accounting:sp.GetRequiredService<UsageAccounting>(),health:sp.GetRequiredService<AiKeyHealth>());
    return new(client,sp.GetRequiredService<ReferenceData>(),sp.GetRequiredService<PayrollEngine>(),sp.GetRequiredService<Repository>(),sp.GetRequiredService<ILogger<AdvisorToolConversation>>(),ai.ExtractionPrompt,sp.GetRequiredService<FleetService>(),sp.GetRequiredService<IResponseNarrationService>());
});
builder.Services.AddScoped<AdvisorService>();
builder.Services.AddRateLimiter(options=>{
    options.RejectionStatusCode=429;
    options.AddPolicy("documents",context=>RateLimitPartition.GetFixedWindowLimiter(context.Items["caller"] is Caller caller?caller.UserId:context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=5,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    options.AddPolicy("car-media",context=>RateLimitPartition.GetFixedWindowLimiter(context.Items["caller"] is Caller caller?caller.UserId:context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=90,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    options.AddPolicy("login",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=8,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    options.AddPolicy("intent",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=12,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
var app=builder.Build();
// Trust only the framework's default loopback proxies unless explicitly configured by the host.
app.UseForwardedHeaders();
try
{
    await StartupProgress.RunAsync("Loading saved administration settings",async ct=>{await app.Services.GetRequiredService<ConfigurationService>().RefreshAsync(ct);return true;},startupTimeout);
}
catch(Exception)
{
    Console.Error.WriteLine("[CarWise] Saved administration settings could not be loaded. Check database access and the active official-data version. No default configuration was activated.");
    await app.DisposeAsync();throw new InvalidOperationException("CarWise startup failed while loading administration settings. Check the connection and network, then restart.");
}
app.UseRateLimiter();
app.Use(async(context,next)=>
{
    var originalCancellation=context.RequestAborted;
    using var chatDeadline=context.Request.Path=="/api/chat"?CancellationTokenSource.CreateLinkedTokenSource(originalCancellation):null;
    if(chatDeadline is not null){chatDeadline.CancelAfter(TimeSpan.FromSeconds(55));context.RequestAborted=chatDeadline.Token;}
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    context.Response.Headers["X-Frame-Options"]="DENY";
    context.Response.Headers.ContentSecurityPolicy="frame-ancestors 'none'";
    context.Response.Headers["Referrer-Policy"]="no-referrer";
    if(context.Request.Path.StartsWithSegments("/api"))context.Response.Headers.CacheControl="no-store";
    try
    {
        var path=context.Request.Path.Value??"";
        if(path.StartsWith("/api/",StringComparison.Ordinal))RequestProtection.Validate(context,builder.Environment.IsDevelopment());
        if(path.StartsWith("/api/") && path is not ("/api/health" or "/api/runtime" or "/api/auth/login" or "/api/setup/admin/status" or "/api/setup/admin"))
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
    catch(OperationCanceledException)when(originalCancellation.IsCancellationRequested){}
    catch(Exception error) when(error is AiUnavailableException or HttpRequestException or OperationCanceledException)
    {
        context.RequestAborted=originalCancellation;
        app.Logger.LogWarning("Service operation failed: {ErrorType}, {TransportCategory}",error.GetType().Name,error is HttpRequestException transport?transport.HttpRequestError.ToString():"not-transport");
        context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error=chatDeadline?.IsCancellationRequested==true?"הבקשה לא הושלמה בתוך מגבלת הזמן. לא מוצג חישוב סופי; אפשר לנסות שוב.":"השירות אינו זמין כרגע. אפשר לבחור רכב מהקטלוג או לנסות שוב מאוחר יותר."});
    }
    catch(Exception error)
    {
        app.Logger.LogError("Request failed with {ErrorType}",error.GetType().Name);
        context.Response.StatusCode=500;await context.Response.WriteAsJsonAsync(new{error="אירעה שגיאת שרת. פרטי התקלה נשמרו בשרת ללא חשיפת נתונים."});
    }
    finally{context.RequestAborted=originalCancellation;}
});
app.MapGet("/api/runtime",(IDataExtractionService extraction)=>new{server="ASP.NET Core 10",database=repository.Provider,demoMode=demo,aiMode=extraction.Mode,authenticationRequired=!demo,configurationRevision=state.Current.Document.Revision,requiresSetup=false,instanceId=ServerInstance.Id});
app.MapGet("/api/health",async(ReferenceData current,CancellationToken ct)=>
{
    await repository.HasReferenceDataAsync(ct);
    return Results.Ok(new{status="ok",records=current.OfficialRowCount,method=$"C# decimal / monthly {current.Rules["netMethod"]!["taxYear"]}",database=repository.Provider});
});
app.MapPost("/api/auth/login",async(LoginRequest request,AccessControl access,CancellationToken ct)=>
{
    if(demo)return Results.BadRequest(new{error="מצב הדגמה אינו דורש התחברות"});
    return Results.Ok(await access.LoginAsync(request.Email,request.Password,ct));
}).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
app.MapGet("/api/data",async(HttpContext context,ConfigurationSnapshot snapshot,AccessControl access,SecretVault vault,CancellationToken ct)=>{
    var reference=snapshot.Data;var payslipStatus=await PayslipAvailability.ReadAsync(snapshot.Document.Configuration.Ai,vault,ct);
    var caller=context.Items["caller"] as Caller;
    var currentUser=new{userId=caller?.UserId??"demo",email=caller?.Email??"",displayName=caller?.DisplayName??"",
        role=demo?"demo":caller?.IsAdmin==true?"admin":caller?.IsAgent==true?"agent":"employee",employeeId=caller?.EmployeeId};
    return new{currentUser,fleetVisible=snapshot.Document.Configuration.Fleet.ShowToEmployees,
    cars=reference.Cars.Select(reference.Enrich),employees=reference.Employees.Where(e=>demo || context.Items["caller"] is Caller c && (c.IsAdmin||e.Id==c.EmployeeId)),
    occupations=reference.OccupationDocuments,rules=reference.Rules,officialCount=reference.OfficialRowCount,canAdmin=access.CanAdmin(context),canAgent=demo||context.Items["caller"] is Caller{IsAgent:true},configurationRevision=snapshot.Document.Revision,payslipEnabled=payslipStatus.Enabled,payslipStatus};
});
void AuthorizeEmployee(HttpContext context,AccessControl access,Employee employee)
{
    if(employee is null)throw new ArgumentException("חסר פרופיל עובד");
    access.Employee(context,employee.Id);
    var reference=context.RequestServices.GetRequiredService<ReferenceData>();
    if(!demo && context.Items["caller"] is not Caller{IsAdmin:true} && reference.Employees.SingleOrDefault(e=>e.Id==employee.Id)?.Occupation!=employee.Occupation)
        throw new UnauthorizedAccessException("Forbidden");
}
app.MapPost("/api/profile",async(SimulateRequest request,HttpContext context,AccessControl access,PayrollEngine engine,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);
    var previous=await repository.ConversationAsync(request.Employee.Id,ct);
    var employee=engine.Validate(request.Employee with{AdvisorState=previous?.Employee.AdvisorState,ChatHistoryStart=previous?.Employee.ChatHistoryStart??0});
    if(employee.AdvisorState is {} state)employee=employee with{AdvisorState=state with{AwaitingField=IntakeFields.Missing(employee).FirstOrDefault()}};
    await repository.SaveConversationAsync(employee,employee.AdvisorState?.Target,ct);return employee;
});
app.MapPost("/api/simulate",async(SimulateRequest request,HttpContext context,AccessControl access,AdvisorService advisor,PayrollEngine engine,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);
    var employee=await advisor.RefreshKnownParticipationAsync(request.Employee,ct);
    employee=engine.Validate(employee);
    if(IntakeFields.Missing(employee).Length>0)return SimulationView.Incomplete(employee);
    return SimulationView.Complete(await advisor.SimulateAsync(employee,ct));
});
app.MapGet("/api/tickets",async(HttpContext context,CancellationToken ct)=>
{
    var caller=context.Items["caller"] as Caller;
    var list=await repository.TicketsAsync(demo||caller?.IsAgent==true?null:caller!.EmployeeId,ct);
    return list.Select(TicketView);
});
app.MapGet("/api/history/{employee}",async(string employee,HttpContext context,AccessControl access,ReferenceData reference,CancellationToken ct)=>
{
    access.Employee(context,employee);var saved=await repository.ConversationAsync(employee,ct);
    var messages=await repository.HistoryAsync(employee,ct,saved?.Employee.ChatHistoryStart??0);var state=saved?.Employee.AdvisorState;
    var candidates=state?.AwaitingField=="currentCar"?reference.Cars.Select(car=>car.Id).ToArray():
        saved is not null&&IntakeFields.Missing(saved.Employee).Length==0?state?.CandidateIds:null;
    var last=messages.FindLastIndex(message=>message.Role=="assistant");
    if(last>=0&&candidates is {Length:>0})messages[last]=messages[last] with{Candidates=candidates.Where(id=>id=="waiver"||reference.Cars.Any(car=>car.Id==id)).ToArray()};
    return messages;
});
app.MapGet("/api/conversation/{employee}",async(string employee,HttpContext context,AccessControl access,CancellationToken ct)=>
{
    access.Employee(context,employee);return await repository.ConversationAsync(employee,ct);
});
app.MapPost("/api/chat",async(ChatRequest request,HttpContext context,AccessControl access,AdvisorService advisor,IntentAnalytics intents,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);context.RequestServices.GetRequiredService<AiOperationContext>().ConversationId=request.Employee.Id;
    var result=await advisor.ChatAsync(request,ct,demo||context.Items["caller"] is Caller{IsAdmin:true});
    if(intents.Policy.Enabled)
    {
        var reply=JsonNode.Parse(JsonDefaults.Serialize(result));
        if(reply?["complete"]?.GetValue<bool>()==true)
        {
            var target=reply["target"]?.GetValue<string>();
            if(FleetRules.Category(request.Text,out var category))target="category:"+category;
            else if(target is null&&request.Text.Contains("חשמלי",StringComparison.Ordinal))target="category:electric";
            if(target is not null)await intents.Observe(context,access,request.Employee.Id,target,"explore",ct);
        }
    }
    return result;
});
app.MapPost("/api/chat/new",async(SimulateRequest request,HttpContext context,AccessControl access,AdvisorService advisor,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);
    return new{employee=await advisor.StartNewChatAsync(request.Employee,ct)};
});
app.MapPost("/api/tickets",async(TicketRequest request,HttpContext context,AccessControl access,PayrollEngine engine,ReferenceData reference,CancellationToken ct)=>
{
    AuthorizeEmployee(context,access,request.Employee);
    var stored=await repository.ConversationAsync(request.Employee.Id,ct);
    var e=engine.Validate(request.Employee with{AdvisorState=stored?.Employee.AdvisorState});IntakeFields.RequireComplete(e);
    var car=reference.Cars.SingleOrDefault(c=>c.Id==request.Target)??throw new ArgumentException("בחרו רכב מתוך הקטלוג");
    if(!engine.Eligibility(e,car).Eligible)throw new ArgumentException("אין זכאות לרכב המבוקש");
    e=e with{AdvisorState=new("targetParticipation",car.Id,car.Drive=="חשמלי")};
    var question=car.ParticipationKnown?request.Question?.Trim()??"מהי השתתפות העובד המאושרת ברכב החדש?":DecisionMath.Question(engine,reference,e,car.Id);
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
    if(ParticipationRules.GeneralTicket(ticket.Target))
    {
        var message="הנציג ענה לגבי הרכב שאינו בקטלוג: "+request.Answer.Trim()+"\nאין עדיין חישוב נטו לרכב שאינו בקטלוג. לאחר השלמת הנתונים בחרו את הדגם המדויק ונמשיך מהנתונים האישיים שכבר נשמרו.";
        await repository.ResolveAsync(id,0,request.Answer.Trim(),message,ct);
        await repository.SaveConversationAsync(e with{AdvisorState=new()},null,ct);
        return Results.Ok(new{status="resolved"});
    }
    var engine=context.RequestServices.GetRequiredService<PayrollEngine>();
    if((!IntakeFields.Known(e,"participation")||ticket.Snapshot.AdvisorState?.AwaitingField=="participation")&&e.CurrentCar==ticket.Target)
    {
        e=IntakeFields.Answer(e,"participation",request.Participation);
        e=e with{AdvisorState=(e.AdvisorState??new()) with{AwaitingField=IntakeFields.Missing(e).FirstOrDefault()}};
        var message=$"הנציג ענה: {request.Answer.Trim()}\nההשתתפות ברכב הנוכחי עודכנה ל־{FinancialText.Number(request.Participation)} ₪. אפשר להמשיך בשיחה ובהשוואה.";
        await repository.ResolveAsync(id,request.Participation,request.Answer.Trim(),message,ct);
        await repository.SaveConversationAsync(e,e.AdvisorState.Target,ct);
        return Results.Ok(new{status="resolved"});
    }
    var current=engine.Simulate(e,new Dictionary<string,decimal>{{ticket.Target,request.Participation}});
    var r=current.Results.Single(r=>r.Id==ticket.Target);
    if(!r.Eligible)throw new ArgumentException("הזכאות השתנתה; יש לעדכן את פרטי העובד לפני אישור ההשתתפות");
    var text=$"הנציג ענה: {request.Answer.Trim()}\nהשתתפות מאושרת: {FinancialText.Number(request.Participation)} ₪. "+
        $"חישבתי מחדש עבור {r.Car!.Model}: נטו {FinancialText.Number(r.Net)} ₪; הפרש {FinancialText.Signed(r.Delta)} ₪ בחודש.";
    await repository.ResolveAsync(id,request.Participation,request.Answer.Trim(),text,ct);return Results.Ok(new{status="resolved"});
});
app.MapGet("/api/setup/admin/status",async(HttpContext context,AuthAccounts accounts,CancellationToken ct)=>{
    LocalSetupAccess.Require(context);return new{needsAdmin=!demo&&await accounts.NeedsAdminAsync(ct)};
});
app.MapPost("/api/setup/admin",async(CreateAccount request,HttpContext context,AuthAccounts accounts,CancellationToken ct)=>{
    LocalSetupAccess.Require(context);if(demo)throw new ArgumentException("יצירת חשבונות דורשת מצב התחברות עם Supabase");
    try{return Results.Ok(await accounts.BootstrapAsync(request,ct));}
    catch(ConfigurationConflictException){return Results.Conflict(new{error="הגדרת מנהל כבר מתבצעת. נסו שוב בעוד מספר דקות."});}
}).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
app.MapAdministration(repository.Provider,supabaseUrl,demo);
// Vehicle marketing photos are public assets, never employee documents.
app.MapGet("/media/cars/{id}.jpg",async(string id,CarImages images,HttpContext context,CancellationToken ct)=>{
    var bytes=await images.ReadAsync(id,ct);
    if(bytes is null)return Results.NotFound();
    context.Response.Headers.CacheControl="public,max-age=31536000,immutable";
    context.Response.Headers.ContentSecurityPolicy="default-src 'none'";
    return Results.File(bytes,"image/jpeg");
});
app.MapPayslips();
app.MapDecisionTools(AuthorizeEmployee);
app.MapFleet();
app.MapIntentAnalytics();
var dist=Path.Combine(root,"dist");
if(!Directory.Exists(dist))dist=Path.Combine(AppContext.BaseDirectory,"wwwroot");
if(Directory.Exists(dist))
{
    app.UseStaticFiles(new StaticFileOptions{FileProvider=new PhysicalFileProvider(dist)});
    app.MapFallback((HttpContext context)=>context.Request.Path.StartsWithSegments("/api")?Results.NotFound():Results.File(Path.Combine(dist,"index.html"),"text/html; charset=utf-8"));
}
app.Logger.LogInformation("CarWise ASP.NET Core ready: {Provider}, {Records} official records available, {Loaded} catalogue matches loaded, demo={Demo}",repository.Provider,state.Current.Data.OfficialRowCount,state.Current.Data.Official.Count,demo);
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
