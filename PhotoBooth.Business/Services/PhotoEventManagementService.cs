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
    public sealed class PhotoEventManagementService : IPhotoEventManagementService
    {
        readonly IEventService events;
        readonly IPhotoEventConfigurationRepository configurations;
        readonly ISettingsService settings;
        readonly IBeautySettingsService beauty;
        readonly IFrameService frames;

        public PhotoEventManagementService(IEventService eventService, IPhotoEventConfigurationRepository repository,
            ISettingsService settingsService, IBeautySettingsService beautySettings, IFrameService frameService)
        {
            events = eventService;
            configurations = repository;
            settings = settingsService;
            beauty = beautySettings;
            frames = frameService;
        }

        public event EventHandler EventsChanged;

        public Task<IReadOnlyList<PhotoEvent>> GetAllAsync(CancellationToken token) => events.GetAllAsync(token);

        public async Task<PhotoEvent> CreateAsync(string name, CancellationToken token)
        {
            name = NormalizeName(name);
            var draft = await events.CreateDraftAsync(null, token).ConfigureAwait(false);
            draft.Name = name;
            var created = await events.CreateAsync(draft, token).ConfigureAwait(false);
            EventsChanged?.Invoke(this, EventArgs.Empty);
            return created;
        }

        public async Task<PhotoEventConfiguration> GetConfigurationAsync(Guid eventId, CancellationToken token)
        {
            var stored = await configurations.GetAsync(eventId, token).ConfigureAwait(false);
            if (stored != null) return stored;
            var workflow = await settings.GetAsync(token).ConfigureAwait(false) ?? new Settings();
            var beautySettings = await beauty.GetAsync(token).ConfigureAwait(false) ?? new BeautySettings();
            var availableFrames = await frames.GetAllAsync(token).ConfigureAwait(false);
            return new PhotoEventConfiguration
            {
                EventId = eventId,
                PhotoCount = workflow.PhotoCount,
                CountdownSeconds = workflow.CountdownSeconds,
                GifFrameDurationMilliseconds = workflow.GifFrameDurationMilliseconds,
                WaitingTimeoutSeconds = workflow.WaitingTimeoutSeconds,
                CustomerLayoutMode = workflow.CustomerLayoutMode,
                ImageRotationDegrees = workflow.ImageRotationDegrees,
                Beauty = beautySettings.Clone(),
                FrameIds = availableFrames.Where(x => x.IsPinned).Take(10).Select(x => x.Id).ToList(),
                RowVersion = 0
            };
        }

        public async Task<PhotoEventConfiguration> SaveAsync(string eventName, PhotoEventConfiguration configuration, CancellationToken token)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            configuration = Normalize(configuration);
            var saved = await configurations.SaveAsync(NormalizeName(eventName), configuration, token).ConfigureAwait(false);
            EventsChanged?.Invoke(this, EventArgs.Empty);
            return saved;
        }

        public async Task ActivateAsync(Guid eventId, CancellationToken token)
        {
            var configuration = await configurations.GetAsync(eventId, token).ConfigureAwait(false);
            if (configuration == null) throw new InvalidOperationException("Hãy lưu cấu hình event trước khi sử dụng.");
            await configurations.ActivateAsync(eventId, token).ConfigureAwait(false);
            // The atomic repository activation already persisted BeautySettings. This
            // same-value save publishes the runtime change event used by live preview.
            await beauty.SaveAsync(configuration.Beauty, token).ConfigureAwait(false);
            EventsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task DeleteAsync(Guid eventId, CancellationToken token)
        {
            await configurations.DeleteAsync(eventId, token).ConfigureAwait(false);
            EventsChanged?.Invoke(this, EventArgs.Empty);
        }

        static PhotoEventConfiguration Normalize(PhotoEventConfiguration value)
        {
            var ids = (value.FrameIds ?? new Guid[0]).Where(x => x != Guid.Empty).Distinct().ToList();
            if (ids.Count == 0) throw new InvalidOperationException("Event phải có ít nhất một frame.");
            if (ids.Count > 10) throw new InvalidOperationException("Mỗi event chỉ được chọn tối đa 10 frame.");
            value.PhotoCount = Math.Max(1, Math.Min(8, value.PhotoCount));
            value.CountdownSeconds = Math.Max(1, Math.Min(10, value.CountdownSeconds));
            value.GifFrameDurationMilliseconds = Math.Max(400, Math.Min(1000, value.GifFrameDurationMilliseconds));
            if (!new[] { 30, 60, 120, 300, 600, 900 }.Contains(value.WaitingTimeoutSeconds)) value.WaitingTimeoutSeconds = 30;
            if (!new[] { 0, 90, 180, -90 }.Contains(value.ImageRotationDegrees)) value.ImageRotationDegrees = 0;
            value.Beauty = value.Beauty?.Clone() ?? new BeautySettings();
            value.Beauty.SmoothSkin = Clamp(value.Beauty.SmoothSkin);
            value.Beauty.BrightenSkin = Clamp(value.Beauty.BrightenSkin);
            value.Beauty.SkinTone = Clamp(value.Beauty.SkinTone);
            value.Beauty.Sharpen = Clamp(value.Beauty.Sharpen);
            value.Beauty.EyeSize = Clamp(value.Beauty.EyeSize);
            value.Beauty.SlimFace = Clamp(value.Beauty.SlimFace);
            value.FrameIds = ids;
            return value;
        }

        static int Clamp(int value) => Math.Max(0, Math.Min(100, value));
        static string NormalizeName(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) throw new InvalidOperationException("Tên event không được để trống.");
            if (value.Length > 80) throw new InvalidOperationException("Tên event không được vượt quá 80 ký tự.");
            return value;
        }
    }
}
