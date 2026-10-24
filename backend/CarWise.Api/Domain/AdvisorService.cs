using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed class AdvisorService(ReferenceData data,PayrollEngine engine,Repository repository,
    IDataExtractionService extraction,IResponseNarrationService narration,AdvisorTiming? timing=null,FleetService? fleet=null,ILogger<AdvisorService>? logger=null,AdvisorToolConversation? tools=null)
{
    private readonly AdvisorTiming limits=(timing??AdvisorTiming.Default).Validate();
    private static readonly Dictionary<string,string> Aliases=new(){["אטו"]="CAR-10",["atto"]="CAR-10",["קרניבל"]="CAR-19",
        ["טסלה"]="CAR-14",["אוקטביה"]="CAR-15",["טוסון"]="CAR-12",["דולפין"]="CAR-03",["נירו"]="CAR-05",
        ["סנטה"]="CAR-20",["קונה"]="CAR-08",["פיקנטו"]="CAR-01",["קורולה קרוס"]="CAR-09",["מאזדה"]="CAR-13",["סיל"]="CAR-17"};
    private static readonly SemaphoreSlim[] Gates=Enumerable.Range(0,128).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    public async Task<Employee> StartNewChatAsync(Employee employee,CancellationToken ct)
    {
        var gate=Gates[(StringComparer.Ordinal.GetHashCode(employee.Id)&int.MaxValue)%Gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            var saved=await repository.ConversationAsync(employee.Id,ct);
            var history=await repository.HistoryAsync(employee.Id,ct);
            var current=saved?.Employee??data.Employees.Single(e=>e.Id==employee.Id);
            current=engine.Validate(current with{AdvisorState=null,ChatHistoryStart=history.Count});
            await repository.SaveConversationAsync(current,null,ct);return current;
        }
        finally{gate.Release();}
    }
    public async Task<Employee> RefreshKnownParticipationAsync(Employee employee,CancellationToken ct)
    {
        if(data.OccupationFor(employee.Occupation).Group.Length==0)employee=await repository.ResolveEligibilityAsync(employee,ct);
        return await new AdvisorOperations(data,engine,repository).RefreshPersonalAsync(employee,ct);
    }
    public Task<AdvisorActionResult> ContinueAfterEligibilityAsync(Employee employee,CancellationToken ct)=>new AdvisorOperations(data,engine,repository).ContinueAsync(engine.Validate(employee),ct);
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
    private async Task<object> ChatCoreAsync(ChatRequest request,CancellationToken ct,bool canChangeOccupation)
    {
        var text=request.Text?.Trim()??"";
        if(text.Length is 0 or >2000)throw new ArgumentException("כתבו שאלה עד 2,000 תווים");
        var conversation=await repository.ConversationAsync(request.Employee.Id,ct);
        var profile=conversation?.Employee??(request.Employee with{AdvisorState=null,ChatHistoryStart=0});
        if(!canChangeOccupation)profile=profile with{Occupation=request.Employee.Occupation};
        var e=engine.Validate(await RefreshKnownParticipationAsync(profile,ct));
        var workflow=new AdvisorWorkflow(e.AdvisorState);workflow.ResolveInputs(e);var state=workflow.State;e=workflow.Attach(e);
        if(AdvisorDialogue.SocialReply(text) is {} social)return await ReplyAsync(e,state with{CandidateIds=null},text,social,[],ct);
        if(AdvisorDialogue.AsksMissing(text))
        {
            var missingStatus=IntakeFields.Missing(e);var answer=AdvisorDialogue.MissingStatus(e,data);
            return await ReplyAsync(e,state with{CandidateIds=null,AwaitingField=missingStatus.FirstOrDefault()??state.AwaitingField,OfferedReferral=missingStatus.Length>0&&answer.Contains("נציג",StringComparison.Ordinal)?missingStatus[0]:null},text,answer,[],ct);
        }
        if(AdvisorScope.ClearlyUnrelated(text))return await ReplyAsync(e,state,text,AdvisorScope.Redirect,[],ct);
        if(workflow.Memory.Pending is {Kind:not "clarification"}&&AdvisorDialogue.AcceptsReferral(text))
        {
            var accepted=await new AdvisorOperations(data,engine,repository).AcceptAsync(e,ct);
            return await ReplyOperationAsync(accepted,text,ct);
        }
        if(workflow.Memory.Pending is {Kind:not "clarification"}&&text is "לא" or "לא תודה" or "עדיף שלא")
        {
            workflow.Decline();e=workflow.Attach(e);return await ReplyAsync(e,workflow.State,text,"בסדר, לא אבצע את הפעולה שהצעתי.",[],ct);
        }
        var numericAnswer=state.Memory?.AskedFields is not {Length:>1}&&state.AwaitingField is {} savedField&&IntakeFields.All.Contains(savedField)&&!IntakeFields.Known(e,savedField)&&IntakeFields.IsNumericAnswer(text,savedField);
        if(request.Target is null&&state.AwaitingField=="currentCar"&&!IntakeFields.Known(e,"currentCar")&&!AdvisorDialogue.RequestsComparison(text))
        {
            var namedCurrent=VehicleSearch.Find(text,data.Cars);
            if(namedCurrent.Count==1)request=request with{Target=namedCurrent[0].Id};
        }
        if(request.Target is null&&!numericAnswer&&AdvisorDialogue.RequestsComparison(text)&&!ParticipationRules.ApprovalRequested(text)&&
            !System.Text.RegularExpressions.Regex.IsMatch(text,"השתתפות|עמלות|בונוס|פנסיה|השתלמות|נקודות זיכוי|שכר"))
        {
            var (targets,sources)=VehicleSearch.Resolve(text,data.Cars);
            if(sources.Count==1&&IntakeFields.Known(e,"currentCar")&&e.CurrentCar!=sources[0].Id)
                return await ReplyAsync(e,state with{Target=null,AwaitingField="currentCarConfirmation",CandidateIds=null},text,"הרכב שממנו ביקשת לעבור שונה מהרכב שמאושר כרגע בפרופיל שלך. לפני החישוב צריך לאשר את הרכב הנוכחי ואת ההשתתפות האישית ב״הנתונים שלי״.",[],ct);
            if(targets.Count==1&&state.AwaitingField!="currentCar")
            {
                if(sources.Count==1&&!IntakeFields.Known(e,"currentCar"))e=IntakeFields.Confirm(e with{CurrentCar=sources[0].Id,KnownFields=e.KnownFields?.Where(f=>f!="participation").ToArray()},"currentCar");
                if(targets[0].Drive=="חשמלי"){state=state with{RequestedCategory="electric",WantsElectric=true};e=e with{AdvisorState=state};}
                request=request with{Target=targets[0].Id};
            }
            else if(targets.Count>1)
                return await ReplyAsync(e,state with{Target=null,AwaitingField="vehicleSelection",CandidateIds=targets.Select(car=>car.Id).ToArray()},text,"מצאתי כמה דגמים או שנות רישום שמתאימים. איזה מהם רצית להשוות?",targets.Select(car=>car.Id).ToList(),ct);
        }
        var operations=new AdvisorOperations(data,engine,repository);
        var reportedGroup=EligibilityInputs.Group(text,data,state.AwaitingField=="eligibility");
        var reportedRole=state.AwaitingField=="occupation"?EligibilityInputs.Occupation(text,data):null;
        if(reportedGroup is not null||reportedRole is not null)
            {
                if(AdvisorDialogue.RequestsComparison(text))
                {workflow.Begin(text.Contains("חשמל",StringComparison.Ordinal)?"electric":text.Contains("ויתור",StringComparison.Ordinal)||text.Contains("לוותר",StringComparison.Ordinal)?"waiver":"specific");if(request.Target is {} specified)workflow.Select(specified);e=workflow.Attach(e);}
                var supplied=await new LocalExtractionService().ExtractAsync(text,e,[],ct);
                e=engine.Validate(IntakeFields.Apply(e,supplied with{Occupation=null}));
                return await ReplyOperationAsync(await operations.EligibilityAsync(e,reportedGroup,reportedRole,text,ct),text,ct);
            }
        if(state.AwaitingField is "eligibility" or "occupation")
        {
            var field=state.AwaitingField;
            if(IntakeFields.IsUnknown(text)||System.Text.RegularExpressions.Regex.IsMatch(text,"נציג|לא יודע|לא יודעת|לא ידוע"))
                return await ReplyOperationAsync(await operations.ReferEligibilityAsync(e,field,ct),text,ct);
        }
        var alternativeTarget=state.Target??(workflow.Memory.Goal=="paused"?workflow.Memory.Backstack?.LastOrDefault()?.Target:null);
        if(request.Target is null&&alternativeTarget is not null&&alternativeTarget!="waiver"&&alternativeTarget!=e.CurrentCar)
        {
            if(state.AwaitingField=="alternativeParticipation"&&IntakeFields.IsNumericAnswer(text,"participation")&&IntakeFields.TryAnswerNumber(text,"participation",out var personalAmount))
                return await ReplyOperationAsync(await operations.ReportAsync(e,alternativeTarget,personalAmount,ct),text,ct);
            if(state.AwaitingField=="alternativeParticipation"&&IntakeFields.IsUnknown(text))
                return await ReplyOperationAsync(operations.AskAlternativeParticipation(e,alternativeTarget,true),text,ct);
            if(AdvisorDialogue.PersonalContributionAgreement(text)&&!text.Contains("נוכחי",StringComparison.Ordinal)&&!System.Text.RegularExpressions.Regex.IsMatch(text,@"\d"))
                return await ReplyOperationAsync(operations.AskAlternativeParticipation(e,alternativeTarget),text,ct);
        }
        if(numericAnswer&&state.AwaitingField is {} numericField)
        {
            if(!IntakeFields.TryAnswerNumber(text,numericField,out var number))return await ReplyAsync(e,state,text,"הערך אינו תקין לשדה שנשאל. "+IntakeFields.Question(numericField,data),[],ct);
            e=engine.Validate(IntakeFields.Answer(e,numericField,number));workflow=new(e.AdvisorState);workflow.Applied("explicit_field_answered");
            return await ReplyOperationAsync(await operations.ContinueAsync(workflow.Attach(e),ct),text,ct);
        }
        if(request.Target is {} exactTarget)
        {
            AdvisorActionResult selected;
            if(state.AwaitingField=="currentCar"&&!IntakeFields.Known(e,"currentCar"))selected=await operations.CurrentCarAsync(e,exactTarget,ct);
            else if(ParticipationRules.ApprovalRequested(text))selected=await operations.ApprovalAsync(e,exactTarget,text,ct);
            else
            {
                var amount=(await new LocalExtractionService().ExtractAsync(text,e,[],ct)).TargetParticipation;
                selected=amount is {} reported?await operations.ReportAsync(e,exactTarget,reported,ct):await operations.CompareAsync(e,exactTarget,ct);
            }
            return await ReplyOperationAsync(selected,text,ct);
        }
        var toolUnavailable=false;
        if(tools is not null&&request.Target is null&&!numericAnswer)
        {
            try
            {
                var history=await repository.HistoryAsync(e.Id,ct,e.ChatHistoryStart);
                var result=await tools.RunAsync(request,e,history,ct);
                var nextState=(result.Employee.AdvisorState??state) with{CandidateIds=result.Candidates};
                return await ReplyAsync(result.Employee,nextState,text,result.Reply,result.Candidates.ToList(),ct);
            }
            catch(AiUnavailableException){toolUnavailable=true;}
        }
        if(request.Target is null&&fleet is not null&&fleet.Policy.Enabled&&fleet.Policy.ShowToEmployees&&fleet.Policy.SwapEnabled&&IntakeFields.Missing(e).Length==0&&FleetRules.Category(text,out var fleetCategory))
            return await ReplyAsync(e,state,text,await fleet.Advice(e,fleetCategory,ct),[],ct);
        if(request.Target is null&&IntakeFields.Missing(e).Length==0&&AdvisorDialogue.WantsDifferent(text))
        {
            var category=text.Contains("חשמל",StringComparison.Ordinal)?"electric":text.Contains("משפח",StringComparison.Ordinal)?"family":state.RequestedCategory??(state.WantsElectric?"electric":"any");
            var alternatives=await operations.BrowseAsync(e,category,true,ct);
            return await ReplyAsync(alternatives.Employee,alternatives.Employee.AdvisorState!,text,alternatives.Reply,alternatives.Candidates.ToList(),ct,toolUnavailable,status:alternatives.Status);
        }
        if(state.Target is not null&&state.Target!="waiver"&&!data.Cars.Any(car=>car.Id==state.Target))state=state with{Target=null};
        // A saved question, rather than model memory or a client's claimed context,
        // determines the meaning of a short numerical answer.
        var awaiting=state.AwaitingField;
        ExtractedData fields=new();var answered=false;var aiFallback=false;
        if(awaiting is not null&&IntakeFields.All.Contains(awaiting)&&!IntakeFields.Known(e,awaiting))
        {
            if(IntakeFields.TryAnswerNumber(text,awaiting,out var number)){e=IntakeFields.Answer(e,awaiting,number);answered=true;}
            else if(awaiting=="currentCar")
            {
                if(request.Target is {} current&&data.Cars.Any(car=>car.Id==current)){e=IntakeFields.Confirm(e with{CurrentCar=current},"currentCar");answered=true;}
                else if(text is "ללא רכב" or "אין רכב" or "אין לי רכב חברה")
                {if(e.WaiverPolicyKnown&&!data.OccupationFor(e).Waiver)throw new ArgumentException("בתפקיד זה נדרש רכב חברה; בחרו את הרכב הנוכחי מהרשימה.");e=IntakeFields.Confirm(e with{CurrentCar=null,Participation=0},"currentCar","participation");answered=true;}
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
            var history=await repository.HistoryAsync(e.Id,ct,e.ChatHistoryStart);
            using var ai=CancellationTokenSource.CreateLinkedTokenSource(ct);ai.CancelAfter(limits.Extraction);
            try{fields=(toolUnavailable?await new LocalExtractionService().ExtractAsync(text,e,[],ct):await extraction.ExtractAsync(text,e,history,ai.Token).WaitAsync(ai.Token));aiFallback=toolUnavailable;}
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
                if(string.IsNullOrWhiteSpace(e.Group)&&topics.Any(topic=>topic is "group" or "budget" or "eligible_cars"))
                {var question=new AdvisorWorkflow(state);question.Ask("eligibility");question.OfferAction(new("representative",Field:"eligibility"));e=question.Attach(e);state=question.State;}
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
            if(next is "eligibility" or "occupation")
            {
                var pendingFlow=new AdvisorWorkflow(state with{Target=target,RequestedCategory=wantsElectric?"electric":state.RequestedCategory,WantsElectric=wantsElectric});
                return await ReplyOperationAsync(await operations.ContinueAsync(pendingFlow.Attach(e),ct),text,ct);
            }
            return await ReplyAsync(e,state with{AwaitingField=next,Target=target,WantsElectric=wantsElectric,CandidateIds=candidates.ToArray()},text,IntakeFields.Question(next,data),choices,ct,aiFallback);
        }
        if(target is null&&candidates.Count==0&&(ParticipationRules.OutsideCatalogue(text)||fields.RequestedModel is {Length:>0} requestedHint&&text.Contains(requestedHint,StringComparison.OrdinalIgnoreCase)))
        {
            var outsideHint=fields.RequestedModel??text;
            var ticketTarget=ParticipationRules.CatalogueTarget(outsideHint);
            e=e with{AdvisorState=state with{AwaitingField="catalogueVehicle",Target=null,WantsElectric=wantsElectric}};
            await repository.CreateTicketAsync(e,ticketTarget,"בדיקת רכב שאינו בקטלוג: "+outsideHint+". נדרשים זיהוי רשמי, זכאות והשתתפות; אין לאשר חישוב לפני הוספת נתוני מקור.",ct,appendAnnouncement:false);
            return await ReplyAsync(e,e.AdvisorState,text,"לא נמצאה התאמה לרכב בקטלוג. העברתי שאלה לנציג; לא אנחש השתתפות או שווי שימוש ולא אציג נטו לרכב הזה. אחרי השלמת הקטלוג נוכל להמשיך בהשוואה.",[],ct);
        }
        if(target is not null&&data.Cars.Any(c=>c.Id==target))
        {
            if(!engine.Eligibility(e,data.Cars.Single(c=>c.Id==target)).Eligible)
                return await ReplyAsync(e,state with{AwaitingField=null,Target=target,WantsElectric=wantsElectric},text,"החלופה אינה בקבוצת הזכאות שלך. אפשר לבחור חלופה אחרת מהקטלוג.",[],ct);
            decimal? reported=fields.TargetParticipation;
            if(reported is null&&request.Target is not null)reported=(await new LocalExtractionService().ExtractAsync(text,e,[],ct)).TargetParticipation;
            if(awaiting=="targetParticipation"&&IntakeFields.TryNumber(text,"participation",out var amount))reported=amount;
            if(reported is not null)await repository.ReportParticipationAsync(e.Id,target,reported.Value,ct);
            if(fields.ParticipationNeedsApproval||ParticipationRules.ApprovalRequested(text))
            {
                e=e with{AdvisorState=state with{AwaitingField="targetParticipation",Target=target,WantsElectric=wantsElectric}};
                await repository.CreateTicketAsync(e,target,"העובד מבקש אישור השתתפות אישית לרכב החדש: "+text,ct);
                return await ReplyAsync(e,e.AdvisorState,text,"שלחתי לנציג בקשה לאישור ההשתתפות האישית שלך. ברירת המחדל מהקטלוג אינה מוצגת כעת כסכום סופי עבורך; ההשוואה לרכב הזה ממתינה לאישור.",[],ct);
            }
        }
        e=e with{AdvisorState=state with{AwaitingField=null,Target=target,WantsElectric=wantsElectric}};
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
            else
            {
                reply=await NarrateAsync(result,ct);
                if(target=="waiver"&&e.CurrentCar is null&&result.Delta==0)reply="לפי הנתונים שאישרת, אין לך כיום רכב חברה. ההשוואה היא מול מצב הוויתור הקיים שלך, ולכן אין שינוי בנטו.\n"+reply;
            }
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
        return await ReplyAsync(e,state with{AwaitingField=nextAwaiting,Target=target,WantsElectric=wantsElectric,CandidateIds=candidates.ToArray()},text,reply,candidates,ct,aiFallback);
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
    private async Task<object> ReplyOperationAsync(AdvisorActionResult result,string text,CancellationToken ct)
    {
        var reply=result.Reply;
        if(result.Calculation is {} calculation)
        {
            reply=await NarrateAsync(calculation,ct);
            if(calculation.Id=="waiver"&&result.Employee.CurrentCar is null&&calculation.Delta==0)reply="לפי הנתונים שאישרת, אין לך כיום רכב חברה. ההשוואה היא מול מצב הוויתור הקיים שלך, ולכן אין שינוי בנטו.\n"+reply;
        }
        return await ReplyAsync(result.Employee,result.Employee.AdvisorState!,text,reply,result.Candidates.ToList(),ct,status:result.Status);
    }
    private async Task<object> ReplyAsync(Employee employee,AdvisorState state,string text,string reply,List<string> candidates,CancellationToken ct,bool aiFallback=false,string? status=null)
    {
        state=AdvisorWorkflow.Reconcile(employee,state,candidates.ToArray());
        if(aiFallback)reply="כדי לוודא שהפרטים נקלטו נכון, בדקו אותם לפני המשך ההשוואה.\n"+reply;
        employee=employee with{AdvisorState=state};reply=AdvisorLanguage.Neutral(reply);
        await repository.SaveConversationAsync(employee,state.Target,ct);
        await repository.AddMessageAsync(employee.Id,"user",text,ct);await repository.AddMessageAsync(employee.Id,"assistant",reply,ct);
        return new{reply,target=state.Target,candidates,employee,status=status??state.Memory?.Phase,mode=aiFallback?"guided-local":extraction.Mode,awaitingField=state.AwaitingField,missingFields=IntakeFields.Missing(employee),complete=IntakeFields.Missing(employee).Length==0&&state.AwaitingField is null&&state.Memory?.Pending is null};
    }
}
