using System.Text.Json;
using System.Text.Json.Serialization;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed class SandboxedPayslipOcr(IOcrSandboxRuntime runtime) : ILocalPayslipOcr
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private readonly SemaphoreSlim capacity=new(2,2),statusGate=new(1,1);
    private readonly Dictionary<string,(DateTimeOffset At,LocalOcrStatus Status)> statuses=[];
    public async Task<LocalOcrStatus> StatusAsync(LocalPayslipSettings settings,CancellationToken ct)
    {
        await statusGate.WaitAsync(ct);
        try
        {
            if(statuses.TryGetValue(settings.Languages,out var cached)&&DateTimeOffset.UtcNow-cached.At<TimeSpan.FromSeconds(20))return cached.Status;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));
            LocalOcrStatus status;
            try
            {
                var reply=Read(await runtime.RunAsync("check",settings,null,0,deadline.Token));
                status=reply.Success?new(true,"ready","חילוץ מקומי מבודד פעיל. התלוש אינו נשלח ל־AI חיצוני; שדות השכר נשמרים רק לאחר בדיקה ושמירה."):
                    new(false,"sandbox_worker_unavailable","מנוע החילוץ המבודד אינו זמין. פנו למנהל המערכת; אפשר להזין נתונים ידנית.");
            }
            catch(Exception error)when(error is OcrSandboxException or JsonException or OperationCanceledException&&!ct.IsCancellationRequested)
            {status=new(false,error is OcrSandboxException sandbox?sandbox.Code:"sandbox_unavailable","החילוץ המבודד אינו זמין. יש להפעיל Docker במצב Linux ולהתקין את תמונת ה־OCR באמצעות npm run ocr:setup. אפשר להזין נתונים ידנית.");}
            statuses[settings.Languages]=(DateTimeOffset.UtcNow,status);return status;
        }
        finally{statusGate.Release();}
    }
    public async Task<PayslipResult> ExtractAsync(AiDocument document,LocalPayslipSettings settings,long revision,CancellationToken ct)
    {
        if(!await capacity.WaitAsync(0,ct))throw new ArgumentException("מנוע החילוץ המבודד עסוק. נסו שוב בעוד מספר שניות.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds+20));
        try
        {
            var reply=Read(await runtime.RunAsync("extract",settings,document.Bytes,revision,deadline.Token));
            return Result(reply,revision);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested)
        {throw new ArgumentException("החילוץ המבודד לא הסתיים בזמן. נסו קובץ קטן יותר או הזינו נתונים ידנית.");}
        catch(Exception error)when(error is OcrSandboxException or JsonException)
        {throw new ArgumentException("לא ניתן לאמת את סביבת החילוץ המבודדת או את התוצאה. לא בוצע עיבוד חלופי; אפשר להזין נתונים ידנית.");}
        finally{capacity.Release();}
    }
    private static OcrWorkerResponse Read(string json)
    {
        var reply=JsonSerializer.Deserialize<OcrWorkerResponse>(json,Json)??throw new OcrSandboxException("sandbox_invalid_output");
        if(reply.Version!=1)throw new OcrSandboxException("sandbox_invalid_output");return reply;
    }
    public static PayslipResult Result(OcrWorkerResponse reply,long revision)
    {
        if(!reply.Success)throw new ArgumentException(reply.Error=="timeout"?"החילוץ המקומי לא הסתיים בזמן. נסו קובץ קצר יותר.":"לא ניתן לפענח את המסמך בבידוד. בדקו את הקובץ ואת מגבלת העמודים, או השלימו נתונים ידנית.");
        if(reply.Version!=1||reply.Source is not ("local-ocr" or "local-pdf-text")||reply.Fields is null||reply.Fields.Count!=PayslipFields.Limits.Count||
            reply.Fields.Any(field=>!PayslipFields.Limits.TryGetValue(field.Key,out var maximum)||field.Value<0||field.Value>maximum))
            throw new OcrSandboxException("sandbox_invalid_output");
        // Never trust free text from a potentially compromised native worker. The protocol
        // permits only known field identifiers, bounded numbers and a known source enum.
        var fields=reply.Fields.ToDictionary(field=>field.Key,field=>new PayslipField(field.Value,null));
        return new(fields,["החילוץ המבודד הוא הצעה בלבד. בדקו כל שדה מול התלוש; ערך חסר או עמום דורש השלמה ידנית. שדות נשמרים רק אחרי אישור ושמירה."],revision){Source=reply.Source};
    }
}
