using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;
using PhotoBooth.Infrastructure;
using PhotoBooth.Shared;

namespace PhotoBooth.Admin.UI
{
    internal static class DeliverySmoke
    {
        const string DefaultApiBaseUrl = "https://api.miuphotobooth.com";
        const string ReportFileName = "PhotoBooth-DeliverySmoke-last.txt";

        public static int Run()
        {
            var reportPath = Path.Combine(Path.GetTempPath(), ReportFileName);
            var root = Path.Combine(Path.GetTempPath(), "PhotoBooth-DeliverySmoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var galleryUrl = RunAsync(root).GetAwaiter().GetResult();
                File.WriteAllText(reportPath, "status=passed" + Environment.NewLine + "upload=verified" + Environment.NewLine + "gallery=verified" + Environment.NewLine, Encoding.UTF8);
                return 0;
            }
            catch (Exception exception)
            {
                File.WriteAllText(reportPath, "status=failed" + Environment.NewLine + "error=" + Safe(exception.Message) + Environment.NewLine, Encoding.UTF8);
                return 1;
            }
            finally
            {
                try { SqliteConnection.ClearAllPools(); } catch { }
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        static async Task<Uri> RunAsync(string root)
        {
            var databasePath = Path.Combine(root, "delivery-smoke.db");
            var options = new ApplicationOptions
            {
                ApplicationName = "PhotoBooth.DeliverySmoke",
                DataDirectory = root,
                DatabasePath = databasePath,
                UseFakeCamera = true,
                RestartLiveViewDuringRecovery = false,
                PhotoApiBaseUrl = Environment.GetEnvironmentVariable("PHOTOBOOTH_API_BASE_URL") ?? DefaultApiBaseUrl,
                UploadMaxRetries = 1,
                UploadTimeoutSeconds = 120
            };
            options.Features["Video"] = false;
            options.Features["VideoNativeEncoder"] = false;

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            services.AddPhotoBoothInfrastructure(options);
            using (var provider = services.BuildServiceProvider())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                provider.InitializePhotoBooth();
                var ownerEmail = RequiredEnvironment("PHOTOBOOTH_SMOKE_OWNER_EMAIL");
                var ownerPassword = RequiredEnvironment("PHOTOBOOTH_SMOKE_OWNER_PASSWORD");
                var employeeUsername = RequiredEnvironment("PHOTOBOOTH_SMOKE_EMPLOYEE_USERNAME");
                var employeePin = RequiredEnvironment("PHOTOBOOTH_SMOKE_EMPLOYEE_PIN");
                var locationId = RequiredEnvironment("PHOTOBOOTH_SMOKE_LOCATION_ID");
                await provider.GetRequiredService<IDeviceIdentityService>().ActivateV3Async(ownerEmail, ownerPassword, "Delivery Smoke", locationId, "3.0.0-smoke", timeout.Token).ConfigureAwait(false);
                var authentication = await provider.GetRequiredService<IPcAuthenticationService>().AuthenticateAsync(employeeUsername, employeePin, false, timeout.Token).ConfigureAwait(false);
                if (!authentication.Succeeded || authentication.Session == null) throw new InvalidOperationException(authentication.Error ?? "Employee smoke sign-in failed.");
                provider.GetRequiredService<IPcSessionContext>().SignIn(authentication.Session);
                var identity = await provider.GetRequiredService<IDeviceIdentityService>().GetAsync(timeout.Token).ConfigureAwait(false);
                if (identity == null || identity.EnrollmentState != DeviceEnrollmentState.Enrolled)
                    throw new InvalidOperationException("This PC is not enrolled for cloud delivery.");

                var eventService = provider.GetRequiredService<IEventService>();
                var eventDraft = await eventService.CreateDraftAsync(null, timeout.Token).ConfigureAwait(false);
                eventDraft.Name = "Delivery smoke " + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                eventDraft.OutputDirectory = Path.Combine(root, "SmokeEvent");
                var photoEvent = await eventService.CreateAsync(eventDraft, timeout.Token).ConfigureAwait(false);
                var boothSession = await provider.GetRequiredService<IBoothSessionService>().StartAsync(photoEvent.Id, null, timeout.Token).ConfigureAwait(false);

                var assetId = Guid.NewGuid().ToString("N");
                var imagePath = Path.Combine(boothSession.OutputDirectory, "Final", assetId + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(imagePath));
                var expectedBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nCEAAAAASUVORK5CYII=");
                File.WriteAllBytes(imagePath, expectedBytes);

                var deliverable = await provider.GetRequiredService<IDeliverableService>().CreateAsync(
                    boothSession.Id, null, assetId, imagePath, new CapturedShot[0], DateTime.UtcNow.AddMinutes(30), timeout.Token).ConfigureAwait(false);
                var completion = new TaskCompletionSource<RemoteGalleryReadyEventArgs>();
                var queue = provider.GetRequiredService<IRemoteDeliveryQueue>();
                EventHandler<RemoteGalleryReadyEventArgs> handler = (sender, args) =>
                {
                    if (string.Equals(args.DeliverableId, deliverable.Id, StringComparison.Ordinal)) completion.TrySetResult(args);
                };
                queue.GalleryReady += handler;
                try
                {
                    await queue.EnqueueAsync(deliverable, timeout.Token).ConfigureAwait(false);
                    var gallery = await WaitForGalleryAsync(provider, deliverable.Id, completion.Task, timeout.Token).ConfigureAwait(false);
                    await VerifyGalleryAsync(options.PhotoApiBaseUrl, gallery.GalleryUrl, assetId, expectedBytes, timeout.Token).ConfigureAwait(false);
                    return gallery.GalleryUrl;
                }
                finally
                {
                    queue.GalleryReady -= handler;
                    using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        try { await queue.StopAsync(stop.Token).ConfigureAwait(false); } catch { }
                    }
                }
            }
        }

        static async Task<RemoteGalleryReadyEventArgs> WaitForGalleryAsync(IServiceProvider provider, string deliverableId, Task<RemoteGalleryReadyEventArgs> completion, CancellationToken token)
        {
            var deliverables = provider.GetRequiredService<IDeliverableService>();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var delay = Task.Delay(TimeSpan.FromSeconds(1), token);
                var finished = await Task.WhenAny(completion, delay).ConfigureAwait(false);
                if (finished == completion) return await completion.ConfigureAwait(false);
                var current = await deliverables.GetAsync(deliverableId, token).ConfigureAwait(false);
                if (string.Equals(current?.Status, "Failed", StringComparison.Ordinal))
                    throw new InvalidOperationException(current.LastError ?? "Remote delivery failed.");
                if (string.Equals(current?.Status, "RetryWaiting", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(current.LastError) &&
                    (current.LastError.StartsWith("device_not_enrolled", StringComparison.Ordinal) ||
                     current.LastError.StartsWith("invalid_device_credential", StringComparison.Ordinal)))
                    throw new InvalidOperationException(current.LastError);
            }
        }

        static async Task VerifyGalleryAsync(string apiBaseUrl, Uri galleryUrl, string assetId, byte[] expectedBytes, CancellationToken token)
        {
            if (galleryUrl == null || !galleryUrl.IsAbsoluteUri) throw new InvalidOperationException("Worker returned an invalid Gallery URL.");
            var segments = galleryUrl.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2 || !string.Equals(segments[0], "g", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(segments[1]))
                throw new InvalidOperationException("Worker returned an unexpected Gallery URL.");

            var galleryApi = new Uri(new Uri(apiBaseUrl.TrimEnd('/') + "/"), "api/v3/gallery/" + Uri.EscapeDataString(segments[1]));
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var response = await http.GetAsync(galleryApi, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Gallery API verification failed (HTTP " + (int)response.StatusCode + ").");
                GalleryEnvelope envelope;
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    envelope = (GalleryEnvelope)new DataContractJsonSerializer(typeof(GalleryEnvelope)).ReadObject(stream);
                var asset = envelope?.Gallery?.Assets?.FirstOrDefault();
                if (asset == null || string.IsNullOrWhiteSpace(asset.Url)) throw new InvalidOperationException("Uploaded asset was not listed by Gallery.");

                using (var assetResponse = await http.GetAsync(new Uri(asset.Url), HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false))
                {
                    if (!assetResponse.IsSuccessStatusCode) throw new InvalidOperationException("Gallery asset download failed (HTTP " + (int)assetResponse.StatusCode + ").");
                    var actualBytes = await assetResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (asset.Bytes != expectedBytes.LongLength || !HashesEqual(actualBytes, expectedBytes))
                        throw new InvalidOperationException("Gallery asset content did not match the uploaded image.");
                }
            }
        }

        static bool HashesEqual(byte[] left, byte[] right)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(left).SequenceEqual(sha.ComputeHash(right));
        }

        static string Safe(string value)
        {
            value = (value ?? "Unknown delivery smoke failure.").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 1000 ? value : value.Substring(0, 1000);
        }

        static string RequiredEnvironment(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(name + " is required for delivery smoke.");
            return value.Trim();
        }

        [DataContract]
        sealed class GalleryEnvelope
        {
            [DataMember(Name = "gallery")] public GalleryPayload Gallery { get; set; }
        }

        [DataContract]
        sealed class GalleryPayload
        {
            [DataMember(Name = "assets")] public List<GalleryAsset> Assets { get; set; }
        }

        [DataContract]
        sealed class GalleryAsset
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "bytes")] public long Bytes { get; set; }
            [DataMember(Name = "url")] public string Url { get; set; }
        }
    }
}
