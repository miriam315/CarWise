using System.Net;
using System.Text.Json.Nodes;
using CarWise.Api.Administration;
using Xunit;

namespace CarWise.Tests;

public sealed class CommonsCarPhotoTests
{
    private static JsonNode Page(string license="CC BY-SA 4.0",string media="https://upload.wikimedia.org/wikipedia/commons/a/ab/car.jpg")=>JsonNode.Parse("""
    {"pageid":123,"title":"File:Test vehicle.jpg","imageinfo":[{"mime":"image/jpeg","thumburl":"MEDIA","descriptionurl":"https://commons.wikimedia.org/wiki/File:Test_vehicle.jpg","extmetadata":{"Artist":{"value":"<a href='https://example.test'>Photographer</a>"},"LicenseShortName":{"value":"LICENSE"},"LicenseUrl":{"value":"https://creativecommons.org/licenses/by-sa/4.0/"}}}]}
    """.Replace("MEDIA",media,StringComparison.Ordinal).Replace("LICENSE",license,StringComparison.Ordinal))!;
    [Fact] public void ImportedCreditKeepsAuthorLicenseSourceAndModificationNotice()
    {
        var item=CommonsCarPhotos.Read(Page());Assert.NotNull(item);
        Assert.Contains("Photographer",item.Credit);Assert.Contains("CC BY-SA 4.0",item.Credit);
        Assert.Contains("https://creativecommons.org/",item.Credit);Assert.Contains("https://commons.wikimedia.org/",item.Credit);
        Assert.Contains("הותאם לתצוגה",item.Credit);Assert.DoesNotContain("<a",item.Credit);
    }
    [Theory][InlineData("All rights reserved")][InlineData("CC BY-NC 4.0")][InlineData("CC BY-ND 4.0")]
    public void RestrictedLicensesAreExcluded(string license)=>Assert.Null(CommonsCarPhotos.Read(Page(license)));
    [Theory]
    [InlineData("http://upload.wikimedia.org/wikipedia/commons/a.jpg")]
    [InlineData("https://upload.wikimedia.org.evil.test/wikipedia/commons/a.jpg")]
    [InlineData("https://127.0.0.1/wikipedia/commons/a.jpg")]
    [InlineData("https://user:password@upload.wikimedia.org/wikipedia/commons/a.jpg")]
    [InlineData("https://upload.wikimedia.org:8443/wikipedia/commons/a.jpg")]
    [InlineData("https://upload.wikimedia.org/private/a.jpg")]
    public void ImageDownloadsCannotTargetUntrustedHostsPathsOrPorts(string url)
    {Assert.False(CommonsCarPhotos.AllowedMediaUrl(url));Assert.Null(CommonsCarPhotos.Read(Page(media:url)));}
    [Fact] public async Task DownloadResolvesNumericPageAndChecksMetadataBeforeFetchingBytes()
    {
        var calls=0;
        using var http=new HttpClient(new FakeHttp(request=>{
            calls++;
            if(calls==1){Assert.Equal("commons.wikimedia.org",request.RequestUri!.Host);Assert.Contains("pageids=123",request.RequestUri.Query);return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["query"]=new JsonObject{["pages"]=new JsonObject{["123"]=Page()}}}.ToJsonString())};}
            Assert.Equal("upload.wikimedia.org",request.RequestUri!.Host);return new(HttpStatusCode.OK){Content=new ByteArrayContent([255,216,255,217])};
        }));
        var result=await new CommonsCarPhotos(http).DownloadAsync(123,default);Assert.Equal("image/jpeg",result.Mime);Assert.Equal(2,calls);
    }
    [Fact] public async Task ProviderTransportFailuresAreExplainedWithoutLeakingTransportDetails()
    {
        using var http=new HttpClient(new FakeHttp(_=>throw new HttpRequestException("SENSITIVE INTERNAL DETAIL")));
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>new CommonsCarPhotos(http).SearchAsync("Kia EV3",default));
        Assert.Contains("Wikimedia",error.Message);Assert.DoesNotContain("SENSITIVE",error.Message);
    }
    [Fact] public void EmptyFileMetadataDoesNotBreakSearchResults()=>Assert.Null(CommonsCarPhotos.Read(JsonNode.Parse("""{"imageinfo":[]}""")!));
    [Theory][InlineData("http","2.5")][InlineData("http","4.0")][InlineData("https","2.5")]
    public void SupportedLicenseMetadataMayUseHttpAndVersionTwoPointFive(string scheme,string version)
    {
        var page=Page("CC BY-SA "+version);page["imageinfo"]![0]!["extmetadata"]!["LicenseUrl"]!["value"]=$"{scheme}://creativecommons.org/licenses/by-sa/{version}/";
        var item=CommonsCarPhotos.Read(page);Assert.NotNull(item);
        Assert.Contains($"https://creativecommons.org/licenses/by-sa/{version}/",item.Credit);Assert.DoesNotContain("http://creativecommons.org",item.Credit);
    }
    [Theory][InlineData("https://creativecommons.org/licenses/by/4.0/")][InlineData("https://creativecommons.org.evil.test/licenses/by-sa/4.0/")][InlineData("https://creativecommons.org/licenses/by-sa/3.0/")]
    public void LicenseNameAndCanonicalLicenseLinkMustAgree(string url)
    {
        var page=Page();page["imageinfo"]![0]!["extmetadata"]!["LicenseUrl"]!["value"]=url;
        Assert.Null(CommonsCarPhotos.Read(page));
    }
    [Fact] public void ThumbnailMimeDeterminesTheActualDownloadedFormat()
    {
        var page=Page();page["imageinfo"]![0]!["mime"]="image/tiff";page["imageinfo"]![0]!["thumbmime"]="image/jpeg";
        Assert.Equal("image/jpeg",CommonsCarPhotos.Read(page)!.Mime);
    }
    [Fact] public async Task EmptySearchIsDifferentFromFilteredResultsAndAnApiError()
    {
        async Task<List<CommonsCarPhoto>> Search(JsonObject body)
        {
            using var http=new HttpClient(new FakeHttp(request=>{
                Assert.Contains("gsrlimit=24",request.RequestUri!.Query);
                return new(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())};}));
            return await new CommonsCarPhotos(http).SearchAsync("Toyota Corolla",default);
        }
        Assert.Empty(await Search(new(){["batchcomplete"]=""}));
        var invalidPage=Page("CC BY-NC 4.0");
        var filtered=await Assert.ThrowsAsync<ArgumentException>(()=>Search(new(){["query"]=new JsonObject{["pages"]=new JsonObject{["123"]=invalidPage}}}));
        Assert.Contains("נמצאו 1",filtered.Message);
        var provider=await Assert.ThrowsAsync<ArgumentException>(()=>Search(new(){["error"]=new JsonObject{["code"]="internal_api_error",["info"]="INTERNAL DETAIL"}}));
        Assert.Contains("שגיאת חיפוש",provider.Message);Assert.DoesNotContain("INTERNAL DETAIL",provider.Message);
    }
    [Fact] public async Task MalformedSuccessResponseIsNotReportedAsNoPhotos()
    {
        using var http=new HttpClient(new FakeHttp(_=>new(HttpStatusCode.OK){Content=new StringContent("not JSON")}));
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>new CommonsCarPhotos(http).SearchAsync("Kia EV3",default));
        Assert.Contains("תגובה שאינה תקינה",error.Message);
    }

}
