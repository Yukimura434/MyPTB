using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    public enum ColorLutAssetStatus { Staging, Ready, Missing, Corrupt, PendingDelete }

    public sealed class ColorLutAsset
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; }
        public string RelativePath { get; set; }
        public string ContentHashSha256 { get; set; }
        public long FileLength { get; set; }
        public int CubeSize { get; set; }
        public float DomainMinR { get; set; }
        public float DomainMinG { get; set; }
        public float DomainMinB { get; set; }
        public float DomainMaxR { get; set; }
        public float DomainMaxG { get; set; }
        public float DomainMaxB { get; set; }
        public ColorLutAssetStatus Status { get; set; }
        public int ValidationVersion { get; set; } = 1;
        public DateTime LastValidatedAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ModifiedAtUtc { get; set; }
        public long RowVersion { get; set; } = 1;
        public bool SupportsLiveView => CubeSize <= 65;
    }

    public sealed class PresetColorSettings
    {
        public Guid PresetId { get; set; }
        public Guid? LutAssetId { get; set; }
        public float Strength { get; set; } = 1f;
        public bool Enabled { get; set; } = true;
        public DateTime ModifiedAtUtc { get; set; }
        public long RowVersion { get; set; } = 1;
    }

    public sealed class ColorLutMetadata
    {
        public string Title { get; set; }
        public int CubeSize { get; set; }
        public float DomainMinR { get; set; }
        public float DomainMinG { get; set; }
        public float DomainMinB { get; set; }
        public float DomainMaxR { get; set; } = 1f;
        public float DomainMaxG { get; set; } = 1f;
        public float DomainMaxB { get; set; } = 1f;
    }

    public sealed class ColorLutValidationResult
    {
        public bool IsValid { get; set; }
        public ColorLutMetadata Metadata { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = new string[0];
        public IReadOnlyList<string> Warnings { get; set; } = new string[0];
    }

    public sealed class ColorLutImportResult
    {
        public ColorLutAsset Asset { get; set; }
        public bool WasDuplicate { get; set; }
        public IReadOnlyList<string> Warnings { get; set; } = new string[0];
    }
}
