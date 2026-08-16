using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Persistence;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Business.Services
{
    public sealed class PrinterService : IPrinterService
    {
        readonly IPrinterProfileRepository profiles;
        public PrinterService(IPrinterProfileRepository profiles) { this.profiles = profiles; }

        public async Task<IReadOnlyList<string>> GetPrintersAsync(CancellationToken token) =>
            (await ScanAsync(token)).Select(x => x.Name).ToList();

        public Task<IReadOnlyList<DiscoveredPrinter>> ScanAsync(CancellationToken token) => Task.Run<IReadOnlyList<DiscoveredPrinter>>(() =>
        {
            token.ThrowIfCancellationRequested();
            var result = new List<DiscoveredPrinter>();
            using (var searcher = new ManagementObjectSearcher("SELECT Name,PortName,DriverName,WorkOffline,PrinterStatus FROM Win32_Printer"))
            using (var printers = searcher.Get())
            foreach (ManagementObject item in printers)
            {
                token.ThrowIfCancellationRequested();
                var name = Convert.ToString(item["Name"]);
                var port = Convert.ToString(item["PortName"]);
                var driver = Convert.ToString(item["DriverName"]);
                var offline = Convert.ToBoolean(item["WorkOffline"] ?? false);
                var status = Convert.ToInt32(item["PrinterStatus"] ?? 0);
                // PrinterStatus 7 means offline. Do not mix saved profiles into discovery.
                if (string.IsNullOrWhiteSpace(name) || offline || status == 7) continue;
                try { result.Add(Describe(name, port, driver)); } catch { }
            }
            return result.OrderBy(x => x.Name).ToList();
        }, token);

        public async Task<DiscoveredPrinter> ConnectAsync(string printerId, CancellationToken token)
        {
            var printer = (await ScanAsync(token)).SingleOrDefault(x => string.Equals(x.Id, printerId, StringComparison.OrdinalIgnoreCase));
            if (printer == null) throw new InvalidOperationException("Printer is no longer available. Scan again.");
            return printer;
        }

        public async Task<bool> IsConnectedAsync(string printerId, CancellationToken token) =>
            !string.IsNullOrWhiteSpace(printerId) && (await ScanAsync(token)).Any(x => string.Equals(x.Id, printerId, StringComparison.OrdinalIgnoreCase));

        public Task<IReadOnlyList<PrinterProfile>> GetProfilesAsync(CancellationToken token) => profiles.GetAllAsync(token);
        public Task SaveProfileAsync(PrinterProfile profile, CancellationToken token) => profiles.SaveAsync(profile, token);
        public Task DeleteProfileAsync(Guid id, CancellationToken token) => profiles.DeleteAsync(id, token);

        public Task PrintAsync(PrintJob job, CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(job.PrinterName)) throw new InvalidOperationException("Printer is not selected.");
            using (var document = new PrintDocument())
            {
                document.PrinterSettings.PrinterName = job.PrinterName;
                document.PrinterSettings.Copies = (short)Math.Max(1, job.Copies);
                if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("Printer offline or disconnected.");
                document.DefaultPageSettings.Landscape = job.Landscape;
                document.DefaultPageSettings.Color = job.PrintInColor && document.PrinterSettings.SupportsColor;
                if (!job.UseDefaultBorder)
                {
                    // Ask the driver for the full physical sheet. The selected paper
                    // size must itself support borderless output in the printer driver.
                    document.OriginAtMargins = false;
                    document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
                }
                var paper = document.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(x => PrinterPaperNames.Match(x.PaperName, job.PaperSize));
                if (paper == null && !string.IsNullOrWhiteSpace(job.PaperSize)) throw new InvalidOperationException("Saved paper size '" + job.PaperSize + "' is not supported by the current printer driver.");
                if (paper != null) document.DefaultPageSettings.PaperSize = paper;
                var source = document.PrinterSettings.PaperSources.Cast<PaperSource>().FirstOrDefault(x => string.Equals(x.SourceName, job.PaperType, StringComparison.OrdinalIgnoreCase));
                if (source != null) document.DefaultPageSettings.PaperSource = source;
                var resolution = document.PrinterSettings.PrinterResolutions.Cast<PrinterResolution>().FirstOrDefault(x => ResolutionName(x) == job.Quality);
                if (resolution != null) document.DefaultPageSettings.PrinterResolution = resolution;
                document.PrintPage += (s, e) => Draw(job, e);
                document.Print();
            }
        }, token);

        static DiscoveredPrinter Describe(string name, string port, string driver)
        {
            var settings = new PrinterSettings { PrinterName = name };
            {
                if (!settings.IsValid) throw new InvalidOperationException();
                return new DiscoveredPrinter
                {
                    Id = StableId(name, port, driver), Name = name, PortName = port, DriverName = driver,
                    ConnectionType = ConnectionType(port), IsOnline = true,
                    SupportsColor = settings.SupportsColor, SupportsDuplex = settings.CanDuplex,
                    PaperSizes = settings.PaperSizes.Cast<PaperSize>().Select(x => x.PaperName).Distinct().ToArray(),
                    PaperSources = settings.PaperSources.Cast<PaperSource>().Select(x => x.SourceName).Distinct().ToArray(),
                    Resolutions = settings.PrinterResolutions.Cast<PrinterResolution>().Select(ResolutionName).Distinct().ToArray()
                };
            }
        }

        static string StableId(string name, string port, string driver) => (name + "|" + port + "|" + driver).Trim().ToUpperInvariant();
        static string ConnectionType(string port) { var p = (port ?? "").ToUpperInvariant(); if (p.Contains("USB")) return "USB"; if (p.Contains("WSD")) return "Wi-Fi Direct / WSD"; if (p.StartsWith("IP_") || p.StartsWith("TCP") || p.StartsWith("\\\\")) return "Local network"; return "Windows printer"; }
        static string ResolutionName(PrinterResolution x) => x.Kind != PrinterResolutionKind.Custom ? x.Kind.ToString() : x.X + " x " + x.Y + " dpi";
        static void Draw(PrintJob job, PrintPageEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(job.FilePath) && System.IO.File.Exists(job.FilePath))
                using (var image = Image.FromFile(job.FilePath))
                {
                    if (job.UseDefaultBorder)
                    {
                        var bounds = e.MarginBounds;
                        var scale = Math.Min(bounds.Width / (double)image.Width, bounds.Height / (double)image.Height);
                        var width = (int)(image.Width * scale); var height = (int)(image.Height * scale);
                        e.Graphics.DrawImage(image, bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
                    }
                    else
                    {
                        // Graphics starts at the printer's printable origin. Move back
                        // by the hard margin, then crop-to-fill so no white band can be
                        // introduced when the image and paper have different ratios.
                        var target = new RectangleF(
                            -e.PageSettings.HardMarginX,
                            -e.PageSettings.HardMarginY,
                            e.PageBounds.Width,
                            e.PageBounds.Height);
                        var targetRatio = target.Width / target.Height;
                        var imageRatio = image.Width / (float)image.Height;
                        RectangleF source;
                        if (imageRatio > targetRatio)
                        {
                            var sourceWidth = image.Height * targetRatio;
                            source = new RectangleF((image.Width - sourceWidth) / 2f, 0, sourceWidth, image.Height);
                        }
                        else
                        {
                            var sourceHeight = image.Width / targetRatio;
                            source = new RectangleF(0, (image.Height - sourceHeight) / 2f, image.Width, sourceHeight);
                        }
                        e.Graphics.DrawImage(image, target, source, GraphicsUnit.Pixel);
                    }
                }
            else using (var title = new Font("Segoe UI", 22, FontStyle.Bold)) using (var body = new Font("Segoe UI", 11))
            { e.Graphics.DrawString("PhotoBooth Test Print", title, Brushes.Black, 60, 70); e.Graphics.DrawString("Printer: " + job.PrinterName + Environment.NewLine + "Time: " + DateTime.Now.ToString("G"), body, Brushes.Black, 60, 125); }
        }
    }
}
