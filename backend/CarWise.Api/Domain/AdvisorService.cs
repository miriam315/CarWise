using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed class AdvisorService(ReferenceData data,PayrollEngine engine,Repository repository,
    IDataExtractionService extraction,IResponseNarrationService narration,AdvisorTiming? timing=null,FleetService? fleet=null,ILogger<AdvisorService>? logger=null,AdvisorToolConversation? tools=null)
{
    private bool suppressMessages;
    private readonly AdvisorTiming limits=(timing??AdvisorTiming.Default).Validate();
    private static readonly Dictionary<string,string> Aliases=new(){["אטו"]="CAR-10",["atto"]="CAR-10",["קרניבל"]="CAR-19",
        ["טסלה"]="CAR-14",["אוקטביה"]="CAR-15",["טוסון"]="CAR-12",["דולפין"]="CAR-03",["נירו"]="CAR-05",
        ["סנטה"]="CAR-20",["קונה"]="CAR-08",["פיקנטו"]="CAR-01",["קורולה קרוס"]="CAR-09",["מאזדה"]="CAR-13",["סיל"]="CAR-17"};
    private static readonly SemaphoreSlim[] Gates=Enumerable.Range(0,128).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    public async Task<Employee> RefreshKnownParticipationAsync(Employee employee,CancellationToken ct)
    {
        if(IntakeFields.Known(employee,"currentCar")&&employee.CurrentCar is null)return IntakeFields.Answer(employee,"participation",0);
        if(IntakeFields.Known(employee,"participation")||employee.CurrentCar is null)return employee;
        var (approved,_,sources)=await repository.ParticipationWithSourcesAsync(employee.Id,ct);
        return sources.GetValueOrDefault(employee.CurrentCar)=="representative"&&approved.TryGetValue(employee.CurrentCar,out var amount)?IntakeFields.Answer(employee,"participation",amount):employee;
    }
    public async Task<Simulation> SimulateAsync(Employee e,CancellationToken ct)
    {
        e=await RefreshKnownParticipationAsync(e,ct);
        var (approved,pending,sources)=await repository.ParticipationWithSourcesAsync(e.Id,ct);
        return engine.Simulate(e,approved,pending,sources);
    }
    public async Task<object> ChatAsync(ChatRequest request,CancellationToken ct,bool canChangeOccupation=true)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(limits.Total);
        var gate=Gates[(StringComparer.Ordinal.GetHashCode(request.Employee.Id)&int.MaxValue)%Gates.Length];var acquired=false;
        try{await gate.WaitAsync(deadline.Token);acquired=true;return await ChatCoreAsync(request,deadline.Token,canChangeOccupation).WaitAsync(deadline.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested)
        {return new{reply="הבקשה לא הושלמה בזמן. לא מוצג חישוב סופי. הנתונים שכבר נשמרו זמינים להמשך; נסו שוב או בחרו חלופה מהקטלוג.",target=(string?)null,candidates=Array.Empty<string>(),employee=(Employee?)null,mode=extraction.Mode,status="timeout",complete=false};}
        finally{if(acquired)gate.Release();}
    }
    private async Task<object> ChatCoreAsync(ChatRequest request,CancellationToken ct,bool canChangeOccupation,bool useTools=true,ExtractedData? suppliedFields=null)
    {
        var text=request.Text?.Trim()??"";
        if(text.Length is 0 or >2000)throw new ArgumentException("כתבו שאלה עד 2,000 תווים");
        var conversation=await repository.ConversationAsync(request.Employee.Id,ct);
        var profile=!useTools&&suppliedFields is not null?request.Employee:conversation?.Employee??(request.Employee with{AdvisorState=null});
        if(!canChangeOccupation)profile=profile with{Occupation=request.Employee.Occupation};
        var e=engine.Validate(profile);
        e=await RefreshKnownParticipationAsync(e,ct);
        var state=e.AdvisorState??new AdvisorState();
        if(AdvisorScope.ClearlyUnrelated(text))return await ReplyAsync(e,state,text,AdvisorScope.Redirect,[],ct);
        var numericAnswer=state.AwaitingField is {} savedField&&IntakeFields.All.Contains(savedField)&&!IntakeFields.Known(e,savedField)&&IntakeFields.IsNumericAnswer(text,savedField);
        var toolUnavailable=false;
        if(useTools&&tools is not null&&request.Target is null&&!numericAnswer)
        {
            try
            {
                var history=await repository.HistoryAsync(e.Id,ct);
                var result=await tools.RunAsync(request,e,history,async(next,fields,token)=>
                {
                    suppressMessages=true;
                    try{return System.Text.Json.Nodes.JsonNode.Parse(JsonDefaults.Serialize(await ChatCoreAsync(next,token,canChangeOccupation,false,fields)))!;}
                    finally{suppressMessages=false;}
                },ct);
                var nextState=(result.Employee.AdvisorState??state) with{CandidateIds=result.Candidates};
                return await ReplyAsync(result.Employee,nextState,text,result.Reply,result.Candidates.ToList(),ct);
            }
            catch(AiUnavailableException){toolUnavailable=true;}
        }
        if(fleet is not null&&fleet.Policy.Enabled&&fleet.Policy.ShowToEmployees&&fleet.Policy.SwapEnabled&&IntakeFields.Missing(e).Length==0&&FleetRules.Category(text,out var fleetCategory))
            return await ReplyAsync(e,state,text,await fleet.Advice(e,fleetCategory,ct),[],ct);
        if(state.Target is not null&&state.Target!="waiver"&&!data.Cars.Any(car=>car.Id==state.Target))state=state with{Target=null};
        // A saved question, rather than model memory or a client's claimed context,
        // determines the meaning of a short numerical answer.
        var awaiting=state.AwaitingField;
        ExtractedData fields=suppliedFields??new();var answered=false;var aiFallback=false;
        if(awaiting is not null&&IntakeFields.All.Contains(awaiting)&&!IntakeFields.Known(e,awaiting))
        {
            if(IntakeFields.TryAnswerNumber(text,awaiting,out var number)){e=IntakeFields.Answer(e,awaiting,number);answered=true;}
            else if(awaiting=="currentCar")
            {
                if(request.Target is {} current&&data.Cars.Any(car=>car.Id==current)){e=IntakeFields.Confirm(e with{CurrentCar=current},"currentCar");answered=true;}
                else if(text is "ללא רכב" or "אין רכב" or "אין לי רכב חברה")
                {if(!data.OccupationFor(e.Occupation).Waiver)throw new ArgumentException("בתפקיד זה נדרש רכב חברה; בחרו את הרכב הנוכחי מהרשימה.");e=IntakeFields.Confirm(e with{CurrentCar=null,Participation=0},"currentCar","participation");answered=true;}
            }
            else if(awaiting=="participation"&&IntakeFields.IsUnknown(text)&&e.CurrentCar is {} current)
            {
                await repository.CreateTicketAsync(e,current,"מהי השתתפות העובד ברכב הנוכחי? ללא סכום מאושר אי אפשר לחשב בסיס השוואה.",ct);
                return await ReplyAsync(e,state with{AwaitingField="participation"},text,"חסר סכום ההשתתפות ברכב הנוכחי. שלחתי שאלה לנציג התחבורה; לא אציג נטו עד לקבלת תשובה.",[],ct);
            }
        }
        if(!answered&&awaiting is not null&&IntakeFields.All.Contains(awaiting)&&IntakeFields.IsNumericAnswer(text,awaiting))
            return await ReplyAsync(e,state,text,"הערך אינו תקין לשדה שנשאל. "+IntakeFields.Question(awaiting,data),[],ct);
        if(!answered&&request.Target is null)
        {
            var history=suppliedFields is null?await repository.HistoryAsync(e.Id,ct):[];
            using var ai=CancellationTokenSource.CreateLinkedTokenSource(ct);ai.CancelAfter(limits.Extraction);
            try{fields=suppliedFields??(toolUnavailable?await new LocalExtractionService().ExtractAsync(text,e,[],ct):await extraction.ExtractAsync(text,e,history,ai.Token).WaitAsync(ai.Token));aiFallback=toolUnavailable;}
            catch(Exception error)when(!ct.IsCancellationRequested&&error is AiUnavailableException or HttpRequestException or OperationCanceledException)
            {
                logger?.LogWarning("AI conversation extraction failed; local guidance used. Code={Code}, HTTP={Status}",
                    error is AiUnavailableException unavailable?unavailable.Code:error is OperationCanceledException?"timeout":"network_error",
                    error is AiProviderException provider?provider.Status:(int?)null);
                aiFallback=true;fields=await new LocalExtractionService().ExtractAsync(text,e,[],ct);
            }
            if(!canChangeOccupation&&fields.Occupation is not null&&fields.Occupation!=e.Occupation)throw new UnauthorizedAccessException("Forbidden");
            e=IntakeFields.Apply(e,fields);
        }
        e=engine.Validate(e);
        // Answer known profile/policy questions before payroll intake. Asking one's group
        // must not require a complete payslip or change the pending comparison.
        if(!answered&&request.Target is null&&!fields.HasUpdates&&!fields.WantsElectric&&!fields.IsWaiverRequested&&fields.RequestedModel is null)
        {
            var topics=(fields.QuestionTopics??[]).Concat(AdvisorKnowledge.Identify(text)).Distinct().ToArray();
            var explanation=AdvisorKnowledge.Answer(topics,e,data,engine);
            if(explanation.Length>0)
            {
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(limits.Narration);
                var facts=explanation.Split("\n\n",StringSplitOptions.RemoveEmptyEntries);
                string response;
                try{response=await narration.ExplainFactsAsync(facts,budget.Token).WaitAsync(budget.Token);}
                catch(OperationCanceledException)when(!ct.IsCancellationRequested){response=explanation;}
                return await ReplyAsync(e,state,text,response,[],ct,aiFallback);
            }
        }
        var lower=text.ToLowerInvariant();var candidates=new List<string>();var target=answered?state.Target:request.Target;
        if(target is null&&(awaiting is not null||fields.HasUpdates||text is "נמשיך" or "המשך" or "מה התשובה"))target=state.Target;
        if((awaiting=="targetParticipation"||fields.TargetParticipation is not null||fields.ParticipationNeedsApproval||ParticipationRules.ApprovalRequested(text))&&target is null)target=state.Target;
        if(fields.IsWaiverRequested)target="waiver";
        if(target is null && fields.VehicleKey is { } key &&
            string.Concat(text.Where(c=>!char.IsWhiteSpace(c))).Contains(
                OfficialRecord.KeyFor(key.TaxYear,key.RegistrationYear,key.VehicleType,key.MakerCode,key.ModelCode),StringComparison.Ordinal))
        {
            var found=data.Cars.Where(c=>key.TaxYear==data.TaxYear && c.Year==key.RegistrationYear && key.VehicleType=="1" &&
                c.MakerCode==key.MakerCode && c.ModelCode==key.ModelCode).ToList();
            if(found.Count==1)target=found[0].Id;
        }
        if(target is null&&!answered)
        {
            var hint=(fields.RequestedModel??lower).ToLowerInvariant();
            foreach(var c in data.Cars)if(hint.Contains(c.Model.ToLowerInvariant()))candidates.Add(c.Id);
            foreach(var (alias,id) in Aliases)if(hint.Contains(alias)&&!candidates.Contains(id))candidates.Add(id);
            // A language model's textual match is only a candidate. Confirm identity by explicit UI choice.
            if(candidates.Count==1 && extraction.Mode=="local-rules")target=candidates[0];
            else if(candidates.Count==0 && fields.IsWaiverRequested)target="waiver";
        }
        if(aiFallback&&target is not null){candidates.Add(target);target=null;}
        if(target is null&&candidates.Count==0&&state.CandidateIds is {} remembered)candidates.AddRange(remembered.Where(id=>id=="waiver"||data.Cars.Any(car=>car.Id==id)));
        if(fields.WantsElectric&&request.Target is null&&!answered)target=null;
        var wantsElectric=fields.WantsElectric||(answered?state.WantsElectric:request.Target is null&&!fields.IsWaiverRequested&&state.WantsElectric);
        var missing=IntakeFields.Missing(e);
        if(missing.Length>0)
        {
            var next=missing[0];var choices=next=="currentCar"?data.Cars.Select(car=>car.Id).ToList():new List<string>();
            return await ReplyAsync(e,new(next,target,wantsElectric,candidates.ToArray(),state.RequestedCategory),text,IntakeFields.Question(next,data),choices,ct,aiFallback);
        }
        if(target is null&&candidates.Count==0&&(ParticipationRules.OutsideCatalogue(text)||fields.RequestedModel is {Length:>0} requestedHint&&text.Contains(requestedHint,StringComparison.OrdinalIgnoreCase)))
        {
            var outsideHint=fields.RequestedModel??text;
            var ticketTarget=ParticipationRules.CatalogueTarget(outsideHint);
            e=e with{AdvisorState=new("catalogueVehicle",null,wantsElectric,RequestedCategory:state.RequestedCategory)};
            await repository.CreateTicketAsync(e,ticketTarget,"בדיקת רכב שאינו בקטלוג: "+outsideHint+". נדרשים זיהוי רשמי, זכאות והשתתפות; אין לאשר חישוב לפני הוספת נתוני מקור.",ct,appendAnnouncement:!suppressMessages);
            return await ReplyAsync(e,e.AdvisorState,text,"לא נמצאה התאמה לרכב בקטלוג. העברתי שאלה לנציג; לא אנחש השתתפות או שווי שימוש ולא אציג נטו לרכב הזה. אחרי השלמת הקטלוג נוכל להמשיך בהשוואה.",[],ct);
        }
        if(target is not null&&data.Cars.Any(c=>c.Id==target))
        {
            if(!engine.Eligibility(e,data.Cars.Single(c=>c.Id==target)).Eligible)
                return await ReplyAsync(e,new(null,target,wantsElectric,RequestedCategory:state.RequestedCategory),text,"החלופה אינה בקבוצת הזכאות שלך. אפשר לבחור חלופה אחרת מהקטלוג.",[],ct);
            decimal? reported=fields.TargetParticipation;
            if(reported is null&&request.Target is not null)reported=(await new LocalExtractionService().ExtractAsync(text,e,[],ct)).TargetParticipation;
            if(awaiting=="targetParticipation"&&IntakeFields.TryNumber(text,"participation",out var amount))reported=amount;
            if(reported is not null)await repository.ReportParticipationAsync(e.Id,target,reported.Value,ct);
            if(fields.ParticipationNeedsApproval||ParticipationRules.ApprovalRequested(text))
            {
                e=e with{AdvisorState=new("targetParticipation",target,wantsElectric,RequestedCategory:state.RequestedCategory)};
                await repository.CreateTicketAsync(e,target,"העובד מבקש אישור השתתפות אישית לרכב החדש: "+text,ct);
                return await ReplyAsync(e,e.AdvisorState,text,"שלחתי לנציג בקשה לאישור ההשתתפות האישית שלך. ברירת המחדל מהקטלוג אינה מוצגת כעת כסכום סופי עבורך; ההשוואה לרכב הזה ממתינה לאישור.",[],ct);
            }
        }
        e=e with{AdvisorState=new(null,target,wantsElectric,RequestedCategory:state.RequestedCategory)};
        var simulation=await SimulateAsync(e,ct);
        string reply;string? nextAwaiting=null;
        if(target is not null)
        {
            var result=simulation.Results.SingleOrDefault(r=>r.Id==target)??throw new ArgumentException("הרכב לא נמצא; בחרו דגם מדויק מהקטלוג");
            if(!result.Eligible)reply="האפשרות הזו אינה זמינה: "+result.Reason+". אפשר לבחור חלופה אחרת מהקטלוג.";
            else if(result.Pending)
            {
                nextAwaiting="targetParticipation";
                await repository.CreateTicketAsync(e,target,DecisionMath.Question(engine,data,e,target),ct);
                reply="חסר סכום השתתפות מאושר. שלחתי שאלה לנציג התחבורה. החלופה ממתינה לאישור; לא מוצג נטו סופי לרכב הזה. "+DecisionMath.Boundary(engine,data,e,target).Explanation;
            }
            else reply=!useTools?FinancialText.Template(result):await NarrateAsync(result,ct);
        }
        else if(candidates.Count>0)reply="כדי לזהות את הרכב לפי המפתח הרשמי, בחרו את הדגם המדויק מהרשימה. שם מסחרי לבדו לא מספיק.";
        else if(wantsElectric||state.RequestedCategory is "family" or "regular")
        {
            var category=wantsElectric?"electric":state.RequestedCategory!;
            candidates=simulation.Results.Where(r=>r.Eligible&&!r.Pending&&r.Car is {} car&&FleetRules.Accepts(category,car)).OrderByDescending(r=>r.Net).Take(3).Select(r=>r.Id).ToList();
            var label=category=="electric"?"חשמליים":category=="family"?"במסלול המשפחתי":"במסלול הרגיל";
            reply=candidates.Count>0?$"הנה רכבים {label} שמתאימים לזכאות שלך. איזה דגם תרצו להשוות?":"לא נמצאו כרגע רכבים זמינים במסלול המבוקש לפי הזכאות והנתונים המאושרים שלך.";
        }
        else reply="אפשר להשוות רכב מסוים, לבדוק מעבר לחשמלי או לשאול על ויתור על הרכב. בחרו דגם מהקטלוג או כתבו את שמו. נתוני השכר וההפרשות ניתנים לעדכון דרך ״הנתונים שלי״.";
        if(fields.HasUpdates||answered)reply="עדכנתי את הנתונים שמסרת ושמרתי אותם.\n"+reply;
        return await ReplyAsync(e,new(nextAwaiting,target,wantsElectric,candidates.ToArray(),state.RequestedCategory),text,reply,candidates,ct,aiFallback);
    }
    private async Task<string> NarrateAsync(Alternative result,CancellationToken ct)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(limits.Narration);
        try{return await narration.NarrateAsync(result,budget.Token).WaitAsync(budget.Token);}
        catch(Exception error)when(!ct.IsCancellationRequested&&error is AiUnavailableException or HttpRequestException or OperationCanceledException)
        {
            logger?.LogWarning("AI comparison narration failed; payroll template used. Code={Code}",
                error is AiUnavailableException unavailable?unavailable.Code:error is OperationCanceledException?"timeout":"network_error");
            return FinancialText.Template(result);
        }
    }
    private async Task<object> ReplyAsync(Employee employee,AdvisorState state,string text,string reply,List<string> candidates,CancellationToken ct,bool aiFallback=false)
    {
        if(aiFallback)reply="כדי לוודא שהפרטים נקלטו נכון, בדקו אותם לפני המשך ההשוואה.\n"+reply;
        employee=employee with{AdvisorState=state};
        await repository.SaveConversationAsync(employee,state.Target,ct);
        if(!suppressMessages){await repository.AddMessageAsync(employee.Id,"user",text,ct);await repository.AddMessageAsync(employee.Id,"assistant",reply,ct);}
        return new{reply,target=state.Target,candidates,employee,mode=aiFallback?"guided-local":extraction.Mode,awaitingField=state.AwaitingField,missingFields=IntakeFields.Missing(employee),complete=IntakeFields.Missing(employee).Length==0&&state.AwaitingField is null};
    }
}
