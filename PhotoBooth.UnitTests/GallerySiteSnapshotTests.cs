using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed partial class PcApiConformanceTests
    {
        [Fact]
        public async Task Gallery_site_snapshot_survives_restart_and_cannot_be_replaced_by_later_configuration()
        {
            using(var f=new Fixture())
            {
                var capture=await f.Capture("event_original");
                capture.GallerySiteSnapshot="gs1.original.signature";capture.StateVersion++;
                await f.LocalSessions.SaveAsync(capture,CancellationToken.None);
                var saved=await f.LocalSessions.GetAsync(capture.Id,CancellationToken.None);
                Assert.Equal(capture.GallerySiteSnapshot,saved.GallerySiteSnapshot);
                saved.GallerySiteSnapshot="gs1.changed.signature";saved.StateVersion++;
                await f.LocalSessions.SaveAsync(saved,CancellationToken.None);
                Assert.Equal("gs1.original.signature",(await f.LocalSessions.GetAsync(capture.Id,CancellationToken.None)).GallerySiteSnapshot);
                var handler=new Handler((request,body)=>{
                    if(request.RequestUri.AbsolutePath.EndsWith("booth-sessions")){
                        Assert.Contains("\"gallerySiteSnapshot\":\"gs1.original.signature\"",body);
                        Assert.Contains("event_original",body);return "{\"session\":{\"id\":\"capture\",\"status\":\"active\"}}";
                    }
                    return "{\"gallery\":{\"url\":\"https://gallery.test/wedding/g/token\",\"expiresAt\":2000000000}}";
                });
                var client=new RemoteMediaDeliveryClient(f.Devices,f.Sessions,f.LocalSessions,null,f.Options,new HttpClient(handler));
                var result=await client.PrepareGalleryAsync(capture.Id,"deliverable",CancellationToken.None);
                Assert.True(result.Succeeded);Assert.Equal("https://gallery.test/wedding/g/token",result.GalleryUrl.AbsoluteUri);
            }
        }

        [Fact]
        public async Task Gallery_site_cache_is_event_scoped_and_missing_snapshot_is_not_silently_refreshed()
        {
            using(var f=new Fixture())
            {
                var handler=new Handler((request,body)=>"{\"snapshot\":\"gs1.cached.signature\",\"expiresAt\":2000000000}");
                var client=new RemoteMediaDeliveryClient(f.Devices,f.Sessions,f.LocalSessions,null,f.Options,new HttpClient(handler));
                Assert.Equal("gs1.cached.signature",await client.FreezeGallerySiteAsync("event_a",CancellationToken.None));
                handler.Response=(request,body)=>throw new HttpRequestException("offline");
                Assert.Equal("gs1.cached.signature",await client.FreezeGallerySiteAsync("event_a",CancellationToken.None));
                Assert.Equal(string.Empty,await client.FreezeGallerySiteAsync("event_b",CancellationToken.None));
                f.Sessions.Current.RemoteSessionToken=null;
                handler.Response=(request,body)=>throw new InvalidOperationException("Offline context must not call authenticated API without a token.");
                Assert.Equal("gs1.cached.signature",await client.FreezeGallerySiteAsync("event_a",CancellationToken.None));
                Assert.Equal(string.Empty,await client.FreezeGallerySiteAsync("event_b",CancellationToken.None));
                var capture=await f.Capture(null);capture.GallerySiteSnapshot=string.Empty;capture.StateVersion++;
                await f.LocalSessions.SaveAsync(capture,CancellationToken.None);
                var saved=await f.LocalSessions.GetAsync(capture.Id,CancellationToken.None);
                Assert.Equal(string.Empty,saved.GallerySiteSnapshot);
                saved.GallerySiteSnapshot="gs1.new.signature";saved.StateVersion++;
                await f.LocalSessions.SaveAsync(saved,CancellationToken.None);
                Assert.Equal(string.Empty,(await f.LocalSessions.GetAsync(capture.Id,CancellationToken.None)).GallerySiteSnapshot);
            }
        }
    }
}
