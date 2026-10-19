using CarWise.Api.Accounting;
using CarWise.Api.AI;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Administration;

public static class AdminEndpoints
{
    public static void MapAdministration(this WebApplication app,string provider,string? supabaseUrl,bool demo)
    {
        var group=app.MapGroup("/api/admin");
        group.AddEndpointFilter(async(context,next)=>{
            context.HttpContext.RequestServices.GetRequiredService<AccessControl>().Admin(context.HttpContext);
            try{return await next(context);}
            catch(ConfigurationConflictException){return Results.Conflict(new{error="ההגדרות השתנו על ידי מנהל אחר. טענו את הגרסה העדכנית לפני שמירה."});}
        });
        group.MapGet("/server-connection",(ServerConnectionManager manager,CancellationToken ct)=>manager.ViewAsync(ct));
        group.MapPost("/server-connection",async(SaveServerConnection request,ServerConnectionManager manager,HttpContext context,CancellationToken ct)=>{
            await manager.SaveAsync(request,Actor(context),ct);return LocalSetupAccess.RestartResult(context,app);
        }).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
        group.MapGet("/accounts",async(AuthAccounts accounts,CancellationToken ct)=>new{items=await accounts.ListAsync(ct)});
        group.MapPost("/accounts",(CreateAccount request,AuthAccounts accounts,CancellationToken ct)=>accounts.CreateAsync(request,ct))
            .WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
        group.MapPost("/accounts/{id}/password",async(string id,ResetAccountPassword request,AuthAccounts accounts,CancellationToken ct)=>{await accounts.ResetAsync(id,request.Password,ct);return new{saved=true};})
            .WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
        group.MapGet("/configuration",(ConfigurationService settings,CancellationToken ct)=>settings.ViewAsync(ct));
        group.MapPost("/configuration",async(SaveConfigurationRequest request,ConfigurationService settings,HttpContext context,CancellationToken ct)=>{
            await settings.SaveAsync(request,Actor(context),ct);return await settings.ViewAsync(ct);
        });
        group.MapPost("/restore",async(RestoreConfigurationRequest request,ConfigurationService settings,HttpContext context,CancellationToken ct)=>{
            await settings.RestoreAsync(request,Actor(context),ct);return await settings.ViewAsync(ct);
        });
        group.MapGet("/connections",async(ConfigurationService settings,Repository repository,CancellationToken ct)=>{
            var databaseReady=await repository.HasReferenceDataAsync(ct);
            return new{database=provider,databaseReady,supabaseHost=Uri.TryCreate(supabaseUrl,UriKind.Absolute,out var uri)?uri.Host:null,
                server="ASP.NET Core 10",demoMode=demo,keyStorage="ASP.NET Data Protection",revision=settings.State.Current.Document.Revision,
                note="כתובת Supabase ומפתח השרת נשמרים דרך הניהול; הרשאות הרשת מוגדרות בתשתית האירוח."};
        });
        group.MapPost("/test-ai",async(TestAiRequest request,SecretVault vault,IHttpClientFactory clients,UsageAccounting accounting,CancellationToken ct)=>{
            ConfigurationValidator.ValidateAi(request.Profile);
            if(request.ApiKey is {Length:>8192})throw new ArgumentException("המפתח ארוך מדי");
            try
            {
                var client=new ConfiguredCompletionClient(request.Profile,vault,clients,string.IsNullOrWhiteSpace(request.ApiKey)?null:request.ApiKey.Trim(),accounting);
                var schema=System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}""")!;
                var response=await client.CompleteAsync("connection_test",schema,"Return exactly a JSON object with ok=true. Do not include personal data.","בדיקת חיבור",ct);
                var valid=System.Text.Json.Nodes.JsonNode.Parse(response)?["ok"]?.GetValue<bool>()==true;
                return Results.Ok(new{success=valid,code=valid?"ok":"invalid_output",message=valid?"הספק החזיר תשובה מובנית תקינה":"הספק לא החזיר את המבנה הנדרש"});
            }
            catch(AiProviderException error){return Results.Ok(new{success=false,code=error.Code,status=error.Status,message=error.Message});}
            catch(Exception error)when(error is AiUnavailableException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {return Results.Ok(new{success=false,code="unavailable",message="הבדיקה נכשלה. בדקו מפתח, מודל, תמיכה ב־JSON Schema והרשאת רשת לדומיין הספק."});}
        }).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
        group.MapGet("/usage/conversation/{id}",(string id,UsageReports reports,CancellationToken ct)=>reports.ConversationAsync(id,ct));
        group.MapGet("/usage",(string? after,int? limit,UsageReports reports,CancellationToken ct)=>reports.ReadAsync(after,limit,ct));
        group.MapGet("/exchange-rate",(ExchangeRates rates)=>rates.View());
        group.MapPost("/exchange-rate/refresh",async(ExchangeRates rates,CancellationToken ct)=>{await rates.RefreshAsync(ct);return rates.View();})
            .WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("login"));
        group.MapGet("/official/versions",async(OfficialVersions versions,ConfigurationService settings,CancellationToken ct)=>{
            await settings.RefreshAsync(ct);return new{activeVersionId=settings.State.Current.Document.Configuration.OfficialVersionId,items=await versions.ListAsync(ct)};
        });
        group.MapPost("/official/upload",async(HttpContext context,OfficialVersions versions,CancellationToken ct)=>{
            var bytes=await UploadReader.ReadAsync(context,OfficialVersions.MaxBytes,ct);
            return await versions.UploadAsync(bytes,context.Request.Query["fileName"].ToString(),Actor(context),ct);
        }).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("documents"));
        group.MapGet("/official/versions/{id}/preview",async(string id,OfficialVersions versions,ConfigurationService settings,CancellationToken ct)=>{
            await settings.RefreshAsync(ct);return (await versions.PreviewAsync(id,settings.State.Current.Document.Configuration,ct)).Preview;
        });
        group.MapPost("/official/preview",async(OfficialPreviewRequest request,OfficialVersions versions,ConfigurationService settings,CancellationToken ct)=>{
            await settings.RefreshAsync(ct);return (await versions.PreviewAsync(request.VersionId,settings.State.Current.Document.Configuration,ct,request.Replacements)).Preview;
        });
        group.MapPost("/official/activate",async(ActivateOfficialRequest request,OfficialVersions versions,ConfigurationService settings,HttpContext context,CancellationToken ct)=>{
            await settings.RefreshAsync(ct);
            if(settings.State.Current.Document.Revision!=request.ExpectedRevision)throw new ConfigurationConflictException();
            var (configuration,preview)=await versions.PreviewAsync(request.VersionId,settings.State.Current.Document.Configuration,ct,request.Replacements);
            if(preview.Missing.Count>0)throw new ArgumentException("לא ניתן להפעיל מאגר: חסרות התאמות לרכבי הקטלוג. עדכנו את הקטלוג או העלו קובץ מלא.");
            await settings.SaveAsync(new(request.ExpectedRevision,configuration,request.Note),Actor(context),ct);return await settings.ViewAsync(ct);
        });
        group.MapGet("/official",async(string? q,int? page,string? version,ReferenceData source,OfficialVersions versions,CancellationToken ct)=>{
            var official=version is null?source.Official:await versions.LoadAsync(version,ct);var taxYear=official.Values.First().TaxYear;
            var query=(q??"").Trim();if(query.Length>100)throw new ArgumentException("החיפוש ארוך מדי");
            var number=page??0;if(number<0||number>10000)throw new ArgumentException("עמוד לא תקין");
            var rows=official.Values.Where(r=>r.TaxYear==taxYear && r.VehicleType=="1" &&
                (query.Length==0||r.Key.Contains(query,StringComparison.OrdinalIgnoreCase)||r.Document.GetValueOrDefault("כינוי מסחרי","").Contains(query,StringComparison.OrdinalIgnoreCase)||r.Document.GetValueOrDefault("תוצר","").Contains(query,StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(r=>r.RegistrationYear).ThenBy(r=>r.MakerCode).ThenBy(r=>r.ModelCode).ToList();
            return new{total=rows.Count,page=number,items=rows.Skip(number*20).Take(20).Select(r=>new{r.TaxYear,year=r.RegistrationYear,r.MakerCode,r.ModelCode,model=r.Document.GetValueOrDefault("כינוי מסחרי",""),brand=r.Document.GetValueOrDefault("תוצר",""),description=r.Document.GetValueOrDefault("תאור דגם",""),value=r.Value,drive=source.Rules["valueOfUse"]!["driveField"]![r.Drive]!.GetValue<string>(),price=decimal.Parse(r.Document["מחיר מחירון"],System.Globalization.CultureInfo.InvariantCulture)})};
        });
    }
    private static string Actor(HttpContext context)=>context.Items["caller"] is Caller caller?caller.UserId:"local-development-admin";
}
