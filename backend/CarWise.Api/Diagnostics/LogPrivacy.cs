using System.Globalization;
using System.Text.RegularExpressions;
namespace CarWise.Api.Diagnostics;

/// <summary>Logging contract: static message templates, no request bodies or user/document text.
/// Only diagnostic fields are rendered; exception messages, scopes and unknown objects are never read.</summary>
public static class LogPrivacy
{
    private static readonly HashSet<string> SafeFields=new(StringComparer.OrdinalIgnoreCase)
    {"Code","ErrorType","TransportCategory","Provider","Records","Loaded","Demo","Phase","Method","Route","StatusCode","ElapsedMs","Operation","Model","Attempt","Status","UsageId","EventName","HostingEnvironment","ApplicationName","EnvironmentName","ProtocolVersion","RequestMethod","RequestUri","Uri","HttpMethod","StatusCode","ElapsedMilliseconds","Address","ContentRootPath"};
    private static readonly HashSet<string> SafeStatic=new(StringComparer.Ordinal)
    {
        "Server process started. Reading saved connection settings...",
        "Supabase setup required. Open http://localhost:8000 on this computer; credentials are entered in the setup page.",
        "Could not refresh administration settings; retaining the last validated snapshot.",
        "Saved exchange rate unavailable.","Exchange rate update failed; retaining the last published rate.",
        "OpenRouter saved pricing unavailable.","OpenRouter pricing update unavailable; retaining the last saved catalogue.",
        "AI factual answer rejected; verified facts used. Code=invalid_factual_template",
        "AI narration rejected by financial safety validation; payroll template used. Code=invalid_narration_template",
        "Application started. Press Ctrl+C to shut down.","Application is shutting down...",
        "Supabase startup timed out. Check the connection/network in the local setup page.",
        "Supabase reference data could not be loaded. Open localhost to check or replace the saved connection.",
        "Saved administration settings could not be loaded. No default configuration was activated."
    };
    public static string Clip(string value,int max)=>value.Length>max?value[..max]+"…":value;
    public static string Clean(string value)
    {
        value=Clip(value,4000);
        value=Regex.Replace(value,@"https?://[^\s<>""']+",match=>Uri.TryCreate(match.Value,UriKind.Absolute,out var uri)?uri.Scheme+"://"+uri.Host+"/[redacted]":"[redacted-url]",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
        value=Regex.Replace(value,@"\bBearer\s+[^\s,;]+|\b(?:sk-|AIza|sb_secret_|sb_publishable_)[A-Za-z0-9_-]{8,}|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[redacted]",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
        value=Regex.Replace(value,@"\b(?:password|api[_-]?key|service[_-]?role|authorization|secret|token|connectionstring)\s*[:=]\s*[^\s,;]+", "[redacted-credential]",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
        value=Regex.Replace(value,@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}","[redacted-email]",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
        return Clip(value.Replace('\r',' ').Replace('\n',' '),1000);
    }
    public static string Message<TState>(TState state)
    {
        if(state is not IEnumerable<KeyValuePair<string,object?>> fields)return "Unstructured log message omitted (privacy).";
        var values=fields.Take(50).ToArray();
        var template=values.FirstOrDefault(pair=>pair.Key=="{OriginalFormat}").Value as string;
        if(string.IsNullOrEmpty(template))return "Unstructured log message omitted (privacy).";
        if(!template.Contains('{')&&!SafeStatic.Contains(template))return "Unstructured log message omitted (privacy).";
        template=Clip(template,2000);
        var rendered=Regex.Replace(template,@"\{(?<key>[A-Za-z][A-Za-z0-9_]*)(?:,[^}:]{1,10})?(?::(?<format>[^}]{1,20}))?\}",match=>{
            var key=match.Groups["key"].Value;
            if(!SafeFields.Contains(key)||key=="ContentRootPath")return "[redacted]";
            var value=values.FirstOrDefault(pair=>pair.Key==key).Value;
            if(value is null)return "—";
            if(value is string text)return Clean(Clip(text,200));
            if(value is bool boolean)return boolean?"true":"false";
            if(value is byte or short or int or long or float or double or decimal)
                return ((IFormattable)value).ToString(null,CultureInfo.InvariantCulture);
            return "[redacted]";
        },RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        return Clean(rendered);
    }
}
