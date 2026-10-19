using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace CarWise.Api.AI;

public sealed record LocalTextLine(string Text,double Confidence=100);
public static class LocalPayrollFields
{
    public static readonly IReadOnlyDictionary<string,string> Labels=new Dictionary<string,string>{
        ["salary"]="שכר בסיס",["commission"]="עמלות",["bonus"]="בונוס",["pension"]="פנסיית עובד (%)",
        ["fund"]="קרן השתלמות עובד (%)",["credits"]="נקודות זיכוי",["participation"]="השתתפות עובד ברכב"};
    private static readonly IReadOnlyDictionary<string,string> Patterns=new Dictionary<string,string>{
        ["salary"]=@"(?:שכר (?:בסיס|יסוד)|משכורת יסוד|base salary|basic salary)",
        ["commission"]=@"(?:עמלות|commissions?)",["bonus"]=@"(?:בונוס|bonus)",
        ["pension"]=@"(?:פנסי(?:ה|י?ת) עובד|הפרשת עובד לפנסיה|employee pension|pension employee)",
        ["fund"]=@"(?:קרן השתלמות עובד|קרן השתלמות - עובד|הפרשת עובד לקרן השתלמות|employee study fund|employee education fund)",
        ["credits"]=@"(?:נקודות זיכוי|נק[.׳']? זיכוי|tax credit points)",
        ["participation"]=@"(?:השתתפות עובד ברכב|השתתפות ברכב|employee car participation)"};
    private const string Number=@"(?:\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)";
    private static string Normalize(string text)=>Regex.Replace(new string(text.Normalize(NormalizationForm.FormKC)
        .Where(c=>char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark)).ToArray()),@"\s+"," ").Trim();
    private static string ReverseVisual(string text)=>Regex.Replace(new string(text.Reverse().ToArray()),@"\d[\d,.]*",m=>new string(m.Value.Reverse().ToArray()));
    public static PayslipResult Extract(IEnumerable<LocalTextLine> lines,int minimumConfidence,long revision,string source,CancellationToken ct=default)
    {
        var found=Patterns.Keys.ToDictionary(key=>key,_=>new HashSet<decimal>());
        var count=0;
        foreach(var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if(++count>10000)throw new ArgumentException("תוכן המסמך ארוך מדי לעיבוד מקומי. העלו רק את העמודים הרלוונטיים.");
            if(line.Confidence<minimumConfidence||line.Text.Length>1000||string.IsNullOrWhiteSpace(line.Text))continue;
            var normalized=Normalize(line.Text);
            foreach(var text in new[]{normalized,ReverseVisual(normalized)}.Distinct())
                foreach(var (key,label) in Patterns)
                {
                    // Only a complete, explicitly labelled row with one value is eligible. Names,
                    // account details, gross totals and multi-column deduction rows cannot match.
                    var amount=@"(?<amount>"+Number+@")\s*(?<unit>%|₪|ש[\""״]?ח|NIS|ILS)?";
                    var match=Regex.Match(text,@"^(?:"+label+@"\s*[:=|\-]?\s*"+amount+@"|"+amount+@"\s*[:=|\-]?\s*"+label+@")$",
                        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                    if(!match.Success||!decimal.TryParse(match.Groups["amount"].Value.Replace(",",""),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))continue;
                    var percent=key is "pension" or "fund";
                    if(percent!=(match.Groups["unit"].Value=="%")||value<0||value>PayslipExtraction.Limits[key])continue;
                    found[key].Add(value);
                }
        }
        var warnings=new List<string>();var fields=new Dictionary<string,PayslipField>();
        foreach(var (key,values) in found)
        {
            decimal? value=values.Count==1?values.Single():null;
            // Evidence is generated from a fixed label and a validated number; raw OCR text never
            // leaves this process or gets stored as a source excerpt.
            fields[key]=new(value,value is null?null:Labels[key]+": "+value.Value.ToString(CultureInfo.InvariantCulture));
            if(values.Count>1)warnings.Add(Labels[key]+": נמצאו ערכים סותרים. השדה נשאר ריק לבדיקה ידנית.");
        }
        if(fields.All(f=>f.Value.Value is null))warnings.Add("לא זוהו שדות חד־משמעיים. אפשר למלא את השדות ידנית לפי התלוש; אין צורך להעביר אותו לספק חיצוני.");
        warnings.Add("החילוץ המקומי הוא הצעה בלבד. בדקו מול התלוש את החודש, הסכומים ואחוזי העובד. שדה חסר או עמום אינו מחליף נתון קיים.");
        return new(fields,warnings,revision){Source=source};
    }
}
