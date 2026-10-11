#region License
/*
PortableDeviceCollection.cs
Copyright (C) 2009 Vincent Lainé
 
This library is free software; you can redistribute it and/or
modify it under the terms of the GNU Lesser General Public
License as published by the Free Software Foundation; either
version 2.1 of the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
Lesser General Public License for more details.

You should have received a copy of the GNU Lesser General Public
License along with this library; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
*/
#endregion
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using PortableDeviceApiLib;

namespace PortableDeviceLib
{
    public class PortableDeviceCollection
    {
        private static PortableDeviceCollection _instance;
        private static readonly object instanceLock = new object();
        private readonly object sync = new object();
        private readonly Dictionary<string, PortableDevice> portableDevices = new Dictionary<string, PortableDevice>();
        private readonly Func<string[]> enumerate;
        private readonly string appName;
        private readonly float majorVersion, minorVersion;

        private PortableDeviceCollection(string appName, float majorVersion, float minorVersion)
            : this(appName, majorVersion, minorVersion, WpdDeviceEnumeration.Read) { }

        internal PortableDeviceCollection(string appName, float majorVersion, float minorVersion, Func<string[]> enumerate)
        {
            if (string.IsNullOrEmpty(appName)) throw new ArgumentNullException("appName");
            this.appName = appName;
            this.majorVersion = majorVersion;
            this.minorVersion = minorVersion;
            this.enumerate = enumerate;
            AutoConnectToPortableDevice = true;
        }

        public bool AutoConnectToPortableDevice { get; set; }
        public int Count { get { return InternalGetDeviceIds().Length; } }
        public IEnumerable<string> DeviceIds { get { return InternalGetDeviceIds(); } }
        public IEnumerable<PortableDevice> Devices
        {
            get
            {
                // One snapshot: unplug between two enumerations must not change this iteration.
                lock (sync) return InternalGetDeviceIds().Select(GetPortableDeviceById).ToArray();
            }
        }
        public static PortableDeviceCollection Instance { get { lock (instanceLock) return _instance; } }

        public PortableDevice GetPortableDeviceById(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
            lock (sync)
            {
                PortableDevice device;
                if (!portableDevices.TryGetValue(id, out device))
                {
                    device = new PortableDevice(id);
                    if (AutoConnectToPortableDevice) device.ConnectToDevice(appName, majorVersion, minorVersion);
                    portableDevices.Add(id, device);
                }
                return device;
            }
        }

        public void RemoveDevice(string id) { lock (sync) portableDevices.Remove(id); }

        public static PortableDeviceCollection CreateInstance(string appName, float majorVersion, float minorVersion)
        {
            if (string.IsNullOrEmpty(appName)) throw new ArgumentNullException("appName");
            lock (instanceLock)
            {
                if (_instance == null) _instance = new PortableDeviceCollection(appName, majorVersion, minorVersion);
                return _instance;
            }
        }

        public void RefreshDevices() { InternalGetDeviceIds(); }

        private string[] InternalGetDeviceIds()
        {
            lock (sync)
            {
                // No shared count/buffer across discovery and connection, and no persistent
                // COM manager created on one apartment then reused on another.
                var ids = enumerate();
                foreach (var id in portableDevices.Keys.Where(id => !ids.Contains(id)).ToArray())
                    portableDevices.Remove(id);
                return ids;
            }
        }
    }

    // The bundled type-library string[] declaration does not describe the native
    // pointer-array ownership reliably. Use the first two IPortableDeviceManager
    // vtable slots explicitly; each returned LPWSTR belongs to CoTaskMemFree.
    [ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWpdDeviceManager
    {
        [PreserveSig] int GetDevices(IntPtr deviceIds, ref uint count);
        [PreserveSig] int RefreshDeviceList();
    }

    internal static class WpdDeviceEnumeration
    {
        internal delegate int GetDevices(IntPtr buffer, ref uint count);
        private const uint MaximumDevices = 4096;

        internal static string[] Read()
        {
            var manager = (IWpdDeviceManager)(object)new PortableDeviceManagerClass();
            try { return Read(manager.GetDevices, manager.RefreshDeviceList); }
            finally { Marshal.ReleaseComObject(manager); }
        }

        internal static string[] Read(GetDevices getDevices, Func<int> refresh)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Marshal.ThrowExceptionForHR(refresh());
                uint capacity = 0;
                Marshal.ThrowExceptionForHR(getDevices(IntPtr.Zero, ref capacity));
                if (capacity == 0) return new string[0];
                if (capacity > MaximumDevices) throw new InvalidOperationException("Invalid WPD device count.");
                int slots = checked((int)capacity);
                IntPtr buffer = Marshal.AllocCoTaskMem(checked(slots * IntPtr.Size));
                for (int i = 0; i < slots; i++) Marshal.WriteIntPtr(buffer, i * IntPtr.Size, IntPtr.Zero);
                try
                {
                    uint written = capacity;
                    int result = getDevices(buffer, ref written);
                    Marshal.ThrowExceptionForHR(result);
                    if (written > capacity) throw new InvalidOperationException("Invalid WPD result length.");
                    // S_FALSE means the list grew. Retry with a fresh bounded snapshot.
                    if (result == 1) continue;
                    var ids = new List<string>();
                    for (int i = 0; i < written; i++)
                    {
                        IntPtr value = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                        if (value != IntPtr.Zero)
                        {
                            string id = Marshal.PtrToStringUni(value);
                            if (!string.IsNullOrEmpty(id)) ids.Add(id);
                        }
                    }
                    return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
                finally
                {
                    // Also free partial results on HRESULT failure or hotplug retry.
                    for (int i = 0; i < slots; i++)
                    {
                        IntPtr value = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                        if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value);
                    }
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
            throw new InvalidOperationException("Portable device list is changing. Retry discovery.");
        }
    }
}
