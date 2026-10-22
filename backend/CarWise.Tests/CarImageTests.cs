using System.Text;
using CarWise.Api.Administration;
using CarWise.Api.Domain;
using Xunit;
namespace CarWise.Tests;
public sealed class CarImageTests
{
    private static byte[] Jpeg()=>Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAADAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDPooor6I8Q/9k=");
    [Fact] public async Task UploadedImageSurvivesANewStoreAndUsesAContentHash()
    {
        var db=new MemoryConfigurationDatabase();string id;
        using(var first=new CarImages(db))id=await first.SaveAsync(Jpeg(),"synthetic-admin",default);
        Assert.True(CarImages.ValidId(id));
        using var restarted=new CarImages(db);var bytes=await restarted.ReadAsync(id,default);
        Assert.Equal(CarImages.Normalize(Jpeg()),bytes);
        Assert.Equal(id,await restarted.SaveAsync(Jpeg(),"synthetic-admin",default));
        Assert.Null(await restarted.ReadAsync("../server-settings",default));
        Assert.Null(await restarted.ReadAsync(new string('a',64),default));
    }
    [Fact] public void AppMetadataAndLocationAreRemovedBeforeStorage()
    {
        var original=Jpeg();var text=Encoding.ASCII.GetBytes("Exif-secret-GPS-location");
        var marked=new byte[original.Length+text.Length+4];original.AsSpan(0,2).CopyTo(marked);
        marked[2]=255;marked[3]=225;marked[4]=0;marked[5]=(byte)(text.Length+2);text.CopyTo(marked,6);original.AsSpan(2).CopyTo(marked.AsSpan(text.Length+6));
        var normalized=CarImages.Normalize(marked);
        Assert.DoesNotContain("GPS",Encoding.ASCII.GetString(normalized));
        Assert.Equal(CarImages.Normalize(original),normalized);
    }
    [Theory][InlineData("<svg onload='alert(1)'/>")][InlineData("%PDF-1.7")][InlineData("not an image")]
    public void ActiveContentAndNonImagesAreRejected(string input)=>Assert.Throws<ArgumentException>(()=>CarImages.Normalize(Encoding.UTF8.GetBytes(input)));
    [Fact] public void OversizedAndTruncatedImagesAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>CarImages.Normalize(new byte[CarImages.MaxBytes+1]));
        Assert.Throws<ArgumentException>(()=>CarImages.Normalize(Jpeg()[..^2]));
    }
    [Fact] public void ImageDimensionsAreBoundedBeforeServing()
    {
        var bytes=Jpeg();var index=Enumerable.Range(0,bytes.Length-8).First(i=>bytes[i]==255&&bytes[i+1]==192);
        bytes[index+5]=255;bytes[index+6]=255;
        Assert.Throws<ArgumentException>(()=>CarImages.Normalize(bytes));
    }
    [Fact] public void GalleriesRejectExternalUrlsAndRepeatedSpinFrames()
    {
        var data=ReferenceData.FromFiles(Path.Combine(AppContext.BaseDirectory,"data"));
        var original=data.Cars[0];var config=new ManagedConfiguration(data.Rules,[..data.Cars],data.OccupationDocuments,data.Employees,new(false,new(),new()));
        config.Cars[0]=original with{Photos=[new("https://external.invalid/image.jpg","exterior","test")]};
        Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(data).Validate(config));
        config.Cars[0]=original with{Photos=Enumerable.Range(0,12).Select(_=>new CarPhoto(new string('a',64),"spin","test")).ToList()};
        Assert.Throws<ArgumentException>(()=>new ConfigurationValidator(data).Validate(config));
    }
}
