using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record PayslipStatus(bool Enabled,string Code,string Message)
{
    public int MaxPages {get;init;}=6;
    public string? Provider {get;init;}
    public string? Model {get;init;}
    public object EmployeeView()=>new{Enabled,Code=Enabled?"ready":"unavailable",Message=Enabled?"חילוץ תלושים זמין. רק העותק שתבדקו ותאשרו יישלח לעיבוד.":"חילוץ תלושים אינו זמין כרגע. אפשר להזין נתונים ידנית או לפנות לתמיכה.",MaxPages};
}
public static class PayslipAvailability
{
    public static AiProfile? DocumentProfile(AiConfiguration ai)
    {
        if(ai.Models.Count==0)return ai.Extraction.SupportsDocuments?ai.Extraction:null;
        return ai.ServiceModels.TryGetValue("payslip",out var id)?ai.Models.SingleOrDefault(m=>m.Id==id)?.Profile:null;
    }
    public static async Task<PayslipStatus> ReadAsync(AiConfiguration ai,SecretVault? vault,CancellationToken ct)
    {
        if(!ai.PayslipEnabled)return new(false,"disabled","חילוץ תלושים כבוי. הפעילו אותו בניהול ← פרומפטים ותלושים.");
        var profile=DocumentProfile(ai);
        if(profile is null||!profile.SupportsDocuments)return new(false,"document_model_required","יש לבחור בניהול מודל לשירות חילוץ תלושים ולסמן שהוא תומך ב־PDF ובתמונות.");
        var configured=false;
        if(vault is not null)foreach(var slot in profile.Keys().Where(k=>k.Enabled))
            if(await vault.IsAvailableAsync(slot.KeyReference,ct,new Uri(profile.BaseUrl).Host)){configured=true;break;}
        return new(configured,configured?"ready":"key_required",configured?
            "חילוץ AI פעיל. רק עותק שתבדקו ותאשרו לאחר השחרה במכשיר יישלח לספק; בדקו את מדיניות השמירה שלו.":
            "יש לשמור מפתח זמין למודל חילוץ התלושים בניהול ← ספקי AI."){Provider=profile.DisplayName.Length>0?profile.DisplayName:profile.Provider,Model=profile.Model};
    }
}
