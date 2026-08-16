using System;
using System.Collections.Generic;
using CameraControl.Devices.Classes;
using CameraControl.Devices.Nikon;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CameraModelResolverTests
    {
        [Theory]
        [InlineData("Canon EOS R6", null, null, CameraBrand.Canon)]
        [InlineData("Z fc", "NIKON CORPORATION", null, CameraBrand.Nikon)]
        [InlineData("ILCE-7M4", "Sony Corporation", null, CameraBrand.Sony)]
        [InlineData(null, null, "usb#vid_04a9&pid_32", CameraBrand.Canon)]
        [InlineData(null, null, "usb#vid_04b0&pid_44", CameraBrand.Nikon)]
        [InlineData(null, null, "usb#vid_054c&pid_55", CameraBrand.Sony)]
        public void Detects_supported_camera_brands(string model, string maker, string id, CameraBrand expected)
        {
            Assert.Equal(expected, CameraModelResolver.DetectBrand(model, maker, id));
        }

        [Theory]
        [InlineData("Z fc")]
        [InlineData("NIKON Z fc")]
        [InlineData("NIKON CORPORATION Z fc")]
        public void Resolves_nikon_model_variants(string model)
        {
            var drivers = new Dictionary<string, Type> { ["Z fc"] = typeof(NikonZ7) };
            Assert.Equal(typeof(NikonZ7), CameraModelResolver.ResolveNativeDriver(model, drivers));
        }
    }
}
