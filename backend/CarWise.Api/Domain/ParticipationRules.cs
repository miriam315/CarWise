using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace CarWise.Api.Domain;
public static class ParticipationRules
{
    public static bool GeneralTicket(string target)=>target.StartsWith("catalogue:",StringComparison.Ordinal)||EligibilityInputs.IsTicket(target);
    public static string CatalogueTarget(string hint)=>"catalogue:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hint.Trim().ToLowerInvariant())))[..20];
    public static bool ApprovalRequested(string text)=>Regex.IsMatch(text,@"נציג|אישור\s+(?:ה?השתתפות|הסכום)|(?:אבקש|מבקש|צריך|רוצה)\s+אישור(?!\s+בחירה)|(?:השתתפות|סוכם|הסכם).*(?:אחר|שונה|לא יודע|לא ידוע)|(?:אצלי|לי)\s+(?:זה\s+)?(?:אחר|שונה)",RegexOptions.None,TimeSpan.FromMilliseconds(100));
    public static bool OutsideCatalogue(string text)=>text.Contains("לא בקטלוג",StringComparison.Ordinal)||text.Contains("לא מופיע בקטלוג",StringComparison.Ordinal)||text.Contains("אינו בקטלוג",StringComparison.Ordinal);
    public static string SourceText(string source)=>source switch{"representative"=>"אישור נציג","employee"=>"דיווח העובד","pending"=>"ממתין לאישור נציג",_=>"ברירת מחדל בקטלוג"};
}
