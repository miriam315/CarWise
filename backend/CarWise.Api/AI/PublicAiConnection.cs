using System.Net;
using System.Net.Sockets;
namespace CarWise.Api.AI;

/// <summary>Custom destinations use a direct, DNS-pinned public connection; no redirects or proxies.</summary>
public static class PublicAiConnection
{
    public static bool ValidHost(string host)=>Uri.CheckHostName(host)==UriHostNameType.Dns&&host.Contains('.')&&
        !host.EndsWith('.')&&!new[]{"localhost","local","internal","test","invalid","example","home","lan"}
            .Any(suffix=>host.Equals(suffix,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+suffix,StringComparison.OrdinalIgnoreCase));
    public static bool IsPublic(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        var b=address.GetAddressBytes();
        if(address.AddressFamily==AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127)&&b[0]<224&&
                !(b[0]==100&&b[1] is >=64 and <=127)&&!(b[0]==169&&b[1]==254)&&
                !(b[0]==172&&b[1] is >=16 and <=31)&&!(b[0]==192&&(b[1]==168||b[1]==0||b[1]==2))&&
                !(b[0]==198&&(b[1] is 18 or 19||b[1]==51&&b[2]==100))&&!(b[0]==203&&b[1]==0&&b[2]==113);
        // Only global unicast IPv6; exclude documentation, transition and special-use 2001::/23.
        return address.AddressFamily==AddressFamily.InterNetworkV6&&address.ScopeId==0&&(b[0]&0xe0)==0x20&&
            !(b[0]==0x20&&b[1]==1&&(b[2]<2||b[2]==0x0d&&b[3]==0xb8))&&
            !(b[0]==0x20&&b[1]==2); // 6to4 can embed a private IPv4 destination.
    }
    public static void ValidateAddresses(IReadOnlyCollection<IPAddress> addresses)
    {
        if(addresses.Count==0||addresses.Any(a=>!IsPublic(a)))
            throw new HttpRequestException("Custom AI destination must resolve only to public addresses.");
    }
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,CancellationToken ct)
    {
        if(context.DnsEndPoint.Port!=443||!ValidHost(context.DnsEndPoint.Host))
            throw new HttpRequestException("Invalid custom AI destination.");
        var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);
        ValidateAddresses(addresses);
        foreach(var address in addresses)
        {
            var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address,443),ct);
                return new NetworkStream(socket,ownsSocket:true);
            }
            catch(SocketException){socket.Dispose();}
            catch{socket.Dispose();throw;}
        }
        throw new HttpRequestException("Unable to connect to custom AI provider.");
    }
}
