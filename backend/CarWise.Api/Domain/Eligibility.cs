using System.Text;
using System.Text.RegularExpressions;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed record EligibilityEvidence(string Group,string? Occupation,string Source,DateTimeOffset RecordedAt)
{public long Revision {get;init;} public string? OccupationSource {get;init;}}
public static class EligibilityInputs
{
    public static string Normalize(string value)=>Regex.Replace(value.Normalize(NormalizationForm.FormKC),"[׳״'\"\\s]","");
    public static string? Group(string text,ReferenceData data,bool shortAnswer=false)
    {
        if(Regex.IsMatch(text,"רכב|דגם")&&!Regex.IsMatch(text,@"אני\s+(?:ב|מ|שיי[ךכ](?:ת)?\s+ל|משוי[ךכ](?:ת)?\s+ל)קבוצ|קבוצת\s+ה?זכאות"))return null;
        var groups=data.Rules["groupOrder"]!.AsArray().Select(g=>g!.GetValue<string>()).ToArray();var clean=Normalize(text);
        var matches=new List<string>();
        foreach(var group in groups)
        {
            if(clean==Normalize(group)&&shortAnswer){matches.Add(group);continue;}
            var pattern="(?:קבוצ(?:ת\\s+ה?זכאות|ה|ת)|זכאות)(?:\\s+שלי)?(?:\\s+(?:היא|הוא))?\\s*[:=]?\\s*[׳'״\"]?"+Regex.Escape(group)+"[׳'״\"]?(?![\\p{L}\\p{N}])";
            foreach(Match match in Regex.Matches(text,pattern,RegexOptions.IgnoreCase))
            {
                var start=text[..match.Index].LastIndexOfAny([',',';','.','!','?','\n'])+1;
                var end=text.IndexOfAny([',',';','.','!','?','\n'],match.Index+match.Length);if(end<0)end=text.Length;
                var clause=text[start..end];
                if(Regex.IsMatch(clause,@"(?:^|\s)(?:לא|אולי|כנראה|נדמה|האם|איזו|איזה|לאיזו|לאיזה|נכון|יתכן)(?:\s|$)|נראה לי|לא בטוח|אני חושב"))continue;
                if(end<text.Length&&text[end]=='?'&&text[(match.Index+match.Length)..end].Trim().Length==0)continue;
                matches.Add(group);
            }
        }
        var unique=matches.Distinct().ToArray();return unique.Length==1?unique[0]:null;
    }
    public static string? Occupation(string text,ReferenceData data)
    {
        var matches=data.OccupationDocuments.Select(o=>o["occupation"].GetString()!).Where(name=>
            data.OccupationFor(name).Group.Length>0&&Regex.IsMatch(text.Normalize(NormalizationForm.FormKC),
                @"(?<![\p{L}\p{N}])"+Regex.Escape(name).Replace(@"\ ",@"\s+").Replace("/ת","(?:ת)?").Replace("/ית","(?:ית)?")+@"(?![\p{L}\p{N}])")).ToArray();
        return matches.Length==1?matches[0]:null;
    }
    public static string Question(string field,ReferenceData data)=>field=="occupation"?
        "מה התפקיד שלך? הוא נדרש לבדיקת כללי הוויתור ואחזקת הרכב. אם התפקיד לא מופיע במיפוי או לא ידוע, אפשר להעביר שאלה לנציג.":
        "לאיזו קבוצת זכאות משויכים אצלך — "+string.Join(", ",data.Rules["groupOrder"]!.AsArray().Select(g=>g!.GetValue<string>()))+"? אם הקבוצה לא ידועה, אפשר לברר עם נציג.";
    public static bool IsTicket(string target)=>target is "eligibility:group" or "eligibility:occupation";
}
