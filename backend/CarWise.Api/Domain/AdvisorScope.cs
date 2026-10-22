using System.Text.RegularExpressions;
namespace CarWise.Api.Domain;

/// <summary>A narrow deterministic guard for unambiguous unrelated requests, including
/// when the provider is unavailable. Ambiguous vehicle follow-ups remain with the planner.</summary>
public static class AdvisorScope
{
    public const string Redirect="אני כאן לייעוץ על רכב החברה וההשפעה על הנטו. אוכל לעזור בזכאות, בתקציב, בהשוואת רכבים או בבירור מול נציג.";
    public static bool ClearlyUnrelated(string text)
    {
        bool Has(string pattern)=>Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(Has(@"רכב|ליסינג|נהיגה|שכר|נטו|השתתפות|זכאות|חשמלי|עמדות? טעינה"))return false;
        return Has(@"(?:איז[הו]|מה).{0,30}(?:מחשב|לפטופ|טלפון|סמארטפון).{0,25}(?:לקנות|כדאי|מומלץ)|(?:מה|איך|איז[הו]).{0,25}(?:לבשל|לאפות|מתכון|עוגה)|(?:כתוב|תכתוב|תכתבי|תן|תני).{0,15}מתכון|מזג\s*האוויר");
    }
}
