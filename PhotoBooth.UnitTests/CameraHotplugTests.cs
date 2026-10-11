using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CameraControl.Devices.Others;
using PhotoBooth.Infrastructure.Cameras;
using PortableDeviceLib;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class CameraHotplugTests
    {
        static void WriteIds(IntPtr buffer, params string[] ids)
        {
            for (int i = 0; i < ids.Length; i++)
                Marshal.WriteIntPtr(buffer, i * IntPtr.Size, Marshal.StringToCoTaskMemUni(ids[i]));
        }

        [Fact]
        public void Empty_device_list_queries_zero_capacity_and_never_fills_null_buffer()
        {
            int calls = 0;
            var ids = WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
            {
                calls++; Assert.Equal(IntPtr.Zero, p); Assert.Equal(0u, count); return 0;
            }, () => 0);
            Assert.Empty(ids); Assert.Equal(1, calls);
        }

        [Fact]
        public void App_before_camera_then_unplug_replug_uses_fresh_single_snapshots()
        {
            string[] present = new string[0]; int scans = 0;
            var collection = new PortableDeviceCollection("test", 1, 0, () =>
            {
                scans++;
                return WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
                {
                    if (p == IntPtr.Zero) { Assert.Equal(0u, count); count = (uint)present.Length; }
                    else { Assert.Equal((uint)present.Length, count); WriteIds(p, present); }
                    return 0;
                }, () => 0);
            }) { AutoConnectToPortableDevice = false };
            Assert.Empty(collection.Devices);
            for (int i = 0; i < 250; i++)
            {
                present = new[] { "camera", "tablet" };
                var connected = collection.Devices.ToArray();
                Assert.Equal(2, connected.Length);
                Assert.Same(connected[0], collection.Devices.First());
                present = new[] { "tablet" };
                Assert.Single(collection.Devices);
                present = new[] { "camera", "tablet" };
                Assert.NotSame(connected[0], collection.Devices.First());
            }
            Assert.Equal(1001, scans); // Devices enumerates exactly once per read.
        }

        [Fact]
        public void Hotplug_growth_retries_partial_native_result_and_shrink_uses_written_length()
        {
            int refreshes = 0;
            var ids = WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
            {
                if (p == IntPtr.Zero) { count = refreshes == 1 ? 1u : 3u; return 0; }
                Assert.Equal(refreshes == 1 ? 1u : 3u, count);
                if (refreshes == 1) { WriteIds(p, "old"); count = 1; return 1; }
                WriteIds(p, "new"); count = 1; return 0;
            }, () => { refreshes++; return 0; });
            Assert.Equal(new[] { "new" }, ids); Assert.Equal(2, refreshes);
        }

        [Fact]
        public void Persistent_hotplug_is_bounded_and_failed_snapshot_preserves_cached_device()
        {
            int refreshes = 0;
            Assert.Throws<InvalidOperationException>(() => WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
            {
                if (p == IntPtr.Zero) { count = 1; return 0; }
                WriteIds(p, "partial"); return 1;
            }, () => { refreshes++; return 0; }));
            Assert.Equal(3, refreshes);
            bool fail = false;
            var collection = new PortableDeviceCollection("test", 1, 0, () =>
            {
                if (fail) throw new COMException("Unplug during discovery");
                return new[] { "camera" };
            }) { AutoConnectToPortableDevice = false };
            var device = collection.Devices.Single(); fail = true;
            Assert.Throws<COMException>(() => collection.Devices.ToArray());
            fail = false; Assert.Same(device, collection.Devices.Single());
        }

        [Fact]
        public void Native_failure_and_invalid_counts_are_rejected()
        {
            Assert.Throws<COMException>(() => WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
            {
                if (p == IntPtr.Zero) { count = 2; return 0; }
                WriteIds(p, "partial"); return unchecked((int)0x80004005);
            }, () => 0));
            Assert.Throws<InvalidOperationException>(() => WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
            { count = uint.MaxValue; return 0; }, () => 0));
            Assert.Throws<InvalidOperationException>(() => WpdDeviceEnumeration.Read((IntPtr p, ref uint count) =>
            { if (p == IntPtr.Zero) count = 1; else count = 2; return 0; }, () => 0));
        }

        [Fact]
        public async Task Concurrent_scan_and_connection_share_one_snapshot_and_cache()
        {
            int active = 0, max = 0;
            var collection = new PortableDeviceCollection("test", 1, 0, () =>
            {
                int value = Interlocked.Increment(ref active); max = Math.Max(max, value);
                Thread.Sleep(1); Interlocked.Decrement(ref active); return new[] { "camera" };
            }) { AutoConnectToPortableDevice = false };
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 30; i++) Assert.Single(collection.Devices);
            })));
            Assert.Equal(1, max);
        }

        [Fact]
        public async Task Webcam_worker_notifies_on_owner_dispatcher_and_ignores_deliberate_stop()
        {
            using (var gate = new CameraOperationGate())
            {
                WebCameraDevice camera = null; object source = null; int owner = 0;
                var notified = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var callback = typeof(WebCameraDevice).GetMethod("CaptureDevice_PlayingFinished", flags);
                var reasonType = callback.GetParameters()[1].ParameterType;
                await gate.RunAsync(() =>
                {
                    owner = Thread.CurrentThread.ManagedThreadId;
                    camera = new WebCameraDevice { IsConnected = true };
                    var field = typeof(WebCameraDevice).GetField("_captureDevice", flags);
                    source = Activator.CreateInstance(field.FieldType, new object[] { "synthetic-device" });
                    field.SetValue(camera, source);
                    typeof(WebCameraDevice).GetField("_owner", flags).SetValue(camera, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    camera.CameraDisconnected += (s, e) => notified.TrySetResult(Thread.CurrentThread.ManagedThreadId);
                }, CancellationToken.None);
                await Task.Run(() => callback.Invoke(camera, new[] { source, Enum.Parse(reasonType, "StoppedByUser") }));
                await gate.RunAsync(() => Assert.True(camera.IsConnected), CancellationToken.None);
                Assert.False(notified.Task.IsCompleted);
                await Task.Run(() => callback.Invoke(camera, new[] { source, Enum.Parse(reasonType, "DeviceLost") }));
                Assert.Same(notified.Task, await Task.WhenAny(notified.Task, Task.Delay(5000)));
                Assert.Equal(owner, await notified.Task);
                await gate.RunAsync(camera.Close, CancellationToken.None);
            }
        }

        [Fact]
        public async Task Webcam_unplug_clears_connection_once_and_ignores_late_old_source_events()
        {
            using (var gate = new CameraOperationGate())
            {
                await gate.RunAsync(() =>
                {
                    var camera = new WebCameraDevice();
                    var field = typeof(WebCameraDevice).GetField("_captureDevice", BindingFlags.NonPublic | BindingFlags.Instance);
                    var source = Activator.CreateInstance(field.FieldType, new object[] { "synthetic-device" });
                    field.SetValue(camera, source); camera.IsConnected = true;
                    var ended = typeof(WebCameraDevice).GetMethod("HandleSourceFinished", BindingFlags.NonPublic | BindingFlags.Instance);
                    int events = 0; camera.CameraDisconnected += (s, e) => events++;
                    ended.Invoke(camera, new[] { new object() });
                    Assert.True(camera.IsConnected); Assert.Equal(0, events);
                    ended.Invoke(camera, new[] { source }); ended.Invoke(camera, new[] { source });
                    Assert.False(camera.IsConnected); Assert.Equal(1, events); Assert.Null(camera.GetLiveViewImage());
                    Assert.Throws<InvalidOperationException>(() => camera.CapturePhoto());
                    camera.Close(); camera.Close(); camera.StopLiveView();
                    Assert.Throws<InvalidOperationException>(() => camera.StartLiveView());
                }, CancellationToken.None);
            }
        }
    }
}
