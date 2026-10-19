using System.Text.Json.Nodes;
namespace CarWise.Api.Domain;

/// <summary>All payroll values, gross-up and differences use decimal; display is the only rounding boundary.</summary>
public sealed class PayrollEngine(ReferenceData data)
{
    private JsonNode N => data.Rules["netMethod"]!;
    private static decimal Number(JsonNode? n) => n?.GetValue<decimal>() ?? throw new InvalidDataException("Missing numeric rule");
    public static decimal Display(decimal value) => decimal.Round(value,0,MidpointRounding.AwayFromZero);
    public Employee Validate(Employee e)
    {
        if(string.IsNullOrWhiteSpace(e.Id) || e.Id.Length>100 || e.Name.Length>100) throw new ArgumentException("מזהה עובד חסר או לא תקין");
        var o=data.OccupationFor(e.Occupation);
        foreach(var (value,max,name) in new[]{(e.Salary,200000m,"salary"),(e.Commission,200000m,"commission"),
            (e.Bonus,200000m,"bonus"),(e.Pension,20m,"pension"),(e.Fund,20m,"fund"),(e.Credits,30m,"credits"),
            (e.Children,30m,"children"),(e.Participation,20000m,"participation")})
            if(value<0 || value>max) throw new ArgumentException("נתון מחוץ לטווח: "+name);
        if(decimal.Truncate(e.Children)!=e.Children) throw new ArgumentException("מספר הילדים חייב להיות שלם");
        if(e.CurrentCar is not null && !data.Cars.Any(c=>c.Id==e.CurrentCar)) throw new ArgumentException("הרכב הנוכחי אינו בקטלוג");
        if(e.CurrentCar is null && !o.Waiver) throw new ArgumentException("בעיסוק זה נדרש רכב נוכחי");
        return e with {Group=o.Group};
    }
    public (bool Eligible,string Reason) Eligibility(Employee e,Car? car)
    {
        var o=data.OccupationFor(e.Occupation);
        if(car is null) return (o.Waiver,o.Waiver?"העיסוק מאפשר ויתור על רכב":"בתפקיד שטח הרכב הוא כלי עבודה ולכן אי אפשר לוותר עליו");
        if(car.Group=="משפחתי")
        {
            var min=Number(data.Rules["familyTrack"]!["minChildren"]);var age=Number(data.Rules["familyTrack"]!["childMaxAge"]);var ok=e.Children>=min;
            return (ok,ok?$"מסלול משפחתי: לפחות {min} ילדים עד גיל {age}; פער התקציב מתחלק לפי המדיניות":$"המסלול המשפחתי דורש לפחות {min} ילדים עד גיל {age}");
        }
        var order=data.Rules["groupOrder"]!.AsArray().Select(x=>x!.GetValue<string>()).ToList();
        var ownOnly=data.Rules["chooseFrom"]!.GetValue<string>()=="own_group_only";
        var eligible=ownOnly?car.Group==o.Group:order.IndexOf(car.Group)<=order.IndexOf(o.Group);
        return (eligible,eligible?(ownOnly?"הרכב בקבוצת הזכאות שלך":"הרכב בקבוצת הזכאות שלך או בקבוצה נמוכה ממנה"):(ownOnly?"המדיניות מאפשרת בחירה רק מתוך קבוצת הזכאות שלך":"הרכב בקבוצה גבוהה מקבוצת הזכאות שלך"));
    }
    public Amounts Calculate(Employee employee,Car? car,decimal? participation=null)
    {
        var e=Validate(employee); var occupation=data.OccupationFor(e.Occupation);
        var gross=e.Salary+e.Commission+e.Bonus;
        var maintenance=car is null?occupation.Maintenance:0m;var value=car is null?0m:data.Lookup(car).Value;
        var contributions=N["contributionsBase"]!.GetValue<string>()=="base"?e.Salary:gross;
        if(data.Rules["waiver"]!["maintenanceIsPensionable"]!.GetValue<bool>())contributions+=maintenance;
        var pension=contributions*e.Pension/100m;var fund=contributions*e.Fund/100m;
        var taxableMaintenance=data.Rules["waiver"]!["maintenanceIsTaxable"]!.GetValue<bool>()?maintenance:0m;
        var part=car is null?0m:participation??car.Participation;
        if(part<0 || part>20000) throw new ArgumentException("השתתפות לא תקינה");
        var budget=Number(data.Rules["budgets"]![occupation.Group]);
        var family=car?.Group=="משפחתי"?Math.Max(0m,car.Cost-budget)*Number(data.Rules["familyTrack"]!["gapSplitEmployee"]):0m;
        (decimal Net,decimal Tax,decimal Insurance,decimal Taxable) Payroll(decimal extra)
        {
            var taxable=gross+taxableMaintenance+value+extra;var tax=0m;var lower=0m;
            foreach(var b in N["brackets"]!.AsArray())
            {
                var upper=b![0] is null?taxable:Number(b[0]);
                tax+=Math.Max(0m,Math.Min(taxable,upper)-lower)*Number(b[1]);lower=upper;
            }
            var p=N["pensionCredit"]!;
            var pensionCredit=Math.Min(pension,Number(p["maxContributionRate"])*Number(p["incomeCeiling"]))*Number(p["rate"]);
            tax=Math.Max(0m,tax-e.Credits*Number(N["creditPointValue"])-pensionCredit);
            var insurance=0m;
            foreach(var name in new[]{"nationalInsurance","health"})
            {
                var rule=N[name]!;var basis=Math.Max(0m,Math.Min(taxable,Number(rule["ceiling"])));
                insurance+=Math.Min(basis,Number(rule["threshold"]))*Number(rule["lowRate"])
                    +Math.Max(0m,basis-Number(rule["threshold"]))*Number(rule["highRate"]);
            }
            return(gross+maintenance+extra-tax-insurance-pension-fund-part-family,tax,insurance,taxable);
        }
        var extra=0m;var initial=Payroll(0m);
        if(car?.Drive=="חשמלי")
        {
            var target=Number(data.Rules["electric"]!["netBonus"]);
            if(data.Rules["electric"]!["method"]!.GetValue<string>()=="gross_up")
            {
                var low=0m;var high=target*100m;
                for(var i=0;i<100;i++)
                {
                    var mid=(low+high)/2m;
                    if(mid==low || mid==high) break;
                    if(Payroll(mid).Net-initial.Net<target) low=mid;else high=mid;
                }
                extra=(low+high)/2m;
            }
            else extra=target;
        }
        var output=Payroll(extra);
        return new(){Net=output.Net,Tax=output.Tax,Insurance=output.Insurance,Taxable=output.Taxable,Gross=gross,
            Pension=pension,Fund=fund,Value=value,Participation=part,FamilyGap=family,Maintenance=maintenance,
            ElectricGross=extra,ElectricNet=output.Net-initial.Net,Budget=budget,Cost=car?.Cost??0m};
    }
    public Simulation Simulate(Employee employee,IReadOnlyDictionary<string,decimal>? overrides=null,IReadOnlySet<string>? pending=null)
    {
        var e=Validate(employee);var currentCar=data.Cars.SingleOrDefault(c=>c.Id==e.CurrentCar);
        var before=Calculate(e,currentCar,e.Participation);var results=new List<Alternative>();
        foreach(var car in data.Cars.Cast<Car?>().Append(null))
        {
            var id=car?.Id??"waiver";var (ok,reason)=Eligibility(e,car);
            if(!ok){results.Add(new(){Id=id,Car=car is null?null:data.Enrich(car),Eligible=false,Reason=reason});continue;}
            decimal? part=overrides?.TryGetValue(id,out var p)==true?p:null;
            var values=Calculate(e,car,part);
            results.Add(new(values){Id=id,Car=car is null?null:data.Enrich(car),Eligible=true,Reason=reason,
                Delta=values.Net-before.Net,Annual=(values.Net-before.Net)*12m,Pending=pending?.Contains(id)==true});
        }
        return new(e,before,results);
    }
}
