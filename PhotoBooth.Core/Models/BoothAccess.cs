using System;

namespace PhotoBooth.Core.Models
{
    public enum BoothAccessCodeType
    {
        PhotoBooth = 0,
        SelfBooth = 1,
        Event = 2
    }

    public sealed class BoothAccessSettings
    {
        public int PhotoCount { get; set; } = 8;
        public int CountdownSeconds { get; set; } = 3;
        public int GifFrameDurationMilliseconds { get; set; } = 800;
        public int ResultDisplaySeconds { get; set; } = 30;
        public int PrintCopies { get; set; } = 1;
        public int? TotalPrintCopies { get; set; }
    }

    public sealed class BoothAccessAuthorization
    {
        public BoothAccessCodeType? CodeType { get; set; }
        public bool IsOneTimeCode => CodeType == BoothAccessCodeType.PhotoBooth;
        public bool IsReusableSelfBooth => CodeType == BoothAccessCodeType.SelfBooth;
        public bool IsEvent => CodeType == BoothAccessCodeType.Event;
        public bool UsesAccessCodeSettings => CodeType.HasValue;
        public string AccessCodeId { get; set; }
        public string AlbumUrl { get; set; }
        public string EventId { get; set; }
        public string BoothSessionId { get; set; }
        public string SessionToken { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string DisplayName { get; set; }
        public BoothAccessSettings Settings { get; set; } = new BoothAccessSettings();
    }

    public sealed class BoothAccessResult
    {
        public bool Succeeded { get; set; }
        public string Error { get; set; }
        public BoothAccessAuthorization Authorization { get; set; }
    }

    public sealed class CreatePhotoBoothCodeRequest
    {
        public string EventId { get; set; }
        public string LocationId { get; set; }
        public BoothAccessSettings Settings { get; set; } = new BoothAccessSettings();
    }

    public sealed class CreateSelfBoothPassRequest
    {
        public string EventId { get; set; }
        public string LocationId { get; set; }
        public int DurationMinutes { get; set; } = 120;
        public BoothAccessSettings Settings { get; set; } = new BoothAccessSettings
        {
            GifFrameDurationMilliseconds = 0,
            PrintCopies = 0
        };
    }

    public sealed class CreatedBoothAccessCode
    {
        public BoothAccessCodeType CodeType { get; set; }
        public string Id { get; set; }
        public string Code { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string AlbumUrl { get; set; }
        public BoothAccessSettings Settings { get; set; }
    }
}
