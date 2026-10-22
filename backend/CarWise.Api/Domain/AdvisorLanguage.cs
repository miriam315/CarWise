using System.Text.RegularExpressions;
namespace CarWise.Api.Domain;

/// <summary>Use natural, neutral Hebrew rather than guessing gender from an employee's name.</summary>
public static class AdvisorLanguage
{
    public const string Instructions="Use singular, gender-neutral written Hebrew: prefer אפשר, רוצה, מה הסכום שסוכם, and שלך. Never infer gender from names. Avoid masculine/feminine/plural imperatives, slash forms and repeated greetings. Start directly with the answer, not שלום or הנה פירוט. Keep to the current question; do not add a menu or repeat profile details.";
    private static readonly Dictionary<string,string> Forms=new()
    {
        ["תרצה"]="רוצה",["תרצי"]="רוצה",["תרצו"]="רוצה",
        ["תוכל"]="אפשר",["תוכלי"]="אפשר",["תוכלו"]="אפשר",
        ["בחרו"]="אפשר לבחור",["בחרי"]="אפשר לבחור",
        ["כתבו"]="אפשר לכתוב",["בדקו"]="כדאי לבדוק",["נסו"]="אפשר לנסות"
    };
    public static string Neutral(string text)
    {
        text=text.Replace("איזה דגם תרצו להשוות","איזה דגם להשוות",StringComparison.Ordinal)
            .Replace("איזו חלופת רכב תרצו לבדוק","איזו חלופת רכב לבדוק",StringComparison.Ordinal)
            .Replace("איזו פעולה תרצו שאבצע","איזו פעולה לבצע",StringComparison.Ordinal)
            .Replace("מה תרצו לברר","מה נברר",StringComparison.Ordinal).Replace("מה תרצו לבדוק","מה נבדוק",StringComparison.Ordinal);
        return Regex.Replace(text,@"\b(?:"+string.Join("|",Forms.Keys)+@")\b",match=>Forms[match.Value],RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    }
    public static bool Suitable(string text)=>!Regex.IsMatch(text,@"\b(?:אתה|אתם|אתן|תרצה|תרצי|תרצו|תוכל|תוכלי|תוכלו|בחרי|בחרו)\b|^(?:שלום|היי)[ .,!]",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
}
