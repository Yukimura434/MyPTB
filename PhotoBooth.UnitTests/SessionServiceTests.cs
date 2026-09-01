using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Repositories;
using PhotoBooth.Business.Services;
using PhotoBooth.Shared;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class SessionServiceTests
    {
        [Fact]
        public async Task Session_can_be_started_persisted_and_completed()
        {
            var repository = new InMemorySessionRepository();
            var root = Path.Combine(Path.GetTempPath(), "PhotoBoothTests", Guid.NewGuid().ToString("N"));
            var service = new SessionService(repository, new ApplicationOptions { DataDirectory = root });
            var session = await service.StartAsync(null, CancellationToken.None);
            Assert.NotEqual(Guid.Empty, session.Id);
            Assert.EndsWith(DateTime.Now.ToString("yyyyMMdd") + "_1", session.OutputDirectory);
            Assert.Equal(1, session.SessionNumber);
            Assert.Equal(DateTime.Now.ToString("yyyyMMdd") + "_1", session.SessionName);
            var second = await service.StartAsync(null, CancellationToken.None);
            Assert.Equal(2, second.SessionNumber);
            await service.CompleteAsync(session, CancellationToken.None);
            var stored = await service.GetAsync(session.Id, CancellationToken.None);
            Assert.NotNull(stored.CompletedAtUtc);
        }

        [Fact]
        public async Task Default_event_is_stable_while_its_legacy_folder_remains_compatible()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));
            var service=new SessionService(new InMemorySessionRepository(),new ApplicationOptions{DataDirectory=root});
            var first=await service.GetBaseAsync(CancellationToken.None);
            var second=await service.GetBaseAsync(CancellationToken.None);
            Assert.Equal(first.Id,second.Id);
            Assert.Equal("Sự kiện mặc định",first.SessionName);
            Assert.Equal(Path.Combine(root,"Captures","Base_session"),first.OutputDirectory);
        }

        [Fact]
        public async Task Events_may_share_a_display_name_because_identity_is_their_id()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));
            IEventService events=new SessionService(new InMemorySessionRepository(),new ApplicationOptions{DataDirectory=root});
            var firstDraft=await events.CreateDraftAsync(null,CancellationToken.None);firstDraft.Name="Đám cưới";
            var first=await events.CreateAsync(firstDraft,CancellationToken.None);
            var secondDraft=await events.CreateDraftAsync(null,CancellationToken.None);secondDraft.Name="Đám cưới";
            var second=await events.CreateAsync(secondDraft,CancellationToken.None);
            Assert.NotEqual(first.Id,second.Id);
            Assert.Equal(first.Name,second.Name);
            Assert.Equal(2,(await events.GetAllAsync(CancellationToken.None)).Count);
        }

        [Fact]
        public async Task Event_output_directory_is_fixed_when_the_event_is_created()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));
            try
            {
                IEventService events=new SessionService(new InMemorySessionRepository(),new ApplicationOptions{DataDirectory=root});
                var selected=Path.Combine(root,"Selected");
                var draft=await events.CreateDraftAsync(null,CancellationToken.None);
                draft.Name="Wedding";
                draft.OutputDirectory=selected;
                var created=await events.CreateAsync(draft,CancellationToken.None);
                Assert.Equal(Path.GetFullPath(selected),created.OutputDirectory);
                Assert.True(Directory.Exists(selected));
                var stored=Assert.Single(await events.GetAllAsync(CancellationToken.None));
                Assert.Equal(created.Id,stored.Id);
                Assert.Equal(Path.GetFullPath(selected),stored.OutputDirectory);
            }
            finally
            {
                if(Directory.Exists(root))Directory.Delete(root,true);
            }
        }
    }
}
