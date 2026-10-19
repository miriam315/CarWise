using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public sealed record LocalOcrStatus(bool Ready,string Code,string Message);
public interface ILocalPayslipOcr
{
    Task<LocalOcrStatus> StatusAsync(LocalPayslipSettings settings,CancellationToken ct);
    Task<PayslipResult> ExtractAsync(AiDocument document,LocalPayslipSettings settings,long revision,CancellationToken ct);
}
/// <summary>Local native tools communicate exclusively through pipes. No document URL, AI client,
/// file upload service or original filename is passed to a child process.</summary>
public sealed class LocalPayslipOcr(IConfiguration configuration,string root) : ILocalPayslipOcr
{
    private readonly SemaphoreSlim capacity=new(2,2);
    private readonly SemaphoreSlim statusGate=new(1,1);
    private readonly Dictionary<string,(DateTimeOffset At,LocalOcrStatus Status)> statuses=[];
    private string? Executable(string setting,string command)
    {
        var configured=configuration["CarWise:PayslipOcr:"+setting];
        if(!string.IsNullOrWhiteSpace(configured))return File.Exists(configured)?Path.GetFullPath(configured):null;
        foreach(var directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
        {
            var candidate=Path.Combine(directory,command+(OperatingSystem.IsWindows()?".exe":""));
            if(File.Exists(candidate))return candidate;
        }
        if(command=="tesseract"&&OperatingSystem.IsWindows())
        {
            var candidate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Tesseract-OCR","tesseract.exe");
            if(File.Exists(candidate))return candidate;
        }
        return null;
    }
    private string? Tessdata
    {
        get{var configured=configuration["CarWise:PayslipOcr:TessdataDirectory"];var bundled=Path.Combine(root,".local","ocr","tessdata");
            return !string.IsNullOrWhiteSpace(configured)?configured:Directory.Exists(bundled)?bundled:null;}
    }
    private List<string> TesseractArguments(params string[] arguments)
    {var result=arguments.ToList();if(Tessdata is { } directory)result.AddRange(["--tessdata-dir",directory]);return result;}
    public async Task<LocalOcrStatus> StatusAsync(LocalPayslipSettings settings,CancellationToken ct)
    {
        await statusGate.WaitAsync(ct);
        try
        {
            if(statuses.TryGetValue(settings.Languages,out var cached)&&DateTimeOffset.UtcNow-cached.At<TimeSpan.FromSeconds(20))return cached.Status;
            var missing=new[]{("TesseractPath","tesseract"),("PdfToTextPath","pdftotext"),("PdfInfoPath","pdfinfo"),("PdfToPpmPath","pdftoppm")}
                .Where(tool=>Executable(tool.Item1,tool.Item2) is null).Select(tool=>tool.Item2).ToArray();
            LocalOcrStatus status;
            if(missing.Length>0)status=new(false,"ocr_tools_missing","נדרשת התקנה מקומית בשרת של Tesseract ו־Poppler. חסרים: "+string.Join(", ",missing)+". פנו למנהל; אפשר להזין נתונים ידנית.");
            else
            {
                try
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var languages=await TextAsync(Executable("TesseractPath","tesseract")!,TesseractArguments("--list-langs"),[],65536,timeout.Token);
                    status=settings.Languages.Split('+').All(lang=>languages.Split('\n').Any(line=>line.Trim()==lang))?
                        new(true,"ready","חילוץ מקומי פעיל. התלוש אינו נשלח ל־AI חיצוני. שדות השכר יישמרו רק לאחר בדיקה ושמירה."):
                        new(false,"ocr_languages_missing","חסרים קובצי שפה ל־OCR המקומי ("+settings.Languages+"). הריצו npm run ocr:setup בשרת ובדקו שוב לאחר כ־20 שניות.");
                }
                catch(Exception error)when(error is ArgumentException or OperationCanceledException&&!ct.IsCancellationRequested)
                {status=new(false,"ocr_unavailable","לא ניתן להפעיל את מנוע ה־OCR המקומי בשרת. בדקו התקנה והרשאות; אפשר להזין נתונים ידנית.");}
            }
            statuses[settings.Languages]=(DateTimeOffset.UtcNow,status);return status;
        }
        finally{statusGate.Release();}
    }
    public async Task<PayslipResult> ExtractAsync(AiDocument document,LocalPayslipSettings settings,long revision,CancellationToken ct)
    {
        var status=await StatusAsync(settings,ct);if(!status.Ready)throw new ArgumentException(status.Message);
        if(!await capacity.WaitAsync(0,ct))throw new ArgumentException("מנוע החילוץ המקומי עסוק. נסו שוב בעוד מספר שניות.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            if(document.MediaType=="application/pdf")
            {
                var info=await TextAsync(Executable("PdfInfoPath","pdfinfo")!,["-"],document.Bytes,65536,deadline.Token);
                var match=Regex.Match(info,@"(?m)^Pages:\s*(\d+)\s*$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                if(!match.Success||!int.TryParse(match.Groups[1].Value,out var pages)||pages<1)throw new ArgumentException("לא ניתן לקרוא את מספר עמודי ה־PDF. בדקו שהקובץ תקין ואינו מוגן בסיסמה.");
                if(pages>settings.MaxPages)throw new ArgumentException($"התלוש מכיל {pages} עמודים; המגבלה שהוגדרה היא {settings.MaxPages}. העלו רק את עמודי התלוש הרלוונטיים.");
                var text=await TextAsync(Executable("PdfToTextPath","pdftotext")!,["-layout","-enc","UTF-8","-","-"],document.Bytes,500_000,deadline.Token);
                var parsed=LocalPayrollFields.Extract(text.Split('\n').Select(line=>new LocalTextLine(line)),settings.MinimumConfidence,revision,"local-pdf-text",deadline.Token);
                if(parsed.Fields.Any(field=>field.Value.Value is not null))return parsed;
                var lines=new List<LocalTextLine>();
                for(var page=1;page<=pages;page++)
                {
                    var image=await RunAsync(Executable("PdfToPpmPath","pdftoppm")!,["-f",page.ToString(CultureInfo.InvariantCulture),"-l",page.ToString(CultureInfo.InvariantCulture),"-r","180","-scale-to","2600","-png","-singlefile","-"],document.Bytes,24*1024*1024,deadline.Token);
                    try{lines.AddRange(await RecognizeAsync(image,settings,deadline.Token));}finally{CryptographicOperations.ZeroMemory(image);}
                }
                return LocalPayrollFields.Extract(lines,settings.MinimumConfidence,revision,"local-ocr",deadline.Token);
            }
            LocalImageLimits.Validate(document);
            return LocalPayrollFields.Extract(await RecognizeAsync(document.Bytes,settings,deadline.Token),settings.MinimumConfidence,revision,"local-ocr",deadline.Token);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested)
        {throw new ArgumentException($"החילוץ המקומי לא הושלם בתוך {settings.TimeoutSeconds} שניות. נסו קובץ קטן יותר או הזינו את הנתונים ידנית.");}
        finally{capacity.Release();}
    }
    private async Task<IReadOnlyList<LocalTextLine>> RecognizeAsync(byte[] bytes,LocalPayslipSettings settings,CancellationToken ct)
    {
        var args=TesseractArguments("stdin","stdout","-l",settings.Languages,"--psm","6","-c","tessedit_create_tsv=1","-c","tessedit_create_txt=0");
        var tsv=await TextAsync(Executable("TesseractPath","tesseract")!,args,bytes,1_000_000,ct);
        var lines=new Dictionary<string,List<(string Text,double Confidence)>>();
        foreach(var row in tsv.Split('\n').Skip(1))
        {
            var cells=row.TrimEnd('\r').Split('\t',12);if(cells.Length!=12||cells[0]!="5"||string.IsNullOrWhiteSpace(cells[11]))continue;
            if(!double.TryParse(cells[10],NumberStyles.Float,CultureInfo.InvariantCulture,out var confidence))continue;
            var key=string.Join(':',cells.Skip(1).Take(4));if(!lines.TryGetValue(key,out var words))lines[key]=words=[];
            words.Add((cells[11],confidence));
        }
        return lines.Values.Select(words=>new LocalTextLine(string.Join(' ',words.Select(w=>w.Text)),words.Min(w=>w.Confidence))).ToArray();
    }
    private static async Task<string> TextAsync(string executable,IEnumerable<string> arguments,byte[] input,int maxBytes,CancellationToken ct)
    {var output=await RunAsync(executable,arguments,input,maxBytes,ct);try{return Encoding.UTF8.GetString(output);}finally{CryptographicOperations.ZeroMemory(output);}}
    private static async Task<byte[]> RunAsync(string executable,IEnumerable<string> arguments,byte[] input,int maxBytes,CancellationToken ct)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        // Native tools do not inherit database credentials, provider keys or application settings.
        start.Environment.Clear();
        foreach(var name in new[]{"PATH","SystemRoot","WINDIR","TEMP","TMP","FONTCONFIG_PATH"})
            if(Environment.GetEnvironmentVariable(name) is { } value)start.Environment[name]=value;
        start.Environment["OMP_THREAD_LIMIT"]="1";start.Environment["LC_ALL"]="C";
        using var process=new Process{StartInfo=start};
        try{if(!process.Start())throw new ArgumentException("לא ניתן להפעיל את כלי החילוץ המקומי.");}
        catch(Win32Exception){throw new ArgumentException("לא ניתן להפעיל את כלי החילוץ המקומי. בדקו התקנה והרשאות.");}
        void Kill(){try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(Win32Exception){}}
        using var registration=ct.Register(Kill);
        using var output=new MemoryStream();
        async Task CopyBounded(Stream stream,MemoryStream? target,int limit)
        {
            var buffer=new byte[65536];var total=0;
            try{int count;while((count=await stream.ReadAsync(buffer,ct))>0){total+=count;if(total>limit){Kill();throw new ArgumentException("תוצאת החילוץ גדולה מהמותר. נסו תלוש קצר יותר.");}if(target is not null)await target.WriteAsync(buffer.AsMemory(0,count),ct);}}
            finally{CryptographicOperations.ZeroMemory(buffer);}
        }
        async Task Write(){try{await process.StandardInput.BaseStream.WriteAsync(input,ct);}catch(IOException){}finally{process.StandardInput.Close();}}
        try
        {
            await Task.WhenAll(Write(),CopyBounded(process.StandardOutput.BaseStream,output,maxBytes),CopyBounded(process.StandardError.BaseStream,null,65536),process.WaitForExitAsync(ct));
            ct.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)throw new ArgumentException("לא ניתן לפענח את המסמך מקומית. בדקו שהקובץ תקין ואינו מוגן בסיסמה, או הזינו את הנתונים ידנית.");
            return output.ToArray();
        }
        finally{Kill();if(output.TryGetBuffer(out var buffer))CryptographicOperations.ZeroMemory(buffer.AsSpan());}
    }
}
