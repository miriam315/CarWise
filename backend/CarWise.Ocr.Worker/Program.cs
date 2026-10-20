using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using Microsoft.Extensions.Configuration;

var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
using var watchdog=new CancellationTokenSource(TimeSpan.FromSeconds(135));
byte[]? bytes=null;
try
{
    var engine=new LocalPayslipOcr(new ConfigurationBuilder().Build(),"/worker");
    if(args.Length==2&&args[0]=="--check")
    {
        if(args[1] is not ("heb+eng" or "heb" or "eng"))throw new ArgumentException();
        var status=await engine.StatusAsync(new(){Languages=args[1]},watchdog.Token);
        Console.Write(JsonSerializer.Serialize(new OcrWorkerResponse(1,status.Ready,Error:status.Ready?null:"tools_unavailable"),json));
    }
    else if(args.SequenceEqual(new[]{"--probe"}))
    {
        // Diagnostic mode has no document input. It is used by the isolated deployment test,
        // never by the public API, and returns flags rather than environment values or files.
        var status=await File.ReadAllTextAsync("/proc/self/status",watchdog.Token);
        string Read(string key)=>status.Split('\n').Single(line=>line.StartsWith(key+":",StringComparison.Ordinal)).Split(':',2)[1].Trim();
        bool writable=false;try{await File.WriteAllTextAsync("/worker/write-probe","synthetic",watchdog.Token);writable=true;}catch(UnauthorizedAccessException){}catch(IOException){}
        bool network=false;using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp))
        {using var deadline=new CancellationTokenSource(500);try{await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("198.51.100.1"),443),deadline.Token);network=true;}catch(SocketException){}catch(OperationCanceledException){}}
        var previousTemporaryFileVisible=File.Exists("/tmp/ephemeral-probe");
        await File.WriteAllTextAsync("/tmp/ephemeral-probe","synthetic",watchdog.Token);
        Console.Write(JsonSerializer.Serialize(new{uid=Read("Uid"),capabilities=Read("CapEff"),noNewPrivileges=Read("NoNewPrivs"),rootWritable=writable,
            networkConnected=network,previousTemporaryFileVisible,interfaces=Directory.GetDirectories("/sys/class/net").Select(Path.GetFileName),
            dockerSocketVisible=File.Exists("/var/run/docker.sock"),applicationVisible=Directory.Exists("/workspace/CarWise"),
            sensitiveEnvironmentPresent=Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key=>key.StartsWith("CARWISE_",StringComparison.Ordinal)||key.StartsWith("SUPABASE",StringComparison.Ordinal)||key.StartsWith("ConnectionStrings",StringComparison.Ordinal)),
            memoryMax=(await File.ReadAllTextAsync("/sys/fs/cgroup/memory.max",watchdog.Token)).Trim(),
            swapMax=(await File.ReadAllTextAsync("/sys/fs/cgroup/memory.swap.max",watchdog.Token)).Trim(),
            pidsMax=(await File.ReadAllTextAsync("/sys/fs/cgroup/pids.max",watchdog.Token)).Trim(),
            cpuMax=(await File.ReadAllTextAsync("/sys/fs/cgroup/cpu.max",watchdog.Token)).Trim()},json));
    }
    else if(args.SequenceEqual(new[]{"--extract"}))
    {
        var input=Console.OpenStandardInput();using var header=new MemoryStream();var single=new byte[1];
        while(true)
        {
            if(header.Length>=4096||await input.ReadAsync(single,watchdog.Token)!=1)throw new ArgumentException();
            if(single[0]==10)break;header.WriteByte(single[0]);
        }
        var request=JsonSerializer.Deserialize<OcrWorkerRequest>(header.ToArray(),json)??throw new ArgumentException();
        if(request.Version!=1||request.Length is <16 or >8*1024*1024||request.Settings is not {} settings||settings.Languages is not ("heb+eng" or "heb" or "eng")||
            settings.MaxPages is <1 or >6||settings.TimeoutSeconds is <10 or >120||settings.MinimumConfidence is <50 or >100)throw new ArgumentException();
        bytes=new byte[request.Length];await input.ReadExactlyAsync(bytes,watchdog.Token);
        var result=await engine.ExtractAsync(AiDocument.Read(bytes),settings,request.Revision,watchdog.Token);
        Console.Write(JsonSerializer.Serialize(new OcrWorkerResponse(1,true,result.Fields.ToDictionary(field=>field.Key,field=>field.Value.Value),result.Source),json));
    }
    else throw new ArgumentException();
}
catch(Exception error)
{
    // Never serialize exception text, input snippets, native stderr or stack traces.
    Console.Write(JsonSerializer.Serialize(new OcrWorkerResponse(1,false,Error:error is OperationCanceledException?"timeout":"bad_document"),json));
}
finally{if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
