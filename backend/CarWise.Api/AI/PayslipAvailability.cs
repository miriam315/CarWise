using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record PayslipStatus(bool Enabled,string Code,string Message)
{public bool BrowserEnabled {get;init;}public LocalPayslipSettings Settings {get;init;}=new();}
public static class PayslipAvailability
{
    public static async Task<PayslipStatus> ReadAsync(AiConfiguration ai,ILocalPayslipOcr? ocr,CancellationToken ct)
    {
        if(!ai.PayslipEnabled)return new(false,"disabled","חילוץ תלושים כבוי בהגדרות השמורות. יש להפעיל ולשמור אותו בניהול ← פרומפטים ותלושים.");
        if(ocr is null)return new(false,"ocr_unavailable","מנוע ה־OCR המקומי אינו מותקן בשרת. אפשר להזין נתונים ידנית."){BrowserEnabled=true,Settings=ai.PayslipOcr};
        var status=await ocr.StatusAsync(ai.PayslipOcr,ct);return new(status.Ready,status.Code,status.Message){BrowserEnabled=true,Settings=ai.PayslipOcr};
    }
}
