using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Repositories;
using PhotoBooth.Business.Services;
using PhotoBooth.Shared;
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
        public async Task Base_session_is_stable_and_defaultable()
        {
            var root=Path.Combine(Path.GetTempPath(),"PhotoBoothTests",Guid.NewGuid().ToString("N"));
            var service=new SessionService(new InMemorySessionRepository(),new ApplicationOptions{DataDirectory=root});
            var first=await service.GetBaseAsync(CancellationToken.None);
            var second=await service.GetBaseAsync(CancellationToken.None);
            Assert.Equal(first.Id,second.Id);
            Assert.Equal("Base_session",first.SessionName);
            Assert.Equal(Path.Combine(root,"Captures","Base_session"),first.OutputDirectory);
        }
    }
}
