using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Administration;
namespace CarWise.Api.AI;

public interface IOcrSandboxRuntime
{
    Task<string> RunAsync(string operation,LocalPayslipSettings settings,byte[]? document,long revision,CancellationToken ct);
}
public interface IOcrSandboxCommands
{Task<string> RunAsync(IReadOnlyList<string> arguments,byte[]? input,int maxBytes,CancellationToken ct);}
public sealed class DockerOcrRuntime(IConfiguration configuration,IOcrSandboxCommands? commands=null) : IOcrSandboxRuntime
{
    public async Task<string> RunAsync(string operation,LocalPayslipSettings settings,byte[]? document,long revision,CancellationToken ct)
    {
        if(operation is not ("check" or "extract" or "probe"))throw new OcrSandboxException("sandbox_invalid_operation");
        var image=configuration["CarWise:PayslipOcr:Image"]??"carwise-ocr:1";
        if(!Regex.IsMatch(image,@"^[A-Za-z0-9][A-Za-z0-9_./:@-]{0,255}$"))throw new OcrSandboxException("sandbox_image_missing");
        // Only local Docker transports are allowed. Do not send documents to a remote TCP daemon.
        var endpoint=(await CommandAsync(["context","inspect","--format","{{.Endpoints.docker.Host}}"],null,65536,ct)).Trim();
        var explicitHost=Environment.GetEnvironmentVariable("DOCKER_HOST");if(!string.IsNullOrWhiteSpace(explicitHost))endpoint=explicitHost;
        if(!endpoint.StartsWith("unix://",StringComparison.Ordinal)&&!endpoint.StartsWith("npipe://",StringComparison.Ordinal))throw new OcrSandboxException("sandbox_remote_engine");
        if((await CommandAsync(["info","--format","{{.OSType}}"],null,65536,ct)).Trim()!="linux")throw new OcrSandboxException("sandbox_linux_required");
        string imageId;
        try{imageId=(await CommandAsync(["image","inspect",image,"--format","{{.Id}}"],null,65536,ct)).Trim();}
        catch(OcrSandboxException){throw new OcrSandboxException("sandbox_image_missing");}
        if(!Regex.IsMatch(imageId,@"^sha256:[a-f0-9]{64}$"))throw new OcrSandboxException("sandbox_image_missing");
        string[] command=operation switch{"check"=>["--check",settings.Languages],"probe"=>["--probe"],_=>["--extract"]};
        var name="carwise-ocr-"+Guid.NewGuid().ToString("N");
        try
        {
            var id=(await CommandAsync(OcrSandboxPolicy.CreateArguments(name,imageId,command),null,65536,ct)).Trim();
            if(!Regex.IsMatch(id,@"^[a-f0-9]{64}$"))throw new OcrSandboxException("sandbox_unavailable");
            var inspect=JsonNode.Parse(await CommandAsync(["inspect",id],null,128*1024,ct))?.AsArray().Single()??throw new OcrSandboxException("sandbox_policy_rejected");
            OcrSandboxPolicy.Validate(inspect,imageId,command); // No document bytes have been sent yet.
            byte[]? input=null;
            try
            {
                if(operation=="extract")
                {
                    if(document is null||document.Length>8*1024*1024)throw new OcrSandboxException("sandbox_invalid_input");
                    var header=JsonSerializer.SerializeToUtf8Bytes(new OcrWorkerRequest(1,document.Length,revision,settings),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    input=new byte[header.Length+1+document.Length];header.CopyTo(input,0);input[header.Length]=10;document.CopyTo(input,header.Length+1);
                }
                return await CommandAsync(["start","--attach","--interactive",id],input,128*1024,ct);
            }
            finally{if(input is not null)CryptographicOperations.ZeroMemory(input);}
        }
        finally
        {
            // Cancellation of the browser request must not cancel cleanup of the container.
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try{await CommandAsync(["rm","--force",name],null,65536,cleanup.Token);}catch(Exception error)when(error is OcrSandboxException or OperationCanceledException){}
        }
    }
    private Task<string> CommandAsync(IReadOnlyList<string> arguments,byte[]? input,int maxBytes,CancellationToken ct)=>
        commands?.RunAsync(arguments,input,maxBytes,ct)??ExecuteAsync(arguments,input,maxBytes,ct);
    private async Task<string> ExecuteAsync(IReadOnlyList<string> arguments,byte[]? input,int maxBytes,CancellationToken ct)
    {
        var executable=configuration["CarWise:PayslipOcr:DockerPath"]??"docker";
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        start.Environment.Clear();
        // These configure the trusted local Docker client, not the worker. No application
        // credentials are passed here, and no host environment is forwarded into the container.
        foreach(var key in new[]{"PATH","HOME","USERPROFILE","SystemRoot","WINDIR","APPDATA","LOCALAPPDATA","TEMP","TMP","DOCKER_HOST","DOCKER_CONTEXT","DOCKER_CONFIG","DOCKER_TLS_VERIFY","DOCKER_CERT_PATH"})
            if(Environment.GetEnvironmentVariable(key) is {} value)start.Environment[key]=value;
        using var process=new Process{StartInfo=start};
        try{if(!process.Start())throw new OcrSandboxException("sandbox_unavailable");}catch(Win32Exception){throw new OcrSandboxException("sandbox_unavailable");}
        void Kill(){try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(Win32Exception){}}
        using var registration=ct.Register(Kill);using var output=new MemoryStream();
        async Task Copy(Stream stream,MemoryStream? target)
        {
            var buffer=new byte[65536];var total=0;
            try{int read;while((read=await stream.ReadAsync(buffer,ct))>0){total+=read;if(total>maxBytes){Kill();throw new OcrSandboxException("sandbox_output_limit");}if(target is not null)await target.WriteAsync(buffer.AsMemory(0,read),ct);}}
            finally{CryptographicOperations.ZeroMemory(buffer);}
        }
        async Task Write(){try{if(input is not null)await process.StandardInput.BaseStream.WriteAsync(input,ct);}catch(IOException){}finally{process.StandardInput.Close();}}
        try
        {
            await Task.WhenAll(Write(),Copy(process.StandardOutput.BaseStream,output),Copy(process.StandardError.BaseStream,null),process.WaitForExitAsync(ct));
            ct.ThrowIfCancellationRequested();if(process.ExitCode!=0)throw new OcrSandboxException("sandbox_unavailable");
            return Encoding.UTF8.GetString(output.GetBuffer(),0,checked((int)output.Length));
        }
        finally{Kill();if(output.TryGetBuffer(out var buffer))CryptographicOperations.ZeroMemory(buffer.AsSpan());}
    }
}
