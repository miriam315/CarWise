using CarWise.Api.AI;
using CarWise.Api.Infrastructure;
namespace CarWise.Api.Domain;

public sealed class AdvisorService(ReferenceData data,PayrollEngine engine,Repository repository,
    IDataExtractionService extraction,IResponseNarrationService narration)
{
    private static readonly Dictionary<string,string> Aliases=new(){["אטו"]="CAR-10",["atto"]="CAR-10",["קרניבל"]="CAR-19",
        ["טסלה"]="CAR-14",["אוקטביה"]="CAR-15",["טוסון"]="CAR-12",["דולפין"]="CAR-03",["נירו"]="CAR-05",
        ["סנטה"]="CAR-20",["קונה"]="CAR-08",["פיקנטו"]="CAR-01",["קורולה קרוס"]="CAR-09",["מאזדה"]="CAR-13",["סיל"]="CAR-17"};
    public async Task<Simulation> SimulateAsync(Employee e,CancellationToken ct)
    {
        var (approved,pending)=await repository.ParticipationAsync(e.Id,ct);
        // Client overrides are deliberately ignored; approved participation comes only from persisted agent answers.
        return engine.Simulate(e,approved,pending);
    }
    public async Task<object> ChatAsync(ChatRequest request,CancellationToken ct,bool canChangeOccupation=true)
    {
        var e=engine.Validate(request.Employee);var text=request.Text?.Trim()??"";
        if(text.Length is 0 or >2000)throw new ArgumentException("כתבו שאלה עד 2,000 תווים");
        var history=await repository.HistoryAsync(e.Id,ct);
        // Explicit catalogue selection bypasses AI availability. Eligibility still runs exclusively in C#.
        var fields=request.Target is not null?new ExtractedData():await extraction.ExtractAsync(text,e,history,ct);
        if(!canChangeOccupation&&fields.Occupation is not null&&fields.Occupation!=e.Occupation)throw new UnauthorizedAccessException("Forbidden");
        e=engine.Validate(fields.Apply(e));var simulation=await SimulateAsync(e,ct);
        var lower=text.ToLowerInvariant();var candidates=new List<string>();var target=request.Target;
        if(target is null && fields.VehicleKey is { } key &&
            string.Concat(text.Where(c=>!char.IsWhiteSpace(c))).Contains(
                OfficialRecord.KeyFor(key.TaxYear,key.RegistrationYear,key.VehicleType,key.MakerCode,key.ModelCode),StringComparison.Ordinal))
        {
            var found=data.Cars.Where(c=>key.TaxYear==data.TaxYear && c.Year==key.RegistrationYear && key.VehicleType=="1" &&
                c.MakerCode==key.MakerCode && c.ModelCode==key.ModelCode).ToList();
            if(found.Count==1)target=found[0].Id;
        }
        if(target is null)
        {
            var hint=(fields.RequestedModel??lower).ToLowerInvariant();
            foreach(var c in data.Cars)if(hint.Contains(c.Model.ToLowerInvariant()))candidates.Add(c.Id);
            foreach(var (alias,id) in Aliases)if(hint.Contains(alias)&&!candidates.Contains(id))candidates.Add(id);
            // A language model's textual match is only a candidate. Confirm identity by explicit UI choice.
            if(candidates.Count==1 && extraction.Mode=="local-rules")target=candidates[0];
            else if(candidates.Count==0 && fields.IsWaiverRequested)target="waiver";
        }
        string reply;
        if(target is not null)
        {
            var result=simulation.Results.SingleOrDefault(r=>r.Id==target)??throw new ArgumentException("הרכב לא נמצא; בחרו דגם מדויק מהקטלוג");
            if(!result.Eligible)reply="האפשרות הזו אינה זמינה: "+result.Reason+". אפשר לבחור חלופה אחרת מהקטלוג.";
            else if(result.Pending)reply="החלופה הזו ממתינה לאישור השתתפות מנציג התחבורה. לא אציג נטו סופי עד לקבלת הסכום המאושר. אפשר בינתיים לבדוק חלופה אחרת.";
            else reply=await narration.NarrateAsync(result,ct);
        }
        else if(candidates.Count>0)reply="כדי לזהות את הרכב לפי המפתח הרשמי, בחרו את הדגם המדויק מהרשימה. שם מסחרי לבדו לא מספיק.";
        else if(fields.WantsElectric)
        {
            candidates=simulation.Results.Where(r=>r.Eligible&&!r.Pending&&r.Car?.Drive=="חשמלי").OrderByDescending(r=>r.Net).Take(3).Select(r=>r.Id).ToList();
            reply="הנה רכבים חשמליים שמתאימים לקבוצת הזכאות שלך, לפי הנטו הגבוה ביותר. בחרו דגם מדויק כדי להציג חישוב והסבר.";
        }
        else reply="אפשר להשוות רכב מסוים, לבדוק מעבר לחשמלי או לשאול על ויתור על הרכב. בחרו דגם מהקטלוג או כתבו את שמו. נתוני השכר וההפרשות ניתנים לעדכון דרך ״הנתונים שלי״.";
        if(fields.HasUpdates)reply="עדכנתי את הנתונים שמסרת, וכל החלופות חושבו מחדש.\n"+reply;
        await repository.SaveConversationAsync(e,target,ct);
        await repository.AddMessageAsync(e.Id,"user",text,ct);await repository.AddMessageAsync(e.Id,"assistant",reply,ct);
        return new{reply,target,candidates,employee=fields.HasUpdates?e:null,mode=extraction.Mode};
    }
}
