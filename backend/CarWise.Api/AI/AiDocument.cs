using System.Text.Json.Nodes;
namespace CarWise.Api.AI;
public sealed record AiDocument(string MediaType,byte[] Bytes)
{
    private string Base64=>Convert.ToBase64String(Bytes);
    public JsonObject OpenAiPart()=>MediaType=="application/pdf"
        ?new(){["type"]="file",["file"]=new JsonObject{["filename"]="payslip.pdf",["file_data"]="data:application/pdf;base64,"+Base64}}
        :new(){["type"]="image_url",["image_url"]=new JsonObject{["url"]="data:"+MediaType+";base64,"+Base64}};
    public JsonObject AnthropicPart()=>new(){["type"]=MediaType=="application/pdf"?"document":"image",
        ["source"]=new JsonObject{["type"]="base64",["media_type"]=MediaType,["data"]=Base64}};
    public JsonObject GeminiPart()=>new(){["inlineData"]=new JsonObject{["mimeType"]=MediaType,["data"]=Base64}};
    public static AiDocument Read(byte[] bytes)
    {
        if(bytes.Length>8*1024*1024)throw new ArgumentException("תלוש מוגבל ל־8MB");
        var span=bytes.AsSpan();var type=span.StartsWith("%PDF-"u8)?"application/pdf":span.StartsWith(new byte[]{137,80,78,71,13,10,26,10})?"image/png":span.StartsWith(new byte[]{255,216,255})?"image/jpeg":null;
        if(type is null||bytes.Length<16)throw new ArgumentException("אפשר להעלות PDF, PNG או JPEG תקינים בלבד");
        return new(type,bytes);
    }
}
