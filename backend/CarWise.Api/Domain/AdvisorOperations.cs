using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed record AdvisorActionResult(Employee Employee,string Reply,string[] Candidates,string Status,object? Data=null,Alternative? Calculation=null);

/// <summary>Business operations shared by conversation actions. Does not parse user language,
/// invoke an LLM, recurse into chat, or save a final choice. The caller persists one turn.</summary>
public sealed class AdvisorOperations(ReferenceData data,PayrollEngine engine,Repository repository)
{
    public async Task<Employee> RefreshPersonalAsync(Employee employee,CancellationToken ct)
    {
        if(IntakeFields.Known(employee,"currentCar")&&employee.CurrentCar is null)return IntakeFields.Answer(employee,"participation",0);
        if(!IntakeFields.Known(employee,"currentCar")||IntakeFields.Known(employee,"participation")||employee.CurrentCar is null)return employee;
        var (approved,_,sources)=await repository.ParticipationWithSourcesAsync(employee.Id,ct);
        return sources.GetValueOrDefault(employee.CurrentCar)=="representative"&&approved.TryGetValue(employee.CurrentCar,out var amount)?IntakeFields.Answer(employee,"participation",amount):employee;
    }
    private static AdvisorActionResult Result(Employee employee,AdvisorWorkflow flow,string text,string status,string[]? candidates=null,object? details=null)=>
        new(flow.Attach(employee),text,candidates??[],status,details);
    public async Task<AdvisorActionResult> ContinueAsync(Employee employee,CancellationToken ct)
    {
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.ResolveInputs(employee);var missing=IntakeFields.Missing(employee);
        if(missing.Length>0)
        {
            if(missing[0] is "eligibility" or "occupation")
            {
                var field=missing[0];var ticketTarget=field=="occupation"?"eligibility:occupation":"eligibility:group";
                if((await repository.TicketsAsync(employee.Id,ct)).Any(ticket=>ticket.Target==ticketTarget&&ticket.Status=="pending"))
                {flow.Ask(field);return Result(employee,flow,"הבירור לגבי "+(field=="occupation"?"העיסוק וכללי הוויתור":"קבוצת הזכאות")+" ממתין לנציג. הנתונים שכבר מסרת נשמרו, ונמשיך כשיגיע המענה.","needs_representative");}
                flow.Ask(field);flow.OfferAction(new("representative",Field:field));return Result(employee,flow,IntakeFields.Question(field,data),"needs_input");
            }
            if(missing[0]=="participation"&&employee.CurrentCar is {} current&&(await repository.TicketsAsync(employee.Id,ct)).Any(ticket=>ticket.Target==current&&ticket.Status=="pending"))
            {flow.Wait("participation",current);return Result(employee,flow,"הבירור על ההשתתפות ברכב הנוכחי ממתין לנציג. לא יוצג נטו סופי עד לקבלת תשובה.","needs_representative");}
            flow.Ask(missing[0]);
            if(missing[0]=="participation")flow.OfferAction(new("representative",Field:"participation"));
            return Result(employee,flow,IntakeFields.Question(missing[0],data),"needs_input",missing[0]=="currentCar"?data.Cars.Select(car=>car.Id).ToArray():[],new{field=missing[0],missingFields=missing});
        }
        if(flow.State.Target is {} target)return await CompareAsync(employee,target,ct);
        if(flow.State.RequestedCategory=="waiver")return await CompareAsync(employee,"waiver",ct);
        if(flow.State.RequestedCategory is {} category)return await BrowseAsync(employee,category,false,ct);
        flow.Clarify("איזו חלופת רכב תרצו לבדוק?");return Result(employee,flow,"איזו חלופת רכב תרצו לבדוק?","needs_clarification");
    }
    public async Task<AdvisorActionResult> EligibilityAsync(Employee employee,string? group,string? occupation,string text,CancellationToken ct)
    {
        var explicitGroup=EligibilityInputs.Group(text,data,employee.AdvisorState?.AwaitingField=="eligibility");var explicitRole=EligibilityInputs.Occupation(text,data);
        if(group is {Length:>0}&&explicitGroup!=group||occupation is {Length:>0}&&explicitRole!=occupation||string.IsNullOrWhiteSpace(group)&&string.IsNullOrWhiteSpace(occupation))throw new ArgumentException("יש למסור קבוצת זכאות או עיסוק מפורשים, בלי להסיק אותם מהקשר");
        var company=data.OccupationFor(employee.Occupation);
        var confirmed=company.Group.Length>0?company.Group:employee.ResolvedEligibility?.Source=="representative"?employee.ResolvedEligibility.Group:null;
        if(confirmed is {Length:>0}&&group is {Length:>0}&&group!=confirmed)
        {
            var unchanged=new AdvisorWorkflow(employee.AdvisorState);unchanged.OfferAction(new("representative",Field:"eligibility"));
            return Result(employee,unchanged,"לפי הנתון שכבר אושר עבורך, הקבוצה היא "+confirmed+". הדיווח שלך שונה ממנו, ולכן לא אשנה אותו אוטומטית. רוצה שאעביר את הבירור לנציג?","needs_clarification");
        }
        employee=await repository.ReportEligibilityAsync(employee,group,occupation,"employee",data,ct);
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.ResolveInputs(employee);flow.Applied("eligibility_reported");
        if(flow.Memory.Pending is {Kind:"representative",Field:"eligibility" or "occupation"})flow.Decline();
        var result=await ContinueAsync(flow.Attach(employee),ct);
        return result with{Reply="קבוצת הזכאות עודכנה לפי הדיווח שלך. "+result.Reply};
    }
    public async Task<AdvisorActionResult> ReferEligibilityAsync(Employee employee,string field,CancellationToken ct)
    {
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.Ask(field);
        await repository.CreateTicketAsync(flow.Attach(employee),field=="occupation"?"eligibility:occupation":"eligibility:group",
            field=="occupation"?"מהו העיסוק לצורך כללי הוויתור ואחזקת הרכב? נדרשת בחירה במיפוי מתועד.":"מהי קבוצת הזכאות של העובד? יש להשלים קבוצה מהמדיניות, ועיסוק אם הוא נדרש לכללי הוויתור.",ct,appendAnnouncement:false);
        return Result(employee,flow,"שלחתי שאלה לנציג לגבי "+(field=="occupation"?"העיסוק וכללי הוויתור":"קבוצת הזכאות")+". נשמרו הנתונים שכבר מסרת, ונמשיך מאותה נקודה אחרי התשובה.","needs_representative");
    }
    public async Task<AdvisorActionResult> PayrollAsync(Employee employee,ExtractedData fields,CancellationToken ct)
    {
        if(fields.Occupation is not null)throw new ArgumentException("Conversation cannot change occupation");
        employee=engine.Validate(IntakeFields.Apply(employee,fields));var flow=new AdvisorWorkflow(employee.AdvisorState);flow.Applied("payroll_updated");
        return await ContinueAsync(flow.Attach(employee),ct);
    }
    public async Task<AdvisorActionResult> CurrentCarAsync(Employee employee,string? target,CancellationToken ct)
    {
        if(IntakeFields.Known(employee,"currentCar")||employee.AdvisorState?.AwaitingField!="currentCar")throw new ArgumentException("No current-car question");
        if(target is not null&&!data.Cars.Any(car=>car.Id==target))throw new ArgumentException("Unknown vehicle");
        if(target is null&&employee.WaiverPolicyKnown&&!data.OccupationFor(employee).Waiver)throw new ArgumentException("Company vehicle is required for this occupation");
        employee=IntakeFields.Confirm(employee with{CurrentCar=target},"currentCar");
        if(target is null)employee=IntakeFields.Answer(employee,"participation",0);
        employee=await RefreshPersonalAsync(employee,ct);
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.Applied("current_car_updated");return await ContinueAsync(flow.Attach(engine.Validate(employee)),ct);
    }
    public AdvisorActionResult AskAlternativeParticipation(Employee employee,string target,bool offerOnly=false)
    {
        var car=data.Cars.SingleOrDefault(car=>car.Id==target)??throw new ArgumentException("Unknown vehicle");
        if(target==employee.CurrentCar||!engine.Eligibility(employee,car).Eligible)throw new ArgumentException("Eligible alternative required");
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.AskAlternativeParticipation(target);
        var reply=offerOnly?"אפשר לברר את הסכום עם נציג. לפתוח פנייה?":$"מה סכום ההשתתפות החודשי שסוכם עבור {car.Model}? אם הסכום עדיין לא ידוע, אפשר לברר עם נציג.";
        return Result(employee,flow,reply,"needs_input",details:new{target,field="alternativeParticipation",net=(decimal?)null,delta=(decimal?)null});
    }
    public async Task<AdvisorActionResult> CompareAsync(Employee employee,string target,CancellationToken ct)
    {
        if(target!="waiver"&&!data.Cars.Any(car=>car.Id==target))throw new ArgumentException("Unknown vehicle");
        if(employee.AdvisorState is {AwaitingField:"alternativeParticipation",Target:{} unresolved}&&unresolved==target)
            return AskAlternativeParticipation(employee,target);
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.Select(target);employee=flow.Attach(employee);
        if(IntakeFields.Missing(employee).Length>0)return await ContinueAsync(employee,ct);
        var (amounts,pending,sources)=await repository.ParticipationWithSourcesAsync(employee.Id,ct);
        var result=engine.Simulate(employee,amounts,pending,sources).Results.Single(row=>row.Id==target);
        if(!result.Eligible)return Result(employee,flow,"האפשרות הזו אינה זמינה: "+result.Reason+". אפשר לבחור חלופה אחרת מהקטלוג.","not_eligible",details:new{target,reason=result.Reason});
        if(result.Pending)
        {
            await repository.CreateTicketAsync(employee,target,DecisionMath.Question(engine,data,employee,target),ct);
            flow.Wait("targetParticipation",target);
            return Result(employee,flow,"חסר סכום השתתפות מאושר. שלחתי שאלה לנציג התחבורה. החלופה ממתינה לאישור; לא מוצג נטו סופי לרכב הזה. "+DecisionMath.Boundary(engine,data,employee,target).Explanation,"needs_representative",details:new{target,net=(decimal?)null,delta=(decimal?)null});
        }
        flow.Calculated(target);
        var reply=FinancialText.Template(result);
        if(target=="waiver"&&employee.CurrentCar is null&&result.Delta==0)reply="לפי הנתונים שאישרת, אין לך כיום רכב חברה. ההשוואה היא מול מצב הוויתור הקיים שלך, ולכן אין שינוי בנטו.\n"+reply;
        return Result(employee,flow,reply,"calculated",details:new{target,result.Net,result.Delta,result.Value,result.Participation,result.ParticipationSource,result.ElectricNet,result.FamilyGap}) with{Calculation=result};
    }
    public async Task<AdvisorActionResult> ReportAsync(Employee employee,string target,decimal amount,CancellationToken ct)
    {
        if(target=="waiver"||target==employee.CurrentCar||!data.Cars.Any(car=>car.Id==target)||amount<0||amount>20000)throw new ArgumentException("Invalid alternative participation");
        await repository.ReportParticipationAsync(employee.Id,target,amount,ct);
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.Select(target);flow.Applied("alternative_participation_reported");return await CompareAsync(flow.Attach(employee),target,ct);
    }
    public async Task<AdvisorActionResult> ApprovalAsync(Employee employee,string target,string question,CancellationToken ct)
    {
        if(!data.Cars.Any(car=>car.Id==target))throw new ArgumentException("Unknown vehicle");
        await repository.CreateTicketAsync(employee,target,"העובד מבקש אישור השתתפות אישית לרכב החדש: "+question,ct);
        var flow=new AdvisorWorkflow(employee.AdvisorState);flow.Select(target);flow.Wait("targetParticipation",target);
        return Result(employee,flow,"שלחתי לנציג בקשה לאישור ההשתתפות האישית שלך. ברירת המחדל מהקטלוג אינה מוצגת כעת כסכום סופי עבורך; ההשוואה לרכב הזה ממתינה לאישור.","needs_representative");
    }
    public async Task<AdvisorActionResult> BrowseAsync(Employee employee,string category,bool different,CancellationToken ct)
    {
        var flow=new AdvisorWorkflow(employee.AdvisorState);
        if(different)flow.Alternatives(category);
        else if(flow.State.Target is not null)flow.Begin(category);
        if(IntakeFields.Missing(employee).Length>0)return await ContinueAsync(flow.Attach(employee),ct);
        var rejected=flow.State.RejectedCandidateIds?.ToHashSet(StringComparer.Ordinal)??[];
        var (reply,choices)=AdvisorDialogue.Alternatives(employee,data,engine,category,null,rejected);
        if(flow.Memory.Preferences?.Contains("lower_cost")==true&&choices.Length>0)
        {
            var (amounts,pending,sources)=await repository.ParticipationWithSourcesAsync(employee.Id,ct);
            var net=engine.Simulate(employee,amounts,pending,sources).Results.Where(row=>!row.Pending&&row.Eligible).ToDictionary(row=>row.Id,row=>row.Net);
            choices=choices.OrderByDescending(id=>net.GetValueOrDefault(id,decimal.MinValue)).ToArray();
            reply="אלה הדגמים הזכאים, לפי הנטו המחושב מהגבוה לנמוך. דגם שממתין לנתון מאושר יופיע בסוף. איזה מהם מעניין אותך?";
        }
        flow.Offered(choices);return Result(employee,flow,reply,choices.Length==0?"catalogue_exhausted":"needs_choice",choices,new{category,vehicles=choices.Select(id=>data.Cars.Single(car=>car.Id==id)).Select(car=>new{car.Id,car.Model,car.Year})});
    }
    public async Task<AdvisorActionResult> AcceptAsync(Employee employee,CancellationToken ct)
    {
        var flow=new AdvisorWorkflow(employee.AdvisorState);var pending=flow.Accept();
        if(pending is null){flow.Clarify("איזו פעולה תרצו שאבצע?");return Result(employee,flow,"איזו פעולה תרצו שאבצע?","needs_clarification");}
        employee=flow.Attach(employee);
        if(pending.Kind=="compare_waiver")return await CompareAsync(employee,"waiver",ct);
        if(pending.Kind=="compare_selected"&&pending.Target is {} selected)return await CompareAsync(employee,selected,ct);
        if(pending.Kind=="alternatives")return await BrowseAsync(employee,flow.State.RequestedCategory??"any",true,ct);
        if(pending is {Kind:"representative",Subject:"alternative_participation",Target:{} alternative})
            return await ApprovalAsync(employee,alternative,"הסכום שסוכם שונה מהקטלוג ונדרש בירור אישי",ct);
        if(pending.Kind=="representative")
        {
            if(pending.Field is "eligibility" or "occupation")return await ReferEligibilityAsync(employee,pending.Field,ct);
            var subject=pending.Subject=="catalogue_alternative"?(flow.State.RequestedCategory=="electric"?"בירור חלופה חשמלית נוספת לפי זכאות העובד":flow.State.RequestedCategory=="family"?"בירור חלופה נוספת במסלול המשפחתי לפי זכאות העובד":"בירור חלופת רכב נוספת לפי זכאות העובד"):
                pending.Field is {} field&&IntakeFields.All.Contains(field)?IntakeFields.Question(field,data):pending.Subject??"בירור ייעוץ רכב";
            var target=pending.Field=="participation"&&employee.CurrentCar is {} current?current:ParticipationRules.CatalogueTarget(subject);
            await repository.CreateTicketAsync(employee,target,subject,ct,appendAnnouncement:false);flow.Wait(pending.Field??"representative",pending.Target);
            return Result(employee,flow,"פתחתי פנייה לנציג לבירור הבקשה שלך. לא אציג חישוב סופי שחסר עבורו נתון מאושר.","needs_representative");
        }
        flow.Clarify("מה תרצו לבדוק בהמשך?");return Result(employee,flow,"מה תרצו לבדוק בהמשך?","needs_clarification");
    }
}
