using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class CustomerShellViewModel : ObservableObject
    {
        readonly IServiceProvider provider;
        readonly CustomerWorkflowStateMachine machine;
        readonly WaitingViewModel waiting;
        readonly BoothLoginViewModel login;
        readonly CustomerWorkflowContext context;
        readonly ISettingsService settings;
        readonly IEventService events;
        readonly IPhotoEventManagementService eventConfigurations;
        readonly IFrameService frames;
        readonly IPresetService presets;
        readonly IColorLutService colorLuts;
        readonly IStorageManager storage;
        readonly IPrinterService printers;
        readonly IPcSessionContext sessions;
        readonly DispatcherTimer idleTimer;
        object current;
        bool kiosk = true, portraitMode, cameraStarted, returningToWaiting;
        int waitingTimeoutSeconds = 30;
        DateTime lastInteractionUtc = DateTime.UtcNow;
        bool validatingForeground, activatingRun;

        public async System.Threading.Tasks.Task ValidateForegroundAsync()
        {
            var authorization = context.Authorization;
            if (validatingForeground || machine.State == CustomerWorkflowState.Complete || authorization?.UsesAccessCodeSettings != true) return;
            validatingForeground = true;
            try
            {
                var result = await provider.GetRequiredService<IBoothAccessService>().ValidateAsync(authorization, CancellationToken.None);
                if (result.Succeeded || !ReferenceEquals(authorization, context.Authorization)) return;
                await provider.GetRequiredService<CaptureViewModel>().ShutdownAsync(CancellationToken.None);
                cameraStarted = false;
                sessions.SignOut();
                context.Authorization = null;
                if (context.ManagerSession != null) { CloseCommand.Execute(null); return; }
                login.Reset();
                login.ShowError(result.Error);
                CurrentPage = login;
            }
            finally { validatingForeground = false; }
        }

        public CustomerShellViewModel(IServiceProvider provider, CustomerWorkflowStateMachine machine, ISettingsService settings,
            WaitingViewModel waiting, BoothLoginViewModel login, CustomerWorkflowContext context,
            IPrinterService printers, IPcSessionContext sessions, IEventService events,
            IPhotoEventManagementService eventConfigurations,
            IFrameService frames, IPresetService presets, IColorLutService colorLuts,
            IStorageManager storage)
        {
            this.provider = provider;
            this.machine = machine;
            this.settings = settings;
            this.waiting = waiting;
            this.login = login;
            this.context = context;
            this.printers = printers;
            this.sessions = sessions;
            this.events = events;
            this.eventConfigurations = eventConfigurations;
            this.frames = frames;
            this.presets = presets;
            this.colorLuts = colorLuts;
            this.storage = storage;
            CloseCommand = new RelayCommand(() => { foreach (Window window in Application.Current.Windows) if (window is MainWindow) window.Close(); });
            provider.GetRequiredService<CompleteViewModel>().Completed += async (sender, args) => await OnRunCompletedAsync();
            machine.StateChanged += (sender, args) => { RegisterInteraction(); Application.Current.Dispatcher.BeginInvoke(new Action(UpdatePage)); };
            waiting.EnterRequested += async (sender, args) => await EnterFromWaitingAsync();
            login.Authorized += async (sender, args) => await BeginAuthorizedRunAsync();
            login.BackRequested += async (sender, args) => await ShowWaitingAsync(false);
            idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            idleTimer.Tick += IdleTick;
            idleTimer.Start();
            CurrentPage = waiting;
            _ = LoadSettings();
        }

        public object CurrentPage { get => current; private set { if(Set(ref current, value)) Raise(nameof(IsWaitingScreen)); } }
        public bool IsWaitingScreen => CurrentPage == waiting;
        public bool IsKiosk { get => kiosk; private set => Set(ref kiosk, value); }
        public bool IsPortraitMode { get => portraitMode; private set { if (Set(ref portraitMode, value)) Raise(nameof(IsLandscapeMode)); } }
        public bool IsLandscapeMode => !IsPortraitMode;
        public void UpdateViewport(double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            IsPortraitMode = height > width;
        }
        public ICommand CloseCommand { get; }

        public void ResetForNewBoothSession()
        {
            machine.ResetToIdleQuiet();
            RestorePreviousSession();
            context.ResetForNewBoothSession();
            cameraStarted = false;
            returningToWaiting = false;
            lastInteractionUtc = DateTime.UtcNow;
            CurrentPage = waiting;
        }

        public async System.Threading.Tasks.Task ActivateAsync()
        {
            returningToWaiting = false;
            idleTimer.Start();
            await LoadSettings();
            if (!cameraStarted)
            {
                cameraStarted = true;
                await provider.GetRequiredService<CaptureViewModel>().ActivateAsync();
            }
            await ShowWaitingAsync(false);
            // Customer landing is always the first surface; code login starts on touch.
        }

        public bool CanEnterManager => !validatingForeground && !activatingRun && context.BoothSession == null && machine.State == CustomerWorkflowState.Idle;

        public async System.Threading.Tasks.Task PrepareReturnToAdminAsync()
        {
            returningToWaiting = true;
            idleTimer.Stop();
            RestorePreviousSession();
            await provider.GetRequiredService<FrameSelectionViewModel>().ShutdownAsync();
            cameraStarted = false;
            await provider.GetRequiredService<CaptureViewModel>().ShutdownAsync(CancellationToken.None);
        }

        public void RegisterInteraction() => lastInteractionUtc = DateTime.UtcNow;

        async System.Threading.Tasks.Task LoadSettings()
        {
            var value = await settings.GetAsync(CancellationToken.None);
            IsKiosk = value?.KioskMode ?? true;

            waitingTimeoutSeconds = NormalizeWaitingTimeout(value?.WaitingTimeoutSeconds ?? 30);
        }

        async System.Threading.Tasks.Task EnterFromWaitingAsync()
        {
            RegisterInteraction();
            if (context.ManagerSession != null)
            {
                if (context.ManagerSession.IsExpired)
                {
                    MessageBox.Show("Phiên vận hành đã hết hạn. Vui lòng quay lại trang quản lý để đăng nhập.", "MiuPhotoBooth");
                    CloseCommand.Execute(null);
                    return;
                }
                sessions.SignIn(context.ManagerSession);
                context.Authorization = context.ManagerSession.EventAuthorization ?? new BoothAccessAuthorization {
                    SessionToken = context.ManagerSession.RemoteSessionToken, DisplayName = context.ManagerSession.DisplayName,
                    ExpiresAtUtc = context.ManagerSession.ExpiresAtUtc };
                context.Settings = await settings.GetAsync(CancellationToken.None) ?? new Settings();
                await BeginAuthorizedRunAsync();
                return;
            }
            if (context.Authorization?.IsReusableSelfBooth == true && context.Authorization.ExpiresAtUtc <= DateTime.UtcNow)
            {
                RestorePreviousSession();
                context.Authorization = null;
            }
            if (context.Authorization != null && (!context.Authorization.IsOneTimeCode || context.Authorization.IsReusableSelfBooth))
            {
                if (!context.Authorization.UsesAccessCodeSettings)
                {
                    context.Settings = await settings.GetAsync(CancellationToken.None) ?? new PhotoBooth.Core.Models.Settings();
                    context.Authorization.Settings = new PhotoBooth.Core.Models.BoothAccessSettings
                    {
                        PhotoCount = context.Settings.PhotoCount,
                        CountdownSeconds = context.Settings.CountdownSeconds,
                        GifFrameDurationMilliseconds = context.Settings.GifFrameDurationMilliseconds,
                        ResultDisplaySeconds = context.Settings.WaitingTimeoutSeconds,
                        PrintCopies = 1
                    };
                    context.RequestedPrintCopies = 1;
                }
                await BeginAuthorizedRunAsync();
                return;
            }
            login.Reset();
            CurrentPage = login;
        }

        async System.Threading.Tasks.Task BeginAuthorizedRunAsync()
        {
            if (activatingRun || returningToWaiting) return;
            activatingRun = true;
            try
            {
                if (context.Authorization == null || context.Authorization.ExpiresAtUtc <= DateTime.UtcNow)
                    throw new InvalidOperationException("Phiên sử dụng đã hết hạn. Vui lòng đăng nhập lại.");
                if (context.Authorization?.UsesAccessCodeSettings == true)
                {
                    var validation = await provider.GetRequiredService<IBoothAccessService>().ValidateAsync(context.Authorization, CancellationToken.None);
                    if (!validation.Succeeded) throw new InvalidOperationException(validation.Error);
                    if (sessions.Current != null) sessions.Current.ExpiresAtUtc = validation.Authorization.ExpiresAtUtc;
                }
                // Revalidation happens only before a new session. The entitlement service
                // bounds temporary offline use and never interrupts capture or printing that
                // is already in progress.
                var access = await provider.GetRequiredService<ICommercialEntitlementService>()
                    .CheckAppAccessForNewSessionAsync(CancellationToken.None);
                if (!access.IsAllowed)
                {
                    throw new InvalidOperationException(access.UserMessage);
                }
                if (context.Authorization?.IsOneTimeCode == true && !string.IsNullOrWhiteSpace(context.Authorization.BoothSessionId))
                {
                    if (!Guid.TryParse(context.Authorization.BoothSessionId, out var captureId)) throw new InvalidOperationException("Không tìm thấy dữ liệu lượt chụp cũ trên máy này.");
                    var capture = await provider.GetRequiredService<IBoothSessionService>().GetAsync(captureId, CancellationToken.None);
                    var previous = (await provider.GetRequiredService<IDeliverableService>().GetByBoothSessionAsync(captureId, CancellationToken.None)).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
                    if (capture == null || previous == null || string.IsNullOrWhiteSpace(capture.FinalImagePath) || !File.Exists(capture.FinalImagePath))
                        throw new InvalidOperationException("Lượt trước chưa có ảnh hoàn tất trên máy này. Vui lòng nhờ nhân viên kiểm tra; mã vẫn gắn với lượt cũ.");
                    context.BoothSession = capture;
                    context.DeliverableId = previous.Id;
                    context.RecoveringDelivery = true;
                    CurrentPage = provider.GetRequiredService<CompleteViewModel>();
                    machine.ResumeCompletedDelivery();
                    return;
                }
                await FreezeRunConfigurationAsync();
                await ConfigurePrinterForRunAsync();
                if (!cameraStarted)
                {
                    await provider.GetRequiredService<CaptureViewModel>().ActivateAsync();
                    cameraStarted = true;
                }
                await provider.GetRequiredService<CaptureViewModel>().ResetToStartAsync();
                CurrentPage = provider.GetRequiredService<CaptureViewModel>();
                RegisterInteraction();
            }
            catch (Exception error)
            {
                context.ResetRunSnapshot();
                if (context.ManagerSession != null)
                {
                    MessageBox.Show("Chưa thể bắt đầu lượt chụp. Hãy kiểm tra thông số booth và thư viện trong trang quản lý.", "MiuPhotoBooth");
                    await ShowWaitingAsync(false);
                    return;
                }
                context.Authorization = null;
                sessions.SignOut();
                login.ShowError(error is InvalidOperationException ? error.Message : "Cấu hình phiên chụp chưa tải đầy đủ. Vui lòng thử lại sau khi đồng bộ.");
                CurrentPage = login;
            }
            finally { activatingRun = false; }
        }

        async System.Threading.Tasks.Task FreezeRunConfigurationAsync()
        {
            context.Settings = await settings.GetAsync(CancellationToken.None) ?? new PhotoBooth.Core.Models.Settings();
            var allEvents = await events.GetAllAsync(CancellationToken.None);
            var selectedEvent = ResolveRunEvent(allEvents);
            if (selectedEvent == null) throw new InvalidOperationException("Không tìm thấy sự kiện đang vận hành.");
            var configurationTask = eventConfigurations.GetActiveConfigurationAsync(selectedEvent.Id, CancellationToken.None);
            var frameTask = frames.GetAllAsync(CancellationToken.None);
            var presetTask = presets.GetAllAsync(CancellationToken.None);
            var lutTask = colorLuts.GetAllAsync(CancellationToken.None);
            await System.Threading.Tasks.Task.WhenAll(configurationTask, frameTask, presetTask, lutTask);
            var configuration = configurationTask.Result;
            // Creative assets follow the booth's current selection for code guests too.
            // Access-code photo/countdown/print limits are still applied below.
            var useBoothLibrary = context.Authorization != null || context.ManagerSession?.CanManageConfiguration == true;
            var selectedFrameIds = useBoothLibrary
                ? PhotoBooth.Core.Models.BoothLibrarySelection.Frames(frameTask.Result, context.Settings).Select(x => x.Id).ToList()
                : configuration?.FrameIds;
            var selectedPresetIds = useBoothLibrary
                ? PhotoBooth.Core.Models.BoothLibrarySelection.Presets(presetTask.Result, context.Settings).Select(x => x.Id).ToList()
                : configuration?.PresetIds;
            if (configuration == null || selectedFrameIds == null || selectedFrameIds.Count == 0)
                throw new InvalidOperationException("Sự kiện chưa có frame.");

            // Re-read the identity and row version before publishing the snapshot. If a
            // sync lands between reads, the customer retries instead of receiving a
            // mixture of two event versions.
            var confirmedEvent = ResolveRunEvent(await events.GetAllAsync(CancellationToken.None));
            var confirmedConfiguration = confirmedEvent == null
                ? null
                : await eventConfigurations.GetActiveConfigurationAsync(confirmedEvent.Id, CancellationToken.None);
            var configurationPresetIds = configuration.PresetIds ?? new Guid[0];
            var confirmedPresetIds = confirmedConfiguration?.PresetIds ?? new Guid[0];
            if (confirmedEvent?.Id != selectedEvent.Id || confirmedConfiguration == null ||
                confirmedConfiguration.RowVersion != configuration.RowVersion ||
                !confirmedConfiguration.FrameIds.SequenceEqual(configuration.FrameIds) ||
                !confirmedPresetIds.SequenceEqual(configurationPresetIds))
                throw new InvalidOperationException("Cấu hình vừa thay đổi. Vui lòng bắt đầu lại để dùng bản mới nhất.");

            var framesById = frameTask.Result.ToDictionary(value => value.Id);
            var runFrames = selectedFrameIds
                .Select(id => framesById.TryGetValue(id, out var value) ? value : null)
                .Where(value => value != null).ToList();
            if (runFrames.Count != selectedFrameIds.Count)
                throw new InvalidOperationException("Một frame của sự kiện chưa được tải đầy đủ.");
            var allPresets = presetTask.Result.ToList();
            var presetsById = allPresets.ToDictionary(value => value.Id);
            var runPresets = (selectedPresetIds ?? new Guid[0])
                .Select(id => presetsById.TryGetValue(id, out var value) ? value : null)
                .Where(value => value != null).ToList();
            if (runPresets.Count != (selectedPresetIds?.Count ?? 0))
                throw new InvalidOperationException("Một preset của sự kiện chưa được tải đầy đủ.");

            // Both code products own their capture/countdown/result settings. PhotoBooth
            // additionally owns GIF timing; employee mode follows the selected event.
            // Layout and visual assets always come from the selected event.
            var usesAccessCodeSettings = context.Authorization?.IsOneTimeCode == true || context.Authorization?.IsReusableSelfBooth == true;
            if (usesAccessCodeSettings)
            {
                var authorizedSettings = context.Authorization.Settings;
                context.Settings.PhotoCount = authorizedSettings.PhotoCount;
                context.Settings.CountdownSeconds = authorizedSettings.CountdownSeconds;
                context.Settings.GifFrameDurationMilliseconds = authorizedSettings.GifFrameDurationMilliseconds;
                context.Settings.WaitingTimeoutSeconds = authorizedSettings.ResultDisplaySeconds;
            }
            else if (!useBoothLibrary || !context.Settings.BoothSettingsConfigured)
            {
                context.Settings.PhotoCount = configuration.PhotoCount;
                context.Settings.CountdownSeconds = configuration.CountdownSeconds;
                context.Settings.GifFrameDurationMilliseconds = configuration.GifFrameDurationMilliseconds;
                context.Settings.WaitingTimeoutSeconds = configuration.WaitingTimeoutSeconds;
            }
            if (!useBoothLibrary || !context.Settings.BoothSettingsConfigured)
            {
                context.Settings.CustomerLayoutMode = configuration.CustomerLayoutMode;
                context.Settings.ImageRotationDegrees = configuration.ImageRotationDegrees;
            }
            context.UsePresetStrength = useBoothLibrary;
            // Event and manager runs use the app library and its selected preset strength.
            context.LiveViewPreset = useBoothLibrary && context.Settings.LiveViewPresetId.HasValue
                ? presetTask.Result.FirstOrDefault(x => x.Id == context.Settings.LiveViewPresetId.Value && x.LutStatus == "Ready" && x.LutCubeSize >= 2 && x.LutCubeSize <= 65)
                : null;
            if (!useBoothLibrary) context.Settings.LiveViewPresetId = null;
            context.Settings.DefaultFrameId = selectedFrameIds[0];
            context.Settings.DefaultPresetId = selectedPresetIds?.Count > 0 ? (Guid?)selectedPresetIds[0] : null;
            var packagePrinting = context.Authorization?.IsOneTimeCode == true || context.Authorization?.IsReusableSelfBooth == true;
            context.PrintPolicyMode = packagePrinting ? EventPrintPolicyMode.Automatic : configuration.PrintPolicyMode;
            var includedPrintCopies = Math.Max(0, configuration.IncludedPrintCopies);
            context.MaximumPrintCopies = packagePrinting ? Math.Max(0, context.Authorization.Settings.PrintCopies) : Math.Max(includedPrintCopies, configuration.MaximumPrintCopies);
            context.RequestedPrintCopies = usesAccessCodeSettings
                ? Math.Min(Math.Max(0, context.Authorization.Settings.PrintCopies), context.MaximumPrintCopies)
                : includedPrintCopies;
            if (!packagePrinting && configuration.PrintPolicyMode == EventPrintPolicyMode.Disabled)
            {
                context.RequestedPrintCopies = 0;
                context.Settings.PrintingEnabled = false;
            }
            if (packagePrinting) context.Settings.PrintingEnabled = context.RequestedPrintCopies > 0;
            context.ColorPresetStrength = Math.Max(0f,Math.Min(1f,configuration.ColorPresetStrengthPercent/100f));

            var snapshotDirectory = Path.Combine(storage.GetPath("Temp"), "configuration-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(snapshotDirectory);
                var frameSnapshots = SnapshotFrames(runFrames, snapshotDirectory);
                var lutFiles = new Dictionary<Guid, string>();
                foreach (var preset in runPresets.Concat(context.LiveViewPreset == null ? new Preset[0] : new[] { context.LiveViewPreset }).GroupBy(x => x.LutAssetId).Select(x => x.First()))
                {
                    var previewOnly = !runPresets.Any(value => value.LutAssetId == preset.LutAssetId);
                    try
                    {
                        var asset = lutTask.Result.FirstOrDefault(value => value.Id == preset.LutAssetId && value.Status == PhotoBooth.Core.Models.ColorLutAssetStatus.Ready);
                        if (asset == null) throw new InvalidOperationException("Một preset của sự kiện chưa sẵn sàng.");
                        var source = storage.GetFullPath(asset.RelativePath);
                        var destination = Path.Combine(snapshotDirectory, asset.Id.ToString("N") + Path.GetExtension(source));
                        File.Copy(source, destination, true);
                        lutFiles[asset.Id] = destination;
                    }
                    catch (Exception error) when (previewOnly &&
                        (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException))
                    {
                        // An optional preview must not block capture. Required result LUTs
                        // still fail above if their frozen source cannot be obtained.
                        context.LiveViewPreset = null;
                    }
                }
                context.EventSnapshot = selectedEvent;
                context.ConfigurationSnapshotDirectory = snapshotDirectory;
                context.AvailableFrames = frameSnapshots;
                context.AvailablePresets = runPresets;
                context.DefaultPreset = context.Settings.DefaultPresetId.HasValue
                    ? runPresets.FirstOrDefault(value => value.Id == context.Settings.DefaultPresetId.Value)
                    : null;
                context.LutSnapshotFiles = lutFiles;
                await provider.GetRequiredService<CaptureViewModel>().LoadLivePresetAsync();

            }
            catch
            {
                try { if (Directory.Exists(snapshotDirectory)) Directory.Delete(snapshotDirectory, true); } catch { }
                throw;
            }
        }

        PhotoBooth.Core.Models.PhotoEvent ResolveRunEvent(IEnumerable<PhotoBooth.Core.Models.PhotoEvent> values)
        {
            var list = (values ?? Enumerable.Empty<PhotoBooth.Core.Models.PhotoEvent>()).ToList();
            var remoteEventId = context.Authorization?.UsesAccessCodeSettings == true && context.Authorization.IsEvent != true ? context.Authorization.EventId : null;
            if (!string.IsNullOrWhiteSpace(remoteEventId))
                return list.FirstOrDefault(value => string.Equals(value.RemoteEventId, remoteEventId, StringComparison.Ordinal));
            return list.FirstOrDefault(value => value.IsDefault);
        }

        static IReadOnlyList<PhotoBooth.Core.Models.Frame> SnapshotFrames(IEnumerable<PhotoBooth.Core.Models.Frame> values, string directory)
        {
            var result = new List<PhotoBooth.Core.Models.Frame>();
            foreach (var value in values)
            {
                var sourcePath = value.SourcePath;
                if (value.Slots == null || value.Slots.Count == 0 || value.PixelWidth <= 0 || value.PixelHeight <= 0 ||
                    string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    throw new InvalidOperationException("Frame của sự kiện chưa sẵn sàng.");
                var copied = Path.Combine(directory, value.Id.ToString("N") + Path.GetExtension(sourcePath));
                File.Copy(sourcePath, copied, true);
                sourcePath = copied;
                result.Add(new PhotoBooth.Core.Models.Frame
                {
                    Id = value.Id, Name = value.Name, SourcePath = sourcePath, ThumbnailPath = sourcePath,
                    PixelWidth = value.PixelWidth, PixelHeight = value.PixelHeight, IsPinned = true,
                    EventId = value.EventId, CreatedAtUtc = value.CreatedAtUtc,
                    Slots = value.Slots.Select(slot => new PhotoBooth.Core.Models.FrameSlot
                    {
                        Id = slot.Id, Index = slot.Index, X = slot.X, Y = slot.Y, Width = slot.Width, Height = slot.Height
                    }).ToList()
                });
            }
            return result;
        }

        async System.Threading.Tasks.Task ConfigurePrinterForRunAsync()
        {
            var requiredByCode = context.Authorization != null && context.RequestedPrintCopies > 0;
            context.PrintingEnabled = requiredByCode;
            context.ConnectedPrinterId = null;
            context.ConnectedPrinterProfileId = null;
            if (context.Settings?.PrintingEnabled != true || context.RequestedPrintCopies <= 0) return;
            context.PrintingEnabled = true;
            try
            {
                var profiles = await printers.GetProfilesAsync(CancellationToken.None);
                var profile = context.Settings.DefaultPrinterProfileId.HasValue
                    ? profiles.FirstOrDefault(value => value.Id == context.Settings.DefaultPrinterProfileId.Value)
                    : profiles.FirstOrDefault(value => value.IsDefault);
                if (profile == null) return;
                // Retain the selected profile even while offline so Retry can recover
                // the same printer without silently disabling the requested print.
                context.ConnectedPrinterId = profile.PrinterId;
                context.ConnectedPrinterProfileId = profile.Id;
                await printers.ConnectAsync(profile.PrinterId, CancellationToken.None);
            }
            catch { /* The finishing screen reports the failure and permits explicit Retry. */ }
        }

        async System.Threading.Tasks.Task OnRunCompletedAsync()
        {
            if (context.Authorization?.IsOneTimeCode == true && context.Authorization.IsReusableSelfBooth != true)
            {
                RestorePreviousSession();
                context.Authorization = null;
            }
            context.ResetRunSnapshot();
            await ShowWaitingAsync(false);
        }

        void RestorePreviousSession()
        {
            if (context.PreviousPcSession != null) sessions.SignIn(context.PreviousPcSession);
            else if (context.Authorization != null) sessions.SignOut();
            context.PreviousPcSession = null;
        }

        async void IdleTick(object sender, EventArgs e)
        {
            if (returningToWaiting || CurrentPage == waiting || !cameraStarted) return;
            // Waiting/login may time out, but an accepted customer run owns its data
            // until completion or an explicit cancel. Never erase review/frame state.
            if (context.BoothSession != null) return;
            if ((DateTime.UtcNow - lastInteractionUtc).TotalSeconds < waitingTimeoutSeconds) return;
            var state = machine.State;
            if (state == CustomerWorkflowState.Countdown || state == CustomerWorkflowState.Smile || state == CustomerWorkflowState.Capturing ||
                state == CustomerWorkflowState.InterShotDelay || state == CustomerWorkflowState.Printing || state == CustomerWorkflowState.Complete) return;
            await ShowWaitingAsync(true);
        }

        async System.Threading.Tasks.Task ShowWaitingAsync(bool reset)
        {
            if (returningToWaiting) return;
            returningToWaiting = true;
            try
            {
                if (reset)
                {
                    await provider.GetRequiredService<FrameSelectionViewModel>().ShutdownAsync();
                    await provider.GetRequiredService<CaptureViewModel>().ResetToStartAsync();
                    if (context.Authorization?.IsOneTimeCode == true && context.Authorization.IsReusableSelfBooth != true)
                    {
                        RestorePreviousSession();
                        context.Authorization = null;
                    }
                    context.ResetRunSnapshot();
                }
                await provider.GetRequiredService<CaptureViewModel>().LoadWaitingLivePresetAsync(colorLuts);
                await waiting.ActivateAsync();
                CurrentPage = waiting;
                lastInteractionUtc = DateTime.UtcNow;
            }
            finally { returningToWaiting = false; }
        }

        void UpdatePage()
        {
            if (CurrentPage == waiting || CurrentPage == login) return;
            if (machine.State == CustomerWorkflowState.FrameSelection || machine.State == CustomerWorkflowState.Printing)
            {
                var frameSelection = provider.GetRequiredService<FrameSelectionViewModel>();
                if (machine.State == CustomerWorkflowState.FrameSelection) frameSelection.Activate();
                CurrentPage = frameSelection;
                return;
            }
            if (machine.State == CustomerWorkflowState.Complete)
            {
                CurrentPage = provider.GetRequiredService<CompleteViewModel>();
                return;
            }
            CurrentPage = provider.GetRequiredService<CaptureViewModel>();
        }

        static int NormalizeWaitingTimeout(int seconds) => Math.Max(10, Math.Min(900, seconds));
    }
}
