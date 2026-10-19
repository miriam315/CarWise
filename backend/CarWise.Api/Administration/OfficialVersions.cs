using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Domain;
using CarWise.Api.Infrastructure;
using Microsoft.VisualBasic.FileIO;
namespace CarWise.Api.Administration;

public sealed record OfficialVersion(string Id,int TaxYear,int RowCount,int Chunks,string FileName,string Sha256,DateTimeOffset UploadedAt,string Actor);
public sealed record MissingOfficial(string Id,string Model,string Key);
public sealed record OfficialCarReplacement(int RegistrationYear,string MakerCode,string ModelCode);
public sealed record OfficialPreview(string VersionId,int TaxYear,int RowCount,IReadOnlyList<object> Matches,IReadOnlyList<MissingOfficial> Missing);
public sealed record OfficialPreviewRequest(string VersionId,Dictionary<string,OfficialCarReplacement>? Replacements=null);
public sealed record ActivateOfficialRequest(string VersionId,long ExpectedRevision,string Note,Dictionary<string,OfficialCarReplacement>? Replacements=null);
public sealed class OfficialVersions(IJsonDatabase database,ReferenceData baseline)
{
    public const int MaxBytes=32*1024*1024;
    private readonly ConcurrentDictionary<string,IReadOnlyDictionary<string,OfficialRecord>> cache=new();
    public static List<OfficialRecord> Parse(byte[] bytes)
    {
        try{return ParseCore(bytes);}
        catch(Exception error)when(error is DecoderFallbackException or MalformedLineException or InvalidDataException or FormatException or OverflowException)
        {throw new ArgumentException("לא ניתן לקרוא את ה־CSV. בדקו קידוד, שורת כותרת ומבנה רשומות.");}
    }
    private static List<OfficialRecord> ParseCore(byte[] bytes)
    {
        if(bytes.Length==0||bytes.Length>MaxBytes)throw new ArgumentException("קובץ CSV נדרש, עד 32MB");
        string text;
        try
        {
            if(bytes.AsSpan().StartsWith(new byte[]{255,254}))text=new UnicodeEncoding(false,true,true).GetString(bytes,2,bytes.Length-2);
            else if(bytes.AsSpan().StartsWith(new byte[]{254,255}))text=new UnicodeEncoding(true,true,true).GetString(bytes,2,bytes.Length-2);
            else text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch(DecoderFallbackException)
        {Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);text=Encoding.GetEncoding(1255,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}
        using var parser=new TextFieldParser(new StringReader(text));parser.SetDelimiters(",");parser.HasFieldsEnclosedInQuotes=true;
        var headers=parser.ReadFields()?.Select(h=>h.Trim()).ToArray()??throw new ArgumentException("חסרה כותרת CSV");
        string[] required=["שנת מס","שנת רישום","קוד סוג רכב","קוד תוצר","קוד דגם","שווי שימוש","1-חשמלי.2-היברידי.3-פלג-אין","מחיר מחירון"];
        if(headers.Length>60||headers.Distinct().Count()!=headers.Length||required.Any(h=>!headers.Contains(h)))throw new ArgumentException("כותרות CSV אינן תואמות למאגר הרשמי. השתמשו במבנה הקובץ המורחב, כולל שנת מס וקודי רכב.");
        var rows=new List<OfficialRecord>();var keys=new HashSet<string>();int? year=null;
        while(!parser.EndOfData)
        {
            if(rows.Count>=200000)throw new ArgumentException("המגבלה היא 200,000 רשומות");
            try
            {
                var fields=parser.ReadFields()!;
                if(fields.Length!=headers.Length||fields.Any(f=>f.Length>2000))throw new InvalidDataException();
                var document=headers.Zip(fields).ToDictionary(p=>p.First,p=>p.Second.Trim());
                var row=ReferenceData.ParseOfficial(document);
                if(row.TaxYear is <2000 or >2100||row.RegistrationYear<1950||row.RegistrationYear>row.TaxYear+1||row.Value is <0 or >1000000||
                    !Regex.IsMatch(row.VehicleType,@"^\d{1,4}$")||!Regex.IsMatch(row.MakerCode,@"^\d{1,12}$")||!Regex.IsMatch(row.ModelCode,@"^\d{1,12}$")||
                    row.Drive is not ("0" or "1" or "2" or "3")||!decimal.TryParse(document["מחיר מחירון"],NumberStyles.Number,CultureInfo.InvariantCulture,out var price)||price<0)
                    throw new InvalidDataException();
                year??=row.TaxYear;if(year!=row.TaxYear)throw new ArgumentException("כל קובץ חייב להכיל שנת מס אחת בלבד");
                if(!keys.Add(row.Key))throw new ArgumentException($"מפתח רכב כפול בשורת נתונים {rows.Count+1}");
                rows.Add(row);
            }
            catch(Exception error)when(error is FormatException or OverflowException or InvalidDataException or MalformedLineException)
            {throw new ArgumentException($"רשומה לא תקינה בשורת נתונים {rows.Count+1}; בדקו מספרים, קודים וכמות עמודות. הקובץ לא הופעל.");}
        }
        if(rows.Count==0)throw new ArgumentException("הקובץ ריק מנתונים");return rows;
    }
    public async Task<OfficialVersion> UploadAsync(byte[] bytes,string fileName,string actor,CancellationToken ct)
    {
        var rows=Parse(bytes);var id=Guid.NewGuid().ToString("N");var chunks=rows.Chunk(250).ToArray();
        for(var start=0;start<chunks.Length;start+=8)
        {
            var batch=new JsonArray();
            for(var i=start;i<Math.Min(start+8,chunks.Length);i++)batch.Add(new JsonObject{["id"]=$"official-chunk:{id}:{i:D5}",
                ["document"]=JsonNode.Parse(JsonDefaults.Serialize(chunks[i].Select(r=>r.Document)))});
            await database.UpsertAsync("carwise_rules",batch,ct);
        }
        var name=Path.GetFileName(fileName.Replace('\\','/'));if(name.Length>160)name=name[..160];
        var version=new OfficialVersion(id,rows[0].TaxYear,rows.Count,chunks.Length,name,Convert.ToHexString(SHA256.HashData(bytes)),DateTimeOffset.UtcNow,actor);
        // A version becomes visible only after every immutable chunk has been stored successfully.
        await database.TryWriteRuleAsync("official-version:"+id,null,JsonNode.Parse(JsonDefaults.Serialize(version))!,ct);
        return version;
    }
    public async Task<List<OfficialVersion>> ListAsync(CancellationToken ct)
    {
        var versions=new List<OfficialVersion>{new("baseline",baseline.TaxYear,baseline.OfficialRowCount,0,"מאגר מקורי", "",DateTimeOffset.MinValue,"system")};string? after=null;
        do
        {
            var rows=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix","official-version:"}},ct:ct,afterId:after);
            versions.AddRange(rows.Select(r=>JsonDefaults.Read<OfficialVersion>(r!["document"]!.ToJsonString())));
            if(rows.Count<1000)break;after=rows[^1]!["id"]!.GetValue<string>();
        }while(true);
        return versions.OrderByDescending(v=>v.UploadedAt).ToList();
    }
    public async Task<IReadOnlyDictionary<string,OfficialRecord>> LoadAsync(string id,CancellationToken ct)
    {
        if(id=="baseline")
        {
            if(baseline.Official.Count==baseline.OfficialRowCount)return baseline.Official;
            if(cache.TryGetValue(id,out var all))return all;
            var baselineRecords=new Dictionary<string,OfficialRecord>();string? cursor=null;
            while(true)
            {
                var rows=await database.ReadAsync("carwise_official",limit:250,ct:ct,afterId:cursor);if(rows.Count==0)break;
                foreach(var row in rows){var record=ReferenceData.ParseOfficial(JsonDefaults.Read<Dictionary<string,string>>(row!["document"]!.ToJsonString()));baselineRecords.Add(record.Key,record);}
                cursor=rows[^1]!["id"]!.GetValue<string>();
            }
            if(baselineRecords.Count!=baseline.OfficialRowCount)throw new InvalidDataException("Official baseline changed; reload the server before continuing.");
            cache.TryAdd(id,baselineRecords);return baselineRecords;
        }
        if(!Regex.IsMatch(id,@"^[a-f0-9]{32}$"))throw new ArgumentException("מזהה גרסת מאגר לא תקין");
        if(cache.TryGetValue(id,out var cached))return cached;
        var metadata=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id","official-version:"+id}},ct:ct);
        if(metadata.Count!=1)throw new ArgumentException("גרסת המאגר אינה קיימת או שהעלאתה לא הושלמה");
        var version=JsonDefaults.Read<OfficialVersion>(metadata[0]!["document"]!.ToJsonString());var records=new Dictionary<string,OfficialRecord>();
        string? after=null;var loadedChunks=0;
        while(loadedChunks<version.Chunks)
        {
            var chunks=await database.ReadAsync("carwise_rules",new Dictionary<string,string>{{"id_prefix",$"official-chunk:{id}:"}},limit:8,ct:ct,afterId:after);
            if(chunks.Count==0)throw new InvalidDataException("גרסת המאגר אינה שלמה");
            foreach(var chunk in chunks)
            {
                foreach(var row in JsonDefaults.Read<List<Dictionary<string,string>>>(chunk!["document"]!.ToJsonString()))
                {var parsed=ReferenceData.ParseOfficial(row);records.Add(parsed.Key,parsed);}
                loadedChunks++;after=chunk!["id"]!.GetValue<string>();
            }
        }
        if(loadedChunks!=version.Chunks||records.Count!=version.RowCount||records.Values.Any(r=>r.TaxYear!=version.TaxYear))throw new InvalidDataException("גרסת המאגר פגומה");
        if(cache.Count>=3)cache.Clear();cache.TryAdd(id,records);return records;
    }
    public async Task<IReadOnlyDictionary<string,OfficialRecord>> LoadForCarsAsync(string id,IEnumerable<Car> cars,CancellationToken ct)
    {
        if(id!="baseline")return await LoadAsync(id,ct);
        var list=cars.ToList();
        var keys=list.Select(c=>OfficialRecord.KeyFor(baseline.TaxYear,c.Year,"1",c.MakerCode,c.ModelCode)).Distinct().ToArray();
        if(keys.All(baseline.Official.ContainsKey))return keys.ToDictionary(k=>k,k=>baseline.Official[k]);
        return await new Repository(database).LoadOfficialForCarsAsync(list,baseline.TaxYear,ct);
    }
    public async Task<(ManagedConfiguration Configuration,OfficialPreview Preview)> PreviewAsync(string id,ManagedConfiguration current,CancellationToken ct,Dictionary<string,OfficialCarReplacement>? replacements=null)
    {
        var rows=await LoadAsync(id,ct);var year=rows.Values.First().TaxYear;var missing=new List<MissingOfficial>();var matches=new List<object>();
        if(replacements is not null&&(replacements.Count>current.Cars.Count||replacements.Keys.Any(key=>!current.Cars.Any(c=>c.Id==key))))throw new ArgumentException("מיפוי רכב לא תקין");
        var cars=current.Cars.Select(car=>{
            var replacement=replacements?.GetValueOrDefault(car.Id);
            var key=replacement is null?OfficialRecord.KeyFor(year,car.Year,"1",car.MakerCode,car.ModelCode):OfficialRecord.KeyFor(year,replacement.RegistrationYear,"1",replacement.MakerCode,replacement.ModelCode);
            if(!rows.TryGetValue(key,out var official)){missing.Add(new(car.Id,car.Model,key));return car;}
            var drive=baseline.Rules["valueOfUse"]!["driveField"]![official.Drive]!.GetValue<string>();
            matches.Add(new{car.Id,car.Model,newModel=replacement is null?car.Model:official.Document.GetValueOrDefault("כינוי מסחרי",car.Model),oldValue=car.Value,newValue=official.Value,oldDrive=car.Drive,newDrive=drive});
            return car with{Value=official.Value,Drive=drive,OfficialValue=official.Value,OfficialKey=key.Split('/'),
                Year=official.RegistrationYear,MakerCode=official.MakerCode,ModelCode=official.ModelCode,
                Model=replacement is null?car.Model:official.Document.GetValueOrDefault("כינוי מסחרי",car.Model),
                Brand=replacement is null?car.Brand:official.Document.GetValueOrDefault("תוצר",car.Brand),
                Description=replacement is null?car.Description:official.Document.GetValueOrDefault("תאור דגם",car.Description),
                Price=replacement is null?car.Price:decimal.Parse(official.Document["מחיר מחירון"],CultureInfo.InvariantCulture)};
        }).ToList();
        var rules=current.Rules.DeepClone();rules["valueOfUse"]=baseline.Rules["valueOfUse"]!.DeepClone();
        if(id!="baseline"){rules["valueOfUse"]!["taxYear"]=year;rules["valueOfUse"]!["source"]=rules["valueOfUse"]!["source"]!.GetValue<string>().Replace(baseline.TaxYear.ToString(),year.ToString(),StringComparison.Ordinal);}
        return(current with{Rules=rules,Cars=cars,Employees=current.Employees.Select(e=>e.CurrentCar is null?e:e with{CurrentModel=cars.Single(c=>c.Id==e.CurrentCar).Model}).ToList(),OfficialVersionId=id},new(id,year,rows.Count,matches,missing));
    }
}
