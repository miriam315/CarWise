using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record AiConnectionTestResult(bool Success,string Code,string Message,int? Status=null);
public static class AiConnectionTest
{
    public static async Task<AiConnectionTestResult> RunAsync(TestAiRequest request,SecretVault vault,IHttpClientFactory clients,
        UsageAccounting? accounting,AiKeyHealth health,CancellationToken ct)
    {
        ConfigurationValidator.ValidateAi(request.Profile);
        if(request.ApiKey is {Length:>8192})throw new ArgumentException("המפתח ארוך מדי");
        if(request.ApiKeys is not null&&(request.ApiKeys.Count>8||request.ApiKeys.Any(k=>
            !request.Profile.Keys().Any(slot=>slot.Id==k.Key)||k.Value is null||k.Value.Length>8192)))
            throw new ArgumentException("מפתחות הבדיקה חייבים להתאים למפתחות המודל ועד 8,192 תווים למפתח");
        if(!string.IsNullOrWhiteSpace(request.ApiKey)&&request.ApiKeys?.Count>0)
            throw new ArgumentException("יש לבחור בדיקת מפתח יחיד או בדיקת מאגר מפתחות");
        var selected=request.KeyId is null?null:request.Profile.Keys().SingleOrDefault(k=>k.Id==request.KeyId)
            ??throw new ArgumentException("מזהה מפתח אינו קיים");
        try
        {
            var client=new ConfiguredCompletionClient(request.Profile,vault,clients,
                string.IsNullOrWhiteSpace(request.ApiKey)?null:request.ApiKey.Trim(),accounting,
                request.KeyId is null?health:new AiKeyHealth(),selected,request.ApiKeys);
            if(request.ToolCalling)
            {
                var tool=new AiTool("carwise_connection_probe","A harmless synthetic connection test; echo the supplied nonce.",JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"nonce":{"type":"string","enum":["carwise"]}},"required":["nonce"]}""")!.AsObject());
                var messages=new List<AiToolMessage>{new("user","Call carwise_connection_probe with nonce carwise. After its successful result, respond exactly CARWISE_TOOL_OK.")};
                var turn=await client.CallToolsAsync("This is a synthetic native tool compatibility test. Use the provided function before responding. No personal data.",[tool],messages,ct);
                if(turn.Calls.Count!=1||turn.Calls[0].Name!=tool.Name||turn.Calls[0].Arguments.Count!=1||turn.Calls[0].Arguments["nonce"]?.ToString()!="carwise")
                    return new(false,"invalid_tool_response","המודל לא ביצע את קריאת הכלי הנדרשת. בדקו תמיכה ב־Tool Calling בפורמט ובמודל שנבחרו.");
                var call=turn.Calls[0];messages.Add(new("assistant",Native:turn.Native));
                messages.Add(new("tool",Native:call.HasNativeId?new JsonObject{["id"]=call.Id}:null,CallId:call.Id,Name:call.Name,Result:new(){["ok"]=true}));
                var final=await client.CallToolsAsync("After the successful tool result respond exactly CARWISE_TOOL_OK.",[tool],messages,ct);
                var passed=final.Calls.Count==0&&final.Text.Trim()=="CARWISE_TOOL_OK";
                return new(passed,passed?"tools_ok":"invalid_tool_response",passed?"קריאת כלי, החזרת תוצאה והמשך תשובת המודל עברו בהצלחה":"המודל לא השלים תשובה לאחר קבלת תוצאת הכלי.");
            }
            var schema=JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}""")!;
            var response=await client.CompleteAsync("connection_test",schema,
                "Return exactly a JSON object with ok=true. Do not include personal data.","בדיקת חיבור",ct);
            var output=JsonNode.Parse(response) as JsonObject;
            var valid=output?["ok"] is JsonValue value&&value.TryGetValue<bool>(out var ok)&&ok;
            var temporary=!string.IsNullOrWhiteSpace(request.ApiKey)||
                (selected is not null&&!string.IsNullOrWhiteSpace(request.ApiKeys?.GetValueOrDefault(selected.Id)));
            if(valid&&selected is not null&&!temporary)health.Success(request.Profile,selected.KeyReference);
            return new(valid,valid?"ok":"invalid_output",valid?"הספק החזיר תשובה מובנית תקינה":"הספק לא החזיר את המבנה הנדרש");
        }
        catch(AiProviderException error)
        {return new(false,error.Code,error.Message+(error.Parameter is null?"":$" הפרמטר שנדחה: {error.Parameter}."),error.Status);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested)
        {return new(false,"timeout",$"הספק לא השלים את הבדיקה בתוך {request.Profile.TimeoutSeconds} שניות. הגדילו את זמן ההמתנה עד 90 שניות או נסו שוב מאוחר יותר.");}
        catch(HttpRequestException)
        {return new(false,"network_error","לא ניתן להתחבר לספק ה־AI. בדקו את כתובת ה־API, חיבור הרשת והרשאת הגישה מהשרת.");}
        catch(JsonException)
        {return new(false,"invalid_output","הספק החזיר תשובה שאינה JSON תקין. בדקו שהמודל תומך בפלט מובנה דרך פורמט ה־API שנבחר.");}
        catch(AiUnavailableException error)
        {return new(false,error.Code,error.Message);}
    }
}
