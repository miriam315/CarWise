using System.Net.Http.Headers;
using CarWise.Api.Accounting;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed class AiProviderException(int status,string code) : AiUnavailableException(
    code is "credit_balance_exhausted" or "insufficient_quota"?"אין יתרת API זמינה בחשבון הספק. בדקו חיוב או מפתח מחשבון בעל מכסה זמינה.":
    status is 401 or 403?"הספק דחה את המפתח או את הרשאותיו.":$"שירות הספק החזיר שגיאה ({status}).")
{
    public int Status=>status;
    public string Code=>code;
    public double? RetryAfterSeconds {get;init;}
    public JsonNode? UsageBody {get;init;}
    public bool CanFailOver=>Status is 401 or 402 or 403 or 429 or 503 or 529;
    public static async Task<AiProviderException> FromResponseAsync(HttpResponseMessage response,CancellationToken ct)
    {
        var code="provider_error";JsonNode? usage=null;
        try
        {
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            usage=new JsonObject{["usage"]=body?["usage"]?.DeepClone(),["usageMetadata"]=body?["usageMetadata"]?.DeepClone()};
            var value=body?["error"]?["code"]?.ToString()??body?["error"]?["type"]?.ToString()??body?["error"]?["status"]?.ToString()??"provider_error";
            if(Regex.IsMatch(value,@"^[A-Za-z0-9_-]{1,80}$"))code=value;
        }
        catch(Exception error)when(error is System.Text.Json.JsonException or InvalidOperationException){ }
        var retry=response.Headers.RetryAfter;
        var seconds=retry?.Delta?.TotalSeconds??(retry?.Date-DateTimeOffset.UtcNow)?.TotalSeconds;
        return new((int)response.StatusCode,code){RetryAfterSeconds=seconds,UsageBody=usage};
    }
}
/// <summary>Native adapters share a provider-independent structured-completion contract.</summary>
public sealed class ConfiguredCompletionClient(AiProfile profile,SecretVault vault,IHttpClientFactory clients,string? testKey=null,UsageAccounting? accounting=null,AiKeyHealth? health=null,AiBackupKey? keyToTest=null) : IAiCompletionClient
{
    public Task<string> CompleteAsync(string name,JsonNode schema,string instructions,string text,CancellationToken ct)=>ExecuteAsync(name,schema,instructions,text,null,ct);
    public Task<string> CompleteDocumentAsync(string name,JsonNode schema,string instructions,AiDocument document,CancellationToken ct)=>ExecuteAsync(name,schema,instructions,"Extract the explicitly printed payroll fields from the attached document.",document,ct);
    private async Task<string> ExecuteAsync(string name,JsonNode schema,string instructions,string text,AiDocument? document,CancellationToken ct)
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
            if(testKey is null&&breaker.Get(profile,slot.KeyReference) is not null)continue;
            string? key;
            try{key=testKey??await vault.ResolveAsync(slot.KeyReference,deadline.Token,new Uri(profile.BaseUrl).Host);}
            catch(ArgumentException){continue;} // Unreadable/replaced vault entries must not block a valid backup.
            if(string.IsNullOrWhiteSpace(key)||!seen.Add(key))continue;
            attempt++;
            var record=accounting is null?null:await accounting.StartAsync(profile,name,deadline.Token,slot.Id,slot.Name,attempt,invocation);
            JsonNode? usageBody=null;Exception? failure=null;
            try
            {
                var result=await SendAsync(name,schema,instructions,text,body=>usageBody=body,document,key,deadline.Token);
                if(testKey is null)breaker.Success(profile,slot.KeyReference);return result;
            }
            catch(AiProviderException error)when(error.CanFailOver)
            {
                failure=last=error;usageBody=error.UsageBody;
                if(testKey is null)breaker.Reject(profile,slot.KeyReference,error);
            }
            catch(Exception error){failure=error;if(error is AiProviderException provider)usageBody=provider.UsageBody;throw;}
            finally{if(record is not null)await accounting!.FinishAsync(record,usageBody,failure);}
        }
        if(last is not null)throw last;
        throw new AiUnavailableException("אין מפתח AI זמין כרגע. המפתחות חסרים, מושבתים או בהמתנה לאחר תקלה. אפשר לנסות מאוחר יותר או לבדוק בניהול.");
    }
    private async Task<string> SendAsync(string name,JsonNode schema,string instructions,string text,Action<JsonNode?> observe,AiDocument? document,string key,CancellationToken ct)
    {
        using var client=clients.CreateClient("AiProviders");
        client.BaseAddress=new Uri(profile.BaseUrl.TrimEnd('/')+"/");client.Timeout=TimeSpan.FromSeconds(profile.TimeoutSeconds);
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
                generationConfig=new{responseMimeType="application/json",responseJsonSchema=schema,maxOutputTokens=profile.MaxOutputTokens}},ct);
        }
        using(response)
        {
            if(!response.IsSuccessStatusCode)throw await AiProviderException.FromResponseAsync(response,ct);
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            observe(body);
            string? output;
            if(profile.Provider=="anthropic")
            {
                if(body?["stop_reason"]?.GetValue<string>()!="end_turn")throw new AiUnavailableException("הספק לא השלים תשובה מובנית");
                output=string.Concat(body?["content"]?.AsArray().Where(p=>p?["type"]?.GetValue<string>()=="text").Select(p=>p!["text"]!.GetValue<string>())??[]);
            }
            else
            {
                var candidate=body?["candidates"]?[0];
                if(candidate?["finishReason"]?.GetValue<string>()!="STOP")throw new AiUnavailableException("הספק חסם או לא השלים את התשובה");
                output=string.Concat(candidate?["content"]?["parts"]?.AsArray().Select(p=>p?["text"]?.GetValue<string>()??"")??[]);
            }
            if(string.IsNullOrWhiteSpace(output))throw new AiUnavailableException("הספק לא החזיר פלט מובנה");
            _=JsonNode.Parse(output);return output;
        }
    }
}
