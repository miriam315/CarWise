using Microsoft.Extensions.FileProviders;
using CarWise.Api.Administration;
namespace CarWise.Api.Infrastructure;
public static class InitialSetup
{
    public static async Task RunAsync(WebApplicationBuilder builder,string root,ServerConnectionManager manager)
    {
        var app=builder.Build();
        app.UseForwardedHeaders();
        app.Use(async(context,next)=>{
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["X-Frame-Options"]="DENY";context.Response.Headers["X-Content-Type-Options"]="nosniff";
            context.Response.Headers.ContentSecurityPolicy="frame-ancestors 'none'";
            try
            {
                if(context.Request.Path.StartsWithSegments("/api")){LocalSetupAccess.Require(context);RequestProtection.Validate(context,builder.Environment.IsDevelopment());}
                await next(context);
            }
            catch(UnauthorizedAccessException){context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{error="הגדרה ראשונית מותרת רק מהמחשב שמריץ את השרת, דרך localhost."});}
            catch(ConfigurationConflictException){context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new{error="ההגדרות השתנו. טענו מחדש לפני שמירה."});}
            catch(ArgumentException error){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error=error.Message});}
            catch(Exception){context.Response.StatusCode=500;await context.Response.WriteAsJsonAsync(new{error="לא ניתן לשמור את החיבור. בדקו הרשאות לתיקיית ההגדרות של השרת."});}
        });
        app.MapGet("/api/runtime",()=>new{server="ASP.NET Core 10",requiresSetup=true,authenticationRequired=false,instanceId=ServerInstance.Id});
        app.MapGet("/api/health",()=>new{status="setup-required"});
        app.MapGet("/api/setup/connection",(CancellationToken ct)=>manager.ViewAsync(ct));
        app.MapPost("/api/setup/connection",async(SaveServerConnection request,HttpContext context,CancellationToken ct)=>{
            await manager.SaveAsync(request,"local-initial-setup",ct);return LocalSetupAccess.RestartResult(context,app);
        });
        var dist=Path.Combine(root,"dist");if(!Directory.Exists(dist))dist=Path.Combine(AppContext.BaseDirectory,"wwwroot");
        if(Directory.Exists(dist))
        {
            app.UseStaticFiles(new StaticFileOptions{FileProvider=new PhysicalFileProvider(dist)});
            app.MapFallback((HttpContext context)=>context.Request.Path.StartsWithSegments("/api")?Results.NotFound():Results.File(Path.Combine(dist,"index.html"),"text/html; charset=utf-8"));
        }
        app.Logger.LogInformation("Supabase setup required. Open http://localhost:8000 on this computer; credentials are entered in the setup page.");
        await app.RunAsync();
    }
}
