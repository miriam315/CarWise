using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
namespace CarWise.Api.AI;

// Geometry stays inside the isolated worker. Only validated numeric fields leave it.
public sealed record PayrollWord(string Text,double X,double Y,double Width,double Height,double Confidence=100,int Page=0)
{public double CenterX=>X+Width/2;public double CenterY=>Y+Height/2;}
public static class LocalPayrollLayout
{
    private sealed record Phrase(string Text,PayrollWord[] Words)
    {
        public double X=>Words.Min(w=>w.X);public double Right=>Words.Max(w=>w.X+w.Width);
        public double CenterX=>(X+Right)/2;public double Y=>Words.Average(w=>w.CenterY);
        public double Height=>Words.Max(w=>w.Height);public int Page=>Words[0].Page;
    }
    private static readonly IReadOnlyDictionary<string,string> Labels=new Dictionary<string,string>{
        ["salary"]=@"(?:שכר (?:בסיס|יסוד)|משכורת יסוד|base salary|basic salary)",
        ["commission"]=@"(?:עמלות|commissions?)",["bonus"]=@"(?:בונוס|bonus)",
        ["participation"]=@"(?:השתתפות (?:עובד )?ברכב|employee car participation)",
        ["pension"]=@"(?:פנסיה|פנסיית עובד|פנסיה עובד|employee pension|pension)",
        ["fund"]=@"(?:קרן השתלמות|קרן השתלמות עובד|employee study fund|study fund)",
        ["credits"]=@"(?:נקודות זיכוי|נק[.׳']? זיכוי|tax credit points)"};
    private const string Amount=@"(?<value>(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?)(?<percent>%)?";
    private static bool Matches(string text,string pattern)=>Regex.IsMatch(text,"^(?:"+pattern+")$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static string Clean(string text)=>LocalPayrollFields.Normalize(text).Trim(' ',':','|');
    private static IEnumerable<string> Variants(string text)
    {yield return Clean(text);yield return Clean(LocalPayrollFields.ReverseVisual(text));}
    public static IReadOnlyList<PayrollWord> ReadPdf(string xml)
    {
        // Poppler emits an XHTML DOCTYPE. Never resolve external entities or fetch its DTD.
        using var reader=XmlReader.Create(new StringReader(xml),new(){DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=4_000_000});
        var document=XDocument.Load(reader);var result=new List<PayrollWord>();var page=0;
        foreach(var element in document.Descendants().Where(e=>e.Name.LocalName=="page"))
        {
            foreach(var word in element.Descendants().Where(e=>e.Name.LocalName=="word"))
            {
                double N(string name)=>double.Parse(word.Attribute(name)!.Value,CultureInfo.InvariantCulture);
                var x=N("xMin");var y=N("yMin");result.Add(new(word.Value,x,y,N("xMax")-x,N("yMax")-y,100,page));
            }
            page++;
        }
        return result;
    }
    public static IReadOnlyList<PayrollWord> ReadTsv(string tsv,int page=0)
    {
        var result=new List<PayrollWord>();
        foreach(var row in tsv.Split('\n').Skip(1))
        {
            var cells=row.TrimEnd('\r').Split('\t',12);if(cells.Length!=12||cells[0]!="5"||string.IsNullOrWhiteSpace(cells[11]))continue;
            var values=new double[5];var valid=true;
            for(var i=0;i<5;i++)valid&=double.TryParse(cells[i==4?10:i+6],NumberStyles.Float,CultureInfo.InvariantCulture,out values[i]);
            if(valid)result.Add(new(cells[11],values[0],values[1],values[2],values[3],values[4],page));
        }
        return result;
    }
    public static IReadOnlyList<PayrollWord> WithHebrewLabels(IReadOnlyList<PayrollWord> bilingual,IReadOnlyList<PayrollWord> hebrew,int minimumConfidence)
    {
        // Hebrew-only Tesseract often damages Arabic numerals. It may repair a Latin
        // misreading of a known Hebrew LABEL, never a number, unit or existing Hebrew word.
        const string allowed=@"(?:עובד|מעסיק|פנסיה|השתלמות|שכר|בסיס|יסוד|זיכוי|נקודות|קרן|עמלות|בונוס|השתתפות|ברכב|אחוז|סכום|תעריף|כמות|קוד|תיאור|רכיב|קופה)";
        return bilingual.Select(word=>
        {
            if(!Regex.IsMatch(word.Text,@"[a-zA-Z]",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))||word.Text.Any(char.IsDigit)||word.Text.Any(c=>c>='א'&&c<='ת'))return word;
            var matches=hebrew.Where(h=>h.Page==word.Page&&h.Confidence>=minimumConfidence&&Matches(Clean(h.Text),allowed)&&
                Math.Abs(h.CenterX-word.CenterX)<Math.Min(h.Width,word.Width)*.2&&Math.Abs(h.CenterY-word.CenterY)<Math.Min(h.Height,word.Height)*.3&&
                Math.Min(h.Width,word.Width)/Math.Max(h.Width,word.Width)>.8).ToArray();
            return matches.Length==1?word with{Text=Clean(matches[0].Text),Confidence=matches[0].Confidence}:word;
        }).ToArray();
    }
    public static PayslipResult Extract(IEnumerable<PayrollWord> input,int minimumConfidence,long revision,string source,CancellationToken ct=default)
    {
        var words=input.Where(w=>double.IsFinite(w.X)&&double.IsFinite(w.Y)&&double.IsFinite(w.Width)&&double.IsFinite(w.Height)&&w.Width>0&&w.Height>0&&w.Text.Length<=160).ToArray();
        if(words.Length>30000)throw new ArgumentException("תוכן המסמך ארוך מדי לעיבוד מקומי.");
        var found=Labels.Keys.ToDictionary(key=>key,_=>new HashSet<decimal>());
        var physicalLines=new List<LocalTextLine>();
        foreach(var page in words.GroupBy(w=>w.Page))
        {
            ct.ThrowIfCancellationRequested();
            var rows=new List<List<PayrollWord>>();
            var punctuation=page.Where(w=>Matches(Clean(w.Text),@"[—–\-_|]+")).ToHashSet();
            foreach(var word in page.Where(w=>!punctuation.Contains(w)).OrderBy(w=>w.CenterY))
            {
                ct.ThrowIfCancellationRequested();
                var row=rows.LastOrDefault(r=>Math.Abs(r.Average(w=>w.CenterY)-word.CenterY)<=Math.Min(word.Height,r.Average(w=>w.Height))*.8);
                if(row is null)rows.Add([word]);else row.Add(word);
            }
            foreach(var word in punctuation)
            {
                var row=rows.OrderBy(r=>Math.Abs(r.Average(w=>w.CenterY)-word.CenterY)).FirstOrDefault();
                if(row is not null&&Math.Abs(row.Average(w=>w.CenterY)-word.CenterY)<row.Average(w=>w.Height))row.Add(word);
            }
            var phrases=new List<Phrase>();
            foreach(var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                // Tesseract block/line IDs can split a table row into "salary 1.00".
                // Flat parsing is allowed only after joining the entire physical row.
                foreach(var sequence in new[]{row.OrderBy(w=>w.X),row.OrderByDescending(w=>w.X)})
                    physicalLines.Add(new(string.Join(' ',sequence.Select(w=>w.Text)),row.Min(w=>w.Confidence)));
                var ordered=row.Where(w=>!punctuation.Contains(w)).OrderBy(w=>w.X).ToArray();
                for(var i=0;i<ordered.Length;i++)for(var length=1;length<=4&&i+length<=ordered.Length;length++)
                {
                    var span=ordered.Skip(i).Take(length).ToArray();
                    if(span.Zip(span.Skip(1)).Any(pair=>pair.Second.X-(pair.First.X+pair.First.Width)>Math.Max(pair.First.Height,pair.Second.Height)*1.6))break;
                    if(span.Any(w=>w.Confidence<minimumConfidence))continue;
                    foreach(var sequence in new[]{span,span.Reverse().ToArray()})foreach(var variant in Variants(string.Join(' ',sequence.Select(w=>w.Text))))
                        phrases.Add(new(variant,span));
                }
            }
            var descriptions=phrases.Where(p=>Matches(p.Text,@"(?:תיאור(?: רכיב)?|רכיב|קופה|description|component|fund)")).ToArray();
            var amountHeaders=phrases.Where(p=>Matches(p.Text,@"(?:סכום(?: חודשי| לתשלום)?|חודש נוכחי|amount|monthly amount)")).ToArray();
            var rateHeaders=phrases.Where(p=>Matches(p.Text,@"(?:אחוז עובד|אחוזי עובד|שיעור עובד|employee (?:rate|percent)|employee %|% employee)")).ToArray();
            var headers=phrases.Where(p=>Matches(p.Text,@"(?:תיאור(?: רכיב)?|רכיב|קופה|קוד|כמות|תעריף|בסיס|סכום(?: (?:עובד|מעסיק|חודשי|לתשלום))?|אחוז (?:עובד|מעסיק)|מצטבר(?: עובד)?|חודש נוכחי|נקודות זיכוי|שווי נקודה|אחוז משרה|שעות עבודה|ימי עבודה|amount|description|code|quantity|rate|base|employee (?:rate|amount)|employer (?:rate|amount)|year to date|tax credit points)")).ToArray();
            foreach(var (key,pattern) in Labels)
            foreach(var label in phrases.Where(p=>Matches(p.Text,pattern)).DistinctBy(p=>(p.X,p.Y,p.Right)))
            {
                ct.ThrowIfCancellationRequested();
                // Never use a short label cut out of a longer monthly/cumulative/gross label.
                var adjacent=page.Where(w=>Math.Abs(w.CenterY-label.Y)<label.Height*.8&&
                    (w.X>=label.Right&&w.X-label.Right<label.Height*1.6||w.X+w.Width<=label.X&&label.X-w.X-w.Width<label.Height*1.6)).ToArray();
                if(adjacent.Any(w=>Variants(w.Text).Any(t=>Matches(t,@"(?:מצטבר(?:ים)?|שנתי|מעסיק|ברוטו|נטו|cumulative|annual|employer|gross|net|YTD)"))))continue;
                if(key is "salary" or "commission" or "bonus" or "participation")
                    if(adjacent.Any(w=>!Regex.IsMatch(Clean(w.Text),@"^[\d,.%₪|:=()—–\-]+$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))))continue;
                if(key=="credits")
                {
                    // Metadata bands: the value is immediately below the credit-points heading,
                    // in its own column, before the next header band.
                    var next=page.Where(w=>w.CenterY>label.Y+label.Height*.6&&w.CenterY<label.Y+label.Height*4&&
                        w.CenterX>=label.X-label.Height&&w.CenterX<=label.Right+label.Height).OrderBy(w=>w.CenterY).ToArray();
                    if(next.Length>0)
                    {
                        var nearest=next.Where(w=>Math.Abs(w.CenterY-next[0].CenterY)<label.Height*.8).ToArray();
                        if(nearest.Length==1)Add(key,nearest[0],false);
                    }
                    continue;
                }
                var percent=key is "pension" or "fund";
                var applicable=(percent?rateHeaders:amountHeaders).Where(h=>h.Y<label.Y-label.Height*.5&&label.Y-h.Y<label.Height*26).OrderByDescending(h=>h.Y).ToArray();
                foreach(var referenceY in applicable.Select(h=>h.Y).Distinct())
                {
                    var headerBand=applicable.Where(h=>Math.Abs(h.Y-referenceY)<label.Height*2).ToArray();
                    var bandDescriptions=descriptions.Where(d=>Math.Abs(d.Y-referenceY)<label.Height*2&&Math.Abs(d.CenterX-label.CenterX)<label.Height*6).OrderBy(d=>Math.Abs(d.CenterX-label.CenterX)).ToArray();
                    if(bandDescriptions.Length==0)continue;
                    var description=bandDescriptions[0];
                    if(descriptions.Any(d=>d.Y>referenceY+label.Height&&d.Y<label.Y-label.Height*.5&&Math.Abs(d.CenterX-label.CenterX)<label.Height*6))break;
                    // Code/description columns delimit adjacent tables. Do not use the
                    // midpoint between descriptions: an amount column may lie past it in a
                    // wide earnings table with quantity and rate columns.
                    var barriers=headers.Where(h=>Math.Abs(h.Y-referenceY)<label.Height*2&&
                        (Matches(h.Text,@"(?:קוד|code)")||Matches(h.Text,@"(?:תיאור(?: רכיב)?|רכיב|קופה|description|component|fund)")&&Math.Abs(h.CenterX-description.CenterX)>label.Height*3)).ToArray();
                    var candidates=headerBand.Where(h=>!barriers.Any(b=>b.CenterX>Math.Min(h.CenterX,description.CenterX)+label.Height&&
                        b.CenterX<Math.Max(h.CenterX,description.CenterX)-label.Height)).DistinctBy(h=>Math.Round(h.CenterX/label.Height)).ToArray();
                    if(candidates.Length!=1)continue;
                    var header=candidates[0];
                    var boundaryHeaders=headers.Where(h=>Math.Abs(h.Y-header.Y)<label.Height*2&&Math.Abs(h.CenterX-header.CenterX)>label.Height*1.4).ToArray();
                    var left=boundaryHeaders.Where(h=>h.CenterX<header.CenterX).Select(h=>(h.CenterX+header.CenterX)/2).DefaultIfEmpty(header.X-label.Height*3).Max();
                    var right=boundaryHeaders.Where(h=>h.CenterX>header.CenterX).Select(h=>(h.CenterX+header.CenterX)/2).DefaultIfEmpty(header.Right+label.Height*3).Min();
                    var values=page.Where(w=>w.CenterX>left&&w.CenterX<right&&Math.Abs(w.CenterY-label.Y)<label.Height*.9).ToArray();
                    // Text or multiple amounts in one cell are ambiguous; never take first/last.
                    if(values.Length==1)Add(key,values[0],percent);
                    break;
                }
            }
        }
        return LocalPayrollFields.Merge([LocalPayrollFields.Result(found,revision,source),
            LocalPayrollFields.Extract(physicalLines,minimumConfidence,revision,source,ct)],revision,source);
        void Add(string key,PayrollWord word,bool percent)
        {
            if(word.Confidence<minimumConfidence)return;
            var text=Clean(word.Text);var match=Regex.Match(text,"^"+Amount+"$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(!match.Success||!percent&&match.Groups["percent"].Success)return;
            if(decimal.TryParse(match.Groups["value"].Value.Replace(",",""),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)&&value<=PayslipFields.Limits[key])found[key].Add(value);
        }
    }
}
