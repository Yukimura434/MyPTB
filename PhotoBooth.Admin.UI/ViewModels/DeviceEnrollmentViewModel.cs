using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public enum DeviceSetupStep
    {
        VerifyOwner,
        ChooseLocation,
        Complete
    }

    public enum DeviceReadinessState
    {
        Checking,
        Unenrolled,
        Ready,
        Suspended,
        Revoked,
        Failed
    }

    public sealed class DeviceEnrollmentViewModel : PageViewModel
    {
        readonly IDeviceIdentityService service;
        readonly IPcSessionContext sessionContext;
        string ownerEmail, password, status, error, deviceName = Environment.MachineName;
        bool busy, showSavedProfiles, showProfileSwitchConfirmation, unlockRequired, ownerVerificationRequired;
        DeviceIdentity selectedProfile, activeProfile, completedIdentity;
        DeviceActivationAttempt activation;
        DeviceActivationLocation selectedLocation;
        DeviceSetupStep step = DeviceSetupStep.VerifyOwner;
        DeviceReadinessState readiness = DeviceReadinessState.Checking;

        public DeviceEnrollmentViewModel(IDeviceIdentityService value, IPcSessionContext sessionContext = null)
        {
            service = value ?? throw new ArgumentNullException(nameof(value));
            this.sessionContext = sessionContext;
            VerifyOwnerCommand = new AsyncCommand(_ => VerifyOwnerAsync(), _ => CanVerifyOwner);
            ActivateCommand = new AsyncCommand(_ => ActivateAsync(), _ => CanActivate);
            SelectProfileCommand = new RelayCommand(_ => ShowProfileSwitchConfirmation = true, _ => !ownerVerificationRequired && !IsBusy && SelectedProfile != null && !ShowProfileSwitchConfirmation);
            ConfirmProfileCommand = new AsyncCommand(_ => SelectAsync(), _ => !ownerVerificationRequired && !IsBusy && SelectedProfile != null && ShowProfileSwitchConfirmation);
            CancelProfileSwitchCommand = new RelayCommand(_ => ShowProfileSwitchConfirmation = false, _ => !IsBusy && ShowProfileSwitchConfirmation);
            BackCommand = new RelayCommand(_ => Back(), _ => !IsBusy && Step == DeviceSetupStep.ChooseLocation);
            ShowSavedProfilesCommand = new RelayCommand(_ => ShowSavedProfiles = !ShowSavedProfiles, _ => !ownerVerificationRequired && !IsBusy && HasProfiles);
            StartNewConnectionCommand = new RelayCommand(_ => StartNewConnection(), _ => !IsBusy);
        }

        public override string Title => "Thiết lập thiết bị";
        public ObservableCollection<DeviceIdentity> Profiles { get; } = new ObservableCollection<DeviceIdentity>();
        public ObservableCollection<DeviceActivationLocation> Locations { get; } = new ObservableCollection<DeviceActivationLocation>();

        public string OwnerEmail
        {
            get => ownerEmail;
            set { if (Set(ref ownerEmail, value)) RefreshCommands(); }
        }

        public string Password
        {
            get => password;
            set { if (Set(ref password, value)) RefreshCommands(); }
        }

        public string DeviceName
        {
            get => deviceName;
            set { if (Set(ref deviceName, value)) RefreshCommands(); }
        }

        public DeviceActivationLocation SelectedLocation
        {
            get => selectedLocation;
            set { if (Set(ref selectedLocation, value)) RefreshCommands(); }
        }

        public DeviceIdentity SelectedProfile
        {
            get => selectedProfile;
            set
            {
                if (!Set(ref selectedProfile, value)) return;
                ShowProfileSwitchConfirmation = false;
                Raise(nameof(SelectedProfileSummary));
                RefreshCommands();
            }
        }

        public DeviceIdentity ActiveProfile
        {
            get => activeProfile;
            private set
            {
                if (!Set(ref activeProfile, value)) return;
                Raise(nameof(HasActiveProfile));
                Raise(nameof(DeviceStatusTitle));
                Raise(nameof(DeviceStatusDetail));
            }
        }

        public DeviceIdentity CompletedIdentity
        {
            get => completedIdentity;
            private set => Set(ref completedIdentity, value);
        }

        public DeviceSetupStep Step
        {
            get => step;
            private set
            {
                if (!Set(ref step, value)) return;
                Raise(nameof(IsVerifyOwnerStep));
                Raise(nameof(IsChooseLocationStep));
                Raise(nameof(IsCompleteStep));
                RefreshCommands();
            }
        }

        public bool IsVerifyOwnerStep => Step == DeviceSetupStep.VerifyOwner;
        public bool IsChooseLocationStep => Step == DeviceSetupStep.ChooseLocation;
        public bool IsCompleteStep => Step == DeviceSetupStep.Complete;
        public bool HasProfiles => Profiles.Count > 0;
        public bool HasActiveProfile => ActiveProfile?.EnrollmentState == DeviceEnrollmentState.Enrolled;
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
        public bool CanVerifyOwner => !IsBusy && IsVerifyOwnerStep && !string.IsNullOrWhiteSpace(OwnerEmail) && !string.IsNullOrEmpty(Password);
        public bool CanActivate => !IsBusy && IsChooseLocationStep && activation != null && SelectedLocation != null && !string.IsNullOrWhiteSpace(DeviceName);
        public string SelectedProfileSummary => SelectedProfile == null
            ? string.Empty
            : (string.IsNullOrWhiteSpace(SelectedProfile.LocationName) ? "Kết nối đã chọn" : SelectedProfile.LocationName)
              + " · " + (string.IsNullOrWhiteSpace(SelectedProfile.DeviceName) ? "PhotoBooth" : SelectedProfile.DeviceName);

        public DeviceReadinessState Readiness
        {
            get => readiness;
            private set
            {
                if (!Set(ref readiness, value)) return;
                Raise(nameof(DeviceStatusTitle));
                Raise(nameof(DeviceStatusDetail));
            }
        }

        public bool ShowSavedProfiles
        {
            get => showSavedProfiles;
            set
            {
                if (!Set(ref showSavedProfiles, value)) return;
                Raise(nameof(ShowWizard));
            }
        }

        public bool ShowWizard => !ShowSavedProfiles;

        public bool ShowProfileSwitchConfirmation
        {
            get => showProfileSwitchConfirmation;
            private set
            {
                if (!Set(ref showProfileSwitchConfirmation, value)) return;
                Raise(nameof(ShowProfileSwitchAction));
                RefreshCommands();
            }
        }

        public bool ShowProfileSwitchAction => !ShowProfileSwitchConfirmation;

        public bool IsBusy
        {
            get => busy;
            private set
            {
                if (!Set(ref busy, value)) return;
                Raise(nameof(CanVerifyOwner));
                Raise(nameof(CanActivate));
                RefreshCommands();
            }
        }

        public string Status { get => status; private set => Set(ref status, value); }
        public string Error
        {
            get => error;
            private set
            {
                if (!Set(ref error, value)) return;
                Raise(nameof(HasError));
            }
        }

        public string VerifiedOwnerSummary => activation == null
            ? string.Empty
            : string.Join(" · ", new[] { activation.OwnerDisplayName, activation.OrganizationName }.Where(x => !string.IsNullOrWhiteSpace(x)));

        public string DeviceStatusTitle
        {
            get
            {
                switch (Readiness)
                {
                    case DeviceReadinessState.Checking: return "Đang kiểm tra thiết bị";
                    case DeviceReadinessState.Ready: return "Thiết bị đã thiết lập";
                    case DeviceReadinessState.Revoked: return "Thiết bị đã bị thu hồi";
                    case DeviceReadinessState.Suspended: return "Thiết bị đang tạm ngưng";
                    case DeviceReadinessState.Failed: return "Chưa đọc được trạng thái thiết bị";
                    default: return "Thiết bị chưa được kết nối";
                }
            }
        }

        public string DeviceStatusDetail
        {
            get
            {
                if (Readiness == DeviceReadinessState.Checking) return "Vui lòng chờ trong giây lát.";
                if (Readiness == DeviceReadinessState.Failed) return "Hãy thử lại hoặc mở phần thiết lập thiết bị.";
                if (ActiveProfile == null) return "Kết nối máy này với một cửa hàng để tiếp tục.";
                var location = string.IsNullOrWhiteSpace(ActiveProfile.LocationName) ? "Chưa xác định cửa hàng" : ActiveProfile.LocationName;
                var device = string.IsNullOrWhiteSpace(ActiveProfile.DeviceName) ? "PhotoBooth" : ActiveProfile.DeviceName;
                return location + " · " + device;
            }
        }

        public ICommand VerifyOwnerCommand { get; }
        public ICommand ActivateCommand { get; }
        public ICommand SelectProfileCommand { get; }
        public ICommand ConfirmProfileCommand { get; }
        public ICommand CancelProfileSwitchCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand ShowSavedProfilesCommand { get; }
        public ICommand StartNewConnectionCommand { get; }

        public event EventHandler PasswordClearRequested;

        public void PrepareForUnlock()
        {
            ownerVerificationRequired = true;
            unlockRequired = true;
            CompletedIdentity = null;
            ShowSavedProfiles = false;
            activation = null;
            Locations.Clear();
            SelectedLocation = null;
            Step = DeviceSetupStep.VerifyOwner;
            Readiness = DeviceReadinessState.Suspended;
            Status = "Thiết bị đang bị khóa tạm thời. Chủ tài khoản hãy xác minh để mở khóa trên đúng máy này.";
            Error = null;
        }

        public void PrepareForReauthentication()
        {
            ownerVerificationRequired = true;
            unlockRequired = false;
            StartNewConnection();
            Readiness = DeviceReadinessState.Unenrolled;
            Status = "Kết nối đã hết hiệu lực. Hãy xác minh chủ tài khoản để đăng nhập lại.";
        }

        public async Task RefreshAsync()
        {
            if (IsBusy) return;
            try
            {
                IsBusy = true;
                Readiness = DeviceReadinessState.Checking;
                Error = null;
                var profiles = await service.ListProfilesAsync(CancellationToken.None);
                var active = await service.GetAsync(CancellationToken.None);
                Profiles.Clear();
                foreach (var profile in profiles) Profiles.Add(profile);
                ActiveProfile = active;
                Readiness = unlockRequired ? DeviceReadinessState.Suspended : ownerVerificationRequired ? DeviceReadinessState.Unenrolled : ReadinessFor(active);
                SelectedProfile = active == null ? Profiles.FirstOrDefault() : Profiles.FirstOrDefault(x => x.ProfileId == active.ProfileId) ?? active;
                Raise(nameof(HasProfiles));
                ShowSavedProfiles = !ownerVerificationRequired && HasProfiles && !IsVerifyOwnerStep;
                Status = unlockRequired
                    ? "Thiết bị đang bị khóa tạm thời. Chủ tài khoản hãy xác minh để mở khóa trên đúng máy này."
                    : ownerVerificationRequired ? "Kết nối đã hết hiệu lực. Hãy xác minh chủ tài khoản để đăng nhập lại."
                    : HasActiveProfile ? "Máy này đã có kết nối hoạt động." : "Máy này cần được kết nối trước khi nhân viên đăng nhập.";
            }
            catch
            {
                Readiness = DeviceReadinessState.Failed;
                Error = "Không thể đọc trạng thái thiết bị trên máy này.";
            }
            finally { IsBusy = false; }
        }

        async Task VerifyOwnerAsync()
        {
            try
            {
                IsBusy = true;
                Error = null;
                Status = "Đang xác minh tài khoản…";
                activation = await service.BeginOwnerActivationAsync(OwnerEmail, Password, Version(), CancellationToken.None);
                Locations.Clear();
                foreach (var location in activation.Locations ?? new DeviceActivationLocation[0]) Locations.Add(location);
                SelectedLocation = Locations.FirstOrDefault();
                Raise(nameof(VerifiedOwnerSummary));
                if (Locations.Count == 0)
                {
                    activation = null;
                    Error = "Tài khoản chưa có cửa hàng đang hoạt động. Hãy tạo cửa hàng trên Web Customer rồi thử lại.";
                    Status = "Chưa thể chọn nơi đặt thiết bị.";
                    return;
                }
                Step = DeviceSetupStep.ChooseLocation;
                Status = "Đã xác minh. Hãy chọn cửa hàng cho thiết bị.";
            }
            catch (Exception exception)
            {
                activation = null;
                Error = FriendlyError(exception, "Không thể xác minh tài khoản. Hãy thử lại.");
                Status = "Xác minh chưa hoàn tất.";
            }
            finally
            {
                Password = null;
                PasswordClearRequested?.Invoke(this, EventArgs.Empty);
                IsBusy = false;
            }
        }

        async Task ActivateAsync()
        {
            try
            {
                IsBusy = true;
                Error = null;
                Status = "Đang kết nối thiết bị…";
                CompletedIdentity = await service.CompleteOwnerActivationAsync(activation, SelectedLocation.Id, DeviceName, Version(), CancellationToken.None);
                unlockRequired = false;
                ownerVerificationRequired = false;
                activation = null;
                Locations.Clear();
                Step = DeviceSetupStep.Complete;
                await ReloadProfilesAfterCompletionAsync();
                Status = "Thiết bị đã được kết nối và sẵn sàng sử dụng.";
            }
            catch (DeviceReenrollmentRequiredException exception)
            {
                sessionContext?.SignOut();
                PrepareForReauthentication();
                await ReloadProfilesAfterCompletionAsync();
                Error = exception.Message;
                Status = "Xác minh lại để đăng nhập trên máy này.";
            }
            catch (Exception exception)
            {
                Error = FriendlyError(exception, "Không thể kết nối thiết bị. Hãy thử lại.");
                Status = "Kết nối chưa hoàn tất.";
            }
            finally { IsBusy = false; }
        }

        async Task SelectAsync()
        {
            if (ownerVerificationRequired) return;
            try
            {
                IsBusy = true;
                Error = null;
                await service.SelectProfileAsync(SelectedProfile.ProfileId, CancellationToken.None);
                CompletedIdentity = SelectedProfile;
                ActiveProfile = SelectedProfile;
                Readiness = ReadinessFor(SelectedProfile);
                Step = DeviceSetupStep.Complete;
                ShowSavedProfiles = false;
                ShowProfileSwitchConfirmation = false;
                Status = "Đã chuyển sang " + DeviceStatusDetail + ".";
            }
            catch (Exception exception)
            {
                Error = FriendlyError(exception, "Không thể dùng kết nối đã chọn.");
            }
            finally { IsBusy = false; }
        }

        async Task ReloadProfilesAfterCompletionAsync()
        {
            var profiles = await service.ListProfilesAsync(CancellationToken.None);
            Profiles.Clear();
            foreach (var profile in profiles) Profiles.Add(profile);
            ActiveProfile = await service.GetAsync(CancellationToken.None);
            Readiness = ReadinessFor(ActiveProfile);
            SelectedProfile = ActiveProfile;
            Raise(nameof(HasProfiles));
        }

        void Back()
        {
            activation = null;
            Locations.Clear();
            SelectedLocation = null;
            Step = DeviceSetupStep.VerifyOwner;
            Status = "Xác minh chủ tài khoản để tiếp tục.";
            Error = null;
            Raise(nameof(VerifiedOwnerSummary));
        }

        void StartNewConnection()
        {
            CompletedIdentity = null;
            ShowSavedProfiles = false;
            ShowProfileSwitchConfirmation = false;
            Back();
        }

        void RefreshCommands()
        {
            Raise(nameof(CanVerifyOwner));
            Raise(nameof(CanActivate));
            CommandManager.InvalidateRequerySuggested();
        }

        static string FriendlyError(Exception exception, string fallback)
        {
            if ((exception is InvalidOperationException || exception is ArgumentException) && !string.IsNullOrWhiteSpace(exception.Message)) return exception.Message;
            return fallback;
        }

        static DeviceReadinessState ReadinessFor(DeviceIdentity identity)
        {
            if (identity == null || identity.EnrollmentState == DeviceEnrollmentState.Unenrolled) return DeviceReadinessState.Unenrolled;
            if (identity.EnrollmentState == DeviceEnrollmentState.Suspended) return DeviceReadinessState.Suspended;
            if (identity.EnrollmentState == DeviceEnrollmentState.Revoked) return DeviceReadinessState.Revoked;
            return DeviceReadinessState.Ready;
        }

        static string Version() => typeof(DeviceEnrollmentViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
