using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
using CarWise.Api.Accounting;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.AI;
public static class PayslipExtraction
{
    public static readonly IReadOnlyDictionary<string,decimal> Limits=PayslipFields.Limits;
    public static JsonNode Schema()
    {
        var properties=new JsonObject();foreach(var key in Limits.Keys)properties[key]=new JsonObject{["type"]="object",["additionalProperties"]=false,
            ["properties"]=new JsonObject{["value"]=new JsonObject{["type"]=new JsonArray("number","null")},["evidence"]=new JsonObject{["type"]=new JsonArray("string","null")}},["required"]=new JsonArray("value","evidence")};
        return new JsonObject{["type"]="object",["additionalProperties"]=false,["properties"]=properties,["required"]=new JsonArray(Limits.Keys.Select(k=>(JsonNode?)JsonValue.Create(k)).ToArray())};
    }
    public static PayslipResult Validate(string json,long revision)
    {
        try{return ValidateCore(json,revision);}
        catch(Exception error)when(error is System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException)
        {throw new AiUnavailableException("חילוץ התלוש לא החזיר שדות תקינים. לא שונו נתוני העובד.");}
    }
    private static PayslipResult ValidateCore(string json,long revision)
    {
        var root=JsonNode.Parse(json)?.AsObject()??throw new AiUnavailableException("לא התקבל חילוץ מובנה");
        if(root.Count!=Limits.Count||root.Any(p=>!Limits.ContainsKey(p.Key)))throw new AiUnavailableException("הספק החזיר שדות שאינם נתמכים");
        var fields=new Dictionary<string,PayslipField>();var warnings=new List<string>();
        foreach(var (key,max) in Limits)
        {
            var node=root[key];decimal? value=node?["value"]?.GetValue<decimal>();var evidence=node?["evidence"]?.GetValue<string>();
            if(value is not null)
            {
                var explicitNumbers=Regex.Matches(evidence??"",@"\d[\d,]*(?:\.\d+)?",RegexOptions.None,TimeSpan.FromMilliseconds(100)).Select(m=>decimal.TryParse(m.Value.Replace(",",""),NumberStyles.Number,CultureInfo.InvariantCulture,out var n)?n:-1).ToHashSet();
                if(value<0||value>max||string.IsNullOrWhiteSpace(evidence)||evidence.Length>160||!explicitNumbers.Contains(value.Value)||((key is "pension" or "fund")&&!evidence.Contains('%')))
                {warnings.Add("שדה "+key+" לא עבר בדיקת טווח/אסמכתה והושמט. הזינו אותו ידנית לפי התלוש.");value=null;evidence=null;}
            }
            fields[key]=new(value,value is null?null:evidence);
        }
        warnings.Add("זהו חילוץ מוצע, לא אימות של התלוש. יש לבדוק את השדות מול המקור; שכר בסיס אינו בהכרח סך הברוטו, ואחוזי ההפרשה חייבים להיות אחוזי עובד.");
        return new(fields,warnings,revision);
    }
    public static void MapPayslips(this WebApplication app)
    {
        app.MapGet("/api/payslip/status",(ConfigurationSnapshot snapshot,ILocalPayslipOcr ocr,CancellationToken ct)=>PayslipAvailability.ReadAsync(snapshot.Document.Configuration.Ai,ocr,ct));
        app.MapPost("/api/payslip/extract",async(HttpContext context,ConfigurationSnapshot snapshot,AccessControl access,ILocalPayslipOcr ocr,CancellationToken ct)=>{
            var employee=context.Request.Query["employee"].ToString();access.Employee(context,employee);
            if(!snapshot.Data.Employees.Any(e=>e.Id==employee))throw new ArgumentException("לא נמצא פרופיל עובד");
            if(context.Request.Query["consent"]!="true")throw new ArgumentException("נדרש אישור לעיבוד מקומי של התלוש");
            var ai=snapshot.Document.Configuration.Ai;
            var status=await PayslipAvailability.ReadAsync(ai,ocr,ct);
            if(!status.Enabled)throw new ArgumentException(status.Message);
            var bytes=await UploadReader.ReadAsync(context,8*1024*1024,ct);
            try{return await ocr.ExtractAsync(AiDocument.Read(bytes),ai.PayslipOcr,snapshot.Document.Revision,ct);}
            finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);}
        }).WithMetadata(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("documents"));
    }
}
