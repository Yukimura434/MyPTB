using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhotoBooth.Admin.UI.Mvvm;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Admin.UI.ViewModels
{
    public sealed class BoothAccessViewModel : PageViewModel
    {
        readonly IBoothAccessService service;
        readonly IQrCodeService qr;
        int photoCount = 8, countdownSeconds = 5, resultSeconds = 30, printCopies = 1;
        BoothAccessCodeType selectedCodeType = BoothAccessCodeType.PhotoBooth;
        DurationChoice duration;
        GifDurationChoice gifDuration;
        string code, message, albumUrl;
        byte[] albumQr;
        DateTime? expiresAt;

        public BoothAccessViewModel(IBoothAccessService service, IQrCodeService qr)
        {
            this.service = service;
            this.qr = qr;
            PhotoCounts = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            CountdownOptions = new[] { 1, 2, 3, 5, 10 };
            DurationOptions = new[] { new DurationChoice("2 giờ", 120), new DurationChoice("4 giờ", 240), new DurationChoice("30 phút", 30), new DurationChoice("1 giờ", 60), new DurationChoice("3 giờ", 180) };
            duration = DurationOptions[0];
            GifDurationOptions = new[] { new GifDurationChoice("0,2 giây", 200), new GifDurationChoice("0,5 giây", 500), new GifDurationChoice("0,8 giây", 800), new GifDurationChoice("1 giây", 1000), new GifDurationChoice("1,5 giây", 1500) };
            gifDuration = GifDurationOptions[2];
            ResultDurations = new[] { 15, 30, 45, 60, 120 };
            PrintCopyOptions = new[] { 0, 1, 2, 3, 4 };
            CreateCommand = new AsyncCommand(_ => CreateAsync());
        }

        public override string Title => "Tạo phiên chụp";
        public int[] PhotoCounts { get; }
        public int[] CountdownOptions { get; }
        public DurationChoice[] DurationOptions { get; }
        public GifDurationChoice[] GifDurationOptions { get; }
        public int[] ResultDurations { get; }
        public int[] PrintCopyOptions { get; }
        public BoothAccessCodeType SelectedCodeType
        {
            get => selectedCodeType;
            set
            {
                if (!Set(ref selectedCodeType, value)) return;
                PrintCopies = value == BoothAccessCodeType.SelfBooth ? 0 : 1;
                ClearCreatedCode();
                Message = null;
                Raise(nameof(IsPhotoBooth));
                Raise(nameof(IsSelfBooth));
                Raise(nameof(PageDescription));
                Raise(nameof(CreateButtonText));
                Raise(nameof(CodeHeading));
            }
        }
        public bool IsPhotoBooth { get => SelectedCodeType == BoothAccessCodeType.PhotoBooth; set { if (value) SelectedCodeType = BoothAccessCodeType.PhotoBooth; } }
        public bool IsSelfBooth { get => SelectedCodeType == BoothAccessCodeType.SelfBooth; set { if (value) SelectedCodeType = BoothAccessCodeType.SelfBooth; } }
        public string PageDescription => IsPhotoBooth
            ? "Mã PhotoBooth dùng một lần cho một lượt chụp và giữ đầy đủ kết quả của chế độ PhotoBooth."
            : "Mã SelfBooth dùng lại đến khi hết hạn, gom ảnh vào một album chung và chỉ tạo ảnh tĩnh.";
        public string CreateButtonText => IsPhotoBooth ? "Tạo mã PhotoBooth" : "Tạo mã SelfBooth";
        public string CodeHeading => IsPhotoBooth ? "MÃ PHOTOBOOTH" : "MÃ SELFBOOTH";
        public int PhotoCount { get => photoCount; set => Set(ref photoCount, value); }
        public int CountdownSeconds { get => countdownSeconds; set => Set(ref countdownSeconds, value); }
        public DurationChoice Duration { get => duration; set => Set(ref duration, value); }
        public GifDurationChoice GifDuration { get => gifDuration; set => Set(ref gifDuration, value); }
        public int ResultSeconds { get => resultSeconds; set => Set(ref resultSeconds, value); }
        public int PrintCopies { get => printCopies; set => Set(ref printCopies, value); }
        public string Code { get => code; private set { if (Set(ref code, value)) Raise(nameof(HasCode)); } }
        public bool HasCode => !string.IsNullOrWhiteSpace(Code);
        public string AlbumUrl { get => albumUrl; private set => Set(ref albumUrl, value); }
        public byte[] AlbumQr { get => albumQr; private set { if (Set(ref albumQr, value)) Raise(nameof(HasAlbumQr)); } }
        public bool HasAlbumQr => AlbumQr != null && AlbumQr.Length > 0;
        public DateTime? ExpiresAt { get => expiresAt; private set => Set(ref expiresAt, value); }
        public string Message { get => message; private set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public ICommand CreateCommand { get; }

        async Task CreateAsync()
        {
            Message = null;
            ClearCreatedCode();
            try
            {
                CreatedBoothAccessCode created;
                if (IsSelfBooth)
                {
                    created = await service.CreateSelfBoothPassAsync(new CreateSelfBoothPassRequest
                    {
                        DurationMinutes = Duration?.Minutes ?? 120,
                        Settings = CurrentSettings(0)
                    }, CancellationToken.None);
                }
                else
                {
                    created = await service.CreatePhotoBoothCodeAsync(new CreatePhotoBoothCodeRequest
                    {
                        Settings = CurrentSettings(GifDuration?.Milliseconds ?? 800)
                    }, CancellationToken.None);
                }
                Code = created.Code;
                ExpiresAt = created.ExpiresAtUtc.ToLocalTime();
                AlbumUrl = created.AlbumUrl;
                if (created.CodeType == BoothAccessCodeType.SelfBooth && Uri.TryCreate(created.AlbumUrl, UriKind.Absolute, out var albumUri))
                    AlbumQr = await qr.GeneratePngAsync(albumUri, 320, CancellationToken.None);
                Message = created.CodeType == BoothAccessCodeType.SelfBooth
                    ? "Mã SelfBooth chỉ hiển thị tại đây một lần, dùng lại đến khi hết hạn và dùng chung một album."
                    : "Mã PhotoBooth chỉ hiển thị tại đây một lần, dùng cho một phiên chụp và hết hạn sau 48 giờ.";
            }
            catch (Exception e) { Message = e.Message; }
        }

        BoothAccessSettings CurrentSettings(int gifFrameDurationMilliseconds) => new BoothAccessSettings
        {
            PhotoCount = PhotoCount,
            CountdownSeconds = CountdownSeconds,
            GifFrameDurationMilliseconds = gifFrameDurationMilliseconds,
            ResultDisplaySeconds = ResultSeconds,
            PrintCopies = PrintCopies
        };

        void ClearCreatedCode()
        {
            Code = null;
            ExpiresAt = null;
            AlbumUrl = null;
            AlbumQr = null;
        }

        public sealed class DurationChoice
        {
            public DurationChoice(string label, int minutes) { Label = label; Minutes = minutes; }
            public string Label { get; }
            public int Minutes { get; }
            public override string ToString() => Label;
        }

        public sealed class GifDurationChoice
        {
            public GifDurationChoice(string label, int milliseconds) { Label = label; Milliseconds = milliseconds; }
            public string Label { get; }
            public int Milliseconds { get; }
            public override string ToString() => Label;
        }
    }
}
