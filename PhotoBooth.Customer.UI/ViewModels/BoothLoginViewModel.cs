using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Customer.UI.Mvvm;
using PhotoBooth.Customer.UI.Workflow;

namespace PhotoBooth.Customer.UI.ViewModels
{
    public sealed class BoothLoginViewModel : ObservableObject
    {
        public bool HasCustomerHeader => true;
        readonly IBoothAccessService boothAccess;
        readonly IPcAuthenticationService authentication;
        readonly IPcSessionContext sessions;
        readonly ISettingsService settings;
        readonly CustomerWorkflowContext context;
        bool isCodeMode = true, isBusy;
        string code, username, password, error;

        public BoothLoginViewModel(IBoothAccessService boothAccess, IPcAuthenticationService authentication, IPcSessionContext sessions, ISettingsService settings, CustomerWorkflowContext context)
        {
            this.boothAccess = boothAccess;
            this.authentication = authentication;
            this.sessions = sessions;
            this.settings = settings;
            this.context = context;
            UseCodeCommand = new RelayCommand(() => IsCodeMode = true);
            UseEmployeeCommand = new RelayCommand(() => IsCodeMode = false);
            SubmitCommand = new AsyncCommand(SubmitAsync, () => CanSubmit);
            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        public event EventHandler Authorized;
        public event EventHandler BackRequested;
        public bool IsCodeMode { get => isCodeMode; set { if (Set(ref isCodeMode, value)) { Raise(nameof(IsEmployeeMode)); Raise(nameof(LoginTitle)); Raise(nameof(LoginIntro)); Raise(nameof(SubmitLabel)); Raise(nameof(BusyLabel)); Raise(nameof(AlternateHint)); Raise(nameof(AlternateLabel)); Error = null; NotifySubmit(); } } }
        public bool IsEmployeeMode => !IsCodeMode;
        public string LoginTitle => IsCodeMode ? "Mã của bạn là gì?" : "Đăng nhập sự kiện";
        public string LoginIntro => IsCodeMode ? "Nhập mã bạn đã nhận để bắt đầu\nlưu lại những khoảnh khắc đẹp." : "Nhập mã sự kiện hoặc tài khoản và mật khẩu để mở booth.";
        public string SubmitLabel => IsCodeMode ? "Bắt đầu chụp" : "Đăng nhập";
        public string BusyLabel => IsCodeMode ? "Đang kiểm tra mã…" : "Đang đăng nhập…";
        public string AlternateHint => IsCodeMode ? "Chụp theo sự kiện?" : "Bạn có mã chụp ảnh?";
        public string AlternateLabel => IsCodeMode ? "Đăng nhập sự kiện" : "Nhập mã chụp ảnh";

        public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) NotifySubmit(); } }
        public string Code { get => code; set { if (Set(ref code, NormalizeCode(value))) NotifySubmit(); } }
        public string Username { get => username; set { Set(ref username, value); NotifySubmit(); } }
        public string Password { get => password; set { Set(ref password, value); NotifySubmit(); } }
        public string Error { get => error; private set { Set(ref error, value); Raise(nameof(HasError)); } }
        public bool CanSubmit => !IsBusy && (IsCodeMode ? Code?.Length == 8 : !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password));
        void NotifySubmit() { Raise(nameof(CanSubmit)); (SubmitCommand as AsyncCommand)?.NotifyCanExecuteChanged(); }
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
        public ICommand UseCodeCommand { get; }
        public ICommand UseEmployeeCommand { get; }
        public ICommand SubmitCommand { get; }
        public ICommand BackCommand { get; }

        public void Reset()
        {
            Code = null;
            Password = null;
            Error = null;
            IsBusy = false;
            IsCodeMode = true;
        }

        public void ShowError(string message)
        {
            Error = string.IsNullOrWhiteSpace(message) ? "Không thể bắt đầu phiên chụp. Vui lòng thử lại." : message;
        }

        async Task SubmitAsync()
        {
            Error = null;
            IsBusy = true;
            try
            {
                if (IsCodeMode) await RedeemCodeAsync();
                else await LoginEmployeeAsync();
            }
            finally { IsBusy = false; }
        }

        async Task RedeemCodeAsync()
        {
            var result = await boothAccess.RedeemAsync(Code, CancellationToken.None);
            if (!result.Succeeded) { Error = result.Error; return; }
            var authorization = result.Authorization;
            var local = await settings.GetAsync(CancellationToken.None) ?? new Settings();
            ApplySnapshot(local, authorization.Settings);
            if (context.Authorization == null && context.PreviousPcSession == null)
                context.PreviousPcSession = sessions.Current;
            context.Authorization = authorization;
            context.Settings = local;
            context.RequestedPrintCopies = authorization.Settings.PrintCopies;
            sessions.SignIn(new PcSession
            {
                AccountId = authorization.AccessCodeId,
                DisplayName = authorization.DisplayName,
                OrganizationId = context.PreviousPcSession?.OrganizationId,
                Role = PcAppRole.BoothOperator,
                AuthenticationMode = PcAuthenticationMode.OnlineDeviceBound,
                AuthenticatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = authorization.ExpiresAtUtc,
                RemoteSessionToken = authorization.SessionToken,
                EventAuthorization = authorization
            });
            Authorized?.Invoke(this, EventArgs.Empty);
        }

        async Task LoginEmployeeAsync()
        {
            var result = await authentication.AuthenticateAsync(Username, Password, false, CancellationToken.None);
            if (!result.Succeeded) { Error = result.Error; return; }
            if (context.Authorization == null && context.PreviousPcSession == null)
                context.PreviousPcSession = sessions.Current;
            sessions.SignIn(result.Session);
            var local = await settings.GetAsync(CancellationToken.None) ?? new Settings();
            context.Authorization = result.Session.EventAuthorization ?? new BoothAccessAuthorization
            {
                SessionToken = result.Session.RemoteSessionToken,
                DisplayName = result.Session.DisplayName,
                ExpiresAtUtc = result.Session.ExpiresAtUtc,
                Settings = new BoothAccessSettings
                {
                    PhotoCount = local.PhotoCount,
                    CountdownSeconds = local.CountdownSeconds,
                    GifFrameDurationMilliseconds = local.GifFrameDurationMilliseconds,
                    ResultDisplaySeconds = local.WaitingTimeoutSeconds,
                    PrintCopies = 1
                }
            };
            context.Settings = local;
            context.RequestedPrintCopies = 1;
            Authorized?.Invoke(this, EventArgs.Empty);
        }

        static void ApplySnapshot(Settings target, BoothAccessSettings source)
        {
            if (source == null) return;
            target.PhotoCount = Math.Max(1, Math.Min(8, source.PhotoCount));
            target.CountdownSeconds = Math.Max(1, Math.Min(10, source.CountdownSeconds));
            target.GifFrameDurationMilliseconds = Math.Max(200, Math.Min(5000, source.GifFrameDurationMilliseconds));
            target.WaitingTimeoutSeconds = Math.Max(10, Math.Min(600, source.ResultDisplaySeconds));
        }

        static string NormalizeCode(string value)
        {
            value = (value ?? string.Empty).ToUpperInvariant();
            var buffer = new System.Text.StringBuilder(8);
            foreach (var character in value)
                if (char.IsLetterOrDigit(character) && buffer.Length < 8) buffer.Append(character);
            return buffer.ToString();
        }
    }
}
