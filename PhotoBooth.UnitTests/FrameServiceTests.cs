using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class FrameServiceTests
    {
        [Fact]
        public async Task SetSlotOrderAsync_reindexes_and_saves_slots_in_requested_order()
        {
            var first = Slot(0, 10);
            var second = Slot(1, 20);
            var third = Slot(2, 30);
            var frame = new Frame { Id = Guid.NewGuid(), Slots = new[] { first, second, third } };
            var repository = new FrameRepository(frame);
            var service = new FrameService(new UnusedAnalyzer(), repository, new UnusedStorage());

            await service.SetSlotOrderAsync(frame.Id, new[] { third.Id, first.Id, second.Id }, CancellationToken.None);

            Assert.Equal(1, repository.SaveCount);
            Assert.Collection(repository.Value.Slots,
                slot => { Assert.Same(third, slot); Assert.Equal(0, slot.Index); Assert.Equal(30, slot.X); },
                slot => { Assert.Same(first, slot); Assert.Equal(1, slot.Index); Assert.Equal(10, slot.X); },
                slot => { Assert.Same(second, slot); Assert.Equal(2, slot.Index); Assert.Equal(20, slot.X); });
        }

        [Fact]
        public async Task SetSlotOrderAsync_rejects_an_incomplete_order_without_saving()
        {
            var first = Slot(0, 10);
            var second = Slot(1, 20);
            var frame = new Frame { Id = Guid.NewGuid(), Slots = new[] { first, second } };
            var repository = new FrameRepository(frame);
            var service = new FrameService(new UnusedAnalyzer(), repository, new UnusedStorage());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetSlotOrderAsync(frame.Id, new[] { first.Id }, CancellationToken.None));

            Assert.Equal(0, repository.SaveCount);
            Assert.Equal(0, first.Index);
            Assert.Equal(1, second.Index);
        }

        static FrameSlot Slot(int index, int x) => new FrameSlot
        {
            Id = Guid.NewGuid(), Index = index, X = x, Y = 1, Width = 100, Height = 100
        };

        sealed class FrameRepository : IFrameRepository
        {
            public FrameRepository(Frame value) { Value = value; }
            public Frame Value { get; private set; }
            public int SaveCount { get; private set; }
            public Task<Frame> GetAsync(Guid id, CancellationToken token) => Task.FromResult(Value != null && Value.Id == id ? Value : null);
            public Task<IReadOnlyList<Frame>> GetAllAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<Frame>>(new[] { Value });
            public Task SaveAsync(Frame frame, CancellationToken token) { Value = frame; SaveCount++; return Task.CompletedTask; }
            public Task DeleteAsync(Guid id, CancellationToken token) => Task.CompletedTask;
        }

        sealed class UnusedAnalyzer : IFrameAnalyzer
        {
            public Frame Analyze(string pngPath, FrameAnalysisOptions options) => throw new NotSupportedException();
            public Frame Analyze(Stream pngStream, string sourceName, FrameAnalysisOptions options) => throw new NotSupportedException();
        }

        sealed class UnusedStorage : IFileStorageService
        {
            public Task<string> SaveAsync(string relativePath, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
