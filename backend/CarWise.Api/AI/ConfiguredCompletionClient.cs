using System.Net.Http.Headers;
using CarWise.Api.Accounting;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed class AiProviderException(int status,string code) : AiUnavailableException(
    code is "credit_balance_exhausted" or "insufficient_quota"?"אין יתרת API זמינה בחשבון הספק. בדקו חיוב או מפתח מחשבון בעל מכסה זמינה.":
    status is 401 or 403?"הספק דחה את המפתח או את הרשאותיו.":
    status==429?"הספק הגביל את קצב הקריאות או את המכסה. בדקו את מגבלות החשבון או נסו מפתח בעל מכסה נפרדת.":
    status==404?"המודל או נתיב ה־API לא נמצאו אצל הספק. בדקו את שם המודל, הכתובת והרשאת החשבון למודל.":
    code=="invalid_thinking_config"?"הספק דחה את הגדרת החשיבה. בחרו בניהול המודל ׳ברירת מחדל של הספק׳ או מצב חשיבה הנתמך במודל המסוים, שמרו ונסו שוב.":
    code=="invalid_output_tokens"?"הספק דחה את מגבלת טוקני הפלט. הגדירו בניהול ערך הנתמך במודל המסוים; המערכת אינה משנה את הערך שהמנהל בחר.":
    code=="invalid_json_schema"?"הספק דחה את הגדרת הפלט המובנה או את סכמת JSON. בדקו שהמודל וגרסת ה־API תומכים בפלט מובנה למסמכים.":
    status is 400 or 422?"הספק דחה את מבנה הבקשה. בדקו התאמה של המודל לפורמט API ול־JSON Schema; טווח טוקנים תקין במערכת אינו מבטיח שהמודל תומך בכל הפרמטרים.":
    status is 503 or 529?"שירות הספק עמוס או לא זמין כרגע. נסו שוב מאוחר יותר.":$"שירות הספק החזיר שגיאה ({status}).",code)
{
    public int Status=>status;
    public double? RetryAfterSeconds {get;init;}
    public JsonNode? UsageBody {get;init;}
    public string? Parameter {get;init;}
    public bool CanFailOver=>Status is 401 or 402 or 403 or 429 or 503 or 529;
    public static async Task<AiProviderException> FromResponseAsync(HttpResponseMessage response,CancellationToken ct)
    {
        var code="provider_error";string? parameter=null;JsonNode? usage=null;
        try
        {
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            usage=new JsonObject{["usage"]=body?["usage"]?.DeepClone(),["usageMetadata"]=body?["usageMetadata"]?.DeepClone()};
            var value=body?["error"]?["code"]?.ToString()??body?["error"]?["type"]?.ToString()??body?["error"]?["status"]?.ToString()??"provider_error";
            if(int.TryParse(value,out _) && body?["error"]?["status"] is JsonValue statusValue)value=statusValue.ToString();
            if(Regex.IsMatch(value,@"^[A-Za-z0-9_-]{1,80}$"))code=value;
            var param=body?["error"]?["param"]?.ToString();
            if(param is "model" or "max_tokens" or "max_completion_tokens" or "response_format" or "response_format.json_schema"
                or "generationConfig.maxOutputTokens" or "generationConfig.responseJsonSchema" or "output_config")parameter=param;
            // Inspect provider diagnostics only to select fixed, safe labels. Never echo or store
            // its message/field descriptions: they can contain document text or credentials.
            if(body?["error"]?["status"] is JsonValue && response.StatusCode is (System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity))
            {
                var diagnostic=body?["error"]?.ToJsonString()??"";
                if(Regex.IsMatch(diagnostic,@"thinking_?(config|budget|level)",RegexOptions.IgnoreCase))
                {code="invalid_thinking_config";parameter="generationConfig.thinkingConfig";}
                else if(Regex.IsMatch(diagnostic,@"max_?(output_?tokens|completion_?tokens|tokens)",RegexOptions.IgnoreCase))
                {code="invalid_output_tokens";parameter="generationConfig.maxOutputTokens";}
                else if(Regex.IsMatch(diagnostic,@"response_?(json_?schema|schema)|json_?schema",RegexOptions.IgnoreCase))
                {code="invalid_json_schema";parameter="generationConfig.responseJsonSchema";}
            }
        }
        catch(Exception error)when(error is System.Text.Json.JsonException or InvalidOperationException){ }
        var retry=response.Headers.RetryAfter;
        var seconds=retry?.Delta?.TotalSeconds??(retry?.Date-DateTimeOffset.UtcNow)?.TotalSeconds;
        return new((int)response.StatusCode,code){RetryAfterSeconds=seconds,UsageBody=usage,Parameter=parameter};
    }
}
/// <summary>Native adapters share a provider-independent structured-completion contract.</summary>
public sealed class ConfiguredCompletionClient(AiProfile profile,SecretVault vault,IHttpClientFactory clients,string? testKey=null,UsageAccounting? accounting=null,AiKeyHealth? health=null,AiBackupKey? keyToTest=null,IReadOnlyDictionary<string,string>? draftKeys=null) : IAiCompletionClient,IAiToolClient
{
    public Task<string> CompleteAsync(string name,JsonNode schema,string instructions,string text,CancellationToken ct)=>ExecuteAsync(name,schema,instructions,text,null,ct);
    public Task<string> CompleteDocumentAsync(string name,JsonNode schema,string instructions,AiDocument document,CancellationToken ct)=>ExecuteAsync(name,schema,instructions,"Extract the explicitly printed payroll fields from the attached document.",document,ct);
    public async Task<AiToolTurn> CallToolsAsync(string instructions,IReadOnlyList<AiTool> tools,IReadOnlyList<AiToolMessage> messages,CancellationToken ct)
    {
        try{return CarWise.Api.Domain.JsonDefaults.Read<AiToolTurn>(await ExecuteAsync("advisor_tool_turn",new JsonObject(),instructions,"",null,ct,tools,messages));}
        catch(Exception error)when(error is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {throw new AiUnavailableException("הספק החזיר קריאת כלים לא תקינה","invalid_tool_response");}
    }
    private async Task<string> ExecuteAsync(string name,JsonNode schema,string instructions,string text,AiDocument? document,CancellationToken ct,IReadOnlyList<AiTool>? tools=null,IReadOnlyList<AiToolMessage>? messages=null)
    {
        ConfigurationValidator.ValidateAi(profile);
        var breaker=health??new AiKeyHealth();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        var invocation=Guid.NewGuid().ToString("N");var attempt=0;AiProviderException? last=null;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var candidates=keyToTest is not null?[keyToTest]:testKey is null?profile.Keys().Where(k=>k.Enabled):[new AiBackupKey("test","בדיקת מפתח","test")];
        foreach(var slot in candidates)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var draftKey=draftKeys?.GetValueOrDefault(slot.Id);
            var temporary=testKey is not null||!string.IsNullOrWhiteSpace(draftKey);
            if(!temporary&&breaker.Get(profile,slot.KeyReference) is not null)continue;
            string? key;
            try{key=testKey??(!string.IsNullOrWhiteSpace(draftKey)?draftKey.Trim():await vault.ResolveAsync(slot.KeyReference,deadline.Token,new Uri(profile.BaseUrl).Host));}
            catch(ArgumentException){continue;} // Unreadable/replaced vault entries must not block a valid backup.
            if(string.IsNullOrWhiteSpace(key)||!seen.Add(key))continue;
            attempt++;
            var record=accounting is null?null:await accounting.StartAsync(profile,name,deadline.Token,slot.Id,slot.Name,attempt,invocation);
            JsonNode? usageBody=null;Exception? failure=null;
            try
            {
                var result=await SendAsync(name,schema,instructions,text,body=>usageBody=body,document,key,deadline.Token,tools,messages);
                if(!temporary)breaker.Success(profile,slot.KeyReference);return result;
            }
            catch(AiProviderException error)when(error.CanFailOver)
            {
                failure=last=error;usageBody=error.UsageBody;
                if(!temporary)breaker.Reject(profile,slot.KeyReference,error);
            }
            catch(Exception error){failure=error;if(error is AiProviderException provider)usageBody=provider.UsageBody;throw;}
            finally{if(record is not null)await accounting!.FinishAsync(record,usageBody,failure);}
        }
        if(last is not null)throw last;
        throw new AiUnavailableException("אין מפתח AI זמין כרגע. המפתחות חסרים, מושבתים או בהמתנה לאחר תקלה. אפשר לנסות מאוחר יותר או לבדוק מפתח מסוים בנפרד בניהול.","key_unavailable");
    }
    private async Task<string> SendAsync(string name,JsonNode schema,string instructions,string text,Action<JsonNode?> observe,AiDocument? document,string key,CancellationToken ct,IReadOnlyList<AiTool>? tools=null,IReadOnlyList<AiToolMessage>? messages=null)
    {
        using var client=clients.CreateClient(profile.CustomProvider?"AiCustomProviders":"AiProviders");
        client.BaseAddress=new Uri(profile.BaseUrl.TrimEnd('/')+"/");client.Timeout=TimeSpan.FromSeconds(profile.TimeoutSeconds);
        if(tools is not null)return await NativeTools.SendAsync(client,profile,key,instructions,tools,messages!,observe,ct);
        if(profile.Provider is "openai" or "compatible")
        {
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
            return await new AiGateway(client,profile.Model,profile.MaxOutputTokens,observe,document).CompleteAsync(name,schema,instructions,text,ct);
        }
        HttpResponseMessage response;
        if(profile.Provider=="anthropic")
        {
            client.DefaultRequestHeaders.Add("x-api-key",key);client.DefaultRequestHeaders.Add("anthropic-version","2023-06-01");
            response=await client.PostAsJsonAsync("messages",new{model=profile.Model,max_tokens=profile.MaxOutputTokens,system=instructions,
                messages=new JsonArray(new JsonObject{["role"]="user",["content"]=document is null?JsonValue.Create(text):new JsonArray(new JsonObject{["type"]="text",["text"]=text},document.AnthropicPart())}),output_config=new{format=new{type="json_schema",schema}}},ct);
        }
        else
        {
            client.DefaultRequestHeaders.Add("x-goog-api-key",key);
            var model=profile.Model.StartsWith("models/",StringComparison.Ordinal)?profile.Model[7..]:profile.Model;
            response=await client.PostAsJsonAsync("models/"+Uri.EscapeDataString(model)+":generateContent",new{
                systemInstruction=new{parts=new[]{new{text=instructions}}},contents=new JsonArray(new JsonObject{["role"]="user",["parts"]=document is null?new JsonArray(new JsonObject{["text"]=text}):new JsonArray(new JsonObject{["text"]=text},document.GeminiPart())}),
                generationConfig=GeminiGeneration.Create(profile,schema,document is not null)},ct);
        }
        using(response)
        {
            if(!response.IsSuccessStatusCode)throw await AiProviderException.FromResponseAsync(response,ct);
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            observe(body);
            string? output;
            if(profile.Provider=="anthropic")
            {
                if(body?["stop_reason"]?.GetValue<string>()=="max_tokens")throw new AiUnavailableException("הפלט נקטע בגלל מגבלת הטוקנים. הגדילו את מגבלת הפלט ונסו שוב.","output_limit");
                if(body?["stop_reason"]?.GetValue<string>()!="end_turn")throw new AiUnavailableException("הספק לא השלים תשובה מובנית");
                output=string.Concat(body?["content"]?.AsArray().Where(p=>p?["type"]?.GetValue<string>()=="text").Select(p=>p!["text"]!.GetValue<string>())??[]);
            }
            else
            {
                var candidate=body?["candidates"]?[0];
                if(candidate?["finishReason"]?.GetValue<string>()=="MAX_TOKENS")throw new AiUnavailableException("Gemini עצר בגלל מגבלת הטוקנים לפלט הכוללת גם חשיבה. בניהול ספקי AI בדקו את מגבלת הפלט ואת הגדרת החשיבה; לחילוץ תלושים בחרו חשיבה אוטומטית מותאמת או מצב מצומצם הנתמך במודל. לא הועברו נתונים חלקיים לטופס.","output_limit");
                if(candidate?["finishReason"]?.GetValue<string>()!="STOP")throw new AiUnavailableException("הספק חסם או לא השלים את התשובה");
                output=string.Concat(candidate?["content"]?["parts"]?.AsArray().Where(p=>p?["thought"]?.GetValue<bool>()!=true).Select(p=>p?["text"]?.GetValue<string>()??"")??[]);
            }
            if(string.IsNullOrWhiteSpace(output))throw new AiUnavailableException("הספק לא החזיר פלט מובנה");
            _=JsonNode.Parse(output);return output;
        }
    }
}
