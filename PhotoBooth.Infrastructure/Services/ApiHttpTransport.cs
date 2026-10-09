using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Infrastructure.Services
{
    internal static class ApiHttpTransport
    {
        public static HttpClient Create(TimeSpan? timeout = null)
        {
            var client = new HttpClient(new ApiAddressHandler());
            if (timeout.HasValue) client.Timeout = timeout.Value;
            return client;
        }
    }

    // Framework HttpClient waits for an unreachable IPv6 address until the whole
    // request expires. Prefer a DNS-provided IPv4 address for direct API traffic.
    // Host retains the original authority: Framework uses it for TLS SNI and
    // certificate name validation as well as HTTP routing. Never bypass TLS or
    // replay a signed/state-changing request after a timeout.
    internal sealed class ApiAddressHandler : DelegatingHandler
    {
        readonly Func<string, Task<IPAddress[]>> resolve;
        readonly Func<Uri, bool> usesProxy;

        internal ApiAddressHandler()
            : this(new HttpClientHandler { AllowAutoRedirect = false }, Dns.GetHostAddressesAsync,
                uri => WebRequest.DefaultWebProxy != null && !WebRequest.DefaultWebProxy.IsBypassed(uri)) { }

        internal ApiAddressHandler(HttpMessageHandler inner, Func<string, Task<IPAddress[]>> resolve, Func<Uri, bool> usesProxy)
            : base(inner)
        {
            this.resolve = resolve;
            this.usesProxy = usesProxy;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var originalUri = request.RequestUri;
            var originalHost = request.Headers.Host;
            if (originalUri.Scheme != Uri.UriSchemeHttps || originalUri.HostNameType != UriHostNameType.Dns ||
                usesProxy(originalUri) || (!string.IsNullOrEmpty(originalHost) &&
                !string.Equals(originalHost, originalUri.Authority, StringComparison.OrdinalIgnoreCase)))
                return await base.SendAsync(request, token).ConfigureAwait(false);

            IPAddress[] addresses;
            try
            {
                var lookup = resolve(originalUri.DnsSafeHost);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(lookup, cancelled.Task).ConfigureAwait(false) != lookup)
                    {
                        // Observe a late DNS failure even when the caller has already cancelled.
                        _ = lookup.ContinueWith(t => { var ignored = t.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        token.ThrowIfCancellationRequested();
                    }
                    addresses = await lookup.ConfigureAwait(false);
                }
            }
            catch (SocketException exception)
            {
                throw new HttpRequestException("API name resolution failed.", exception);
            }
            token.ThrowIfCancellationRequested();
            var address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork);
            if (address == null) return await base.SendAsync(request, token).ConfigureAwait(false);

            request.Headers.Host = originalUri.Authority;
            request.RequestUri = new UriBuilder(originalUri) { Host = address.ToString() }.Uri;
            try { return await base.SendAsync(request, token).ConfigureAwait(false); }
            finally
            {
                request.RequestUri = originalUri;
                request.Headers.Host = originalHost;
            }
        }
    }
}
