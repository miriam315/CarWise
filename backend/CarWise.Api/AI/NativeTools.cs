using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
namespace CarWise.Api.AI;

public sealed record AiTool(string Name,string Description,JsonObject Parameters);
public sealed record AiToolCall(string Id,string Name,JsonObject Arguments,bool HasNativeId=false);
public sealed record AiToolMessage(string Role,string? Text=null,JsonNode? Native=null,string? CallId=null,string? Name=null,JsonObject? Result=null);
public sealed record AiToolTurn(List<AiToolCall> Calls,string Text,JsonNode Native);
public interface IAiToolClient
{
    Task<AiToolTurn> CallToolsAsync(string instructions,IReadOnlyList<AiTool> tools,IReadOnlyList<AiToolMessage> messages,CancellationToken ct);
}
public static class NativeTools
{
    public static async Task<string> SendAsync(HttpClient client,AiProfile profile,string key,string instructions,IReadOnlyList<AiTool> tools,IReadOnlyList<AiToolMessage> messages,Action<JsonNode?> observe,CancellationToken ct)
    {
        JsonObject payload;string path;
        JsonNode Copy(JsonNode node)=>node.DeepClone();
        if(profile.Provider is "openai" or "compatible")
        {
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);path="chat/completions";
            var history=new JsonArray(new JsonObject{["role"]="system",["content"]=instructions});
            foreach(var message in messages)history.Add(message.Role=="assistant"?Copy(message.Native!):message.Role=="tool"?
                new JsonObject{["role"]="tool",["tool_call_id"]=message.CallId,["content"]=message.Result!.ToJsonString()}:
                new JsonObject{["role"]="user",["content"]=message.Text});
            payload=new(){["model"]=profile.Model,["max_completion_tokens"]=profile.MaxOutputTokens,["messages"]=history,
                ["tool_choice"]="auto",["tools"]=new JsonArray(tools.Select(t=>(JsonNode)new JsonObject{["type"]="function",["function"]=new JsonObject{["name"]=t.Name,["description"]=t.Description,["parameters"]=Copy(t.Parameters),["strict"]=true}}).ToArray())};
        }
        else if(profile.Provider=="anthropic")
        {
            client.DefaultRequestHeaders.Add("x-api-key",key);client.DefaultRequestHeaders.Add("anthropic-version","2023-06-01");path="messages";
            var history=new JsonArray();
            foreach(var message in messages)
            {
                var role=message.Role=="assistant"?"assistant":"user";
                JsonNode content=message.Role=="assistant"?Copy(message.Native!):message.Role=="tool"?
                    new JsonArray(new JsonObject{["type"]="tool_result",["tool_use_id"]=message.CallId,["content"]=message.Result!.ToJsonString()}):
                    new JsonArray(new JsonObject{["type"]="text",["text"]=message.Text});
                if(role=="user"&&history.LastOrDefault() is JsonObject last&&last["role"]!.ToString()=="user")
                    foreach(var block in content.AsArray())last["content"]!.AsArray().Add(block!.DeepClone());
                else history.Add(new JsonObject{["role"]=role,["content"]=content});
            }
            payload=new(){["model"]=profile.Model,["max_tokens"]=profile.MaxOutputTokens,["system"]=instructions,["messages"]=history,
                ["tools"]=new JsonArray(tools.Select(t=>(JsonNode)new JsonObject{["name"]=t.Name,["description"]=t.Description,["input_schema"]=Copy(t.Parameters)}).ToArray())};
        }
        else
        {
            client.DefaultRequestHeaders.Add("x-goog-api-key",key);
            var model=profile.Model.StartsWith("models/",StringComparison.Ordinal)?profile.Model[7..]:profile.Model;
            path="models/"+Uri.EscapeDataString(model)+":generateContent";
            var history=new JsonArray();
            foreach(var message in messages)
            {
                if(message.Role=="assistant"){history.Add(Copy(message.Native!));continue;}
                var part=message.Role=="tool"?new JsonObject{["functionResponse"]=new JsonObject{["name"]=message.Name,["response"]=Copy(message.Result!)}}:new JsonObject{["text"]=message.Text};
                if(message.Role=="tool"&&message.Native?["id"] is JsonNode responseId)part["functionResponse"]!["id"]=responseId.DeepClone();
                if(history.LastOrDefault() is JsonObject last&&last["role"]!.ToString()=="user")last["parts"]!.AsArray().Add(part);
                else history.Add(new JsonObject{["role"]="user",["parts"]=new JsonArray(part)});
            }
            var generation=GeminiGeneration.Create(profile,new JsonObject(),false);generation.Remove("responseMimeType");generation.Remove("responseJsonSchema");
            payload=new(){["systemInstruction"]=new JsonObject{["parts"]=new JsonArray(new JsonObject{["text"]=instructions})},["contents"]=history,["generationConfig"]=generation,
                ["tools"]=new JsonArray(new JsonObject{["functionDeclarations"]=new JsonArray(tools.Select(t=>(JsonNode)new JsonObject{["name"]=t.Name,["description"]=t.Description,["parametersJsonSchema"]=Copy(t.Parameters)}).ToArray())})};
        }
        using var response=await client.PostAsJsonAsync(path,payload,ct);
        if(!response.IsSuccessStatusCode)throw await AiProviderException.FromResponseAsync(response,ct);
        var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))??throw new AiUnavailableException("לא התקבלה תשובת מודל","empty_output");
        observe(body);
        JsonNode native;var calls=new List<AiToolCall>();string text;string finish;
        if(profile.Provider is "openai" or "compatible")
        {
            var choice=body["choices"]?[0]??throw new AiUnavailableException("לא התקבלה תשובת מודל","empty_output");
            finish=choice["finish_reason"]?.ToString()??"";native=choice["message"]!.DeepClone();text=native["content"]?.GetValue<string>()??"";
            if(native["tool_calls"] is JsonArray array)foreach(var call in array)
                calls.Add(new(call!["id"]!.GetValue<string>(),call["function"]!["name"]!.GetValue<string>(),JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!.AsObject()));
        }
        else if(profile.Provider=="anthropic")
        {
            finish=body["stop_reason"]?.ToString()??"";native=body["content"]!.DeepClone();text=string.Concat(native.AsArray().Where(p=>p?["type"]?.ToString()=="text").Select(p=>p!["text"]!.GetValue<string>()));
            foreach(var block in native.AsArray().Where(p=>p?["type"]?.ToString()=="tool_use"))calls.Add(new(block!["id"]!.GetValue<string>(),block["name"]!.GetValue<string>(),block["input"]!.DeepClone().AsObject()));
        }
        else
        {
            var candidate=body["candidates"]?[0]??throw new AiUnavailableException("לא התקבלה תשובת מודל","empty_output");
            finish=candidate["finishReason"]?.ToString()??"";
            // Preserve the entire content, including opaque thoughtSignature fields required by Gemini.
            native=candidate["content"]!.DeepClone();text=string.Concat(native["parts"]!.AsArray().Where(p=>p?["thought"]?.GetValue<bool>()!=true).Select(p=>p?["text"]?.ToString()??""));
            foreach(var part in native["parts"]!.AsArray())if(part?["functionCall"] is JsonObject function)
                calls.Add(new(function["id"]?.ToString()??"call_"+calls.Count,function["name"]!.GetValue<string>(),(function["args"]??new JsonObject()).DeepClone().AsObject(),function["id"] is not null));
        }
        if(finish is "length" or "max_tokens" or "MAX_TOKENS")throw new AiUnavailableException("פלט קריאת הכלים נקטע. בדקו את מגבלת הפלט בניהול המודל.","output_limit");
        if(finish is not ("stop" or "tool_calls" or "end_turn" or "tool_use" or "STOP"))throw new AiUnavailableException("המודל לא השלים קריאת כלים","incomplete_output");
        if(calls.Count>8||calls.Any(c=>string.IsNullOrWhiteSpace(c.Id)||c.Id.Length>200||c.Name.Length>100)||calls.Select(c=>c.Id).Distinct().Count()!=calls.Count)
            throw new AiUnavailableException("קריאת כלים לא תקינה","invalid_tool_call");
        return JsonDefaults.Serialize(new AiToolTurn(calls,text,native));
    }
}
