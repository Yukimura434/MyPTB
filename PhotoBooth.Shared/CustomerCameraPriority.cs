using System;
using System.Threading;

namespace PhotoBooth.Shared
{
    /// <summary>Coordinates exclusive camera ownership between the two desktop processes.</summary>
    public sealed class CustomerCameraPriority : IDisposable
    {
        const string EventName = "PhotoBooth.CustomerCameraPriority";
        readonly EventWaitHandle signal = new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
        readonly bool owner;

        public CustomerCameraPriority(bool customerOwner)
        {
            owner = customerOwner;
            if (owner) signal.Set();
        }

        public bool IsCustomerActive { get { return signal.WaitOne(0); } }
        public void Dispose() { if (owner) signal.Reset(); signal.Dispose(); }
    }
}
