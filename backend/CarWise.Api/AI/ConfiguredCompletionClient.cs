using System.Net.Http.Headers;
using CarWise.Api.Accounting;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed class AiProviderException(int status,string code) : AiUnavailableException(
    code is "credit_balance_exhausted" or "insufficient_quota"?"אין יתרת API זמינה בחשבון הספק. יש לעדכן חיוב; אין צורך במפתח חדש.":
    status is 401 or 403?"הספק דחה את המפתח או את הרשאותיו.":$"שירות הספק החזיר שגיאה ({status}).")
{
    public int Status=>status;
    public string Code=>code;
    public static async Task<AiProviderException> FromResponseAsync(HttpResponseMessage response,CancellationToken ct)
    {
        var code="provider_error";
        try
        {
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var value=body?["error"]?["code"]?.ToString()??body?["error"]?["type"]?.ToString()??"provider_error";
            if(Regex.IsMatch(value,@"^[A-Za-z0-9_-]{1,80}$"))code=value;
        }
        catch(System.Text.Json.JsonException){ }
        return new((int)response.StatusCode,code);
    }
}
/// <summary>Native adapters share a provider-independent structured-completion contract.</summary>
public sealed class ConfiguredCompletionClient(AiProfile profile,SecretVault vault,IHttpClientFactory clients,string? testKey=null,UsageAccounting? accounting=null) : IAiCompletionClient
{
    public async Task<string> CompleteAsync(string name,JsonNode schema,string instructions,string text,CancellationToken ct)
    {
        ConfigurationValidator.ValidateAi(profile);
        var record=accounting is null?null:await accounting.StartAsync(profile,name,ct);
        JsonNode? usageBody=null;Exception? failure=null;
        try{return await SendAsync(name,schema,instructions,text,body=>usageBody=body,ct);}
        catch(Exception error){failure=error;throw;}
        finally{if(record is not null)await accounting!.FinishAsync(record,usageBody,failure);}
    }
    private async Task<string> SendAsync(string name,JsonNode schema,string instructions,string text,Action<JsonNode?> observe,CancellationToken ct)
    {
        var key=testKey??await vault.ResolveAsync(profile.KeyReference,ct,new Uri(profile.BaseUrl).Host);
        if(string.IsNullOrWhiteSpace(key))throw new AiUnavailableException("לא הוגדר מפתח לספק שנבחר");
        using var client=clients.CreateClient("AiProviders");
        client.BaseAddress=new Uri(profile.BaseUrl.TrimEnd('/')+"/");client.Timeout=TimeSpan.FromSeconds(profile.TimeoutSeconds);
        if(profile.Provider is "openai" or "compatible")
        {
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
            return await new AiGateway(client,profile.Model,profile.MaxOutputTokens,observe).CompleteAsync(name,schema,instructions,text,ct);
        }
        HttpResponseMessage response;
        if(profile.Provider=="anthropic")
        {
            client.DefaultRequestHeaders.Add("x-api-key",key);client.DefaultRequestHeaders.Add("anthropic-version","2023-06-01");
            response=await client.PostAsJsonAsync("messages",new{model=profile.Model,max_tokens=profile.MaxOutputTokens,system=instructions,
                messages=new[]{new{role="user",content=text}},output_config=new{format=new{type="json_schema",schema}}},ct);
        }
        else
        {
            client.DefaultRequestHeaders.Add("x-goog-api-key",key);
            var model=profile.Model.StartsWith("models/",StringComparison.Ordinal)?profile.Model[7..]:profile.Model;
            response=await client.PostAsJsonAsync("models/"+Uri.EscapeDataString(model)+":generateContent",new{
                systemInstruction=new{parts=new[]{new{text=instructions}}},contents=new[]{new{role="user",parts=new[]{new{text}}}},
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
