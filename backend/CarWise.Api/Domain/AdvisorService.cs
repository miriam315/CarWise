using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed class AdvisorService(ReferenceData data,PayrollEngine engine,Repository repository,
    IDataExtractionService extraction,IResponseNarrationService narration,AdvisorTiming? timing=null)
{
    private readonly AdvisorTiming limits=(timing??AdvisorTiming.Default).Validate();
    private static readonly Dictionary<string,string> Aliases=new(){["אטו"]="CAR-10",["atto"]="CAR-10",["קרניבל"]="CAR-19",
        ["טסלה"]="CAR-14",["אוקטביה"]="CAR-15",["טוסון"]="CAR-12",["דולפין"]="CAR-03",["נירו"]="CAR-05",
        ["סנטה"]="CAR-20",["קונה"]="CAR-08",["פיקנטו"]="CAR-01",["קורולה קרוס"]="CAR-09",["מאזדה"]="CAR-13",["סיל"]="CAR-17"};
    private static readonly SemaphoreSlim[] Gates=Enumerable.Range(0,128).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    public async Task<Employee> RefreshKnownParticipationAsync(Employee employee,CancellationToken ct)
    {
        if(IntakeFields.Known(employee,"currentCar")&&employee.CurrentCar is null)return IntakeFields.Answer(employee,"participation",0);
        if(IntakeFields.Known(employee,"participation")||employee.CurrentCar is null)return employee;
        var (approved,_)=await repository.ParticipationAsync(employee.Id,ct);
        return approved.TryGetValue(employee.CurrentCar,out var amount)?IntakeFields.Answer(employee,"participation",amount):employee;
    }
    public async Task<Simulation> SimulateAsync(Employee e,CancellationToken ct)
    {
        e=await RefreshKnownParticipationAsync(e,ct);
        var (approved,pending)=await repository.ParticipationAsync(e.Id,ct);
        return engine.Simulate(e,approved,pending);
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
        var profile=conversation?.Employee??(request.Employee with{AdvisorState=null});
        if(!canChangeOccupation)profile=profile with{Occupation=request.Employee.Occupation};
        var e=engine.Validate(profile);
        e=await RefreshKnownParticipationAsync(e,ct);
        var state=e.AdvisorState??new AdvisorState();
        if(state.Target is not null&&state.Target!="waiver"&&!data.Cars.Any(car=>car.Id==state.Target))state=state with{Target=null};
        // A saved question, rather than model memory or a client's claimed context,
        // determines the meaning of a short numerical answer.
        var awaiting=state.AwaitingField;
        ExtractedData fields=new();var answered=false;var aiFallback=false;
        if(awaiting is not null&&IntakeFields.All.Contains(awaiting)&&!IntakeFields.Known(e,awaiting))
        {
            if(IntakeFields.TryNumber(text,awaiting,out var number)){e=IntakeFields.Answer(e,awaiting,number);answered=true;}
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
        if(!answered&&awaiting is not null&&IntakeFields.All.Contains(awaiting)&&System.Text.RegularExpressions.Regex.IsMatch(text,@"^[\d, .%₪ש״ח+\-]+$",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
            return await ReplyAsync(e,state,text,"הערך אינו תקין לשדה שנשאל. "+IntakeFields.Question(awaiting,data),[],ct);
        if(!answered&&request.Target is null)
        {
            var history=await repository.HistoryAsync(e.Id,ct);
            using var ai=CancellationTokenSource.CreateLinkedTokenSource(ct);ai.CancelAfter(limits.Extraction);
            try{fields=await extraction.ExtractAsync(text,e,history,ai.Token).WaitAsync(ai.Token);}
            catch(Exception error)when(!ct.IsCancellationRequested&&error is AiUnavailableException or HttpRequestException or OperationCanceledException)
            {aiFallback=true;fields=await new LocalExtractionService().ExtractAsync(text,e,[],ct);}
            if(!canChangeOccupation&&fields.Occupation is not null&&fields.Occupation!=e.Occupation)throw new UnauthorizedAccessException("Forbidden");
            e=IntakeFields.Apply(e,fields);
        }
        e=engine.Validate(e);
        var lower=text.ToLowerInvariant();var candidates=new List<string>();var target=answered?state.Target:request.Target;
        if(target is null&&(awaiting is not null||fields.HasUpdates||text is "נמשיך" or "המשך" or "מה התשובה"))target=state.Target;
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
            return await ReplyAsync(e,new(next,target,wantsElectric,candidates.ToArray()),text,IntakeFields.Question(next,data),choices,ct,aiFallback);
        }
        e=e with{AdvisorState=new(null,target,wantsElectric)};
        var simulation=await SimulateAsync(e,ct);
        string reply;string? nextAwaiting=null;
        if(target is not null)
        {
            var result=simulation.Results.SingleOrDefault(r=>r.Id==target)??throw new ArgumentException("הרכב לא נמצא; בחרו דגם מדויק מהקטלוג");
            if(!result.Eligible)reply="האפשרות הזו אינה זמינה: "+result.Reason+". אפשר לבחור חלופה אחרת מהקטלוג.";
            else if(result.Pending)
            {
                nextAwaiting="targetParticipation";
                await repository.CreateTicketAsync(e,target,"מהי השתתפות העובד המאושרת ברכב החדש? הסכום אינו ידוע בקטלוג או ממתין לאישור.",ct);
                reply="חסר סכום השתתפות מאושר. שלחתי שאלה לנציג התחבורה. החלופה ממתינה לאישור ותתעדכן אחרי התשובה; לא מוצג נטו סופי לרכב הזה.";
            }
            else reply=await NarrateAsync(result,ct);
        }
        else if(candidates.Count>0)reply="כדי לזהות את הרכב לפי המפתח הרשמי, בחרו את הדגם המדויק מהרשימה. שם מסחרי לבדו לא מספיק.";
        else if(wantsElectric)
        {
            candidates=simulation.Results.Where(r=>r.Eligible&&!r.Pending&&r.Car?.Drive=="חשמלי").OrderByDescending(r=>r.Net).Take(3).Select(r=>r.Id).ToList();
            reply="הנה רכבים חשמליים שמתאימים לקבוצת הזכאות שלך, לפי הנטו הגבוה ביותר. בחרו דגם מדויק כדי להציג חישוב והסבר.";
        }
        else reply="אפשר להשוות רכב מסוים, לבדוק מעבר לחשמלי או לשאול על ויתור על הרכב. בחרו דגם מהקטלוג או כתבו את שמו. נתוני השכר וההפרשות ניתנים לעדכון דרך ״הנתונים שלי״.";
        if(fields.HasUpdates||answered)reply="עדכנתי את הנתונים שמסרת ושמרתי אותם.\n"+reply;
        return await ReplyAsync(e,new(nextAwaiting,target,wantsElectric,candidates.ToArray()),text,reply,candidates,ct,aiFallback);
    }
    private async Task<string> NarrateAsync(Alternative result,CancellationToken ct)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(limits.Narration);
        try{return await narration.NarrateAsync(result,budget.Token).WaitAsync(budget.Token);}
        catch(Exception error)when(!ct.IsCancellationRequested&&error is AiUnavailableException or HttpRequestException or OperationCanceledException)
        {return FinancialText.Template(result);}
    }
    private async Task<object> ReplyAsync(Employee employee,AdvisorState state,string text,string reply,List<string> candidates,CancellationToken ct,bool aiFallback=false)
    {
        if(aiFallback)reply="שירות ה־AI אינו זמין כרגע. רק נתונים בניסוח מפורש נקלטו; בדקו את הפרטים לפני בחירה.\n"+reply;
        employee=employee with{AdvisorState=state};
        await repository.SaveConversationAsync(employee,state.Target,ct);
        await repository.AddMessageAsync(employee.Id,"user",text,ct);await repository.AddMessageAsync(employee.Id,"assistant",reply,ct);
        return new{reply,target=state.Target,candidates,employee,mode=aiFallback?"guided-local":extraction.Mode,awaitingField=state.AwaitingField,missingFields=IntakeFields.Missing(employee),complete=IntakeFields.Missing(employee).Length==0&&state.AwaitingField is null};
    }
}
