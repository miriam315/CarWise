using System.Text.Json.Nodes;
namespace CarWise.Api.AI;

public static class OcrSandboxPolicy
{
    public const long MemoryBytes=536870912;
    private static readonly HashSet<string> ProxyEnvironment=["HTTP_PROXY","HTTPS_PROXY","FTP_PROXY","ALL_PROXY","NO_PROXY","http_proxy","https_proxy","ftp_proxy","all_proxy","no_proxy"];
    public const string TemporaryFileSystem="rw,noexec,nosuid,nodev,size=67108864,mode=0700,uid=65532,gid=65532";
    public static IReadOnlyList<string> CreateArguments(string name,string imageId,IReadOnlyList<string> command)=>[
        "create","--pull","never","--rm","--interactive","--name",name,"--label","carwise.ocr.job=true",
        // Docker client configuration can automatically inject proxy URLs. Override them
        // explicitly so neither internal endpoints nor embedded credentials reach the worker.
        ..ProxyEnvironment.SelectMany(key=>new[]{"--env",key+"="}),
        "--network","none","--read-only","--user","65532:65532","--cap-drop","ALL",
        "--security-opt","no-new-privileges","--cgroupns","private","--ipc","none",
        "--memory",MemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),"--memory-swap",MemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--cpus","1","--pids-limit","64","--ulimit","nofile=128:128","--ulimit","core=0:0",
        "--tmpfs","/tmp:"+TemporaryFileSystem,"--log-driver","none","--stop-timeout","2","--no-healthcheck",
        imageId,..command];
    public static void Validate(JsonNode inspection,string imageId,IReadOnlyList<string> command)
    {
        try
        {
            var host=inspection["HostConfig"]!;var config=inspection["Config"]!;
            bool Empty(JsonNode? node)=>node is null||node.AsArray().Count==0;
            bool Has(JsonNode? node,string value)=>node?.AsArray().Any(item=>item?.GetValue<string>()==value)==true;
            var environment=new HashSet<string>{"PATH","HOME","TMPDIR","OMP_THREAD_LIMIT","DOTNET_ROOT","DOTNET_EnableDiagnostics","DOTNET_RUNNING_IN_CONTAINER","DOTNET_VERSION","ASPNET_VERSION","ASPNETCORE_HTTP_PORTS","APP_UID"};
            var mounts=inspection["Mounts"]?.AsArray()??[];
            var tmpfs=host["Tmpfs"]?.AsObject();
            var limits=host["Ulimits"]?.AsArray()??[];
            bool Limit(string key,long value)=>limits.Any(limit=>limit?["Name"]?.GetValue<string>()==key&&limit["Soft"]?.GetValue<long>()==value&&limit["Hard"]?.GetValue<long>()==value);
            if(inspection["Image"]?.GetValue<string>()!=imageId||config["User"]?.GetValue<string>()!="65532:65532"||config["WorkingDir"]?.GetValue<string>()!="/worker"||
                config["Entrypoint"]?.ToJsonString()!="[\"dotnet\",\"CarWise.Ocr.Worker.dll\"]"||
                !(config["Cmd"]?.AsArray().Select(item=>item!.GetValue<string>()).SequenceEqual(command)??false)||
                config["Tty"]?.GetValue<bool>()!=false||config["OpenStdin"]?.GetValue<bool>()!=true||
                host["Privileged"]?.GetValue<bool>()!=false||host["ReadonlyRootfs"]?.GetValue<bool>()!=true||host["AutoRemove"]?.GetValue<bool>()!=true||
                host["NetworkMode"]?.GetValue<string>()!="none"||host["PidMode"]?.GetValue<string>()!=""||host["IpcMode"]?.GetValue<string>()!="none"||host["CgroupnsMode"]?.GetValue<string>()!="private"||
                !Empty(host["CapAdd"])||!Has(host["CapDrop"],"ALL")||!Has(host["SecurityOpt"],"no-new-privileges")||
                host["SecurityOpt"]!.AsArray().Count!=1||!Empty(host["Binds"])||!Empty(host["Devices"])||!Empty(host["DeviceRequests"])||!Empty(host["DeviceCgroupRules"])||
                host["PortBindings"]?.AsObject().Count>0||host["PublishAllPorts"]?.GetValue<bool>()==true||
                host["Memory"]?.GetValue<long>()!=MemoryBytes||host["MemorySwap"]?.GetValue<long>()!=MemoryBytes||host["NanoCpus"]?.GetValue<long>()!=1_000_000_000||host["PidsLimit"]?.GetValue<long>()!=64||
                host["OomKillDisable"]?.GetValue<bool>()==true||host["LogConfig"]?["Type"]?.GetValue<string>()!="none"||
                !Limit("core",0)||!Limit("nofile",128)||tmpfs?.Count!=1||tmpfs["/tmp"]?.GetValue<string>()!=TemporaryFileSystem||
                mounts.Any(mount=>mount?["Type"]?.GetValue<string>()!="tmpfs"||mount["Destination"]?.GetValue<string>()!="/tmp")||
                config["Env"]?.AsArray().Any(item=>{var pair=item!.GetValue<string>().Split('=',2);return ProxyEnvironment.Contains(pair[0])?pair.Length!=2||pair[1]!="":!environment.Contains(pair[0]);})!=false)
                throw new InvalidDataException();
        }
        catch(Exception error)when(error is InvalidDataException or InvalidOperationException or FormatException or NullReferenceException)
        {throw new OcrSandboxException("sandbox_policy_rejected");}
    }
}

public sealed class OcrSandboxException(string code) : Exception("OCR sandbox could not be verified")
{public string Code {get;}=code;}
