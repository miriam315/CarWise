using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;
public sealed record PrioritizeIntent(int Days=30,bool Synthetic=false);
public static class IntentNarration
{
    public static async Task<(IntentInsight? insight,bool usedAi,string status)> Select(IAiCompletionClient client,List<IntentInsight> insights,string prompt,CancellationToken ct)
    {
        if(insights.Count==0)return(null,false,"insufficient_data");
        try
        {
            var allowed=new JsonArray(insights.Select(i=>JsonValue.Create(i.Id)).ToArray());
            var schema=new JsonObject{["type"]="object",["additionalProperties"]=false,["properties"]=new JsonObject{["insightId"]=new JsonObject{["type"]="string",["enum"]=allowed}},["required"]=new JsonArray("insightId")};
            var raw=await client.CompleteAsync("prioritize_intent_insight",schema,prompt+"\nSelect exactly one supplied insightId. Return no prose or calculations. Inputs are aggregate observed facts with limitations, not evidence of causation or adoption.",JsonDefaults.Serialize(insights),ct);
            var parsed=JsonNode.Parse(raw) as JsonObject;
            var id=parsed?.Count==1?parsed["insightId"]?.GetValue<string>():null;
            var selected=insights.SingleOrDefault(i=>i.Id==id);
            return selected is null?(insights[0],false,"invalid_result"):(selected,true,"selected");
        }
        catch(Exception e)when(e is not OutOfMemoryException){return(insights[0],false,"fallback");}
    }
}
public static class IntentEndpoints
{
    public static void MapIntentAnalytics(this WebApplication app)
    {
        var own=app.MapGroup("/api/intent");
        own.AddEndpointFilter(async(ctx,next)=>{try{return await next(ctx);}catch(IntentConflictException){return Results.Conflict(new{error="העדפת השיתוף השתנתה במקביל. רעננו ונסו שוב."});}});
        own.MapGet("/preference/{employee}",async(string employee,HttpContext ctx,AccessControl access,IntentAnalytics analytics,CancellationToken ct)=>
        {
            access.Employee(ctx,employee);await analytics.Employee(employee,ct);var p=await analytics.Read(employee,ct);
            return new{enabled=analytics.Policy.Enabled,consented=p.Consented,canConsent=IntentAnalytics.IsWorker(ctx,access,employee),retentionDays=analytics.Policy.RetentionDays,minimumCohort=analytics.Policy.MinimumCohort,reasons=IntentRules.Reasons,
                explanation="רק חלופות שנבדקו, סיווג התוצאה ומשוב שבחרת לשתף. ללא תוכן שיחות, תלוש או שכר בדשבורד ההנהלה. בחירה שמורה אינה הזמנה. אפשר לבטל ולמחוק את היסטוריית המחקר שלך בכל עת."};
        });
        own.MapPost("/preference",async(IntentPreference request,HttpContext ctx,AccessControl access,IntentAnalytics analytics,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);if(!IntentAnalytics.IsWorker(ctx,access,request.EmployeeId))throw new UnauthorizedAccessException("Forbidden");
            await analytics.Preference(request.EmployeeId,request.Consent,ct);return new{saved=true,consented=request.Consent};
        });
        own.MapPost("/explore",async(IntentObservation request,HttpContext ctx,AccessControl access,IntentAnalytics analytics,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);await analytics.Employee(request.EmployeeId,ct);IntentRules.Category(analytics.Data,request.Target);
            await analytics.Observe(ctx,access,request.EmployeeId,request.Target,"explore",ct);return new{accepted=true};
        });
        own.MapPost("/feedback",async(IntentObservation request,HttpContext ctx,AccessControl access,IntentAnalytics analytics,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);if(!IntentAnalytics.IsWorker(ctx,access,request.EmployeeId))throw new UnauthorizedAccessException("Forbidden");
            if(!analytics.Policy.Enabled||!(await analytics.Read(request.EmployeeId,ct)).Consented)throw new ArgumentException("משוב למחקר דורש הסכמה פעילה לשיתוף מצומצם");
            if(request.Reason is null||!IntentRules.Reasons.ContainsKey(request.Reason))throw new ArgumentException("בחרו סיבה מהרשימה, ללא פרטים אישיים");
            await analytics.Capture(request.EmployeeId,request.Target,"feedback",request.Reason,ct);return new{saved=true};
        });
        var admin=app.MapGroup("/api/admin/intent");
        admin.AddEndpointFilter(async(ctx,next)=>{ctx.HttpContext.RequestServices.GetRequiredService<AccessControl>().Admin(ctx.HttpContext);return await next(ctx);});
        admin.MapGet("/report",(int? days,IntentAnalytics analytics,CancellationToken ct)=>analytics.Summary(days??30,ct));
        admin.MapPost("/scenario",(IntentScenario request,IntentAnalytics analytics,CancellationToken ct)=>analytics.Scenario(request,ct))
            .WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("intent"));
        admin.MapPost("/demo",(IntentScenario request,IntentAnalytics analytics)=>analytics.Demo(request))
            .WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("intent"));
        admin.MapPost("/purge",async(IntentAnalytics analytics,CancellationToken ct)=>{await analytics.Purge(ct);return new{completed=true};});
        admin.MapPost("/prioritize",async(PrioritizeIntent request,IntentAnalytics analytics,ConfigurationSnapshot snapshot,SecretVault vault,IHttpClientFactory clients,UsageAccounting accounting,AiKeyHealth health,AiOperationContext operation,CancellationToken ct)=>
        {
            var report=request.Synthetic?JsonNode.Parse(JsonDefaults.Serialize(analytics.Demo(new("electric",200,request.Days))))!["report"]!:JsonNode.Parse(JsonDefaults.Serialize(await analytics.Summary(request.Days,ct)))!;
            var cards=JsonDefaults.Read<List<IntentInsight>>(report["insights"]!.ToJsonString());
            if(cards.Count==0)return Results.Ok(new{insight=(IntentInsight?)null,usedAi=false,status="insufficient_data",synthetic=request.Synthetic});
            var ai=snapshot.Document.Configuration.Ai;
            if(!analytics.Policy.AiInsightsEnabled||!ai.Enabled)return Results.Ok(new{insight=cards[0],usedAi=false,status="deterministic",synthetic=request.Synthetic});
            var profile=ai.ProfileFor(ai.Models.Count>0&&ai.ServiceModels.ContainsKey("insights")?"insights":"narration");
            operation.ConversationId="management:intent";
            var gateway=new ConfiguredCompletionClient(profile,vault,clients,accounting:accounting,health:health,logger:app.Services.GetRequiredService<ILogger<ConfiguredCompletionClient>>());
            var result=await IntentNarration.Select(gateway,cards,analytics.Policy.Prompt,ct);
            return Results.Ok(new{insight=result.insight,usedAi=result.usedAi,status=result.status,synthetic=request.Synthetic});
        }).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("intent"));
    }
}
