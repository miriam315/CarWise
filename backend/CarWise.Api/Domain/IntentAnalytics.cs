using System.Text.Json;
using System.Text.Json.Nodes;
using CarWise.Api.Accounting;
using CarWise.Api.Administration;
using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed record IntentPolicy
{
    public bool Enabled {get;init;}=false;
    public int MinimumCohort {get;init;}=5;
    public int RetentionDays {get;init;}=90;
    public bool AiInsightsEnabled {get;init;}=false;
    public string Prompt {get;init;}="תעדף תובנה ניהולית אחת מתוך המזהים שסופקו בלבד. אין להסיק סיבתיות, שיעורי מעבר או חיסכון שלא נמדדו.";
}
public sealed record IntentEvent(DateOnly Day,string Kind,string Target,string Category,string Bucket,string? Reason,long ConfigurationRevision);
public sealed record IntentDocument(long Revision,bool Consented,List<IntentEvent> Events,DateTimeOffset UpdatedAt);
public sealed record IntentMember(string Id,IntentDocument Document);
public sealed record IntentCohort(string Category,bool Suppressed,int? Explored,int? SavedChoice,int? UnselectedAfterSevenDays,
    int? NegativeNet,int? Pending,decimal? TrendPercent,Dictionary<string,int?> Feedback);
public sealed record IntentInsight(string Id,string Title,string Text,string Evidence);
public sealed record IntentScenario(string Category,decimal AdditionalBenefit,int Days=30,decimal? AssumedAdoptionPercent=null,
    decimal? MonthlyOperatingSaving=null,decimal? MonthlyGrantCost=null,decimal? OneOffCost=null);
public sealed record IntentScenarioResult(string Category,bool Suppressed,int? Interested,int? Calculable,int? Missing,int? FinanciallyViableBefore,
    int? FinanciallyViableAfter,int? NewlyViable,List<object> Curve,object? Planning,string Explanation);
public sealed record IntentPreference(string EmployeeId,bool Consent);
public sealed record IntentObservation(string EmployeeId,string Target,string? Reason=null);
public sealed class IntentConflictException:Exception;
public static class IntentRules
{
    public static readonly Dictionary<string,string> Reasons=new(){["net"]="הפער בנטו",["charging"]="גישה לטעינה",["range"]="טווח או התאמה לנסיעות",["timing"]="מועד החלפה או חוזה",["undecided"]="עדיין מתלבטים",["other"]="שיקול אחר"};
    public static void Validate(IntentPolicy policy)
    {
        if(policy is null||policy.MinimumCohort is <5 or >100||policy.RetentionDays is <30 or >365||policy.Prompt is null||policy.Prompt.Length>12000)
            throw new ArgumentException("מחקר כוונות: סף קבוצה 5–100, שמירה 30–365 ימים ופרומפט עד 12,000 תווים");
    }
    public static string Category(ReferenceData data,string target)=>target is "category:electric" or "category:family" or "category:regular"?target[9..]:target=="waiver"?"waiver":data.Cars.SingleOrDefault(c=>c.Id==target) is {} car?
        car.Group=="משפחתי"?"family":car.Drive=="חשמלי"?"electric":"regular":throw new ArgumentException("חלופה אינה בקטלוג הפעיל");
    public static int? Mask(int value,int total,int k)=>value==0?0:value<k||(total-value>0&&total-value<k)?null:value;
    public static List<IntentEvent> Prune(IEnumerable<IntentEvent> events,DateOnly today,int days)=>events.Where(e=>e.Day<=today&&e.Day>=today.AddDays(1-days)).TakeLast(300).ToList();
    public static List<IntentCohort> Summarize(IEnumerable<IntentMember> source,DateOnly today,int days,int k)
    {
        if(days is not (30 or 90))throw new ArgumentException("חלונות הדיווח הם 30 או 90 ימים");
        var members=source.Where(m=>m.Document.Consented).ToList();var start=today.AddDays(1-days);var previous=start.AddDays(-days);
        return new[]{"electric","waiver","family","regular"}.Select(category=>
        {
            var current=members.Where(m=>m.Document.Events.Any(e=>e.Kind=="explore"&&e.Category==category&&e.Day>=start&&e.Day<=today)).ToList();var n=current.Count;
            if(n<k)return new IntentCohort(category,true,null,null,null,null,null,null,Reasons.Keys.ToDictionary(r=>r,_=>(int?)null));
            bool Saved(IntentMember m)=>m.Document.Events.Any(e=>e.Kind=="choice_saved"&&e.Category==category&&e.Day>=start&&e.Day<=today);
            var prior=members.Count(m=>m.Document.Events.Any(e=>e.Kind=="explore"&&e.Category==category&&e.Day>=previous&&e.Day<start));
            var negative=current.Count(m=>m.Document.Events.Any(e=>e.Kind=="explore"&&e.Category==category&&e.Bucket=="loss"&&e.Day>=start&&e.Day<=today));
            var pending=current.Count(m=>m.Document.Events.Any(e=>e.Kind=="explore"&&e.Category==category&&e.Bucket is "pending" or "missing"&&e.Day>=start&&e.Day<=today));
            var unresolved=current.Count(m=>!Saved(m)&&m.Document.Events.Any(e=>e.Kind=="explore"&&e.Category==category&&e.Day>=start&&e.Day<=today.AddDays(-7)));
            var feedback=Reasons.Keys.ToDictionary(reason=>reason,reason=>Mask(current.Count(m=>m.Document.Events.Any(e=>e.Kind=="feedback"&&e.Category==category&&e.Reason==reason&&e.Day>=start&&e.Day<=today)),n,k));
            return new IntentCohort(category,false,n,Mask(current.Count(Saved),n,k),Mask(unresolved,n,k),Mask(negative,n,k),Mask(pending,n,k),prior>=k?decimal.Round((n-prior)*100m/prior,1):null,feedback);
        }).ToList();
    }
    public static ReferenceData ScenarioData(ReferenceData data,string category,decimal addition)
    {
        if(category is not ("electric" or "waiver")||addition<0||addition>5000)throw new ArgumentException("בחרו חשמל או ויתור ותוספת של 0–5,000 ₪");
        var rules=data.Rules.DeepClone();var occupations=data.OccupationDocuments.Select(d=>new Dictionary<string,JsonElement>(d)).ToList();
        if(category=="electric")
        {
            var bonus=rules["electric"]!["netBonus"]!.GetValue<decimal>()+addition;
            if(bonus>5000)throw new ArgumentException("סך הטבת החשמל חורג מ־5,000 ₪");rules["electric"]!["netBonus"]=bonus;
        }
        else foreach(var o in occupations.Where(o=>o["waiver"].GetBoolean()))
        {
            var amount=o["maintenance"].GetDecimal()+addition;if(amount>30000)throw new ArgumentException("אחזקת הרכב חורגת מ־30,000 ₪");
            o["maintenance"]=JsonSerializer.SerializeToElement(amount);
        }
        return ReferenceData.FromSnapshot(new(rules,data.Cars,data.Employees,occupations,data.Official.Values.ToList()),data.OfficialRowCount);
    }
    public static object? Plan(IntentScenario input,int? viable,int k)
    {
        if(input.AssumedAdoptionPercent is <0 or >100||input.MonthlyOperatingSaving is <-20000 or >20000||input.MonthlyGrantCost is <0 or >30000||input.OneOffCost is <0 or >1000000)throw new ArgumentException("הנחות התכנון מחוץ לטווח");
        if(input.MonthlyGrantCost is {} cost&&cost<input.AdditionalBenefit)throw new ArgumentException("עלות הסבסוד המשוערת אינה יכולה להיות נמוכה מהתוספת שהוגדרה");
        if(viable is null||input.AssumedAdoptionPercent is null||input.MonthlyOperatingSaving is null||input.MonthlyGrantCost is null||input.OneOffCost is null)return null;
        var adopters=(int)decimal.Floor(viable.Value*input.AssumedAdoptionPercent.Value/100m);
        if(adopters>0&&(adopters<k||viable.Value-adopters>0&&viable.Value-adopters<k))return new{suppressed=true,label="תוצאת תכנון מוסתרת לקבוצה קטנה"};
        var monthly=input.MonthlyOperatingSaving.Value-input.MonthlyGrantCost.Value;
        return new{suppressed=false,assumedAdopters=adopters,annualSampleNet=adopters*(monthly*12-input.OneOffCost.Value),paybackMonths=monthly>0&&adopters>0?(decimal?)decimal.Round(input.OneOffCost.Value/monthly,2):null,
            label="תרחיש הנחות למדגם בלבד, לא תחזית מעבר ולא חיסכון ארגוני מאומת",assumptions=input};
    }
}
public sealed class IntentAnalytics(IJsonDatabase db,ConfigurationSnapshot snapshot,Repository repository,ILogger<IntentAnalytics> logger)
{
    private const string Prefix="intent:";
    public IntentPolicy Policy=>snapshot.Document.Configuration.IntentAnalytics;
    public ReferenceData Data=>snapshot.Data;
    public async Task<IntentDocument> Read(string id,CancellationToken ct)
    {
        var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",Prefix+id}},limit:1,ct:ct);
        return rows.Count==0?new(0,false,[],DateTimeOffset.UtcNow):JsonDefaults.Read<IntentDocument>(rows[0]!["document"]!.ToJsonString());
    }
    private Task<bool> Write(string id,IntentDocument before,IntentDocument next,CancellationToken ct)=>db.TryWriteRuleAsync(Prefix+id,before.Revision==0?null:before.Revision,JsonNode.Parse(JsonDefaults.Serialize(next with{Revision=before.Revision+1,UpdatedAt=DateTimeOffset.UtcNow}))!,ct);
    public async Task<List<IntentMember>> Members(CancellationToken ct)
    {
        var members=new List<IntentMember>();
        for(var offset=0;;)
        {
            var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix",Prefix}},offset:offset,ct:ct);
            if(rows.Count==0)break;offset+=rows.Count;
            members.AddRange(rows.Select(r=>new IntentMember(r!["id"]!.GetValue<string>()[Prefix.Length..],JsonDefaults.Read<IntentDocument>(r["document"]!.ToJsonString()))));
        }
        return members;
    }
    public async Task<Employee> Employee(string id,CancellationToken ct)
    {
        var reference=Data.Employees.SingleOrDefault(e=>e.Id==id)??throw new ArgumentException("עובד אינו בפרופילים הפעילים");
        var e=(await repository.ConversationAsync(id,ct))?.Employee??reference;
        return new PayrollEngine(Data).Validate(e with{Occupation=reference.Occupation});
    }
    public async Task Preference(string id,bool consent,CancellationToken ct)
    {
        await Employee(id,ct);if(consent&&!Policy.Enabled)throw new ArgumentException("מחקר הכוונות אינו פעיל");
        var old=await Read(id,ct);var next=old with{Consented=consent,Events=consent?IntentRules.Prune(old.Events,DateOnly.FromDateTime(DateTime.UtcNow),Policy.RetentionDays):[]};
        if(!await Write(id,old,next,ct))throw new IntentConflictException();
    }
    public static bool IsWorker(HttpContext context,AccessControl access,string id)=>access.DemoMode||context.Items["caller"] is Caller{IsAdmin:false} c&&c.EmployeeId==id;
    public async Task Capture(string id,string target,string kind,string? reason,CancellationToken ct)
    {
        if(!Policy.Enabled)return;
        var category=IntentRules.Category(Data,target);if(kind is not ("explore" or "choice_saved" or "feedback")||reason is not null&&!IntentRules.Reasons.ContainsKey(reason))throw new ArgumentException("סוג אירוע או סיבת משוב אינם מוכרים");
        var today=DateOnly.FromDateTime(DateTime.UtcNow);
        for(var attempt=0;attempt<3;attempt++)
        {
            var old=await Read(id,ct);if(!old.Consented)return;
            var e=await Employee(id,ct);var bucket="missing";
            if(IntakeFields.Missing(e).Length==0)
            {
                var p=await repository.ParticipationAsync(id,ct);var simulation=new PayrollEngine(Data).Simulate(e,p.Overrides,p.Pending);
                if(target.StartsWith("category:",StringComparison.Ordinal))bucket=simulation.Results.Any(r=>r.Car is not null&&r.Eligible&&CategoryMatches(r.Car,category))?"pending":"ineligible";
                else {var result=simulation.Results.Single(r=>r.Id==target);bucket=!result.Eligible?"ineligible":result.Pending?"pending":result.Delta<0?"loss":result.Delta>0?"gain":"neutral";}
            }
            var events=IntentRules.Prune(old.Events,today,Policy.RetentionDays);
            void Add(string eventKind,string? eventReason){if(!events.Any(v=>v.Day==today&&v.Kind==eventKind&&v.Target==target&&v.Reason==eventReason&&v.Bucket==bucket))events.Add(new(today,eventKind,target,category,bucket,eventReason,snapshot.Document.PolicyVersion));}
            Add("explore",null);if(kind!="explore")Add(kind,reason);
            if(events.SequenceEqual(old.Events))return;
            if(await Write(id,old,old with{Events=events.TakeLast(300).ToList()},ct))return;
        }
        throw new IntentConflictException();
    }
    // A telemetry outage must not break a payroll calculation or an already saved choice.
    public async Task Observe(HttpContext context,AccessControl access,string id,string target,string kind,CancellationToken ct)
    {
        if(!Policy.Enabled||!IsWorker(context,access,id))return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try{await Capture(id,target,kind,null,deadline.Token);}
        catch(Exception error)when(error is not OutOfMemoryException){logger.LogWarning("Intent observation unavailable ({Type}); primary operation continues.",error.GetType().Name);}
    }
    public async Task Purge(CancellationToken ct)
    {
        foreach(var member in await Members(ct))
        {
            var retained=Data.Employees.Any(e=>e.Id==member.Id)?IntentRules.Prune(member.Document.Events,DateOnly.FromDateTime(DateTime.UtcNow),Policy.RetentionDays):[];
            if(retained.Count!=member.Document.Events.Count)await Write(member.Id,member.Document,member.Document with{Events=retained},ct);
        }
    }
    public async Task<object> Summary(int days,CancellationToken ct)
    {
        var today=DateOnly.FromDateTime(DateTime.UtcNow);var members=(await Members(ct)).Where(m=>Data.Employees.Any(e=>e.Id==m.Id)).Select(m=>m with{Document=m.Document with{Events=IntentRules.Prune(m.Document.Events,today,Policy.RetentionDays)}}).ToList();
        var cohorts=IntentRules.Summarize(members,today,days,Policy.MinimumCohort);
        return Report(cohorts,days,false);
    }
    public object Report(List<IntentCohort> cohorts,int days,bool synthetic)=>new{synthetic,generatedAt=DateTimeOffset.UtcNow,days,minimumCohort=Policy.MinimumCohort,collectionEnabled=Policy.Enabled,
        retentionDays=Policy.RetentionDays,cohorts,insights=Insights(cohorts),reasons=IntentRules.Reasons,maximumElectricAddition=5000-Data.Rules["electric"]!["netBonus"]!.GetValue<decimal>(),
        limitations=new[]{"בחירה שמורה אינה עסקה או ביצוע בפועל; אין חיבור לאימות הזמנות או שכר","אין להסיק נטישה או סיבה מהיעדר בחירה; סיבות נאספות רק במשוב מפורש","אין שיוך מאומת לאגפים ולכן אין פילוח אגפי","אין מודל התנהגות מכויל או נתוני נסועה ופליטות; לא מוצגת תחזית מעבר או ESG"}};
    public static List<IntentInsight> Insights(List<IntentCohort> cohorts)
    {
        var cards=new List<IntentInsight>();
        foreach(var c in cohorts.Where(c=>!c.Suppressed))
        {
            var label=c.Category switch{"electric"=>"רכב חשמלי","waiver"=>"ויתור על רכב","family"=>"המסלול המשפחתי",_=>"רכב רגיל"};
            if(c.TrendPercent is >0)cards.Add(new(c.Category+"-trend","עלייה בעניין: "+label,$"מספר העובדים שבדקו את החלופה גדל ב־{c.TrendPercent}% לעומת החלון הקודם באותו אורך. זו עלייה בבדיקות, לא בהזמנות.","עובדים ייחודיים עם הסכמה בכל חלון זמן"));
            if(c.NegativeNet is >0)cards.Add(new(c.Category+"-net","כדאי לבדוק רגישות למדיניות: "+label,$"אצל {c.NegativeNet} מהעובדים שתועדו בבדיקת החלופה נצפה לפחות חישוב אחד עם ירידה בנטו. אפשר לבדוק באיזה סבסוד הכדאיות משתנה; אין בכך הוכחה לסיבת אי־בחירה.","סיווג תוצאת מנוע החישוב בזמן הבדיקה"));
            if(c.Feedback.GetValueOrDefault("net") is >0)cards.Add(new(c.Category+"-feedback","חסם שהעובדים ציינו: "+label,$"{c.Feedback["net"]} עובדים ציינו במפורש שהפער בנטו הוא שיקול. משוב זה אינו מדגם מייצג ואינו תחזית שיעור מעבר.","משוב אופציונלי ומפורש בלבד"));
        }
        return cards;
    }
    private async Task<List<(Employee e,List<string> targets,Dictionary<string,decimal> approved,HashSet<string> pending)>> Candidates(string category,int days,List<IntentMember> members,CancellationToken ct)
    {
        var today=DateOnly.FromDateTime(DateTime.UtcNow);var start=today.AddDays(1-days);var list=new List<(Employee,List<string>,Dictionary<string,decimal>,HashSet<string>)>();
        foreach(var m in members.Where(m=>m.Document.Consented&&Data.Employees.Any(e=>e.Id==m.Id)))
        {
            var events=m.Document.Events.Where(e=>e.Day>=start&&e.Day<=today&&e.Category==category).ToList();
            if(!events.Any(e=>e.Kind=="explore")||events.Any(e=>e.Kind=="choice_saved"))continue;
            var e=await Employee(m.Id,ct);
            if(category=="waiver"&&e.CurrentCar is null||category=="electric"&&Data.Cars.SingleOrDefault(c=>c.Id==e.CurrentCar)?.Drive=="חשמלי")continue;
            var targets=events.Where(e=>e.Kind=="explore").SelectMany(e=>e.Target.StartsWith("category:",StringComparison.Ordinal)?Data.Cars.Where(c=>CategoryMatches(c,category)).Select(c=>c.Id):e.Target=="waiver"||Data.Cars.Any(c=>c.Id==e.Target)?[e.Target]:Array.Empty<string>()).Distinct().ToList();
            var approved=await repository.ParticipationAsync(e.Id,ct);list.Add((e,targets,new(approved.Overrides),new(approved.Pending)));
        }
        return list;
    }
    public async Task<IntentScenarioResult> Scenario(IntentScenario input,CancellationToken ct)
    {
        IntentRules.Plan(input,null,Policy.MinimumCohort);if(input.Days is not (30 or 90))throw new ArgumentException("חלון דיווח לא תקין");
        var changed=IntentRules.ScenarioData(Data,input.Category,input.AdditionalBenefit);
        var today=DateOnly.FromDateTime(DateTime.UtcNow);
        var members=(await Members(ct)).Select(m=>m with{Document=m.Document with{Events=IntentRules.Prune(m.Document.Events,today,Policy.RetentionDays)}}).ToList();
        return Calculate(input,await Candidates(input.Category,input.Days,members,ct),changed,false);
    }
    private static bool CategoryMatches(Car car,string category)=>category=="family"?car.Group=="משפחתי":category=="electric"?car.Drive=="חשמלי"&&car.Group!="משפחתי":car.Group!="משפחתי"&&car.Drive!="חשמלי";
    public IntentScenarioResult Calculate(IntentScenario input,List<(Employee e,List<string> targets,Dictionary<string,decimal> approved,HashSet<string> pending)> candidates,ReferenceData changed,bool synthetic)
    {
        var k=Policy.MinimumCohort;var n=candidates.Count;
        if(n<k)return new(input.Category,true,null,null,null,null,null,null,[],null,"אין קבוצה גדולה מספיק לפרסום תרחיש מצרפי.");
        var originalEngine=new PayrollEngine(Data);
        List<(decimal before,decimal after)> Results(ReferenceData scenario)
        {
            var engine=new PayrollEngine(scenario);var outcomes=new List<(decimal,decimal)>();
            foreach(var candidate in candidates)
            {
                var e=candidate.e;if(IntakeFields.Missing(e).Length>0)continue;
                var baseline=originalEngine.Calculate(e,Data.Cars.SingleOrDefault(c=>c.Id==e.CurrentCar),e.Participation).Net;
                var eligible=candidate.targets.Where(t=>!candidate.pending.Contains(t)).Select(t=>(id:t,car:t=="waiver"?null:Data.Cars.Single(c=>c.Id==t)))
                    .Where(t=>originalEngine.Eligibility(e,t.car).Eligible).Select(t=>(t.car,part:candidate.approved.TryGetValue(t.id,out var p)?(decimal?)p:t.car is null?0:t.car.ParticipationKnown?t.car.Participation:null)).Where(t=>t.part is not null).ToList();
                if(eligible.Count==0)continue;
                outcomes.Add((eligible.Max(t=>originalEngine.Calculate(e,t.car,t.part!.Value).Net)-baseline,eligible.Max(t=>engine.Calculate(e,t.car,t.part!.Value).Net)-baseline));
            }
            return outcomes;
        }
        var results=Results(changed);var before=results.Count(r=>r.before>=0);var after=results.Count(r=>r.after>=0);var cross=results.Count(r=>r.before<0&&r.after>=0);
        int? Mask(int x)=>IntentRules.Mask(x,n,k);var curve=new List<object>();
        foreach(var fraction in new[]{0m,.25m,.5m,.75m,1m})
        {
            var amount=decimal.Round(input.AdditionalBenefit*fraction,2);var point=Results(IntentRules.ScenarioData(Data,input.Category,amount));
            curve.Add(new{additionalBenefit=amount,financiallyViable=Mask(point.Count(p=>p.after>=0)),newlyViable=Mask(point.Count(p=>p.before<0&&p.after>=0))});
        }
        return new(input.Category,false,n,Mask(results.Count),Mask(n-results.Count),Mask(before),Mask(after),Mask(cross),curve,IntentRules.Plan(input,Mask(after),k),
            (synthetic?"נתונים סינתטיים להדגמה בלבד. ":"")+"חישוב רגישות על נתוני השכר הנוכחיים של עובדים שהסכימו, בדקו את המסלול ולא שמרו בחירה בו בחלון הזמן. כדאיות פירושה אי־הפחתת נטו. אין כאן ניבוי התנהגות, אישור החלפה, הזמנה או חיסכון ארגוני מאומת; שינוי המדיניות לא נשמר.");
    }
    public object Demo(IntentScenario input)
    {
        IntentRules.Plan(input,null,Policy.MinimumCohort);if(input.Days is not (30 or 90))throw new ArgumentException("חלון דיווח לא תקין");
        var today=DateOnly.FromDateTime(DateTime.UtcNow);var n=Math.Max(40,Policy.MinimumCohort*4);var people=new List<IntentMember>();
        var samples=new List<(Employee,List<string>,Dictionary<string,decimal>,HashSet<string>)>();var engine=new PayrollEngine(Data);
        var basis=Data.Employees.FirstOrDefault(e=>Data.OccupationFor(e.Occupation).Waiver&&e.CurrentCar is not null)??Data.Employees[0];
        var current=Data.Cars.FirstOrDefault(c=>c.Drive!="חשמלי"&&c.Group!="משפחתי"&&engine.Eligibility(basis,c).Eligible)??Data.Cars.FirstOrDefault(c=>engine.Eligibility(basis,c).Eligible);
        var target=input.Category=="waiver"?"waiver":Data.Cars.FirstOrDefault(c=>c.Drive=="חשמלי"&&c.Group!="משפחתי"&&engine.Eligibility(basis,c).Eligible)?.Id;
        if(current is null||target is null)return new{synthetic=true,report=Report(IntentRules.Summarize([],today,input.Days,Policy.MinimumCohort),input.Days,true),scenario=Calculate(input,[],IntentRules.ScenarioData(Data,input.Category,input.AdditionalBenefit),true)};
        for(var i=0;i<n;i++)
        {
            var e=engine.Validate(basis with{Id="DEMO-INTENT-"+i,Name="",Question="",CurrentModel="",AdvisorState=null,KnownFields=null,Salary=18000+i*100,Commission=1000,Bonus=0,Pension=6,Fund=2.5m,Credits=2.25m,Children=4,Participation=350,CurrentCar=current.Id});
            var events=new List<IntentEvent>{new(today.AddDays(-8),"explore",target,input.Category,i%4==0?"gain":"loss",null,snapshot.Document.PolicyVersion)};
            if(i<n/2)events.Add(new(today.AddDays(-input.Days-8),"explore",target,input.Category,"loss",null,snapshot.Document.PolicyVersion));
            if(i<Policy.MinimumCohort)events.Add(new(today.AddDays(-2),"choice_saved",target,input.Category,"gain",null,snapshot.Document.PolicyVersion));
            if(i<Policy.MinimumCohort*2)events.Add(new(today.AddDays(-3),"feedback",target,input.Category,"loss","net",snapshot.Document.PolicyVersion));
            var overrides=new Dictionary<string,decimal>();
            if(target!="waiver")
            {
                var threshold=engine.Calculate(e,Data.Cars.Single(c=>c.Id==target),0).Net-engine.Calculate(e,Data.Cars.Single(c=>c.Id==e.CurrentCar),e.Participation).Net;
                overrides[target]=Math.Clamp(threshold+(i-Policy.MinimumCohort)*25m,0,20000);
            }
            var observed=engine.Simulate(e,overrides).Results.Single(r=>r.Id==target);
            var bucket=!observed.Eligible?"ineligible":observed.Pending?"pending":observed.Delta<0?"loss":observed.Delta>0?"gain":"neutral";
            events=events.Select(v=>v with{Bucket=bucket}).ToList();
            people.Add(new(e.Id,new(1,true,events,DateTimeOffset.UtcNow)));
            if(i>=Policy.MinimumCohort)samples.Add((e,[target],overrides,[]));
        }
        return new{synthetic=true,report=Report(IntentRules.Summarize(people.Select(p=>p with{Document=p.Document with{Events=IntentRules.Prune(p.Document.Events,today,Policy.RetentionDays)}}),today,input.Days,Policy.MinimumCohort),input.Days,true),scenario=Calculate(input,samples,IntentRules.ScenarioData(Data,input.Category,input.AdditionalBenefit),true)};
    }
}
public sealed class IntentRetentionWorker(IServiceScopeFactory scopes,ILogger<IntentRetentionWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for application startup; privacy retention also runs while collection is paused.
        using var timer=new PeriodicTimer(TimeSpan.FromHours(1));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try{using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<IntentAnalytics>().Purge(stoppingToken);}
            catch(Exception e)when(!stoppingToken.IsCancellationRequested){logger.LogWarning("Intent retention unavailable ({Type}).",e.GetType().Name);}
        }
    }
}
