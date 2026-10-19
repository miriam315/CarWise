using System.Buffers.Binary;
namespace CarWise.Api.AI;

public static class LocalImageLimits
{
    public static void Validate(AiDocument document)
    {
        var bytes=document.Bytes.AsSpan();int width=0,height=0;
        if(document.MediaType=="image/png"&&bytes.Length>=24)
        {
            var w=BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);var h=BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
            if(w<=int.MaxValue&&h<=int.MaxValue){width=(int)w;height=(int)h;}
        }
        else if(document.MediaType=="image/jpeg")
        {
            for(var offset=2;offset+4<=bytes.Length;)
            {
                if(bytes[offset++]!=255)break;while(offset<bytes.Length&&bytes[offset]==255)offset++;
                if(offset>=bytes.Length)break;var marker=bytes[offset++];
                if(marker is 0xD9 or 0xDA)break;
                if(marker is 0x01 or >=0xD0 and <=0xD8)continue;
                if(offset+2>bytes.Length)break;var length=BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if(length<2||offset+length>bytes.Length)break;
                if(marker is >=0xC0 and <=0xCF and not (0xC4 or 0xC8 or 0xCC)&&length>=7)
                {height=BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset+3)..]);width=BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset+5)..]);break;}
                offset+=length;
            }
        }
        if(width<1||height<1)throw new ArgumentException("לא ניתן לקרוא את ממדי התמונה. העלו PNG או JPEG תקינים.");
        if(width>8000||height>8000||(long)width*height>20_000_000)
            throw new ArgumentException("התמונה גדולה מדי לעיבוד מקומי: עד 20 מיליון פיקסלים ועד 8,000 פיקסלים בכל צלע. הקטינו אותה או העלו PDF.");
    }
}
