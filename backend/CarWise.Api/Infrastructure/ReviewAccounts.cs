using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace CarWise.Api.Infrastructure;

public sealed record ReviewAccountEntry(string Id,string Email,string Role,string? EmployeeId,bool Published=true,string? Ciphertext=null);
public sealed record ReviewAccountDirectory(long Revision=0,bool Enabled=true,bool Imported=false,List<ReviewAccountEntry>? Accounts=null);
public sealed record ReviewAccountSettings(bool Enabled);
public sealed record ReviewAccountUpdate(bool Published,string? Password=null);

/// <summary>Assessment-only public credentials. Separate from payroll, policy versions and AI secrets.</summary>
public sealed class ReviewAccounts(IJsonDatabase database,IDataProtectionProvider protection,IConfiguration configuration)
{
    private const string RuleId="review-accounts";
    private readonly IDataProtector protector=protection.CreateProtector("CarWise.ReviewAccounts.v1");
    public bool Allowed=>configuration.GetValue("CarWise:ReviewAccountsEnabled",true);
    // Migration of credentials already explicitly published for assessment. Never invent/reset a password.
    private static readonly Dictionary<string,string> PreviouslyPublished=new(StringComparer.OrdinalIgnoreCase)
    {
        ["1234@gmail.com"]="a2j9HMdkZaWGpd4",["5678@gmail.com"]="9vYqBZcHw8Hs9M8",
        ["98765@gmail.com"]="v5GZ8wr7P3QDfEt",["654@gmail.com"]="a2j9HMdkZaWGpd4",["111@gmail.com"]="9vYqBZcHw8Hs9M8"
    };
    private async Task<ReviewAccountDirectory?> LoadAsync(CancellationToken ct)
    {
        var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",RuleId}},ct:ct);
        var node=rows.FirstOrDefault()?["document"];
        return node is null?null:JsonDefaults.Read<ReviewAccountDirectory>(node.ToJsonString());
    }
    private async Task ChangeAsync(Func<ReviewAccountDirectory,ReviewAccountDirectory> update,CancellationToken ct)
    {
        for(var attempt=0;attempt<6;attempt++)
        {
            var previous=await LoadAsync(ct);var current=previous??new();
            var next=update(current);
            if(next==current)return;
            next=next with{Revision=current.Revision+1};
            if(await database.TryWriteRuleAsync(RuleId,previous?.Revision,JsonNode.Parse(JsonDefaults.Serialize(next))!,ct))return;
        }
        throw new ConfigurationConflictException();
    }
    public async Task SyncAsync(IReadOnlyList<AuthAccount> users,CancellationToken ct)
    {
        await ChangeAsync(current=>{
            var entries=(current.Accounts??[]).ToDictionary(a=>a.Id);
            var next=users.Where(u=>!u.Disabled&&u.Role is "admin" or "agent" or "employee")
                .Where(u=>u.Role=="admin"||!string.IsNullOrWhiteSpace(u.EmployeeId))
                .Select(u=>{
                    var entry=entries.GetValueOrDefault(u.Id)??new(u.Id,u.Email,u.Role,u.EmployeeId);
                    // A changed email is a changed login identity: do not reuse its stored password.
                    if(!entry.Email.Equals(u.Email,StringComparison.OrdinalIgnoreCase))entry=entry with{Ciphertext=null};
                    if(!current.Imported&&Allowed&&current.Enabled&&PreviouslyPublished.TryGetValue(u.Email,out var password))
                        entry=entry with{Ciphertext=protector.Protect(password)};
                    return entry with{Email=u.Email,Role=u.Role,EmployeeId=u.EmployeeId};
                }).OrderBy(a=>a.Id,StringComparer.Ordinal).ToList();
            return current.Imported&&(current.Accounts??[]).SequenceEqual(next)?current:current with{Accounts=next,Imported=true};
        },ct);
    }
    public async Task<bool> EnabledAsync(CancellationToken ct)=>Allowed&&(await LoadAsync(ct))?.Enabled!=false;
    public Task SetEnabledAsync(bool enabled,CancellationToken ct)
    {
        if(enabled&&!Allowed)throw new ArgumentException("פרסום חשבונות בדיקה הושבת בהגדרות השרת.");
        return ChangeAsync(current=>current with{Enabled=enabled},ct);
    }
    public Task CaptureAsync(AuthAccount account,string password,CancellationToken ct)=>ChangeAsync(current=>{
        if(!Allowed||!current.Enabled)
        {
            // An unpublished reset must not resurrect an obsolete password when review mode is re-enabled.
            if(!(current.Accounts??[]).Any(a=>a.Id==account.Id&&a.Ciphertext is not null))return current;
            return current with{Accounts=current.Accounts!.Select(a=>a.Id==account.Id?a with{Ciphertext=null}:a).ToList()};
        }
        var items=(current.Accounts??[]).Where(a=>a.Id!=account.Id).ToList();
        var existing=current.Accounts?.Find(a=>a.Id==account.Id);
        items.Add(new(account.Id,account.Email,account.Role,account.EmployeeId,existing?.Published??true,protector.Protect(password)));
        return current with{Accounts=items};
    },ct);
    public Task UpdateAsync(string id,ReviewAccountUpdate request,CancellationToken ct)=>ChangeAsync(current=>{
        if(!(current.Accounts??[]).Any(a=>a.Id==id))throw new ArgumentException("החשבון אינו קיים ברשימת הבדיקה. רעננו את הרשימה.");
        return current with{Accounts=current.Accounts!.Select(a=>a.Id==id?a with{Published=request.Published,
            Ciphertext=string.IsNullOrEmpty(request.Password)?a.Ciphertext:protector.Protect(request.Password)}:a).ToList()};
    },ct);
    private string? Password(ReviewAccountEntry entry)
    {
        if(entry.Ciphertext is null)return null;
        try{return protector.Unprotect(entry.Ciphertext);}catch(CryptographicException){return null;}
    }
    public async Task<object> AdminViewAsync(CancellationToken ct)
    {
        var current=await LoadAsync(ct)??new();
        return new{enabled=Allowed&&current.Enabled,available=Allowed,items=(current.Accounts??[]).Select(a=>new{
            a.Id,a.Email,a.Role,a.EmployeeId,a.Published,passwordSaved=Password(a) is not null})};
    }
    public async Task<object> PublicViewAsync(CancellationToken ct)
    {
        var current=await LoadAsync(ct)??new();var enabled=Allowed&&current.Enabled;
        return new{enabled,items=(enabled?current.Accounts??[]:[]).Where(a=>a.Published).Select(a=>new{
            a.Id,a.Email,a.Role,password=Password(a)})};
    }
}
