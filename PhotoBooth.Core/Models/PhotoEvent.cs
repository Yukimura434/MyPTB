using System;

namespace PhotoBooth.Core.Models
{
    /// <summary>Operator-managed business scope that groups many customer booth sessions.</summary>
    public sealed class PhotoEvent
    {
        public Guid Id { get; set; }
        public Guid? PresetId { get; set; }
        public string Name { get; set; }
        /// <summary>Operator-selected folder that receives exported media for this event.</summary>
        public string OutputDirectory { get; set; }
        public bool IsDefault { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public override string ToString() => Name ?? string.Empty;
    }
}
