using System.Globalization;
using System.Text.RegularExpressions;
using CarWise.Api.AI;
namespace CarWise.Api.Domain;

public sealed record AdvisorState(string? AwaitingField=null,string? Target=null,bool WantsElectric=false,string[]? CandidateIds=null,string? RequestedCategory=null,string? OfferedReferral=null,string[]? LastOfferedIds=null,string[]? RejectedCandidateIds=null,AdvisorMemory? Memory=null);
public static class IntakeFields
{
    public static readonly string[] All=["salary","commission","bonus","pension","fund","credits","children","currentCar","participation"];
    public static bool Known(Employee employee,string field)=>employee.KnownFields is null||employee.KnownFields.Contains(field);
    public static string[] Missing(Employee employee)=>All.Where(field=>!Known(employee,field)&&!(field=="participation"&&Known(employee,"currentCar")&&employee.CurrentCar is null)).ToArray();
    public static void ValidateKnowledge(Employee employee)
    {
        if(employee.KnownFields is {} fields&&(fields.Length>All.Length||fields.Distinct().Count()!=fields.Length||fields.Except(All).Any()))throw new ArgumentException("רשימת שדות ידועים אינה תקינה");
    }
    public static void RequireComplete(Employee employee)
    {if(Missing(employee).Length>0)throw new ArgumentException("יש להשלים את נתוני השכר והרכב לפני חישוב נטו. נתון חסר אינו אפס.");}
    public static Employee Confirm(Employee employee,params string[] fields)=>employee.KnownFields is null?employee:employee with{KnownFields=employee.KnownFields.Concat(fields).Distinct().ToArray()};
    public static Employee Apply(Employee employee,ExtractedData fields)
    {
        var updated=fields.Apply(employee);var confirmed=new List<string>();
        foreach(var (name,value) in new[]{("salary",fields.Salary),("commission",fields.Commission),("bonus",fields.Bonus),("pension",fields.Pension),("fund",fields.Fund),("credits",fields.Credits),("children",fields.Children)})if(value is not null)confirmed.Add(name);
        return Confirm(updated,confirmed.ToArray());
    }
    public static string Question(string field,ReferenceData data)=>field switch{
        "salary"=>"מה שכר הבסיס החודשי שלך, לפני עמלות ובונוס?",
        "commission"=>"מה לגבי עמלות מעבר לשכר הבסיס — יש לך, ובאיזה סכום חודשי?",
        "bonus"=>"ומה לגבי בונוס — יש סכום חודשי שצריך להביא בחשבון?",
        "pension"=>"כמה אחוזים מהשכר שלך מופרשים לפנסיה בחלק העובד?",
        "fund"=>"יש לך קרן השתלמות, ומה אחוז ההפרשה שלך אליה?",
        "credits"=>"כמה נקודות זיכוי מופיעות בתלוש שלך?",
        "children"=>$"כמה ילדים עד גיל {data.Rules["familyTrack"]!["childMaxAge"]} יש לך?",
        "currentCar"=>"איזה רכב חברה יש לך היום, אם בכלל — דגם ושנת רישום?",
        "participation"=>"כמה מנכים לך בחודש על הרכב הנוכחי? אם הסכום לא ידוע, אפשר לברר עם נציג.",
        _=>throw new ArgumentException("Unknown intake field")};
    public static bool IsUnknown(string text)=>Regex.IsMatch(text,@"^(?:לא (?:יודע(?:ת)?|ידוע)|אין לי מושג|צריך(?:ה)? נציג)[.!?]?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static string AnswerText(string text,string field)
    {
        var label=field switch{
            "salary"=>@"(?:שכר(?:\s+ה?בסיס)?|בסיס)","commission"=>"עמלות", "bonus"=>"בונוס",
            "pension"=>@"(?:פנסיה|אחוז\s+(?:הפרשת\s+העובד\s+)?(?:ההפרשה\s+)?לפנסיה)",
            "fund"=>@"(?:קרן\s+השתלמות|הפרשה\s+לקרן\s+השתלמות)","credits"=>"נקודות זיכוי",
            "children"=>"ילדים", "participation"=>@"השתתפות(?:\s+(?:ברכב\s+הנוכחי|הנוכחית))?",_=>null};
        if(label is null)return text.Trim();
        var match=Regex.Match(text.Trim(),@"^(?:ה)?"+label+@"(?:\s+שלי)?(?:\s+(?:הוא|היא|הן))?\s*[:=]?\s*(?<value>.+)$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        return match.Success?match.Groups["value"].Value:text.Trim();
    }
    public static bool ExplicitNone(string text,string field,bool requireLabel=false)
    {
        var label=field switch{"commission"=>"עמלות","bonus"=>"בונוס","fund"=>"קרן השתלמות","children"=>"ילדים","participation"=>"השתתפות",_=>null};
        if(label is null)return false;
        var shortAnswer=requireLabel?"":"לא|אין|אין לי|ללא|";
        return Regex.IsMatch(text.Trim(),@"^(?:"+shortAnswer+@"(?:אין(?: לי)?|ללא) "+label+@"|לא מקבל(?:ת)? "+label+@")[.!]?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    }
    public static bool ExplicitNoneStatement(string text,string field)=>Regex.Split(text,@"[,;]|\s+(?:וגם\s+|ו(?=אין|לא|ללא))",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Any(part=>ExplicitNone(part,field,true));
    public static bool TryAnswerNumber(string text,string field,out decimal value)
    {if(ExplicitNone(text,field)){value=0;return true;}return TryNumber(AnswerText(text,field),field,out value);}
    public static bool IsNumericAnswer(string text,string field)
    {
        if(field=="currentCar")return false;
        if(ExplicitNone(text,field))return true;
        var value=AnswerText(text,field);
        return Regex.IsMatch(value,@"\d",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))&&
            Regex.IsMatch(value,"""^[\d, .%₪ש״ח"'+\-]+$""",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    }
    public static bool TryNumber(string text,string field,out decimal value)
    {
        value=0;if(field=="currentCar")return false;
        var match=Regex.Match(text.Trim(),"""^(?:₪\s*)?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*(%|₪|ש[״"']?ח)?$""",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(!match.Success||!decimal.TryParse(match.Groups[1].Value.Replace(",",""),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value))return false;
        var max=field=="children"?30:PayslipFields.Limits[field];
        return value<=max&&(field!="children"||decimal.Truncate(value)==value)&&(!match.Groups[2].Success||((field is "pension" or "fund")?match.Groups[2].Value=="%":field is not ("children" or "credits")&&match.Groups[2].Value!="%"));
    }
    public static Employee Answer(Employee employee,string field,decimal number)=>Confirm(field switch{
        "salary"=>employee with{Salary=number},"commission"=>employee with{Commission=number},"bonus"=>employee with{Bonus=number},
        "pension"=>employee with{Pension=number},"fund"=>employee with{Fund=number},"credits"=>employee with{Credits=number},
        "children"=>employee with{Children=number},"participation"=>employee with{Participation=number},_=>throw new ArgumentException("Unknown numeric field")},field);
}
