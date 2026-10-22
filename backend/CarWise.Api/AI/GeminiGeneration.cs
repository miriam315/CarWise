using System.Text.Json.Nodes;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;
public static class GeminiGeneration
{
    public static JsonObject Create(AiProfile profile,JsonNode schema,bool document)
    {
        var result=new JsonObject{["responseMimeType"]="application/json",["responseJsonSchema"]=schema.DeepClone(),["maxOutputTokens"]=profile.MaxOutputTokens};
        var mode=profile.GeminiThinking;
        if(mode=="auto")
        {
            var model=profile.Model.Replace("models/","",StringComparison.Ordinal).ToLowerInvariant();
            mode=!document?"default":model.StartsWith("gemini-2.5-flash",StringComparison.Ordinal)?"off":
                model.StartsWith("gemini-3",StringComparison.Ordinal)?(model.Contains("flash",StringComparison.Ordinal)?"minimal":"low"):"default";
        }
        if(mode=="off")result["thinkingConfig"]=new JsonObject{["thinkingBudget"]=0};
        else if(mode is "minimal" or "low")result["thinkingConfig"]=new JsonObject{["thinkingLevel"]=mode};
        return result;
    }
}
