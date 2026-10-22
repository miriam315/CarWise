using System.Text.RegularExpressions;
namespace CarWise.Api.Domain;

/// <summary>Conversation pacing is server-owned; tool context is not a transcript to dump.</summary>
public static class AdvisorDialogue
{
    public static bool AcceptsReferral(string text)=>Regex.IsMatch(text,@"^(?:כן(?: בבקשה)?|כן,? (?:תפתח|תפתחי|שלח|שלחי)(?: פנייה| פניה)?|בבקשה)[.!]?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static bool WantsDifferent(string text)=>Regex.IsMatch(text,@"(?:רכב|דגם|אפשרות|חלופה)\s+(?:חשמלי\s+)?אחר|(?:עוד|נוספ).{0,12}(?:רכב|דגם|אפשרות|חלופה)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static bool RequestsComparison(string text)=>Regex.IsMatch(text,
        @"(?:מה|איך).{0,25}(?:יקרה|ישתנה|ישנה).{0,25}נטו|כמה.{0,35}(?:נטו|ישנה|ישתנה|ארוויח|אפסיד)|(?:שוקל|רוצה|מעוניי?ן|מתכנן).{0,25}(?:לעבור|להחליף|לוותר)|לעבור לרכב|להשוות|השווה|איזה רכב.{0,20}(?:משתלם|כדאי)",
        RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static string NextQuestion(string field,ReferenceData data,bool updated,bool first)=>
        (first?"בשמחה, נבדוק את זה יחד. ":"")+IntakeFields.Question(field,data);
    // Only a question is model-authored here. Amounts, eligibility and actions remain server-owned.
    public static bool ValidQuestion(string question,string field)
    {
        var subject=field switch{"salary"=>"שכר|בסיס","commission"=>"עמל", "bonus"=>"בונוס","pension"=>"פנסי","fund"=>"השתלמות","credits"=>"זיכוי","children"=>"ילד","currentCar"=>"רכב","participation"=>"רכב|השתתפות|מנכ",_=>"(?!)"};
        var text=question.Replace("{CHILD_AGE}","");
        return question.Length is >5 and <=240&&question.EndsWith('?')&&question.Count(c=>c=='?')==1&&
            (field!="children"||question.Contains("{CHILD_AGE}",StringComparison.Ordinal))&&
            (field is not ("pension" or "fund")||text.Contains("אחוז",StringComparison.Ordinal))&&
            !Regex.IsMatch(text,@"[\p{N}{}\r\n₪]")&&Regex.IsMatch(text,subject)&&
            Regex.IsMatch(text,@"^(?:ו?מה|ו?כמה|האם|יש|ו?איז|ו?איך|לגבי|כדי|ומה|אפשר|תרצ|רוצ|נברר)")&&
            !Regex.IsMatch(text,"מגיע לך|זכאי|זכאית|אישרתי|שלחתי|שמרתי|עדכנתי|תכתוב|כתבו|הקלד|רשמו");
    }
    public static string Question(string wording,ReferenceData data)=>wording.Replace("{CHILD_AGE}",data.Rules["familyTrack"]!["childMaxAge"]!.ToString(),StringComparison.Ordinal);
    public static (string Reply,string[] Candidates) Alternatives(Employee employee,ReferenceData data,PayrollEngine engine,string? category,string? excluded)
    {
        var selected=data.Cars.Where(car=>engine.Eligibility(employee,car).Eligible&&(category is null or "any" or "specific"||FleetRules.Accepts(category,car)))
            .Where(car=>car.Id!=excluded).Select(car=>car.Id).ToArray();
        var label=category=="electric"?"חשמלי":category=="family"?"במסלול המשפחתי":"";
        var reply=selected.Length==0?$"בקטלוג הנוכחי אין כרגע רכב {label} נוסף שמתאים לזכאות שלך. רוצה שאפתח פנייה לנציג לבירור חלופה נוספת?":
            excluded is not null?"אלה הדגמים האחרים שמתאימים לזכאות שלך. איזה מהם מעניין אותך?":"אלה הדגמים שמתאימים לזכאות שלך. איזה מהם מעניין אותך?";
        return (reply,selected);
    }
    public static string[] SelectFacts(string text,IReadOnlyList<string> facts,IReadOnlyList<string> tools)
    {
        var required=Enumerable.Range(0,facts.Count).Where(i=>tools[i] is "set_payroll_fields" or "set_current_car" or "set_current_participation" or "report_alternative_participation" or "ask_representative" or "compare_vehicle" or "ask_missing_details").ToArray();
        // Once an actual comparison is available, catalogue search and profile reads remain context.
        if(required.Length>0)return required.Select(i=>facts[i]).ToArray();
        var selected=Regex.Matches(text,@"\{FACT_(\d+)\}").Select(m=>int.TryParse(m.Groups[1].Value,out var i)?i:-1)
            .Where(i=>i>=0&&i<facts.Count).Distinct().Take(2).ToArray();
        return (selected.Length>0?selected:[facts.Count-1]).Where(i=>i>=0).Select(i=>facts[i]).ToArray();
    }
}
