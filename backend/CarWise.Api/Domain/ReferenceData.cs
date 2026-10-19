using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;
namespace CarWise.Api.Domain;

public sealed class ReferenceData
{
    public required JsonNode Rules { get; init; }
    public required List<Car> Cars { get; init; }
    public required List<Employee> Employees { get; init; }
    public required List<Dictionary<string, JsonElement>> OccupationDocuments { get; init; }
    public required Dictionary<string, OfficialRecord> Official { get; init; }
    private int? totalOfficialRows;
    public int OfficialRowCount {get=>totalOfficialRows??Official.Count;init=>totalOfficialRows=value;}
    public int TaxYear => Rules["valueOfUse"]?["taxYear"]?.GetValue<int>() ?? 2026;
    public static ReferenceData FromSnapshot(DatabaseSnapshot snapshot,int? officialRowCount=null)
    {
        var data = new ReferenceData { Rules=snapshot.Rules, Cars=snapshot.Cars, Employees=snapshot.Employees,
            OccupationDocuments=snapshot.Occupations, Official=snapshot.Official.ToDictionary(x=>x.Key),OfficialRowCount=officialRowCount??snapshot.Official.Count };
        data.Verify(); return data;
    }
    public DatabaseSnapshot Snapshot() => new(Rules, Cars, Employees, OccupationDocuments, Official.Values.ToList());
    public static ReferenceData FromFiles(string path)
    {
        T Read<T>(string name) => JsonDefaults.Read<T>(File.ReadAllText(Path.Combine(path, name+".json")));
        using var parser = new TextFieldParser(Path.Combine(path,"tax-authority-2026.csv"));
        parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes=true;
        var headers=parser.ReadFields() ?? throw new InvalidDataException("Missing CSV header");
        var rows = new List<OfficialRecord>();
        while (!parser.EndOfData)
        {
            var fields=parser.ReadFields()!;
            if (fields.Length!=headers.Length) throw new InvalidDataException("Malformed official CSV row");
            var d=headers.Zip(fields).ToDictionary(x=>x.First.Trim(), x=>x.Second.Trim());
            rows.Add(ParseOfficial(d));
        }
        return FromSnapshot(new(Read<JsonNode>("rules"),Read<List<Car>>("cars"),Read<List<Employee>>("employees"),
            Read<List<Dictionary<string,JsonElement>>>("occupations"),rows));
    }
    public static OfficialRecord ParseOfficial(Dictionary<string,string> d) => new(
        int.Parse(d["שנת מס"],CultureInfo.InvariantCulture),int.Parse(d["שנת רישום"],CultureInfo.InvariantCulture),
        d["קוד סוג רכב"],d["קוד תוצר"],d["קוד דגם"],decimal.Parse(d["שווי שימוש"],CultureInfo.InvariantCulture),
        d["1-חשמלי.2-היברידי.3-פלג-אין"],d);
    public OfficialRecord Lookup(Car car)
    {
        var key=OfficialRecord.KeyFor(TaxYear,car.Year,"1",car.MakerCode,car.ModelCode);
        return Official.TryGetValue(key,out var value) ? value : throw new ArgumentException("לא נמצאה התאמה מלאה לרכב במאגר הרשמי. נדרשת בדיקת נציג.");
    }
    public Car Enrich(Car c) => c with { OfficialValue=Lookup(c).Value,
        OfficialKey=[TaxYear.ToString(CultureInfo.InvariantCulture),c.Year.ToString(CultureInfo.InvariantCulture),"1",c.MakerCode,c.ModelCode] };
    public Occupation OccupationFor(string name)
    {
        var d=OccupationDocuments.SingleOrDefault(o=>o["occupation"].GetString()==name)
            ?? throw new ArgumentException("יש לבחור עיסוק תקין");
        return new(name,d["group"].GetString()!,d["mandatory"].GetBoolean(),d["waiver"].GetBoolean(),d["maintenance"].GetDecimal());
    }
    private void Verify()
    {
        if (Official.Count==0 || Cars.Count==0) throw new InvalidDataException("Missing reference data");
        foreach(var car in Cars)
        {
            var row=Lookup(car);
            if(row.Value!=car.Value || Rules["valueOfUse"]!["driveField"]![row.Drive]!.GetValue<string>()!=car.Drive)
                throw new InvalidDataException($"Catalogue mismatch for {car.Id}");
        }
    }
}
