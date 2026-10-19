using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record PayslipStatus(bool Enabled,string Code,string Message);
public static class PayslipAvailability
{
    public static async Task<PayslipStatus> ReadAsync(AiConfiguration ai,SecretVault vault,CancellationToken ct)
    {
        if(!ai.PayslipEnabled)return new(false,"disabled","חילוץ תלושים כבוי בהגדרות השמורות. יש להפעיל ולשמור אותו בניהול ← פרומפטים ותלושים.");
        var profile=ai.ProfileFor("payslip");
        if(ai.Models.Count>0&&!profile.SupportsDocuments)return new(false,"documents_unsupported","המודל שנבחר לקריאת תלושים אינו מסומן כתומך PDF ותמונות. יש לעדכן את בחירת המודל בניהול ← ספקי AI.");
        foreach(var key in profile.Keys().Where(k=>k.Enabled))
            if(await vault.IsAvailableAsync(key.KeyReference,ct,new Uri(profile.BaseUrl).Host))
                return new(true,"ready","חילוץ תלושים פעיל בהגדרות השמורות. נדרשים אישור שליחה ובדיקת השדות לפני שמירה.");
        return new(false,"missing_key","לא נמצא מפתח פעיל וזמין למודל קריאת התלושים. יש להגדיר ולשמור מפתח למודל בניהול ← ספקי AI.");
    }
}
