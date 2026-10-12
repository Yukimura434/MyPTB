using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Core.Services
{
    public sealed class PrintLimitExceededException : InvalidOperationException
    {
        public PrintLimitExceededException(string message) : base(message) {}
    }

    public sealed class EventPrintReservation
    {
        public string EventId { get; set; }
        public bool IsSelfBooth { get; set; }
        public string Id { get; set; }
        public string State { get; set; }
        public string SessionToken { get; set; }
    }

    public interface IEventPrintAuthorizationService
    {
        Task<EventPrintReservation> ReserveAsync(Guid captureId, int copies, string idempotencyKey, CancellationToken token);
        Task SubmitAsync(EventPrintReservation reservation, CancellationToken token);
        Task CommitAsync(EventPrintReservation reservation, CancellationToken token);
    }
}
