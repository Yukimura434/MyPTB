using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Models;

namespace PhotoBooth.Core.Services
{
    public interface IPcAuthenticationService
    {
        Task<PcAuthenticationResult> AuthenticateAsync(string loginIdentifier, string password, bool rememberLogin, CancellationToken cancellationToken);
        Task<PcAuthenticationResult> TryResumeAsync(CancellationToken cancellationToken);
        Task ForgetAsync(CancellationToken cancellationToken);
        Task EnsureDevelopmentAccountsAsync(CancellationToken cancellationToken);
        Task<string> CreateHandoffAsync(CancellationToken cancellationToken);
        Task<PcAuthenticationResult> AcceptHandoffAsync(string protectedHandoff, CancellationToken cancellationToken);
    }

    public interface IPcSessionContext
    {
        PcSession Current { get; }
        event EventHandler Changed;
        void SignIn(PcSession session);
        void SignOut();
    }
}
