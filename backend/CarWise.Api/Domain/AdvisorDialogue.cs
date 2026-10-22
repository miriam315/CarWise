using System.Text.RegularExpressions;
namespace CarWise.Api.Domain;

/// <summary>Conversation pacing is server-owned; tool context is not a transcript to dump.</summary>
public static class AdvisorDialogue
{
    public static bool RequestsComparison(string text)=>Regex.IsMatch(text,
        @"(?:מה|איך).{0,25}(?:יקרה|ישתנה|ישנה).{0,25}נטו|כמה.{0,35}(?:נטו|ישנה|ישתנה|ארוויח|אפסיד)|(?:שוקל|רוצה|מעוניי?ן|מתכנן).{0,25}(?:לעבור|להחליף|לוותר)|לעבור לרכב|להשוות|השווה|איזה רכב.{0,20}(?:משתלם|כדאי)",
        RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static string NextQuestion(string field,ReferenceData data,bool updated,bool first)=>
        (updated?"קיבלתי, עדכנתי את הנתונים. ":first?"בשמחה, נבדוק את זה יחד. ":"")+IntakeFields.Question(field,data);
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
