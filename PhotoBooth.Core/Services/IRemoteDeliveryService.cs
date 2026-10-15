using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public class RemoteDeliveryOperationResult
    {
        public bool Succeeded { get; set; }
        public bool Retryable { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string ExternalReference { get; set; }
        public string ReplicaProvider { get; set; }
    }

    public sealed class RemoteGalleryResult : RemoteDeliveryOperationResult
    {
        public string DeliveryToken { get; set; }
        public Uri GalleryUrl { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
    }

    public sealed class RemoteGalleryReadyEventArgs : EventArgs
    {
        public string DeliverableId { get; set; }
        public Uri GalleryUrl { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }
    public sealed class RemoteDeliveryFailedEventArgs : EventArgs
    {
        public string DeliverableId { get; set; }
        public string ErrorMessage { get; set; }
    }

    public interface IRemoteMediaDeliveryClient
    {
        Task<string> FreezeGallerySiteAsync(string remoteEventId, CancellationToken token);
        Task<RemoteDeliveryOperationResult> UploadAssetAsync(Guid boothSessionId, string deliverableId, MediaAssetRecord asset, string idempotencyKey, string employeeSessionToken, CancellationToken token);
        Task<RemoteDeliveryOperationResult> CommitDeliverableAsync(Guid boothSessionId, string deliverableId, string idempotencyKey, string employeeSessionToken, CancellationToken token);
        Task<RemoteGalleryResult> CreateGalleryAsync(Guid boothSessionId, string deliverableId, string idempotencyKey, string employeeSessionToken, CancellationToken token);
        Task<RemoteGalleryResult> PrepareGalleryAsync(Guid boothSessionId, string deliverableId, CancellationToken token);
        Task<RemoteDeliveryOperationResult> ReportRevenueAsync(RevenueReport report, string idempotencyKey, CancellationToken token);
    }

    public interface IRevenueSyncService
    {
        Task SynchronizeAsync(CancellationToken token);
    }

    public interface IRemoteDeliveryQueue
    {
        event EventHandler<RemoteGalleryReadyEventArgs> GalleryReady;
        event EventHandler<RemoteDeliveryFailedEventArgs> DeliveryFailed;
        Task<RemoteGalleryResult> PrepareGalleryAsync(Guid boothSessionId, string deliverableId, CancellationToken token);
        Task EnqueueAsync(Deliverable deliverable, CancellationToken token);
        Task StartAsync(CancellationToken token);
        Task StopAsync(CancellationToken token);
        Task<int> RetryPendingAsync(string deliverableId, CancellationToken token);
    }

    public static class RemoteDeliveryAssetPolicy
    {
        public static IReadOnlyList<DeliverableAsset> Select(IReadOnlyList<DeliverableAsset> assets)
        {
            var source = (assets ?? new DeliverableAsset[0])
                .Where(value => value != null && IsIncludedRole(value.Role))
                .OrderBy(value => RoleOrder(value.Role))
                .ThenBy(value => value.Position)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToList();

            var finalVideoAdded = false;
            var result = new List<DeliverableAsset>();
            foreach (var asset in source)
            {
                if (string.Equals(asset.Role, DeliverableAssetRoles.CompositeVideo, StringComparison.OrdinalIgnoreCase))
                {
                    if (finalVideoAdded) continue;
                    finalVideoAdded = true;
                }
                result.Add(asset);
            }
            return result;
        }

        public static bool IsIncludedRole(string role)
        {
            return string.Equals(role, DeliverableAssetRoles.OriginalPicture, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, DeliverableAssetRoles.FinalComposite, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, DeliverableAssetRoles.Gif, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, DeliverableAssetRoles.CompositeVideo, StringComparison.OrdinalIgnoreCase);
        }

        static int RoleOrder(string role)
        {
            if (string.Equals(role, DeliverableAssetRoles.FinalComposite, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(role, DeliverableAssetRoles.OriginalPicture, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(role, DeliverableAssetRoles.Gif, StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }
    }
}
