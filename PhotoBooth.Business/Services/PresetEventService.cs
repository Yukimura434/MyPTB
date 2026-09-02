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
    public sealed class PresetEventService : IPresetEventService
    {
        readonly IPresetEventRepository events;
        readonly IPresetRepository presets;

        public PresetEventService(IPresetEventRepository events, IPresetRepository presets)
        {
            this.events = events;
            this.presets = presets;
        }

        public Task<IReadOnlyList<PresetEvent>> GetAllAsync(CancellationToken token) => events.GetAllAsync(token);

        public async Task<PresetEvent> CreateAsync(string name, CancellationToken token)
        {
            name = Normalize(name);
            var all = await events.GetAllAsync(token);
            if (all.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Sự kiện đã tồn tại.");
            var value = new PresetEvent { Id = Guid.NewGuid(), Name = name, CreatedAtUtc = DateTime.UtcNow };
            await events.SaveAsync(value, token);
            return value;
        }

        public async Task RenameAsync(Guid id, string name, CancellationToken token)
        {
            name = Normalize(name);
            var all = await events.GetAllAsync(token);
            var value = all.FirstOrDefault(x => x.Id == id);
            if (value == null) throw new InvalidOperationException("Không tìm thấy sự kiện.");
            if (all.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Sự kiện đã tồn tại.");
            value.Name = name;
            await events.SaveAsync(value, token);
        }

        public Task DeleteAsync(Guid id, CancellationToken token) => events.DeleteAsync(id, token);

        public async Task AssignPresetAsync(Guid presetId, Guid? eventId, CancellationToken token)
        {
            if (eventId.HasValue && !(await events.GetAllAsync(token)).Any(x => x.Id == eventId.Value))
                throw new InvalidOperationException("Không tìm thấy sự kiện.");
            var preset = await presets.GetAsync(presetId, token);
            if (preset == null) throw new InvalidOperationException("Không tìm thấy preset.");
            preset.EventId = eventId;
            preset.ModifiedAtUtc = DateTime.UtcNow;
            await presets.SaveAsync(preset, token);
        }

        static string Normalize(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) throw new InvalidOperationException("Tên sự kiện không được để trống.");
            if (name.Length > 80) throw new InvalidOperationException("Tên sự kiện không được vượt quá 80 ký tự.");
            return name;
        }
    }
}
