namespace CarWise.Api.Infrastructure;
public static class RequestProtection
{
    public static void Validate(HttpContext context,bool development)
    {
        var healthProbe=context.Request.Path=="/api/health"&&(HttpMethods.IsGet(context.Request.Method)||HttpMethods.IsHead(context.Request.Method));
        if(!development&&!context.Request.IsHttps&&!healthProbe)throw new ArgumentException("נדרש חיבור HTTPS בסביבת ייצור");
        if(HttpMethods.IsGet(context.Request.Method)||HttpMethods.IsHead(context.Request.Method)||HttpMethods.IsOptions(context.Request.Method))return;
        var origin=context.Request.Headers.Origin.ToString();if(string.IsNullOrEmpty(origin))return;
        if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.GetLeftPart(UriPartial.Authority)!=$"{context.Request.Scheme}://{context.Request.Host}")
            throw new UnauthorizedAccessException("Forbidden");
    }
}
