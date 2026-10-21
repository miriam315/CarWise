using System.Globalization;
using System.Text.RegularExpressions;
using CarWise.Api.AI;
namespace CarWise.Api.Domain;

public sealed record AdvisorState(string? AwaitingField=null,string? Target=null,bool WantsElectric=false,string[]? CandidateIds=null);
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
        "commission"=>"מה סכום העמלות החודשי לחישוב? אם אין עמלות, כתבו 0.",
        "bonus"=>"מה סכום הבונוס החודשי לחישוב? אם אין בונוס, כתבו 0.",
        "pension"=>"מה אחוז הפרשת העובד לפנסיה? למשל 6. אין להזין את סכום הניכוי בשקלים.",
        "fund"=>"מה אחוז הפרשת העובד לקרן השתלמות? אם אין, כתבו 0.",
        "credits"=>"כמה נקודות זיכוי יש לך? למשל 2.25.",
        "children"=>$"כמה ילדים עד גיל {data.Rules["familyTrack"]!["childMaxAge"]} יש לך? אם אין, כתבו 0.",
        "currentCar"=>"איזה רכב יש לך היום? בחרו דגם ושנת רישום מדויקים מהרשימה. אם אין רכב חברה, כתבו ״ללא רכב״.",
        "participation"=>"מה ההשתתפות החודשית שלך ברכב הנוכחי? אם אין השתתפות, כתבו 0; אם הסכום לא ידוע, כתבו ״לא יודע״ ואפנה לנציג.",
        _=>throw new ArgumentException("Unknown intake field")};
    public static bool IsUnknown(string text)=>Regex.IsMatch(text,@"^(?:לא (?:יודע(?:ת)?|ידוע)|אין לי מושג|צריך(?:ה)? נציג)[.!?]?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static bool TryNumber(string text,string field,out decimal value)
    {
        value=0;if(field=="currentCar")return false;
        var match=Regex.Match(text.Trim(),@"^(?:₪\s*)?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*(%|₪|שח|ש״ח)?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(!match.Success||!decimal.TryParse(match.Groups[1].Value.Replace(",",""),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value))return false;
        var max=field=="children"?30:PayslipFields.Limits[field];
        return value<=max&&(field!="children"||decimal.Truncate(value)==value)&&(!match.Groups[2].Success||((field is "pension" or "fund")?match.Groups[2].Value=="%":field is not ("children" or "credits")&&match.Groups[2].Value!="%"));
    }
    public static Employee Answer(Employee employee,string field,decimal number)=>Confirm(field switch{
        "salary"=>employee with{Salary=number},"commission"=>employee with{Commission=number},"bonus"=>employee with{Bonus=number},
        "pension"=>employee with{Pension=number},"fund"=>employee with{Fund=number},"credits"=>employee with{Credits=number},
        "children"=>employee with{Children=number},"participation"=>employee with{Participation=number},_=>throw new ArgumentException("Unknown numeric field")},field);
}
