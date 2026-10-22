using System.Text;
using System.Text.RegularExpressions;
namespace CarWise.Api.Domain;

/// <summary>Resolve names against catalogue rows, never invent a vehicle ID or financial data.</summary>
public static class VehicleSearch
{
    private static readonly (string Alias,string Name)[] Names=[
        ("קורולה קרוס","corolla cross"),("יאריס קרוס","yaris cross hsd"),("בי ווי די","byd"),
        ("אטו","atto"),("דולפין","byd dolphin"),("קורולה","corolla"),("קונה","kona ev"),
        ("נירו","niro"),("פיקנטו","picanto"),("טוסון","tucson"),("קרניבל","carnival"),
        ("אוקטביה","octavia"),("מאזדה","mazda"),("ספורטאז׳","sportage"),("סנטה פה","santa fe"),
        ("סיאט","seat"),("טויוטה","toyota"),("טסלה","tesla"),("מודל","model"),("סיל","seal")];
    public static string Normalize(string text)
    {
        text=text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        foreach(var (alias,name) in Names)
            text=Regex.Replace(text,@"(?<![\p{L}\p{N}])(?<prefix>[ובמלכהש]{0,2})"+Regex.Escape(alias)+@"(?=$|[^\p{L}]|\d)",m=>m.Groups["prefix"].Value+" "+name,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        text=Regex.Replace(text,@"(?<=[a-z])(?=\d)|(?<=\d)(?=[a-z])|(?<=[\u0590-\u05ff])(?=[a-z])"," ");
        return Regex.Replace(Regex.Replace(text,@"[^\p{L}\p{N}]+"," "),@"\s+"," ").Trim();
    }
    public static List<Car> Find(string text,IEnumerable<Car> catalogue)
    {
        var cars=catalogue.ToList();var query=Normalize(text);if(query.Length==0)return cars;
        var exact=cars.SelectMany(car=>Regex.Matches(query,@"(?<![\p{L}\p{N}])"+Regex.Escape(Normalize(car.Model))+@"(?![\p{L}\p{N}])")
            .Select(match=>(Car:car,Start:match.Index,Length:match.Length))).ToList();
        // COROLLA inside COROLLA CROSS is not a second vehicle mention.
        var matches=exact.Where(x=>!exact.Any(other=>other.Length>x.Length&&other.Start<=x.Start&&other.Start+other.Length>=x.Start+x.Length)).Select(x=>x.Car).DistinctBy(car=>car.Id).ToList();
        if(matches.Count==0)matches=cars.Where(car=>query.Split(' ').All(token=>(Normalize(car.Brand+" "+car.Model+" "+car.Description+" "+car.Drive)+" "+car.Year).Split(' ').Contains(token))).ToList();
        var year=Regex.Match(query,@"\b20\d{2}\b");
        if(year.Success)matches=matches.Where(car=>car.Year.ToString()==year.Value).ToList();
        return matches;
    }
    public static (List<Car> Targets,List<Car> Sources) Resolve(string text,IEnumerable<Car> catalogue)
    {
        var query=Normalize(text);var parts=Regex.Split(query,@"(?:^|\s)(?:ל|אל)\s+",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(parts.Length>1)
        {
            var target=Find(parts[^1],catalogue);
            if(target.Count>0)return (target,Regex.IsMatch(parts[0],@"(?:^|\s)(?:מ|מה)\s+")?Find(parts[0],catalogue):[]);
        }
        return (Find(query,catalogue),[]);
    }
}
