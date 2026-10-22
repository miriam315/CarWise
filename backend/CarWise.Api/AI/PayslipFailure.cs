namespace CarWise.Api.AI;
public sealed record PayslipFailure(string Code,string Message,int? ProviderStatus=null,string? Parameter=null)
{
    public static PayslipFailure From(Exception error)=>error switch
    {
        AiProviderException provider=>new(provider.Code,provider.Message,provider.Status,provider.Parameter),
        AiUnavailableException ai=>new(ai.Code,ai.Message),
        OperationCanceledException=>new("timeout","חילוץ התלוש לא הסתיים בזמן ההמתנה שהוגדר למודל. אפשר לנסות מסמך קצר יותר או לעדכן את זמן ההמתנה בניהול ספקי AI."),
        System.Text.Json.JsonException=>new("invalid_output","מודל ה־AI לא החזיר נתונים במבנה הנדרש. לא שונו נתוני העובד. נסו שוב או בחרו מודל תומך מסמכים ופלט מובנה."),
        _=>new("provider_connection","לא ניתן להתחבר לספק ה־AI. בדקו גישה מהרשת של השרת לספק ונסו שוב. לא שונו נתוני העובד.")
    };
}
