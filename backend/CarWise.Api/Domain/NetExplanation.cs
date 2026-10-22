using System.Globalization;

namespace CarWise.Api.Domain;

public sealed record NetDriver(string Id,string Title,string Description,decimal Impact);
public sealed record NetExplanation(decimal MonthlyChange,decimal AnnualChange,IReadOnlyList<NetDriver> Drivers)
{
    private static string Money(decimal amount)=>PayrollEngine.Display(amount).ToString("N0",CultureInfo.GetCultureInfo("he-IL"))+" ₪";
    public static NetExplanation Compare(Amounts before,Amounts after)
    {
        var drivers=new List<NetDriver>
        {
            new("taxes","מסים ושווי שימוש",
                $"שווי השימוש: {Money(before.Value)} היום, {Money(after.Value)} בחלופה. זהו בסיס למס, לא סכום שיורד מהנטו. ההשפעה כאן היא השינוי הכולל במס הכנסה, ביטוח לאומי ובריאות, כולל מס על תוספות חייבות.",
                before.Tax+before.Insurance-after.Tax-after.Insurance),
            new("participation","השתתפות ברכב",
                $"השתתפות חודשית מ־{Money(before.Participation)} ל־{Money(after.Participation)}; חלק העובד בפער המסלול המשפחתי מ־{Money(before.FamilyGap)} ל־{Money(after.FamilyGap)}.",
                before.Participation+before.FamilyGap-after.Participation-after.FamilyGap),
            new("income","החזרים והפרשות",
                $"אחזקת רכב מ־{Money(before.Maintenance)} ל־{Money(after.Maintenance)}; החזר חשמל ברוטו מ־{Money(before.ElectricGross)} ל־{Money(after.ElectricGross)}. הפרשות עובד לפנסיה ולהשתלמות מ־{Money(before.Pension+before.Fund)} ל־{Money(after.Pension+after.Fund)}; נכללים גם שינויי שכר ברוטו. המס על התוספות מופיע ברכיב המסים.",
                after.Gross-before.Gross+after.Maintenance-before.Maintenance+after.ElectricGross-before.ElectricGross+before.Pension+before.Fund-after.Pension-after.Fund)
        };
        var delta=after.Net-before.Net;
        return new(delta,delta*12,drivers.OrderByDescending(d=>Math.Abs(d.Impact)).ToList());
    }
}
