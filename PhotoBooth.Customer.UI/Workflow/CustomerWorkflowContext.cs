using System;
using System.Collections.Generic;
using System.IO;
using PhotoBooth.Core.Cameras;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Customer.UI.Workflow
{
    public sealed class CustomerWorkflowContext
    {
        public Settings Settings { get; set; }
        public bool VideoEnabled => Settings?.CreateVideo == true && Authorization?.IsReusableSelfBooth != true && Authorization?.IsEvent != true;
        public BoothAccessAuthorization Authorization { get; set; }
        public PcSession PreviousPcSession { get; set; }
        public PcSession ManagerSession { get; set; }
        public bool RecoveringDelivery { get; set; }
        public int RequestedPrintCopies { get; set; } = 1;
        public int MaximumPrintCopies { get; set; } = 10;
        public EventPrintPolicyMode PrintPolicyMode { get; set; } = EventPrintPolicyMode.Manual;
        public bool UsePresetStrength { get; set; }
        public float PresetStrength(Preset preset) => UsePresetStrength && preset != null
            ? Math.Max(0, Math.Min(100, preset.StrengthPercent)) / 100f : ColorPresetStrength;
        public float ColorPresetStrength { get; set; } = 0.5f;
        public BoothSession BoothSession { get; set; }
        public string DeliverableId { get; set; }
        public string WorkingDirectory { get; set; }
        public List<CapturedShot> CurrentShots { get; set; } = new List<CapturedShot>();
        public List<PendingCapture> PendingCaptures { get; set; } = new List<PendingCapture>();
        public string PreferredCameraId { get; set; }
        public CameraInfo Camera { get; set; }
        public PhotoEvent EventSnapshot { get; set; }
        public IReadOnlyList<Frame> AvailableFrames { get; set; } = new Frame[0];
        public IReadOnlyList<Preset> AvailablePresets { get; set; } = new Preset[0];
        public IReadOnlyDictionary<Guid, string> LutSnapshotFiles { get; set; } = new Dictionary<Guid, string>();
        public string ConfigurationSnapshotDirectory { get; set; }
        public Frame SelectedFrame { get; set; }
        public Preset DefaultPreset { get; set; }
        public Preset LiveViewPreset { get; set; }
        public bool CameraDisconnectedPending { get; set; }
        public bool PrintingEnabled { get; set; }
        public string ConnectedPrinterId { get; set; }
        public Guid? ConnectedPrinterProfileId { get; set; }

        public void ResetRunSnapshot()
        {
            RecoveringDelivery = false;
            DisposeConfigurationSnapshot();
            Settings = null;
            EventSnapshot = null;
            AvailableFrames = new Frame[0];
            AvailablePresets = new Preset[0];
            LutSnapshotFiles = new Dictionary<Guid, string>();
            SelectedFrame = null;
            DefaultPreset = null;
            LiveViewPreset = null;
            RequestedPrintCopies = 1;
            MaximumPrintCopies = 10;
            PrintPolicyMode = EventPrintPolicyMode.Manual;
            ColorPresetStrength = 0.5f;
            UsePresetStrength = false;
            PrintingEnabled = false;
            ConnectedPrinterId = null;
            ConnectedPrinterProfileId = null;
        }

        public void DisposeConfigurationSnapshot()
        {
            var directory = ConfigurationSnapshotDirectory;
            ConfigurationSnapshotDirectory = null;
            if (string.IsNullOrWhiteSpace(directory)) return;
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { }
        }

        public void ResetForNewBoothSession()
        {
            ResetRunSnapshot();
            Authorization = null;
            PreviousPcSession = null;
            BoothSession = null;
            DeliverableId = null;
            WorkingDirectory = null;
            CurrentShots?.Clear();
            PendingCaptures?.Clear();
            Camera = null;
            CameraDisconnectedPending = false;
        }
    }
}
