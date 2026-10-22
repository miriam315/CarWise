using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CarWise.Api.Administration;

public sealed record CommonsCarPhoto(int PageId,string Title,string Credit,string Source,string License,string MediaUrl,string Mime);
public sealed class CommonsCarPhotos(HttpClient http)
{
    private const string Api="https://commons.wikimedia.org/w/api.php?format=json&prop=imageinfo&iiprop=url%7Cmime%7Cextmetadata&iiurlwidth=1200&action=query&";
    private static string Text(string? html)=>WebUtility.HtmlDecode(Regex.Replace(html??"","<[^>]*>"," ",RegexOptions.None,TimeSpan.FromMilliseconds(200))).Trim();
    public static bool AllowedMediaUrl(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="upload.wikimedia.org"&&uri.Port==443&&uri.UserInfo==""&&uri.AbsolutePath.StartsWith("/wikipedia/commons/",StringComparison.Ordinal);
    private static string Meta(JsonNode? metadata,string field)=>Text(metadata?[field]?["value"]?.GetValue<string>());

    public static CommonsCarPhoto? Read(JsonNode page)
    {
        var info=(page["imageinfo"] as JsonArray)?.FirstOrDefault();if(info is null)return null;
        var metadata=info["extmetadata"];var license=Meta(metadata,"LicenseShortName");var licenseUrl=Meta(metadata,"LicenseUrl");
        var publicDomain=license is "CC0" or "Public domain";
        if(!publicDomain&&!(Regex.IsMatch(license,@"^CC BY(?:-SA)? [1-4]\.0$")&&Uri.TryCreate(licenseUrl,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="creativecommons.org"))return null;
        var mime=info["mime"]?.GetValue<string>()??"";
        var media=info["thumburl"]?.GetValue<string>()??info["url"]?.GetValue<string>()??"";
        var source=info["descriptionurl"]?.GetValue<string>()??"";
        if(mime is not ("image/jpeg" or "image/png" or "image/webp")||!AllowedMediaUrl(media)||!Uri.TryCreate(source,UriKind.Absolute,out var sourceUri)||sourceUri.Scheme!="https"||sourceUri.Host!="commons.wikimedia.org")return null;
        var title=page["title"]!.GetValue<string>();var author=Meta(metadata,"Artist");
        if(string.IsNullOrWhiteSpace(author)&&!publicDomain)return null;
        var credit=$"{author} · {title} · {license} {licenseUrl} · {source} · הותאם לתצוגה";
        if(credit.Length>1000)return null;
        return new(page["pageid"]!.GetValue<int>(),title,credit,source,license,media,mime);
    }
    private async Task<JsonObject> MetadataAsync(string parameters,CancellationToken ct)
    {
        using var response=await GetAsync(Api+parameters,ct);
        if(!response.IsSuccessStatusCode)throw new ArgumentException("Wikimedia אינו נגיש כרגע. אפשר להעלות תמונות מהמחשב או לנסות שוב מאוחר יותר.");
        var bytes=await BytesAsync(response,1_000_000,ct);
        var body=JsonNode.Parse(bytes);return body?["query"]?["pages"]?.AsObject()??new();
    }
    public async Task<List<CommonsCarPhoto>> SearchAsync(string query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query)||query.Length>100)throw new ArgumentException("יש להזין דגם לחיפוש, עד 100 תווים.");
        var pages=await MetadataAsync("generator=search&gsrnamespace=6&gsrlimit=12&gsrsearch="+Uri.EscapeDataString(query),ct);
        return pages.Select(p=>p.Value is null?null:Read(p.Value)).OfType<CommonsCarPhoto>().ToList();
    }
    public async Task<(byte[] Bytes,string Mime)> DownloadAsync(int pageId,CancellationToken ct)
    {
        if(pageId<1)throw new ArgumentException("מזהה תמונה לא תקין.");
        var pages=await MetadataAsync("pageids="+pageId,ct);
        var item=pages.FirstOrDefault().Value is {} page?Read(page):null;
        if(item is null)throw new ArgumentException("התמונה אינה זמינה ברישיון ובסוג קובץ נתמכים.");
        using var response=await GetAsync(item.MediaUrl,ct);
        if(!response.IsSuccessStatusCode)throw new ArgumentException("לא ניתן להוריד את התמונה מ־Wikimedia כרגע.");
        return(await BytesAsync(response,12*1024*1024,ct),item.Mime);
    }
    private async Task<HttpResponseMessage> GetAsync(string url,CancellationToken ct)
    {
        try{return await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);}
        catch(HttpRequestException){throw new ArgumentException("Wikimedia אינו נגיש כרגע מהרשת של השרת. אפשר להעלות תמונות מהמחשב.");}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new ArgumentException("Wikimedia לא השיב בזמן. אפשר לנסות שוב או להעלות תמונות מהמחשב.");}
    }
    private static async Task<byte[]> BytesAsync(HttpResponseMessage response,int maximum,CancellationToken ct)
    {
        if(response.Content.Headers.ContentLength>maximum)throw new ArgumentException("תגובת מקור התמונות גדולה מהמותר.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();
        var chunk=new byte[32768];int count;
        while((count=await stream.ReadAsync(chunk,ct))>0){if(buffer.Length+count>maximum)throw new ArgumentException("תגובת מקור התמונות גדולה מהמותר.");await buffer.WriteAsync(chunk.AsMemory(0,count),ct);}
        return buffer.ToArray();
    }
}
