using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;

namespace PhotoBooth.Business.Services
{
    // Windows driver access stays outside the UI. Legacy PaperType remains the paper source.
    internal static class WindowsPrinterOptions
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PrinterInfo4 { public string Name; public string Server; public uint Attributes; }
        [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool EnumPrinters(uint flags, string name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);
        [DllImport("winspool.drv", EntryPoint="OpenPrinterW", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
        [DllImport("winspool.drv", EntryPoint="GetPrinterW", SetLastError=true)]
        static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buffer, uint size, out uint needed);
        [DllImport("winspool.drv")] static extern bool ClosePrinter(IntPtr handle);

        public static bool IsAvailable(string name)
        {
            IntPtr handle;
            if (!OpenPrinter(name, out handle, IntPtr.Zero)) return false;
            var buffer = IntPtr.Zero;
            try
            {
                uint needed;
                GetPrinter(handle, 6, IntPtr.Zero, 0, out needed);
                if (needed < sizeof(uint)) return false;
                var size = needed;
                buffer = Marshal.AllocHGlobal(checked((int)size));
                if (!GetPrinter(handle, 6, buffer, size, out needed)) return false;
                return (Marshal.ReadInt32(buffer) & 0x80) == 0;
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); ClosePrinter(handle); }
        }

        public static string[] InstalledNames()
        {
            uint needed, count;
            EnumPrinters(6, null, 4, IntPtr.Zero, 0, out needed, out count);
            if (needed == 0) return new string[0];
            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!EnumPrinters(6, null, 4, buffer, needed, out needed, out count)) throw new System.ComponentModel.Win32Exception();
                var names = new List<string>();
                var stride = Marshal.SizeOf(typeof(PrinterInfo4));
                for (var i = 0; i < count; i++) names.Add(((PrinterInfo4)Marshal.PtrToStructure(IntPtr.Add(buffer, i * stride), typeof(PrinterInfo4))).Name);
                return names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static string[] MediaTypes(string name)
        {
            try {
                using (var server = new LocalPrintServer())
                using (var queue = server.GetPrintQueue(name))
                    return queue.GetPrintCapabilities().PageMediaTypeCapability.Where(x=>x!=PageMediaType.Unknown).Select(MediaLabel).Distinct().ToArray();
            }
            catch(PrintSystemException) { return new string[0]; }
            catch(NotSupportedException) { return new string[0]; }
        }

        public static string MediaLabel(PageMediaType type)
        {
            switch (type)
            {
                case PageMediaType.Plain: return "Giấy thường";
                case PageMediaType.Photographic: return "Giấy ảnh";
                case PageMediaType.PhotographicGlossy: return "Giấy ảnh bóng";
                case PageMediaType.PhotographicHighGloss: return "Giấy ảnh siêu bóng";
                case PageMediaType.PhotographicMatte: return "Giấy ảnh mờ";
                case PageMediaType.PhotographicSatin: return "Giấy ảnh bán bóng";
                case PageMediaType.PhotographicSemiGloss: return "Giấy ảnh bán bóng (semi-gloss)";
                case PageMediaType.CardStock: return "Giấy bìa";
                case PageMediaType.EnvelopePlain: return "Phong bì";
                default: return type.ToString();
            }
        }

        public static void ApplyMedia(PrintDocument document, string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return;
            using (var server = new LocalPrintServer())
            using (var queue = server.GetPrintQueue(document.PrinterSettings.PrinterName))
            using (var converter = new PrintTicketConverter(document.PrinterSettings.PrinterName, PrintTicketConverter.MaxPrintSchemaVersion))
            {
                var match = queue.GetPrintCapabilities().PageMediaTypeCapability.Where(x => MediaLabel(x) == label).Select(x => (PageMediaType?)x).FirstOrDefault();
                if (!match.HasValue) throw new InvalidOperationException("Loại giấy đã lưu không được máy in hỗ trợ. Hãy chọn lại loại giấy.");
                var ticket = (queue.UserPrintTicket ?? queue.DefaultPrintTicket).Clone();
                ticket.PageMediaType = match;
                var validated = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
                if (validated.PageMediaType != match) throw new InvalidOperationException("Máy in không chấp nhận loại giấy đã chọn.");
                var bytes = converter.ConvertPrintTicketToDevMode(validated, BaseDevModeType.UserDefault);
                var memory = Marshal.AllocHGlobal(bytes.Length);
                try { Marshal.Copy(bytes, 0, memory, bytes.Length); document.PrinterSettings.SetHdevmode(memory); document.DefaultPageSettings.SetHdevmode(memory); }
                finally { Marshal.FreeHGlobal(memory); }
            }
        }
    }
}
