using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using CarWise.Api.Administration;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed record FleetPolicy
{
    // No company-specific replacement rule is asserted until an administrator enables it.
    public bool Enabled {get;init;}=false;
    public int ReplacementMonths {get;init;}=36;
    public bool SwapEnabled {get;init;}=true;
    public bool ShowToEmployees {get;init;}=true;
    public bool AllowEarlySwap {get;init;}=true;
    public int RequestValidityDays {get;init;}=30;
    public string[] ExceptionReasons {get;init;}=["role","family","location","medical","vehicle"];
}
public sealed record FleetAssignment(string Id,string EmployeeId,string CarId,DateOnly AssignedOn,DateOnly? LeaseEndsOn,
    decimal? EarlyReturnFee,bool TransferAllowed,string? ExceptionReason,DateOnly? ExceptionUntil,
    string VehicleKey,Guid Stamp,string Actor,DateTimeOffset UpdatedAt);
public sealed record FleetRequest(Guid Id,string EmployeeId,string Category,string AssignmentId,Guid AssignmentStamp,
    DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,bool Open=true);
public sealed record FleetProposal(Guid Id,Guid Left,Guid Right,string[] Consents,string Status,DateTimeOffset CreatedAt,
    string? Officer=null,string? Note=null,DateTimeOffset? DecidedAt=null,string Basis="")
{
    public DateOnly? HandedOverOn {get;init;}
    public string? HandoverNote {get;init;}
    public string? HandoverActor {get;init;}
}
public sealed record FleetAudit(DateTimeOffset At,string Actor,string Action,string Subject);
public sealed record FleetDocument(long Revision,List<FleetAssignment> Assignments,List<FleetRequest> Requests,List<FleetProposal> Proposals,List<FleetAudit> Audit);
public sealed record ReplacementStatus(string Status,DateOnly? EligibleOn,bool CanReplace,string Message)
{public int PendingOffers {get;init;}}
public sealed record FleetNet(decimal? Delta,bool Pending,string Message);
public sealed class FleetConflictException:Exception;
public static class FleetRules
{
    public static readonly Dictionary<string,string> Reasons=new(){["role"]="שינוי תפקיד או זכאות",["family"]="שינוי בצורכי המשפחה",["location"]="שינוי מיקום או נסיעות",["medical"]="צורך רפואי שאושר",["vehicle"]="תקלה או בעיה ברכב"};
    public static void Validate(FleetPolicy policy)
    {
        if(policy is null||policy.ReplacementMonths is <1 or >120||policy.RequestValidityDays is <1 or >90||policy.ExceptionReasons is null||policy.ExceptionReasons.Distinct().Count()!=policy.ExceptionReasons.Length||policy.ExceptionReasons.Any(r=>!Reasons.ContainsKey(r)))
            throw new ArgumentException("מדיניות החלפה: תקופה 1–120 חודשים, תוקף בקשה 1–90 ימים וסיבות חריג מוכרות בלבד");
    }
    public static string Key(Car car)=>$"{car.Year}/{car.MakerCode}/{car.ModelCode}";
    public static bool Accepts(string category,Car car)=>category switch{"electric"=>car.Drive=="חשמלי"&&car.Group!="משפחתי","family"=>car.Group=="משפחתי","regular"=>car.Group!="משפחתי"&&car.Drive!="חשמלי",_=>false};
    public static bool Category(string text,out string category)
    {
        category=text.Contains("ברוכת")||text.Contains("מרובת")||text.Contains("7 מקומות")?"family":text.Contains("חשמלי")?"electric":text.Contains("רכב רגיל")?"regular":"";
        return category!=""&&(text.Contains("החלפ")||text.Contains("להחליף")||text.Contains("חוזה")||text.Contains("ליסינג")||text.Contains("לעבור"));
    }
    public static ReplacementStatus Replacement(FleetPolicy policy,FleetAssignment? assignment,DateOnly today)
    {
        if(!policy.Enabled)return new("unconfigured",null,false,"מדיניות מועדי ההחלפה טרם הופעלה בניהול. אין להסיק זכאות או קנס.");
        if(assignment is null)return new("unknown",null,false,"חסרים נתוני הקצאת רכב מאומתים. קצין הרכב צריך להזין מועד קבלה ונתוני חוזה.");
        var due=assignment.AssignedOn.AddMonths(policy.ReplacementMonths);
        if(assignment.LeaseEndsOn is {} lease&&lease<due)due=lease;
        if(assignment.ExceptionReason is {} reason&&policy.ExceptionReasons.Contains(reason)&&assignment.ExceptionUntil>=today)
            return new("exception",due,true,"אושר חריג בתוקף: "+Reasons[reason]+". המעבר עצמו עדיין דורש תיאום ואישור קצין רכב.");
        return today>=due?new("due",due,true,"הגיע מועד ההחלפה לפי המדיניות או סיום החוזה המאומת; נדרש תיאום עם קצין הרכב."):
            new("locked",due,false,$"מועד ההחלפה הרגיל: {due:dd/MM/yyyy}. "+(policy.ShowToEmployees&&policy.SwapEnabled?"אפשר לבקש חריג מאושר או לבדוק החלפה פנימית, לפי המדיניות.":"אפשר לפנות לקצין הרכב לבדיקת חריג מאושר, לפי המדיניות."));
    }
    public static bool Active(FleetRequest request,FleetDocument doc,DateTimeOffset now)=>request.Open&&request.ExpiresAt>now&&doc.Assignments.Any(a=>a.Id==request.AssignmentId&&a.EmployeeId==request.EmployeeId&&a.Stamp==request.AssignmentStamp);
    public static bool Reserved(FleetDocument doc,string employee)=>doc.Proposals.Any(p=>p.Status=="approved"&&(doc.Requests.Any(r=>(r.Id==p.Left||r.Id==p.Right)&&r.EmployeeId==employee)));
    public static bool Mutual(FleetDocument doc,FleetRequest left,FleetRequest right,FleetPolicy policy,ReferenceData data,Employee le,Employee re,DateTimeOffset now)
    {
        if(!policy.Enabled||!policy.SwapEnabled||left.EmployeeId==right.EmployeeId||!Active(left,doc,now)||!Active(right,doc,now)||Reserved(doc,left.EmployeeId)||Reserved(doc,right.EmployeeId))return false;
        var la=doc.Assignments.Single(a=>a.Id==left.AssignmentId);var ra=doc.Assignments.Single(a=>a.Id==right.AssignmentId);
        var lc=data.Cars.SingleOrDefault(c=>c.Id==la.CarId);var rc=data.Cars.SingleOrDefault(c=>c.Id==ra.CarId);
        if(lc is null||rc is null||lc.Id==rc.Id||la.Id==ra.Id||!la.TransferAllowed||!ra.TransferAllowed||Key(lc)!=la.VehicleKey||Key(rc)!=ra.VehicleKey||le.CurrentCar!=la.CarId||re.CurrentCar!=ra.CarId)return false;
        // An expired lease is no longer offered as a live internal reassignment.
        var today=DateOnly.FromDateTime(now.UtcDateTime);
        if(la.LeaseEndsOn<today||ra.LeaseEndsOn<today)return false;
        var engine=new PayrollEngine(data);
        if(!Accepts(left.Category,rc)||!Accepts(right.Category,lc)||!engine.Eligibility(le,rc).Eligible||!engine.Eligibility(re,lc).Eligible)return false;
        return policy.AllowEarlySwap||(Replacement(policy,la,today).CanReplace&&Replacement(policy,ra,today).CanReplace);
    }
}
public sealed class FleetStore(IJsonDatabase db)
{
    private const string Key="fleet:active";
    public async Task<FleetDocument> Read(CancellationToken ct)
    {
        var rows=await db.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id",Key}},limit:1,ct:ct);
        return rows.Count==0?new(0,[],[],[],[]):JsonDefaults.Read<FleetDocument>(rows[0]!["document"]!.ToJsonString());
    }
    public async Task<FleetDocument> Write(FleetDocument before,FleetDocument next,long expected,string actor,string action,string subject,CancellationToken ct)
    {
        if(before.Revision!=expected)throw new FleetConflictException();
        if(next.Assignments.Count>5000||next.Requests.Count>5000||next.Proposals.Count>5000)throw new ArgumentException("מאגר הצי הגיע למגבלת הרשומות; נדרש ארכוב בניהול");
        next=next with{Revision=before.Revision+1,Audit=before.Audit.Append(new(DateTimeOffset.UtcNow,actor,action,subject)).TakeLast(200).ToList()};
        if(!await db.TryWriteRuleAsync(Key,before.Revision==0?null:before.Revision,JsonNode.Parse(JsonDefaults.Serialize(next))!,ct))throw new FleetConflictException();
        return next;
    }
}
public sealed class FleetService(FleetStore store,ConfigurationSnapshot snapshot,Repository repository)
{
    public FleetPolicy Policy=>snapshot.Document.Configuration.Fleet;
    public ReferenceData Data=>snapshot.Data;
    public FleetAssignment? Assignment(FleetDocument doc,Employee employee)
    {
        var a=doc.Assignments.SingleOrDefault(a=>a.EmployeeId==employee.Id);
        return a is not null&&a.CarId==employee.CurrentCar&&Data.Cars.SingleOrDefault(c=>c.Id==a.CarId) is {} car&&FleetRules.Key(car)==a.VehicleKey?a:null;
    }
    public async Task<Employee> Employee(string id,CancellationToken ct)
    {
        var reference=Data.Employees.SingleOrDefault(e=>e.Id==id)??throw new ArgumentException("העובד אינו במאגר הפעיל");
        var saved=(await repository.ConversationAsync(id,ct))?.Employee??reference;
        // Occupation/entitlement comes from the administrator, never a stale saved role.
        return new PayrollEngine(Data).Validate(saved with{Occupation=reference.Occupation});
    }
    public async Task<bool> Mutual(FleetDocument doc,FleetRequest left,FleetRequest right,CancellationToken ct)
    {
        if(left.EmployeeId==right.EmployeeId||!FleetRules.Active(left,doc,DateTimeOffset.UtcNow)||!FleetRules.Active(right,doc,DateTimeOffset.UtcNow)||!Data.Employees.Any(e=>e.Id==left.EmployeeId)||!Data.Employees.Any(e=>e.Id==right.EmployeeId))return false;
        return FleetRules.Mutual(doc,left,right,Policy,Data,await Employee(left.EmployeeId,ct),await Employee(right.EmployeeId,ct),DateTimeOffset.UtcNow);
    }
    public async Task<string> Basis(FleetRequest left,FleetRequest right,CancellationToken ct)
    {
        async Task<object> Source(string id)
        {
            var e=await Employee(id,ct);var approved=await repository.ParticipationAsync(id,ct);
            return new{employee=e with{Name="",Question="",CurrentModel="",AdvisorState=null},approved.Overrides,approved.Pending};
        }
        var serialized=JsonDefaults.Serialize(new{configurationRevision=snapshot.Document.PolicyVersion,left=await Source(left.EmployeeId),right=await Source(right.EmployeeId),left.AssignmentStamp,rightStamp=right.AssignmentStamp});
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }
    public async Task<FleetNet> Net(Employee employee,string incoming,CancellationToken ct)
    {
        if(IntakeFields.Missing(employee).Length>0)return new(null,true,"חסרים נתוני שכר מאושרים להשוואה");
        var approved=await repository.ParticipationAsync(employee.Id,ct);
        var sim=new PayrollEngine(Data).Simulate(employee,approved.Overrides,approved.Pending);
        var result=sim.Results.SingleOrDefault(r=>r.Id==incoming);
        return result is null||!result.Eligible||result.Pending?new(null,true,"נדרש אישור זכאות או השתתפות; לא מוצג נטו סופי"):
            new(result.Delta,false,"לפי מנוע החישוב, השכר וההשתתפות המאושרים. תנאי ההשתתפות ברכב המועבר יאושרו שוב לפני מסירה.");
    }
    public async Task<object> View(string id,CancellationToken ct)
    {
        var doc=await store.Read(ct);var e=await Employee(id,ct);var assignment=Assignment(doc,e);
        var own=doc.Requests.SingleOrDefault(r=>r.EmployeeId==id&&FleetRules.Active(r,doc,DateTimeOffset.UtcNow));
        var matches=new List<object>();
        if(own is not null)foreach(var other in doc.Requests.Where(r=>r.EmployeeId!=id))if(await Mutual(doc,own,other,ct))
        {
            var incoming=doc.Assignments.Single(a=>a.Id==other.AssignmentId);var car=Data.Cars.Single(c=>c.Id==incoming.CarId);
            matches.Add(new{requestId=other.Id,car=Data.Enrich(car),net=await Net(e,car.Id,ct)});
        }
        var proposals=new List<object>();
        foreach(var p in doc.Proposals)
        {
            var l=doc.Requests.Single(r=>r.Id==p.Left);var r=doc.Requests.Single(r=>r.Id==p.Right);if(l.EmployeeId!=id&&r.EmployeeId!=id)continue;
            var other=l.EmployeeId==id?r:l;var a=doc.Assignments.SingleOrDefault(a=>a.Id==other.AssignmentId);
            var valid=await Mutual(doc,l,r,ct)&&p.Basis==await Basis(l,r,ct);
            proposals.Add(new{p.Id,p.Status,p.CreatedAt,p.Note,p.HandedOverOn,p.HandoverNote,ownConsent=p.Consents.Contains(id),bothConsented=p.Consents.Length==2,valid,
                car=Data.Cars.SingleOrDefault(c=>c.Id==a?.CarId) is {} car?Data.Enrich(car):null});
        }
        var engine=new PayrollEngine(Data);
        return new{revision=doc.Revision,policy=Policy,replacement=FleetRules.Replacement(Policy,assignment,DateOnly.FromDateTime(DateTime.UtcNow)),assignment,
            request=own,matches,proposals,categories=new[]{"electric","family","regular"}.Where(cat=>Data.Cars.Any(c=>FleetRules.Accepts(cat,c)&&engine.Eligibility(e,c).Eligible)),
            reserved=FleetRules.Reserved(doc,id),notice="החלפה דורשת הסכמה של שני העובדים ואישור קצין הרכב. אישור אינו מסירה או שינוי שכר. אין הבטחה לביטול עמלות ללא בדיקת החוזים."};
    }
    public async Task<string> ReplacementAdvice(Employee employee,CancellationToken ct)
    {
        var doc=await store.Read(ct);var assignment=Assignment(doc,employee);
        var status=FleetRules.Replacement(Policy,assignment,DateOnly.FromDateTime(DateTime.UtcNow));
        return Policy.Enabled?$"לפי מדיניות הצי הפעילה, פרק הזמן הרגיל להחלפה הוא {Policy.ReplacementMonths} חודשים, בכפוף לסיום חוזה או חריג מאושר. "+status.Message:status.Message;
    }
    public async Task<string> Advice(Employee employee,string category,CancellationToken ct)
    {
        var doc=await store.Read(ct);var a=Assignment(doc,employee);
        var status=FleetRules.Replacement(Policy,a,DateOnly.FromDateTime(DateTime.UtcNow));
        if(!Policy.Enabled)return status.Message+" ניתן להמשיך בחישובי הנטו; בדיקת מועד ההחלפה תתאפשר לאחר הגדרת המדיניות.";
        if(!Data.Cars.Any(c=>FleetRules.Accepts(category,c)&&new PayrollEngine(Data).Eligibility(employee,c).Eligible))return status.Message+" כרגע אין לך זכאות לרכב מהמסלול המבוקש לפי הנתונים והכללים הפעילים.";
        if(a is null)return status.Message+" אמירה בשיחה על הזמן שנותר לחוזה אינה מחליפה נתון מאומת.";
        var temporary=new FleetRequest(Guid.NewGuid(),employee.Id,category,a.Id,a.Stamp,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(1));
        var withRequest=doc with{Requests=doc.Requests.Append(temporary).ToList()};
        var found=new List<FleetRequest>();
        foreach(var other in doc.Requests.Where(r=>r.EmployeeId!=employee.Id))if(await Mutual(withRequest,temporary,other,ct))found.Add(other);
        var fee=a.EarlyReturnFee is {} amount?$" דמי החזרה מוקדמת שנרשמו בניהול: {amount:N2} ₪; החיוב בפועל דורש בדיקת חוזה.":" לא הוזנו דמי החזרה מאומתים, ולכן איני קובע שיש קנס או מחשב חיסכון ממנו.";
        if(found.Count==0)return status.Message+fee+" כרגע לא נמצאה בקשת החלפה הדדית מתאימה. במסך ״החלפות רכבים״ אפשר לפרסם בקשה בהסכמה, ולבדוק שוב כשיתווספו בקשות.";
        var incoming=doc.Assignments.Single(x=>x.Id==found[0].AssignmentId);var net=await Net(employee,incoming.CarId,ct);
        var delta=net.Delta is {} d?$" הפער החודשי המחושב בנטו בחלופה שנמצאה: {d:+#,##0.00;-#,##0.00;0} ₪.":" אין עדיין נטו סופי לחלופה: "+net.Message+".";
        return status.Message+delta+$" זיהיתי הזדמנות עבורך: נמצאו {found.Count} בקשות הדדיות מתאימות של עובדים שהסכימו להשתתף, כולל {Data.Cars.Single(c=>c.Id==incoming.CarId).Model}."+fee+" במסך ״החלפות רכבים״ אפשר לפרסם בקשה ולהציע החלפה. רק לאחר הסכמה של שניכם הבקשה תועבר לקצין הרכב; אין התחייבות לביטול עמלות.";
    }
}
public sealed record FleetAssignmentInput(long ExpectedRevision,string Id,string EmployeeId,string CarId,DateOnly AssignedOn,DateOnly? LeaseEndsOn,decimal? EarlyReturnFee,bool TransferAllowed,string? ExceptionReason=null,DateOnly? ExceptionUntil=null);
public sealed record FleetRequestInput(long ExpectedRevision,string EmployeeId,string Category,bool Consent);
public sealed record FleetAction(long ExpectedRevision,string EmployeeId,Guid Id,bool Accept=true);
public sealed record FleetDecision(long ExpectedRevision,Guid Id,bool Approve,string Note);
public sealed record FleetHandover(long ExpectedRevision,Guid Id,DateOnly HandoverOn,string Note);
public static class FleetRoutes
{
    private static string Actor(HttpContext context)=>context.Items["caller"] is Caller c?c.UserId:"local-development-officer";
    public static void MapFleet(this WebApplication app)
    {
        var group=app.MapGroup("/api/fleet");
        group.AddEndpointFilter(async(ctx,next)=>{try{return await next(ctx);}catch(FleetConflictException){return Results.Conflict(new{error="נתוני הצי השתנו. רעננו ובדקו את ההחלפה לפני ניסיון נוסף."});}});
        group.MapGet("/state/{employee}",async(string employee,HttpContext ctx,AccessControl access,FleetService service,CancellationToken ct)=>{access.Employee(ctx,employee);return !service.Policy.ShowToEmployees?Results.NotFound(new{error="אפשרות ההחלפות אינה מוצגת לעובדים בארגון"}):Results.Ok(await service.View(employee,ct));});
        group.MapGet("/status/{employee}",async(string employee,HttpContext ctx,AccessControl access,FleetService service,FleetStore store,CancellationToken ct)=>{
            access.Employee(ctx,employee);var e=await service.Employee(employee,ct);var doc=await store.Read(ct);
            return FleetRules.Replacement(service.Policy,service.Assignment(doc,e),DateOnly.FromDateTime(DateTime.UtcNow)) with{
                PendingOffers=service.Policy.ShowToEmployees?doc.Proposals.Count(p=>p.Status=="pendingConsent"&&!p.Consents.Contains(employee)&&doc.Requests.Any(r=>(r.Id==p.Left||r.Id==p.Right)&&r.EmployeeId==employee)):0};
        });
        group.MapGet("/officer",async(HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Agent(ctx);var doc=await store.Read(ct);
            return new{doc.Revision,doc.Assignments,doc.Requests,doc.Proposals,doc.Audit,canManage=access.CanAdmin(ctx),policy=service.Policy,reasons=FleetRules.Reasons,
                employees=service.Data.Employees.Select(e=>new{e.Id,e.Name,e.CurrentCar}),cars=service.Data.Cars.Select(c=>new{c.Id,c.Model,c.Group,c.Drive})};
        });
        group.MapPost("/assignment",async(FleetAssignmentInput request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Admin(ctx);var doc=await store.Read(ct);var e=await service.Employee(request.EmployeeId,ct);var today=DateOnly.FromDateTime(DateTime.UtcNow);
            if(!Regex.IsMatch(request.Id??"",@"^[A-Za-z0-9_-]{1,80}$")||request.AssignedOn.Year<2000||request.AssignedOn>today||request.LeaseEndsOn<request.AssignedOn||request.LeaseEndsOn?.Year>2100||request.EarlyReturnFee is <0 or >1000000)throw new ArgumentException("נתוני הקצאה או חוזה אינם תקינים");
            var car=service.Data.Cars.SingleOrDefault(c=>c.Id==request.CarId)??throw new ArgumentException("רכב לא נמצא בקטלוג");
            if(e.CurrentCar!=car.Id)throw new ArgumentException("ההקצאה חייבת להתאים לרכב הנוכחי בפרופיל העובד; יש לתקן את נתוני המקור תחילה");
            if(request.ExceptionReason is not null&&(!service.Policy.ExceptionReasons.Contains(request.ExceptionReason)||request.ExceptionUntil is null||request.ExceptionUntil<today||request.ExceptionUntil>today.AddYears(1))||request.ExceptionReason is null&&request.ExceptionUntil is not null)throw new ArgumentException("חריג דורש סיבה מותרת ותאריך תוקף עד שנה קדימה");
            if(FleetRules.Reserved(doc,e.Id)||doc.Assignments.Any(a=>a.Id==request.Id&&FleetRules.Reserved(doc,a.EmployeeId)))throw new ArgumentException("קיימת החלפה מאושרת. יש לבטל את האישור לפני שינוי הקצאה");
            if(doc.Assignments.Any(a=>a.EmployeeId==e.Id&&a.Id!=request.Id)||doc.Assignments.Any(a=>a.Id==request.Id&&a.EmployeeId!=e.Id))throw new ArgumentException("רכב פיזי ועובד יכולים להופיע בהקצאה אחת בלבד; אין לשייך רכב פעיל לעובד נוסף");
            var a=new FleetAssignment(request.Id!,e.Id,car.Id,request.AssignedOn,request.LeaseEndsOn,request.EarlyReturnFee,request.TransferAllowed,request.ExceptionReason,request.ExceptionUntil,FleetRules.Key(car),Guid.NewGuid(),Actor(ctx),DateTimeOffset.UtcNow);
            // Changed source data invalidates both the request and any pending consent.
            var affected=doc.Requests.Where(r=>r.AssignmentId==a.Id).Select(r=>r.Id).ToHashSet();
            var next=doc with{Assignments=doc.Assignments.Where(x=>x.Id!=a.Id).Append(a).ToList(),Requests=doc.Requests.Select(r=>affected.Contains(r.Id)?r with{Open=false}:r).ToList(),
                Proposals=doc.Proposals.Select(p=>(affected.Contains(p.Left)||affected.Contains(p.Right))&&p.Status is "pendingConsent" or "pendingOfficer"?p with{Status="withdrawn",Note="נתוני ההקצאה השתנו; נדרשת בקשה והסכמה חדשה"}:p).ToList()};
            await store.Write(doc,next,request.ExpectedRevision,Actor(ctx),"assignment",a.Id,ct);return Results.Ok(new{saved=true});
        });
        group.MapPost("/request",async(FleetRequestInput request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);var doc=await store.Read(ct);var e=await service.Employee(request.EmployeeId,ct);
            if(!service.Policy.Enabled||!service.Policy.SwapEnabled||!service.Policy.ShowToEmployees||!request.Consent)throw new ArgumentException("נדרשת אפשרות החלפות מוצגת, מדיניות פעילה והסכמה לפרסום בקשה אנונימית");
            if(!new[]{"electric","family","regular"}.Contains(request.Category)||!service.Data.Cars.Any(c=>FleetRules.Accepts(request.Category,c)&&new PayrollEngine(service.Data).Eligibility(e,c).Eligible))throw new ArgumentException("אין זכאות למסלול המבוקש");
            var a=service.Assignment(doc,e)??throw new ArgumentException("נדרשת הקצאת רכב מאומתת בניהול, שתואמת לפרופיל ולזהות הרכב הפעילים");
            if(FleetRules.Reserved(doc,e.Id)||doc.Requests.Any(r=>r.EmployeeId==e.Id&&FleetRules.Active(r,doc,DateTimeOffset.UtcNow)))throw new ArgumentException("כבר קיימת בקשה פעילה או החלפה מאושרת");
            if(!a.TransferAllowed||a.LeaseEndsOn<DateOnly.FromDateTime(DateTime.UtcNow))throw new ArgumentException("החוזה אינו מאושר להעברה פנימית או שהסתיים");
            var next=new FleetRequest(Guid.NewGuid(),e.Id,request.Category,a.Id,a.Stamp,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(service.Policy.RequestValidityDays));
            await store.Write(doc,doc with{Requests=doc.Requests.Append(next).ToList()},request.ExpectedRevision,Actor(ctx),"request",next.Id.ToString(),ct);
            return await service.View(e.Id,ct);
        });
        group.MapPost("/withdraw",async(FleetAction request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);var doc=await store.Read(ct);
            if(!doc.Requests.Any(r=>r.Id==request.Id&&r.EmployeeId==request.EmployeeId))throw new UnauthorizedAccessException("Forbidden");
            if(FleetRules.Reserved(doc,request.EmployeeId))throw new ArgumentException("לאחר אישור יש לפנות לקצין הרכב לביטול");
            var next=doc with{Requests=doc.Requests.Select(r=>r.Id==request.Id?r with{Open=false}:r).ToList(),Proposals=doc.Proposals.Select(p=>p.Left==request.Id||p.Right==request.Id?p with{Status="withdrawn"}:p).ToList()};
            await store.Write(doc,next,request.ExpectedRevision,Actor(ctx),"withdraw",request.Id.ToString(),ct);return await service.View(request.EmployeeId,ct);
        });
        group.MapPost("/propose",async(FleetAction request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);var doc=await store.Read(ct);
            if(!service.Policy.ShowToEmployees)throw new ArgumentException("אפשרות ההחלפות מוסתרת לעובדים");
            var own=doc.Requests.SingleOrDefault(r=>r.EmployeeId==request.EmployeeId&&FleetRules.Active(r,doc,DateTimeOffset.UtcNow));var other=doc.Requests.SingleOrDefault(r=>r.Id==request.Id);
            if(own is null||other is null||!await service.Mutual(doc,own,other,ct))throw new ArgumentException("ההתאמה אינה פעילה או שהזכאות השתנתה");
            if(doc.Proposals.Any(p=>p.Status is "pendingConsent" or "pendingOfficer"&&new[]{p.Left,p.Right}.Intersect(new[]{own.Id,other.Id}).Any()))throw new ArgumentException("אחד הצדדים כבר בתהליך החלפה; נדרשת הסכמה או ביטול ההצעה הקודמת");
            var proposal=new FleetProposal(Guid.NewGuid(),own.Id,other.Id,[request.EmployeeId],"pendingConsent",DateTimeOffset.UtcNow,Basis:await service.Basis(own,other,ct));
            await store.Write(doc,doc with{Proposals=doc.Proposals.Append(proposal).ToList()},request.ExpectedRevision,Actor(ctx),"propose",proposal.Id.ToString(),ct);return await service.View(request.EmployeeId,ct);
        });
        group.MapPost("/consent",async(FleetAction request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Employee(ctx,request.EmployeeId);var doc=await store.Read(ct);var p=doc.Proposals.SingleOrDefault(p=>p.Id==request.Id)??throw new ArgumentException("הצעה לא נמצאה");
            if(request.Accept&&!service.Policy.ShowToEmployees)throw new ArgumentException("אפשרות ההחלפות מוסתרת לעובדים");
            var l=doc.Requests.Single(r=>r.Id==p.Left);var r=doc.Requests.Single(r=>r.Id==p.Right);
            if(l.EmployeeId!=request.EmployeeId&&r.EmployeeId!=request.EmployeeId)throw new UnauthorizedAccessException("Forbidden");
            if(p.Status is not ("pendingConsent" or "pendingOfficer"))throw new ArgumentException("ההצעה כבר נסגרה");
            if(request.Accept&&(!await service.Mutual(doc,l,r,ct)||p.Basis!=await service.Basis(l,r,ct)))throw new ArgumentException("הנתונים השתנו או שהבקשה פגה; נדרשת הצעה והסכמות חדשות");
            var consents=p.Consents.Append(request.EmployeeId).Distinct().ToArray();var updated=p with{Consents=consents,Status=!request.Accept?"withdrawn":consents.Length==2?"pendingOfficer":"pendingConsent"};
            await store.Write(doc,doc with{Proposals=doc.Proposals.Select(x=>x.Id==p.Id?updated:x).ToList()},request.ExpectedRevision,Actor(ctx),request.Accept?"consent":"decline",p.Id.ToString(),ct);return await service.View(request.EmployeeId,ct);
        });
        group.MapPost("/decision",async(FleetDecision request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Agent(ctx);if(string.IsNullOrWhiteSpace(request.Note)||request.Note.Length>500)throw new ArgumentException("נדרשת הערת קצין רכב עד 500 תווים, ללא פרטי שכר או מידע רגיש");
            var doc=await store.Read(ct);var p=doc.Proposals.SingleOrDefault(p=>p.Id==request.Id)??throw new ArgumentException("הצעה לא נמצאה");
            var l=doc.Requests.Single(r=>r.Id==p.Left);var r=doc.Requests.Single(r=>r.Id==p.Right);
            if(request.Approve&&(p.Status!="pendingOfficer"||p.Consents.Length!=2||!await service.Mutual(doc,l,r,ct)||p.Basis!=await service.Basis(l,r,ct)))throw new ArgumentException("האישור מחייב שתי הסכמות תקפות על הנתונים העדכניים, זכאות ומדיניות פעילה; שינוי נתונים דורש הצעה חדשה");
            if(!request.Approve&&p.Status is not ("pendingConsent" or "pendingOfficer" or "approved"))throw new ArgumentException("ההצעה כבר נסגרה");
            var decided=p with{Status=request.Approve?"approved":"rejected",Officer=Actor(ctx),Note=request.Note.Trim(),DecidedAt=DateTimeOffset.UtcNow};
            var next=doc with{Proposals=doc.Proposals.Select(x=>x.Id==p.Id?decided:x).ToList()};
            await store.Write(doc,next,request.ExpectedRevision,Actor(ctx),request.Approve?"approve-awaiting-handover":"reject",p.Id.ToString(),ct);
            return Results.Ok(new{saved=true,message="ההחלטה נשמרה. אין מסירה אוטומטית, שינוי פרופיל או חיוב. יש לתאם מסירה, ביטוח ותנאי שכר מול הגורמים המוסמכים."});
        });
        group.MapPost("/handover",async(FleetHandover request,HttpContext ctx,AccessControl access,FleetStore store,FleetService service,CancellationToken ct)=>
        {
            access.Agent(ctx);var doc=await store.Read(ct);var p=doc.Proposals.SingleOrDefault(p=>p.Id==request.Id)??throw new ArgumentException("הצעה לא נמצאה");
            var today=DateOnly.FromDateTime(DateTime.UtcNow);
            if(p.Status!="approved"||p.DecidedAt is null||request.HandoverOn<DateOnly.FromDateTime(p.DecidedAt.Value.UtcDateTime)||request.HandoverOn>today||string.IsNullOrWhiteSpace(request.Note)||request.Note.Length>500)throw new ArgumentException("תיעוד מסירה דורש אישור קודם, תאריך מסירה בפועל והערה עד 500 תווים");
            var l=doc.Requests.Single(r=>r.Id==p.Left);var r=doc.Requests.Single(r=>r.Id==p.Right);
            var la=doc.Assignments.Single(a=>a.Id==l.AssignmentId);var ra=doc.Assignments.Single(a=>a.Id==r.AssignmentId);
            var le=await service.Employee(l.EmployeeId,ct);var re=await service.Employee(r.EmployeeId,ct);
            if(la.Stamp!=l.AssignmentStamp||ra.Stamp!=r.AssignmentStamp||la.EmployeeId!=le.Id||ra.EmployeeId!=re.Id||le.CurrentCar!=ra.CarId||re.CurrentCar!=la.CarId||!IntakeFields.Known(le,"participation")||!IntakeFields.Known(re,"participation"))throw new ArgumentException("לפני תיעוד המסירה יש לעדכן ולאשר במקור את הרכב החדש וההשתתפות של שני העובדים. המערכת אינה ממציאה סכומי השתתפות או מעדכנת שכר אוטומטית");
            var engine=new PayrollEngine(service.Data);
            if(!engine.Eligibility(le,service.Data.Cars.Single(c=>c.Id==ra.CarId)).Eligible||!engine.Eligibility(re,service.Data.Cars.Single(c=>c.Id==la.CarId)).Eligible)throw new ArgumentException("הזכאות השתנתה; יש להסדיר את השיוך מול קצין הרכב");
            FleetAssignment Transfer(FleetAssignment a,string employee)=>a with{EmployeeId=employee,AssignedOn=request.HandoverOn,EarlyReturnFee=null,TransferAllowed=false,ExceptionReason=null,ExceptionUntil=null,Stamp=Guid.NewGuid(),Actor=Actor(ctx),UpdatedAt=DateTimeOffset.UtcNow};
            var next=doc with{Assignments=doc.Assignments.Select(a=>a.Id==la.Id?Transfer(a,re.Id):a.Id==ra.Id?Transfer(a,le.Id):a).ToList(),
                Requests=doc.Requests.Select(q=>q.Id==l.Id||q.Id==r.Id?q with{Open=false}:q).ToList(),
                Proposals=doc.Proposals.Select(q=>q.Id==p.Id?q with{Status="completed",HandedOverOn=request.HandoverOn,HandoverNote=request.Note.Trim(),HandoverActor=Actor(ctx)}:q).ToList()};
            await store.Write(doc,next,request.ExpectedRevision,Actor(ctx),"actual-handover",p.Id.ToString(),ct);
            return Results.Ok(new{saved=true,message="המסירה תועדה ושני שיוכי הרכב עודכנו יחד. מועדי סיום החוזים נשמרו; עלויות והרשאות העברה עתידיות דורשות בדיקה חדשה."});
        });
    }
}
