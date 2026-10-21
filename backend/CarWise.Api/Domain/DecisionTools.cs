using System.Globalization;
using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed record ParticipationBoundary(string Target,string Outcome,decimal RawThreshold,decimal? MaximumWithoutLoss,decimal Minimum,decimal Maximum,string Explanation);
public static class DecisionMath
{
    public static bool SameVehicle(Car left,Car right)=>left.Year==right.Year&&left.MakerCode==right.MakerCode&&left.ModelCode==right.ModelCode;
    public static ParticipationBoundary Boundary(PayrollEngine engine,ReferenceData data,Employee employee,string target)
    {
        var e=engine.Validate(employee);IntakeFields.RequireComplete(e);
        var car=data.Cars.SingleOrDefault(c=>c.Id==target)??throw new ArgumentException("בחרו רכב מתוך הקטלוג");
        if(!engine.Eligibility(e,car).Eligible)throw new ArgumentException("אין זכאות לרכב המבוקש");
        var current=engine.Calculate(e,data.Cars.SingleOrDefault(c=>c.Id==e.CurrentCar),e.Participation);
        // Participation is deducted from net, so the crossing is exact; taxes and EV gross-up
        // are calculated first by the authoritative decimal engine, never by a language model.
        var threshold=engine.Calculate(e,car,0).Net-current.Net;
        var outcome=threshold<0?"never":threshold>=20000?"always":"crosses";
        decimal? maximum=threshold<0?null:Math.Min(20000,decimal.Floor(threshold*100)/100);
        var explanation=outcome=="never"?"גם בהשתתפות אפס החלופה אינה שומרת על הנטו הנוכחי.":outcome=="always"?
            "בכל טווח ההשתתפות המותר לחישוב החלופה שומרת על הנטו הנוכחי.":
            $"עד השתתפות של {maximum!.Value.ToString("N2",CultureInfo.GetCultureInfo("he-IL"))} ₪ החלופה אינה מפחיתה את הנטו; מעל נקודת האיזון היא מפחיתה אותו. זהו תנאי לחישוב בלבד, לא סכום השתתפות מאושר.";
        return new(target,outcome,threshold,maximum,0,20000,explanation);
    }
    public static string Question(PayrollEngine engine,ReferenceData data,Employee employee,string target)=>
        "מהי השתתפות העובד המאושרת ברכב החדש? "+Boundary(engine,data,employee,target).Explanation;
}
public sealed record ReviewedMonth(string Period,Dictionary<string,decimal> Fields,string BonusKind);
public sealed record ChoiceSnapshot(DateTimeOffset SavedAt,long ConfigurationRevision,string OfficialVersionId,Employee Employee,string Target,
    Amounts Current,Alternative Chosen,JsonNode Rules)
{public Car? CurrentCar {get;init;}};
public sealed record DecisionDocument(long Revision,List<ReviewedMonth> Months,ChoiceSnapshot? Choice);
public sealed class DecisionStore(IJsonDatabase db)
{
    private static string Key(string employee)=>"decision:"+employee;
    public async Task<DecisionDocument> Read(string employee,CancellationToken ct)
    {
        var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",Key(employee)}},limit:1,ct:ct);
        return rows.Count==0?new(0,[],null):JsonDefaults.Read<DecisionDocument>(rows[0]!["document"]!.ToJsonString());
    }
    public async Task<DecisionDocument> Write(string employee,DecisionDocument before,List<ReviewedMonth> months,ChoiceSnapshot? choice,CancellationToken ct)
    {
        var next=new DecisionDocument(before.Revision+1,months,choice);
        if(!await db.TryWriteRuleAsync(Key(employee),before.Revision==0?null:before.Revision,JsonNode.Parse(JsonDefaults.Serialize(next))!,ct))
            throw new ArgumentException("הנתונים השתנו בחלון אחר. רעננו ונסו שוב.");
        return next;
    }
}
public sealed record DecisionRequest(Employee Employee,string Target,decimal? Commission=null,decimal? Bonus=null,decimal? HypotheticalParticipation=null,string Axis="commission",decimal RangeMaximum=10000);
public sealed record SaveMonthRequest(Employee Employee,long ExpectedRevision,ReviewedMonth Month);
public sealed record RemoveMonthRequest(Employee Employee,long ExpectedRevision,string Period);
public sealed record SaveChoiceRequest(Employee Employee,long ExpectedRevision,string Target);
public static class DecisionRoutes
{
    public static void MapDecisionTools(this WebApplication app,Action<HttpContext,AccessControl,Employee> authorize)
    {
        app.MapPost("/api/decision/analyze",async(DecisionRequest request,HttpContext context,AccessControl access,ConfigurationSnapshot snapshot,Repository repository,CancellationToken ct)=>
        {
            authorize(context,access,request.Employee);var data=snapshot.Data;var engine=new PayrollEngine(data);
            var original=engine.Validate(request.Employee);IntakeFields.RequireComplete(original);
            if(request.Axis is not ("commission" or "bonus" or "participation")||request.RangeMaximum<100||request.RangeMaximum>(request.Axis=="participation"?20000:200000))throw new ArgumentException("טווח תרחיש לא תקין");
            if(request.HypotheticalParticipation is <0 or >20000)throw new ArgumentException("השתתפות לתרחיש מחוץ לטווח");
            var scenario=engine.Validate(original with{Commission=request.Commission??original.Commission,Bonus=request.Bonus??original.Bonus});
            var approved=await repository.ParticipationAsync(original.Id,ct);
            var car=data.Cars.SingleOrDefault(c=>c.Id==request.Target);
            if(request.Target!="waiver"&&car is null)throw new ArgumentException("בחרו חלופה מתוך הקטלוג");
            if(!engine.Eligibility(scenario,car).Eligible)throw new ArgumentException("אין זכאות לחלופה");
            decimal? known=approved.Overrides.TryGetValue(request.Target,out var amount)?amount:car?.ParticipationKnown==true?car.Participation:car is null?0:null;
            if(approved.Pending.Contains(request.Target))known=null;
            var assumptions=request.HypotheticalParticipation??known;
            object? outcome=null;
            if(assumptions is {} part)
            {
                var now=engine.Calculate(scenario,data.Cars.SingleOrDefault(c=>c.Id==scenario.CurrentCar),scenario.Participation);
                var after=engine.Calculate(scenario,car,part);outcome=new{current=now,alternative=after,delta=after.Net-now.Net};
            }
            var points=new List<object>();
            // Curves for unknown participation are conditional, even when their numbers are
            // displayed in the laboratory. Nothing is saved to the actual employee profile.
            if(assumptions is not null||request.Axis=="participation")for(var i=0;i<=10;i++)
            {
                ct.ThrowIfCancellationRequested();var x=request.RangeMaximum*i/10m;
                var e=scenario with{Commission=request.Axis=="commission"?x:scenario.Commission,Bonus=request.Axis=="bonus"?x:scenario.Bonus};
                var current=engine.Calculate(e,data.Cars.SingleOrDefault(c=>c.Id==e.CurrentCar),e.Participation);
                var curvePart=request.Axis=="participation"?x:assumptions!.Value;var after=engine.Calculate(e,car,curvePart);
                var available=engine.Simulate(e,approved.Overrides,approved.Pending).Results.Where(r=>r.Eligible&&!r.Pending).ToList();
                var hypothetical=new Alternative(after){Id=request.Target,Eligible=true,Delta=after.Net-current.Net};
                available.RemoveAll(r=>r.Id==request.Target||r.Id==e.CurrentCar||(e.CurrentCar is null&&r.Id=="waiver"));
                available.Add(new Alternative(current){Id="current",Eligible=true});available.Add(hypothetical);
                var best=available.OrderByDescending(r=>r.Net).ThenBy(r=>r.Id,StringComparer.Ordinal).First();
                points.Add(new{x,currentNet=current.Net,targetNet=after.Net,delta=after.Net-current.Net,bestTarget=best.Id,bestNet=best.Net});
            }
            return Results.Ok(new{configurationRevision=snapshot.Document.Revision,conditional=known is null||request.HypotheticalParticipation is not null||request.Axis=="participation",profileUnchanged=true,
                boundary=car is null?null:DecisionMath.Boundary(engine,data,scenario,request.Target),outcome,points,
                note="תרחיש בלבד לפי הכללים הפעילים. נקודות הגרף הן דגימות; גבול השתתפות מחושב בדיוק בנפרד. הנתונים המקוריים לא שונו."});
        });
        app.MapPost("/api/decision/month",async(SaveMonthRequest request,HttpContext context,AccessControl access,PayrollEngine engine,DecisionStore store,CancellationToken ct)=>
        {
            authorize(context,access,request.Employee);engine.Validate(request.Employee);var month=request.Month??throw new ArgumentException("חסרים נתוני חודש");
            if(!DateOnly.TryParseExact(month.Period+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var period)||period.Year is <2000 or >2100)throw new ArgumentException("יש לאשר חודש תלוש תקין");
            if(month.Fields is null||month.Fields.Count!=PayslipFields.Limits.Count||month.Fields.Any(p=>!PayslipFields.Limits.TryGetValue(p.Key,out var max)||p.Value<0||p.Value>max))throw new ArgumentException("יש לבדוק ולאשר את כל שבעת שדות השכר");
            if(month.BonusKind is not ("recurring" or "oneOff" or "none")||month.Fields["bonus"]>0&&month.BonusKind=="none"||month.Fields["bonus"]==0&&month.BonusKind!="none")throw new ArgumentException("יש לאשר האם הבונוס קבוע, חד־פעמי או אפס");
            var saved=await store.Read(request.Employee.Id,ct);if(saved.Revision!=request.ExpectedRevision)throw new ArgumentException("הנתונים השתנו. רעננו ונסו שוב.");
            var months=saved.Months.Where(m=>m.Period!=month.Period).Append(month).OrderBy(m=>m.Period,StringComparer.Ordinal).ToList();
            if(months.Count>12)throw new ArgumentException("אפשר לשמור עד 12 חודשי שכר להשוואה");
            return await store.Write(request.Employee.Id,saved,months,saved.Choice,ct);
        });
        app.MapPost("/api/decision/month/remove",async(RemoveMonthRequest request,HttpContext context,AccessControl access,DecisionStore store,CancellationToken ct)=>
        {
            authorize(context,access,request.Employee);var saved=await store.Read(request.Employee.Id,ct);
            if(saved.Revision!=request.ExpectedRevision)throw new ArgumentException("הנתונים השתנו. רעננו ונסו שוב.");
            if(!saved.Months.Any(m=>m.Period==request.Period))throw new ArgumentException("החודש אינו שמור");
            return await store.Write(request.Employee.Id,saved,saved.Months.Where(m=>m.Period!=request.Period).ToList(),saved.Choice,ct);
        });
        app.MapGet("/api/decision/state/{employee}",async(string employee,HttpContext context,AccessControl access,ConfigurationSnapshot snapshot,Repository repository,DecisionStore store,CancellationToken ct)=>
        {
            access.Employee(context,employee);var data=snapshot.Data;var engine=new PayrollEngine(data);var saved=await store.Read(employee,ct);
            var e=(await repository.ConversationAsync(employee,ct))?.Employee??data.Employees.SingleOrDefault(e=>e.Id==employee)??throw new ArgumentException("לא נמצא פרופיל עובד");
            authorize(context,access,e);var approved=await repository.ParticipationAsync(employee,ct);
            var months=saved.Months.Select(month=>
            {
                var f=month.Fields;var known=IntakeFields.All.Where(k=>IntakeFields.Known(e,k)).Union(f.Keys).ToArray();
                var monthly=e with{Salary=f["salary"],Commission=f["commission"],Bonus=f["bonus"],Pension=f["pension"],Fund=f["fund"],Credits=f["credits"],Participation=f["participation"],KnownFields=known};
                JsonNode? actual=null,regular=null;
                if(IntakeFields.Missing(monthly).Length==0)
                {
                    actual=SimulationView.Complete(engine.Simulate(monthly,approved.Overrides,approved.Pending));
                    regular=month.BonusKind=="oneOff"?SimulationView.Complete(engine.Simulate(monthly with{Bonus=0},approved.Overrides,approved.Pending)):actual.DeepClone();
                }
                return new{month.Period,month.Fields,month.BonusKind,actual,regular,missingFields=IntakeFields.Missing(monthly)};
            }).ToArray();
            object? change=null;
            if(saved.Choice is {} choice)
            {
                try
                {
                    var car=data.Cars.SingleOrDefault(c=>c.Id==choice.Target);var before=choice.Chosen;
                    if(car is null&&choice.Target!="waiver")change=new{status="unavailable",message="החלופה שנשמרה אינה בקטלוג הפעיל. נדרשת בחירה מחדש."};
                    else if(car is not null&&before.Car is not null&&!DecisionMath.SameVehicle(car,before.Car)||choice.CurrentCar is {} savedCurrent&&data.Cars.SingleOrDefault(c=>c.Id==choice.Employee.CurrentCar) is {} activeCurrent&&!DecisionMath.SameVehicle(savedCurrent,activeCurrent))
                        change=new{status="review",message="זהות הרכב בקטלוג השתנתה מאז הבחירה: שנת הרישום או קוד היצרן/דגם שונים. יש לבדוק ולבחור מחדש; לא משווים אוטומטית בין שני רכבים כאילו זה עדכון מס בלבד."};
                    else
                    {
                        var newEmployee=engine.Validate(choice.Employee);var simulation=engine.Simulate(newEmployee,approved.Overrides,approved.Pending);var after=simulation.Results.Single(r=>r.Id==choice.Target);
                        var changes=new List<object>();
                        void Changed(string label,decimal oldValue,decimal newValue){if(oldValue!=newValue)changes.Add(new{label,before=oldValue,after=newValue});}
                        if(after.Eligible&&!after.Pending)
                        {
                            Changed("שווי שימוש",before.Value,after.Value);Changed("השתתפות",before.Participation,after.Participation);Changed("תקציב",before.Budget,after.Budget);
                            Changed("הטבת חשמל נטו",before.ElectricNet,after.ElectricNet);Changed("פער משפחתי",before.FamilyGap,after.FamilyGap);
                        }
                        var policyChanged=!JsonNode.DeepEquals(choice.Rules,data.Rules);var versionChanged=choice.OfficialVersionId!=snapshot.Document.Configuration.OfficialVersionId;
                        change=new{status=!after.Eligible?"ineligible":after.Pending?"pending":"ready",policyChanged,officialVersionChanged=versionChanged,
                            previousRevision=choice.ConfigurationRevision,currentRevision=snapshot.Document.Revision,oldNet=before.Net,newNet=after.Eligible&&!after.Pending?(decimal?)after.Net:null,
                            oldDelta=before.Delta,newDelta=after.Eligible&&!after.Pending?(decimal?)after.Delta:null,
                            impact=after.Eligible&&!after.Pending?(decimal?)(after.Net-before.Net):null,changes,
                            explanation="החישוב חוזר על נתוני השכר שנשמרו בזמן הבחירה, לפי המדיניות והמאגר הפעילים בלבד. זו השפעת שינוי נתונים על אותו תרחיש, לא תחזית שכר עתידית."};
                    }
                }
                catch(ArgumentException){change=new{status="review",message="נתוני הבחירה אינם מתאימים לקטלוג או לעיסוק הפעיל. נדרשת בדיקה מחדש."};}
            }
            return new{revision=saved.Revision,months,choice=saved.Choice,change,configurationRevision=snapshot.Document.Revision};
        });
        app.MapPost("/api/decision/choice",async(SaveChoiceRequest request,HttpContext context,AccessControl access,ConfigurationSnapshot snapshot,Repository repository,DecisionStore store,CancellationToken ct)=>
        {
            authorize(context,access,request.Employee);var engine=new PayrollEngine(snapshot.Data);var e=engine.Validate(request.Employee);IntakeFields.RequireComplete(e);
            var approved=await repository.ParticipationAsync(e.Id,ct);var sim=engine.Simulate(e,approved.Overrides,approved.Pending);
            var chosen=sim.Results.SingleOrDefault(r=>r.Id==request.Target)??throw new ArgumentException("חלופה לא תקינה");
            if(!chosen.Eligible||chosen.Pending)throw new ArgumentException("אפשר לשמור למעקב רק בחירה זכאית שסכומיה ידועים ומאושרים");
            var before=await store.Read(e.Id,ct);if(before.Revision!=request.ExpectedRevision)throw new ArgumentException("הנתונים השתנו. רעננו ונסו שוב.");
            var minimized=e with{Name="",Question="",CurrentModel="",AdvisorState=null};
            var choice=new ChoiceSnapshot(DateTimeOffset.UtcNow,snapshot.Document.Revision,snapshot.Document.Configuration.OfficialVersionId,minimized,request.Target,sim.Current,chosen,snapshot.Data.Rules.DeepClone()){CurrentCar=snapshot.Data.Cars.SingleOrDefault(c=>c.Id==e.CurrentCar) is {} currentCar?snapshot.Data.Enrich(currentCar):null};
            return await store.Write(e.Id,before,before.Months,choice,ct);
        });
    }
}
