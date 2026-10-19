using Microsoft.AspNetCore.Http.Features;
namespace CarWise.Api.Infrastructure;
public static class UploadReader
{
    public static async Task<byte[]> ReadAsync(HttpContext context,int maxBytes,CancellationToken ct)
    {
        var limit=context.Features.Get<IHttpMaxRequestBodySizeFeature>();if(limit is {IsReadOnly:false})limit.MaxRequestBodySize=maxBytes;
        if(context.Request.ContentLength>maxBytes)throw new ArgumentException("הקובץ גדול מהמותר");
        using var stream=new MemoryStream();var buffer=new byte[64*1024];int read;
        try
        {
            while((read=await context.Request.Body.ReadAsync(buffer,ct))>0)
            {if(stream.Length+read>maxBytes)throw new ArgumentException("הקובץ גדול מהמותר");await stream.WriteAsync(buffer.AsMemory(0,read),ct);}
            if(stream.Length==0)throw new ArgumentException("לא נבחר קובץ");return stream.ToArray();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            if(stream.TryGetBuffer(out var storage))System.Security.Cryptography.CryptographicOperations.ZeroMemory(storage.AsSpan());
        }
    }
}
