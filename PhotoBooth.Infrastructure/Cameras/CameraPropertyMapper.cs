using System;
using System.Collections.Generic;
using System.Linq;
using CameraControl.Devices;
using CameraControl.Devices.Classes;
using PhotoBooth.Core.Cameras;

namespace PhotoBooth.Infrastructure.Cameras
{
    internal static class CameraPropertyMapper
    {
        public static CameraProperties Map(ICameraDevice camera) => new CameraProperties(new[]
        {
            Map(CameraPropertyKind.CameraMode, camera.Mode),
            Map(CameraPropertyKind.Iso, camera.IsoNumber),
            Map(CameraPropertyKind.ShutterSpeed, camera.ShutterSpeed),
            Map(CameraPropertyKind.Aperture, camera.FNumber),
            Map(CameraPropertyKind.WhiteBalance, camera.WhiteBalance),
            Map(CameraPropertyKind.FocusMode, camera.FocusMode),
            Map(CameraPropertyKind.MeteringMode, camera.ExposureMeteringMode),
            Map(CameraPropertyKind.Compression, camera.CompressionSetting),
            Map(CameraPropertyKind.ExposureCompensation, camera.ExposureCompensation)
        });

        public static PropertyValue<long> Resolve(ICameraDevice camera, CameraPropertyKind kind)
        {
            switch (kind)
            {
                case CameraPropertyKind.CameraMode: return camera.Mode;
                case CameraPropertyKind.Iso: return camera.IsoNumber;
                case CameraPropertyKind.ShutterSpeed: return camera.ShutterSpeed;
                case CameraPropertyKind.Aperture: return camera.FNumber;
                case CameraPropertyKind.WhiteBalance: return camera.WhiteBalance;
                case CameraPropertyKind.FocusMode: return camera.FocusMode;
                case CameraPropertyKind.MeteringMode: return camera.ExposureMeteringMode;
                case CameraPropertyKind.Compression: return camera.CompressionSetting;
                case CameraPropertyKind.ExposureCompensation: return camera.ExposureCompensation;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static CameraProperty Map(CameraPropertyKind kind, PropertyValue<long> source) => new CameraProperty
        {
            Kind = kind,
            Value = source?.Value,
            NumericValue = source == null ? 0 : source.NumericValue,
            AllowedValues = source == null ? new List<string>() : source.Values.ToList(),
            IsAvailable = source != null && source.Available,
            IsReadOnly = source == null || !source.IsEnabled
        };
    }
}
