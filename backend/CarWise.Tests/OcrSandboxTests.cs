using System.Text.Json.Nodes;
using CarWise.Api.AI;
using CarWise.Api.Administration;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace CarWise.Tests;

public sealed class OcrSandboxTests
{
    private const string Image="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static JsonNode Inspection()=>JsonNode.Parse("""
    {"Image":"IMAGE","Config":{"User":"65532:65532","WorkingDir":"/worker","Entrypoint":["dotnet","CarWise.Ocr.Worker.dll"],"Cmd":["--extract"],"Tty":false,"OpenStdin":true,"Env":["PATH=/opt/dotnet:/usr/bin","HOME=/tmp"]},
    "Mounts":[],"HostConfig":{"Privileged":false,"ReadonlyRootfs":true,"AutoRemove":true,"NetworkMode":"none","PidMode":"","IpcMode":"none","CgroupnsMode":"private","CapAdd":null,"CapDrop":["ALL"],"SecurityOpt":["no-new-privileges"],"Binds":null,"Devices":[],"DeviceRequests":null,"DeviceCgroupRules":null,"PortBindings":{},"PublishAllPorts":false,"Memory":536870912,"MemorySwap":536870912,"NanoCpus":1000000000,"PidsLimit":64,"OomKillDisable":false,"LogConfig":{"Type":"none"},"Ulimits":[{"Name":"core","Soft":0,"Hard":0},{"Name":"nofile","Soft":128,"Hard":128}],"Tmpfs":{"/tmp":"TMPFS"}}}
    """.Replace("IMAGE",Image).Replace("TMPFS",OcrSandboxPolicy.TemporaryFileSystem))!;

    [Fact] public void CompletePolicyIsAccepted()=>OcrSandboxPolicy.Validate(Inspection(),Image,["--extract"]);
    [Theory]
    [InlineData("NetworkMode","bridge")][InlineData("PidMode","host")][InlineData("IpcMode","host")]
    [InlineData("CgroupnsMode","host")]
    public void SharedNamespacesAreRejected(string key,string value)
    {var state=Inspection();state["HostConfig"]![key]=value;Assert.Throws<OcrSandboxException>(()=>OcrSandboxPolicy.Validate(state,Image,["--extract"]));}
    [Fact] public void MissingLimitsSecretsMountsAndWritableRootAreRejected()
    {
        Action<JsonNode>[] changes=[s=>s["Config"]!["Env"]!.AsArray().Add("SUPABASE_KEY=synthetic"),s=>s["Config"]!["User"]="0",
            s=>s["Config"]!["Env"]!.AsArray().Add("HTTPS_PROXY=http://synthetic-credential@internal-proxy"),
            s=>s["HostConfig"]!["ReadonlyRootfs"]=false,s=>s["HostConfig"]!["Privileged"]=true,
            s=>s["HostConfig"]!["Memory"]=0,s=>s["HostConfig"]!["MemorySwap"]=-1,s=>s["HostConfig"]!["PidsLimit"]=-1,
            s=>s["HostConfig"]!["NanoCpus"]=0,s=>s["HostConfig"]!["CapDrop"]=new JsonArray(),
            s=>s["HostConfig"]!["SecurityOpt"]=new JsonArray("seccomp=unconfined"),
            s=>s["HostConfig"]!["Binds"]=new JsonArray("/var/run/docker.sock:/var/run/docker.sock"),
            s=>s["Mounts"]!.AsArray().Add(JsonNode.Parse("{\"Type\":\"bind\",\"Destination\":\"/secrets\"}")),
            s=>s["HostConfig"]!["LogConfig"]!["Type"]="json-file",
            s=>s["HostConfig"]!["Tmpfs"]!["/tmp"]="rw,size=1g",s=>s["Config"]!["Cmd"]=new JsonArray("sh")];
        foreach(var change in changes){var state=Inspection();change(state);Assert.Throws<OcrSandboxException>(()=>OcrSandboxPolicy.Validate(state,Image,["--extract"]));}
    }
    private sealed class Commands(bool unsafePolicy=false,bool cancel=false):IOcrSandboxCommands
    {
        public List<string> Calls {get;}=[];
        public bool ReceivedDocument {get;private set;}
        public Task<string> RunAsync(IReadOnlyList<string> args,byte[]? input,int maxBytes,CancellationToken ct)
        {
            Calls.Add(args[0]);ReceivedDocument|=input is not null;
            if(args[0]=="start"&&cancel)throw new OperationCanceledException();
            var state=Inspection();if(unsafePolicy)state["HostConfig"]!["NetworkMode"]="bridge";
            return Task.FromResult(args[0] switch{"context"=>"unix:///var/run/docker.sock","info"=>"linux","image"=>Image,"create"=>new string('b',64),"inspect"=>new JsonArray(state).ToJsonString(),"start"=>"{}",_=>""});
        }
    }
    [Fact] public async Task UnsafeContainerNeverReceivesDocumentAndIsRemoved()
    {
        var commands=new Commands(unsafePolicy:true);var runtime=new DockerOcrRuntime(new ConfigurationBuilder().Build(),commands);
        await Assert.ThrowsAsync<OcrSandboxException>(()=>runtime.RunAsync("extract",new(),new byte[32],1,CancellationToken.None));
        Assert.False(commands.ReceivedDocument);Assert.DoesNotContain("start",commands.Calls);Assert.Equal("rm",commands.Calls.Last());
    }
    [Fact] public async Task CancellationStillRemovesContainer()
    {
        var commands=new Commands(cancel:true);var runtime=new DockerOcrRuntime(new ConfigurationBuilder().Build(),commands);
        await Assert.ThrowsAsync<OperationCanceledException>(()=>runtime.RunAsync("extract",new(),new byte[32],1,CancellationToken.None));
        Assert.True(commands.ReceivedDocument);Assert.Equal("rm",commands.Calls.Last());
        Assert.True(commands.Calls.IndexOf("inspect")<commands.Calls.IndexOf("start"));
    }
    [Fact] public void WorkerOutputCannotReturnUnknownFieldsPersonalExcerptsOrUnboundedValues()
    {
        var fields=PayslipFields.Limits.ToDictionary(item=>item.Key,_=>(decimal?)null);fields["salary"]=19000.25m;
        var good=new OcrWorkerResponse(1,true,fields,"local-ocr");var result=SandboxedPayslipOcr.Result(good,7);
        Assert.Equal(19000.25m,result.Fields["salary"].Value);Assert.All(result.Fields.Values,field=>Assert.Null(field.Evidence));
        fields["bank"]=123;Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(good,7));fields.Remove("bank");
        fields["salary"]=200001;Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(good,7));
        Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(good with{Source="personal excerpt"},7));
    }
    [Fact] public void CoordinateMetadataIsBoundedAndCannotSmuggleTextOrRegionsForUnknownValues()
    {
        var fields=PayslipFields.Limits.ToDictionary(p=>p.Key,_=>(decimal?)null);fields["salary"]=19000;
        var reply=new OcrWorkerResponse(1,true,fields,"local-pdf-text",Regions:new(){["salary"]=[new(0,.1,.2,.3,.04,"value")]});
        Assert.Single(SandboxedPayslipOcr.Result(reply,1).Regions!["salary"]);
        Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(reply with{Regions=new(){["salary"]=[new(0,double.NaN,0,.1,.1,"value")]}},1));
        Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(reply with{Regions=new(){["salary"]=[new(0,.9,0,.3,.1,"value")]}},1));
        Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(reply with{Regions=new(){["salary"]=[new(0,0,0,.1,.1,"PRIVATE")]}},1));
        Assert.Throws<OcrSandboxException>(()=>SandboxedPayslipOcr.Result(reply with{Regions=new(){["bonus"]=[new(0,0,0,.1,.1,"value")]}},1));
    }
    private sealed class ReplyRuntime(string reply):IOcrSandboxRuntime
    {
        public int Calls {get;private set;}
        public Task<string> RunAsync(string operation,LocalPayslipSettings settings,byte[]? document,long revision,CancellationToken ct)
        {Calls++;return Task.FromResult(reply);}
    }
    [Fact] public async Task UntrustedWorkerFreeTextIsRejectedWithoutFallbackOrReflection()
    {
        var runtime=new ReplyRuntime("""{"version":1,"success":true,"rawText":"PRIVATE-PERSON"}""");
        var service=new SandboxedPayslipOcr(runtime);
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>service.ExtractAsync(new("image/png",new byte[32]),new(),1,CancellationToken.None));
        Assert.DoesNotContain("PRIVATE",error.Message);Assert.Equal(1,runtime.Calls);
        Assert.False((await service.StatusAsync(new(),CancellationToken.None)).Ready);
    }
}

// Run explicitly with npm run ocr:verify after building the image. The normal suite
// does not require Docker; this test exercises the production runtime and policy.
public sealed class OcrSandboxIntegrationTests
{
    [OcrIntegrationFact] public async Task MalformedDocumentFailsWithoutLeakingTextAndNextWorkerIsHealthy()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var service=new SandboxedPayslipOcr(new DockerOcrRuntime(new ConfigurationBuilder().Build()));
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>service.ExtractAsync(AiDocument.Read("%PDF-1.7\nSYNTHETIC-PRIVATE-PERSON\ninvalid pdf"u8.ToArray()),new(),1,deadline.Token));
        Assert.DoesNotContain("PRIVATE",error.Message);Assert.DoesNotContain("pdfinfo",error.Message);
        Assert.True((await service.StatusAsync(new(),deadline.Token)).Ready);
    }
    [OcrIntegrationFact] public async Task ActualWorkerHasNoNetworkSecretsHostFilesOrWriteableRoot()
    {
        var runtime=new DockerOcrRuntime(new ConfigurationBuilder().Build());using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var reply=JsonNode.Parse(await runtime.RunAsync("probe",new(),null,0,deadline.Token))!;
        Assert.StartsWith("65532",reply["uid"]!.GetValue<string>());Assert.Equal("0000000000000000",reply["capabilities"]!.GetValue<string>());
        Assert.Equal("1",reply["noNewPrivileges"]!.GetValue<string>());
        foreach(var key in new[]{"rootWritable","networkConnected","dockerSocketVisible","applicationVisible","sensitiveEnvironmentPresent"})Assert.False(reply[key]!.GetValue<bool>(),key);
        Assert.Equal(new[]{"lo"},reply["interfaces"]!.AsArray().Select(value=>value!.GetValue<string>()));
        Assert.Equal("536870912",reply["memoryMax"]!.GetValue<string>());Assert.Equal("0",reply["swapMax"]!.GetValue<string>());
        Assert.Equal("64",reply["pidsMax"]!.GetValue<string>());Assert.Equal("100000 100000",reply["cpuMax"]!.GetValue<string>());
        Assert.False(reply["previousTemporaryFileVisible"]!.GetValue<bool>());
        var second=JsonNode.Parse(await runtime.RunAsync("probe",new(),null,0,deadline.Token))!;
        Assert.False(second["previousTemporaryFileVisible"]!.GetValue<bool>());
        var status=await new SandboxedPayslipOcr(runtime).StatusAsync(new(),deadline.Token);Assert.True(status.Ready,status.Message);
    }
}
public sealed class OcrIntegrationFactAttribute:FactAttribute
{public OcrIntegrationFactAttribute(){if(Environment.GetEnvironmentVariable("CARWISE_VERIFY_OCR")!="true")Skip="Run npm run ocr:verify with the local worker image.";}}
