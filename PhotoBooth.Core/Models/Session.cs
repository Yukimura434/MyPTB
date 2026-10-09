using System;
using System.Collections.Generic;

namespace PhotoBooth.Core.Models
{
    /// <summary>
    /// Application projection shared by the Event and BoothSession workflows.
    /// Persistence maps each kind to its separate canonical v2 table.
    /// </summary>
    public class Session
    {
        public Guid Id { get; set; }
        public Guid? PresetId { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string OutputDirectory { get; set; }
        public IReadOnlyList<string> CapturedFiles { get; set; }
        public IReadOnlyList<string> CapturedVideoFiles { get; set; }
        public IReadOnlyList<CapturedShot> CapturedShots { get; set; }
        public string FinalImagePath { get; set; }
        public string SessionName { get; set; }
        public int SessionNumber { get; set; }
        public IReadOnlyList<string> CapturedImageIds { get; set; }
        public bool IsDefault { get; set; }
        public int CaptureIndex { get; set; }
        public int FrameIndex { get; set; }
        public string FinalImageId { get; set; }
        public string Kind { get; set; } = SessionKinds.Event;
        public Guid? EventId { get; set; }
        public string RemoteEventId { get; set; }
        public string EventLifecycleState { get; set; } = PhotoEventLifecycleStates.Draft;
        public DateTime? EventScheduledStartAtUtc { get; set; }
        public DateTime? EventScheduledEndAtUtc { get; set; }
        public DateTime? EventOpenedAtUtc { get; set; }
        public DateTime? EventClosedAtUtc { get; set; }
        public DateTime? EventDeliveredAtUtc { get; set; }
        public DateTime? EventArchivedAtUtc { get; set; }
        public string ConfigurationVersionId { get; set; }
        public string Status { get; set; } = BoothSessionStates.Active;
        public long StateVersion { get; set; }
        public string TerminalReason { get; set; }
        public string DisplayCode { get; set; }
        public long BasePriceMinor { get; set; }
        public long AdditionalPrintPriceMinor { get; set; }
        public string Currency { get; set; } = "VND";
        public DateTime UpdatedAtUtc { get; set; }
        public bool IsBoothSession => string.Equals(Kind, SessionKinds.Booth, StringComparison.Ordinal);
        public override string ToString() => SessionName ?? string.Empty;
    }

    /// <summary>One independent customer turn inside a photo event.</summary>
    public sealed class BoothSession : Session
    {
        public static BoothSession From(Session value)
        {
            if (value == null) return null;
            var existing = value as BoothSession;
            if (existing != null) return existing;
            return new BoothSession
            {
                Id = value.Id,
                PresetId = value.PresetId,
                StartedAtUtc = value.StartedAtUtc,
                CompletedAtUtc = value.CompletedAtUtc,
                OutputDirectory = value.OutputDirectory,
                CapturedFiles = value.CapturedFiles,
                CapturedVideoFiles = value.CapturedVideoFiles,
                CapturedShots = value.CapturedShots,
                FinalImagePath = value.FinalImagePath,
                SessionName = value.SessionName,
                SessionNumber = value.SessionNumber,
                CapturedImageIds = value.CapturedImageIds,
                IsDefault = value.IsDefault,
                CaptureIndex = value.CaptureIndex,
                FrameIndex = value.FrameIndex,
                FinalImageId = value.FinalImageId,
                Kind = value.Kind,
                EventId = value.EventId,
                RemoteEventId = value.RemoteEventId,
                EventLifecycleState = value.EventLifecycleState,
                EventScheduledStartAtUtc = value.EventScheduledStartAtUtc,
                EventScheduledEndAtUtc = value.EventScheduledEndAtUtc,
                EventOpenedAtUtc = value.EventOpenedAtUtc,
                EventClosedAtUtc = value.EventClosedAtUtc,
                EventDeliveredAtUtc = value.EventDeliveredAtUtc,
                EventArchivedAtUtc = value.EventArchivedAtUtc,
                ConfigurationVersionId = value.ConfigurationVersionId,
                Status = value.Status,
                StateVersion = value.StateVersion,
                TerminalReason = value.TerminalReason,
                DisplayCode = value.DisplayCode,
                BasePriceMinor = value.BasePriceMinor,
                AdditionalPrintPriceMinor = value.AdditionalPrintPriceMinor,
                Currency = value.Currency,
                UpdatedAtUtc = value.UpdatedAtUtc
            };
        }
    }
}
