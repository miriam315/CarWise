using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CarWise.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
namespace CarWise.Api.Administration;

/// <summary>Only encrypted AI key material is stored in PostgreSQL. Plaintext is never returned.</summary>
public sealed class SecretVault(IJsonDatabase database,IDataProtectionProvider protection,IConfiguration configuration)
{
    private readonly IDataProtector protector=protection.CreateProtector("CarWise.Admin.AiKeys.v1");
    public async Task<string> StoreAsync(string value,string host,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>8192||value.Any(char.IsControl))throw new ArgumentException("מפתח AI ריק או לא תקין");
        var reference="vault:"+Guid.NewGuid().ToString("N");
        var document=new JsonObject{["ciphertext"]=protector.Protect(value.Trim()),["host"]=host,["createdAt"]=DateTimeOffset.UtcNow.ToString("O")};
        await database.UpsertAsync("carwise_rules",new JsonArray(new JsonObject{["id"]=reference,["document"]=document}),ct);
        return reference;
    }
    public async Task<string?> ResolveAsync(string reference,CancellationToken ct,string? expectedHost=null)
    {
        if(reference.StartsWith("environment:CARWISE_AI_",StringComparison.Ordinal))
        {
            var name=reference["environment:".Length..];
            if(expectedHost is not null && (name!="CARWISE_AI_API_KEY" || expectedHost!="api.openai.com"))return null;
            var value=Environment.GetEnvironmentVariable(name);
            if(string.IsNullOrWhiteSpace(value)&&name=="CARWISE_AI_API_KEY")value=configuration["Ai:ApiKey"];
            return string.IsNullOrWhiteSpace(value)?null:value;
        }
        if(!System.Text.RegularExpressions.Regex.IsMatch(reference,@"^vault:[a-f0-9]{32}$"))throw new ArgumentException("הפניה למפתח אינה תקינה");
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",reference}},ct:ct);
        var document=rows.FirstOrDefault()?["document"];
        if(expectedHost is not null && document?["host"]?.GetValue<string>()!=expectedHost)return null;
        var encrypted=document?["ciphertext"]?.GetValue<string>();
        if(encrypted is null)return null;
        try{return protector.Unprotect(encrypted);}
        catch(CryptographicException){throw new ArgumentException("לא ניתן לפענח את המפתח. יש לשחזר את תיקיית מפתחות השרת או להזין את מפתח הספק מחדש.");}
    }
    public async Task<bool> IsAvailableAsync(string reference,CancellationToken ct,string? expectedHost=null)
    {try{return !string.IsNullOrWhiteSpace(await ResolveAsync(reference,ct,expectedHost));}catch(ArgumentException){return false;}}
}
