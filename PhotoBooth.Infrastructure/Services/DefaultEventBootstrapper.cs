using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Business.Services;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;
using PhotoBooth.Shared;

namespace PhotoBooth.Infrastructure.Services
{
    /// <summary>
    /// Installs the product-owned starter content without overwriting an event that
    /// an operator has already configured.
    /// </summary>
    internal sealed class DefaultEventBootstrapper
    {
        const string ResourcePrefix = "PhotoBooth.Shared.Assets.DefaultFrames.";
        const string MutexName = "Local\\MiuPhotoBooth.DefaultEventBootstrap.v1";

        static readonly DefaultFrameDefinition[] DefaultFrames =
        {
            new DefaultFrameDefinition("default-frame-01.png", new Guid("7d5f8d70-0b39-4aae-ae3a-a3042b458101"), "Khung mặc định 01"),
            new DefaultFrameDefinition("default-frame-02.png", new Guid("7d5f8d70-0b39-4aae-ae3a-a3042b458102"), "Khung mặc định 02"),
            new DefaultFrameDefinition("default-frame-03.png", new Guid("7d5f8d70-0b39-4aae-ae3a-a3042b458103"), "Khung mặc định 03"),
            new DefaultFrameDefinition("default-frame-04.png", new Guid("7d5f8d70-0b39-4aae-ae3a-a3042b458104"), "Khung mặc định 04"),
            new DefaultFrameDefinition("default-frame-05.png", new Guid("7d5f8d70-0b39-4aae-ae3a-a3042b458105"), "Khung mặc định 05")
        };

        readonly BusinessWorkflowService workflow;
        readonly IEventService events;
        readonly IPhotoEventManagementService eventManagement;
        readonly IPhotoEventConfigurationRepository configurations;
        readonly IFrameRepository frameRepository;
        readonly IFrameAnalyzer frameAnalyzer;
        readonly IStorageManager storage;

        public DefaultEventBootstrapper(
            BusinessWorkflowService businessWorkflow,
            IEventService eventService,
            IPhotoEventManagementService photoEventManagement,
            IPhotoEventConfigurationRepository configurationRepository,
            IFrameRepository frames,
            IFrameAnalyzer analyzer,
            IStorageManager storageManager)
        {
            workflow = businessWorkflow;
            events = eventService;
            eventManagement = photoEventManagement;
            configurations = configurationRepository;
            frameRepository = frames;
            frameAnalyzer = analyzer;
            storage = storageManager;
        }

        public async Task EnsureReadyAsync(CancellationToken token)
        {
            using (var mutex = new Mutex(false, MutexName))
            {
                var ownsMutex = false;
                try
                {
                    try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex) throw new TimeoutException("Không thể chuẩn bị dữ liệu mặc định vì ứng dụng khác đang cập nhật.");

                    var frameIds = await EnsureDefaultFramesAsync(token).ConfigureAwait(false);
                    var baseSession = await workflow.GetBaseAsync(token).ConfigureAwait(false);
                    var allEvents = await events.GetAllAsync(token).ConfigureAwait(false);
                    var defaultEvent = allEvents.FirstOrDefault(value => value.Id == baseSession.Id);
                    if (defaultEvent == null) throw new InvalidOperationException("Không thể chuẩn bị sự kiện mặc định.");

                    var configuration = await configurations.GetAsync(defaultEvent.Id, token).ConfigureAwait(false);
                    if (configuration == null)
                    {
                        configuration = new PhotoEventConfiguration
                        {
                            EventId = defaultEvent.Id,
                            PhotoCount = 8,
                            CountdownSeconds = 5,
                            GifFrameDurationMilliseconds = 800,
                            WaitingTimeoutSeconds = 60,
                            CustomerLayoutMode = CustomerLayoutMode.Landscape,
                            ImageRotationDegrees = 0,
                            Beauty = new BeautySettings(),
                            PrintPolicyMode = EventPrintPolicyMode.Manual,
                            IncludedPrintCopies = 1,
                            MaximumPrintCopies = 10,
                            ColorPresetStrengthPercent = 50,
                            FrameIds = frameIds,
                            PresetIds = new Guid[0]
                        };
                        await eventManagement.SaveAsync(defaultEvent.Name, defaultEvent.OutputDirectory, configuration, token).ConfigureAwait(false);
                    }

                    // Keep an operator-selected event intact. A clean installation has
                    // no selection, so the ready starter event becomes operational.
                    if (!allEvents.Any(value => value.IsSelectedForThisDevice))
                        await eventManagement.ActivateAsync(defaultEvent.Id, token).ConfigureAwait(false);
                }
                finally
                {
                    if (ownsMutex) mutex.ReleaseMutex();
                }
            }
        }

        async Task<IReadOnlyList<Guid>> EnsureDefaultFramesAsync(CancellationToken token)
        {
            var existing = await frameRepository.GetAllAsync(token).ConfigureAwait(false);
            var freshLibrary = existing.Count == 0;
            var result = new List<Guid>(DefaultFrames.Length);
            foreach (var definition in DefaultFrames)
            {
                token.ThrowIfCancellationRequested();
                var current = existing.FirstOrDefault(value => value.Id == definition.Id);
                if (current != null && !string.IsNullOrWhiteSpace(current.SourcePath) && File.Exists(current.SourcePath))
                {
                    result.Add(current.Id);
                    continue;
                }

                var target = Path.Combine(storage.GetPath("Frames"), definition.FileName);
                if (!File.Exists(target)) CopyEmbeddedFrame(definition.FileName, target);
                var analyzed = frameAnalyzer.Analyze(target, new FrameAnalysisOptions
                {
                    AlphaThreshold = 8,
                    MinimumArea = 10000,
                    MinimumWidth = 40,
                    MinimumHeight = 40,
                    IgnoreBorderConnectedRegions = true,
                    MaximumSlots = 8
                });
                analyzed.Id = definition.Id;
                analyzed.Name = current?.Name ?? definition.DisplayName;
                analyzed.SourcePath = target;
                analyzed.ThumbnailPath = target;
                analyzed.CreatedAtUtc = current?.CreatedAtUtc ?? DateTime.UtcNow;
                analyzed.IsPinned = current?.IsPinned ?? freshLibrary;
                await frameRepository.SaveAsync(analyzed, token).ConfigureAwait(false);
                result.Add(analyzed.Id);
            }
            return result;
        }

        static void CopyEmbeddedFrame(string fileName, string target)
        {
            var assembly = typeof(DesktopApplicationDefaults).Assembly;
            using (var source = assembly.GetManifestResourceStream(ResourcePrefix + fileName))
            {
                if (source == null) throw new InvalidOperationException("Thiếu khung ảnh mặc định: " + fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var staging = target + ".new";
                try
                {
                    using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                    if (!File.Exists(target)) File.Move(staging, target);
                }
                finally
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
            }
        }

        sealed class DefaultFrameDefinition
        {
            public DefaultFrameDefinition(string fileName, Guid id, string displayName) { FileName = fileName; Id = id; DisplayName = displayName; }
            public string FileName { get; }
            public Guid Id { get; }
            public string DisplayName { get; }
        }
    }
}
