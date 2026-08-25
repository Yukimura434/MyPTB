using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    public sealed class FrameEventService : IFrameEventService
    {
        readonly IFrameEventRepository events;
        readonly IFrameRepository frames;
        public FrameEventService(IFrameEventRepository events, IFrameRepository frames) { this.events = events; this.frames = frames; }
        public Task<IReadOnlyList<FrameEvent>> GetAllAsync(CancellationToken token) => events.GetAllAsync(token);
        public async Task<FrameEvent> CreateAsync(string name, CancellationToken token)
        {
            name = Normalize(name);
            var all = await events.GetAllAsync(token);
            if (all.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Sự kiện đã tồn tại.");
            var value = new FrameEvent { Id = Guid.NewGuid(), Name = name, CreatedAtUtc = DateTime.UtcNow };
            await events.SaveAsync(value, token); return value;
        }
        public async Task RenameAsync(Guid id, string name, CancellationToken token)
        {
            name = Normalize(name); var all = await events.GetAllAsync(token); var value = all.FirstOrDefault(x => x.Id == id);
            if (value == null) throw new InvalidOperationException("Không tìm thấy sự kiện.");
            if (all.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Sự kiện đã tồn tại.");
            value.Name = name; await events.SaveAsync(value, token);
        }
        public Task DeleteAsync(Guid id, CancellationToken token) => events.DeleteAsync(id, token);
        public async Task AssignFrameAsync(Guid frameId, Guid? eventId, CancellationToken token)
        {
            if (eventId.HasValue && !(await events.GetAllAsync(token)).Any(x => x.Id == eventId.Value)) throw new InvalidOperationException("Không tìm thấy sự kiện.");
            var frame = await frames.GetAsync(frameId, token); if (frame == null) throw new InvalidOperationException("Không tìm thấy frame.");
            frame.EventId = eventId; await frames.SaveAsync(frame, token);
        }
        static string Normalize(string name) { name = (name ?? string.Empty).Trim(); if (name.Length == 0) throw new InvalidOperationException("Tên sự kiện không được để trống."); if (name.Length > 80) throw new InvalidOperationException("Tên sự kiện không được vượt quá 80 ký tự."); return name; }
    }
}
