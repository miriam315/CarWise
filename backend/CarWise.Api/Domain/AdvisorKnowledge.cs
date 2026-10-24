using System.Text.RegularExpressions;
using CarWise.Api.AI;
namespace CarWise.Api.Domain;

/// <summary>The model identifies topics; all facts and amounts are supplied by server data.</summary>
public static class AdvisorKnowledge
{
    public static string[] Identify(string text)
    {
        bool Has(string pattern)=>Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
        var topics=new List<string>();
        if(Has(@"(?:איז[הו]|מה|לאיז[הו]|באיז[הו]).{0,24}קבוצ|קבוצ.{0,18}(?:שלי|שייכ|זכא)"))topics.Add("group");
        if(Has(@"(?:מה|כמה|איזה).{0,24}תקציב|תקציב.{0,18}(?:שלי|מגיע|זכא)"))topics.Add("budget");
        if(Has(@"(?:איזה|מה).{0,20}רכב.{0,20}(?:שלי|היום|נוכחי|יש לי)"))topics.Add("current_car");
        if(Has(@"(?:כמה|מה).{0,20}(?:השתתפות|משל[םמ]ת?).{0,20}(?:היום|נוכחי|שלי)"))topics.Add("current_participation");
        if(Has(@"(?:איזה|אילו|באילו).{0,20}רכבים.{0,25}(?:מותר|זכא|אפשר|לבחור)|מה.{0,12}מותר לי לבחור"))topics.Add("eligible_cars");
        if(Has(@"(?:מה|איך).{0,20}(?:מחושב|מחשב|חישוב).{0,20}נטו|איך.{0,20}מחשב.{0,20}הנטו"))topics.Add("calculation");
        if(Has(@"(?:מה|איך|אילו|איזה).{0,20}(?:כללים|תנאים|זכאות).{0,20}(?:משפחתי|ברוכת)|(?:כמה|מאיזה).{0,12}ילדים.{0,15}(?:משפחתי|ברוכת)"))topics.Add("family_rules");
        if(Has(@"(?:מה|כמה).{0,20}(?:הטבה|תוספת|החזר).{0,20}חשמל"))topics.Add("electric_rules");
        if(Has(@"(?:האם|מותר|אפשר).{0,15}(?:אני|לי).{0,15}לוותר|(?:מה|איך).{0,15}(?:כללים|תנאים).{0,15}ויתור"))topics.Add("waiver_rules");
        return topics.ToArray();
    }
    public static string Answer(IEnumerable<string> topics,Employee e,ReferenceData data,PayrollEngine engine)
    {
        var answers=new List<string>();var occupation=data.OccupationFor(e.Occupation);
        if(occupation.Group.Length==0)return IntakeFields.Question("eligibility",data);
        var ownOnly=data.Rules["chooseFrom"]!.GetValue<string>()=="own_group_only";
        foreach(var topic in topics.Distinct().Take(9))
        {
            var answer=topic switch{
                "group"=>$"לפי העיסוק הרשום שלך, {e.Occupation}, קבוצת הזכאות שלך היא {occupation.Group}. "+(ownOnly?"המדיניות מאפשרת בחירה מתוך הקבוצה שלך בלבד.":"אפשר לבחור רכב מהקבוצה שלך או מקבוצה נמוכה ממנה.")+" מקור: מיפוי העיסוקים והמדיניות הפעילה.",
                "budget"=>$"התקציב לקבוצת {occupation.Group} שלך הוא {FinancialText.Number(data.Rules["budgets"]![occupation.Group]!.GetValue<decimal>())} ₪ לחודש, לפי טבלת התקציבים הפעילה. זה תקציב הרכב; הוא אינו תוספת ישירה לנטו או סכום ההשתתפות שלך.",
                "current_car"=>!IntakeFields.Known(e,"currentCar")?"הרכב הנוכחי שלך עדיין לא נמסר. מה הרכב שלך היום?":e.CurrentCar is null?"לפי הנתונים שמסרת, אין לך כיום רכב חברה.":$"הרכב הנוכחי הרשום שלך הוא {data.Cars.Single(c=>c.Id==e.CurrentCar).Brand} {data.Cars.Single(c=>c.Id==e.CurrentCar).Model}. מקור: הפרופיל שלך.",
                "current_participation"=>!IntakeFields.Known(e,"participation")?"עדיין אין סכום השתתפות אישי מאושר לרכב הנוכחי שלך. כמה ההשתתפות שלך היום? אם הסכום לא ידוע, אפשר לברר עם נציג. לא אשתמש בברירת המחדל של הקטלוג במקום הסכום האישי שלך.":$"ההשתתפות הנוכחית שלך היא {FinancialText.Number(e.Participation)} ₪ לחודש, לפי הנתון האישי שנשמר בפרופיל.",
                "eligible_cars"=>"לפי העיסוק והמדיניות הפעילה, הרכבים בקבוצות הזכאות שלך כוללים: "+string.Join(", ",data.Cars.Where(c=>c.Group!="משפחתי"&&engine.Eligibility(e,c).Eligible).Select(c=>c.Brand+" "+c.Model))+". אפשר לבחור דגם מהקטלוג כדי להשוות נטו. המסלול המשפחתי נבדק בנפרד לפי נתוני הילדים.",
                "waiver_rules"=>occupation.Waiver?$"לפי מיפוי העיסוקים, בתפקיד {e.Occupation} אפשר לוותר על הרכב. אחזקת הרכב היא {FinancialText.Number(occupation.Maintenance)} ₪ ברוטו לחודש; השפעתה על הנטו מחושבת עם המס וההפרשות שלך.":$"לפי מיפוי העיסוקים, בתפקיד {e.Occupation} נדרש רכב חברה ולא ניתן לבחור במסלול ויתור.",
                "family_rules"=>$"לפי המדיניות הפעילה, המסלול המשפחתי דורש לפחות {data.Rules["familyTrack"]!["minChildren"]} ילדים עד גיל {data.Rules["familyTrack"]!["childMaxAge"]}. השתתפות העובד בפער מעל התקציב היא {FinancialText.Number(100*data.Rules["familyTrack"]!["gapSplitEmployee"]!.GetValue<decimal>())}%. את הזכאות האישית נבדוק לפי נתוני הילדים שאושרו.",
                "electric_rules"=>$"לפי המדיניות הפעילה, ברכב חשמלי נכללת הטבה של {FinancialText.Number(data.Rules["electric"]!["netBonus"]!.GetValue<decimal>())} ₪ נטו לחודש. שווי השימוש נשלף מהרשומה הרשמית של הדגם, והשתתפות ברכב חדש נלקחת מהקטלוג אלא אם נמסר או אושר הסכם אישי אחר.",
                "calculation"=>"הנטו מחושב בקוד לפי השכר, העמלות והבונוס, המס, נקודות הזיכוי והפרשות העובד. לכל חלופת רכב מתווספים שווי שימוש מהמאגר הרשמי והשתתפות ממקור מתועד; בויתור נבדקת אחזקת הרכב לפי העיסוק. כשהנתון חסר נשלים אותו איתך או עם נציג לפני הצגת נטו סופי.",
                _=>""};
            if(answer.Length>0)answers.Add(answer);
        }
        return string.Join("\n\n",answers);
    }
}
