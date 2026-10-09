using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Infrastructure.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class ApiHttpTransportTests
    {
        static readonly Uri Endpoint = new Uri("https://api.example.test:8443/a%2Fb?key=x%2Fy&v=1");

        [Fact]
        public async Task Direct_request_uses_DNS_IPv4_and_preserves_authority_body_and_signed_path()
        {
            var originalBody = Encoding.UTF8.GetBytes("{\"pin\":\"synthetic\"}");
            var inner = new Handler(async request =>
            {
                Assert.Equal("192.0.2.8", request.RequestUri.Host);
                Assert.Equal(8443, request.RequestUri.Port);
                Assert.Equal(Endpoint.Authority, request.Headers.Host);
                Assert.Equal(Endpoint.PathAndQuery, request.RequestUri.PathAndQuery);
                Assert.Equal(originalBody, await request.Content.ReadAsByteArrayAsync());
                Assert.Equal("synthetic-signature", string.Join("", request.Headers.GetValues("X-Device-Signature")));
                Assert.Equal("synthetic-nonce", string.Join("", request.Headers.GetValues("X-Device-Nonce")));
                Assert.Equal("Bearer synthetic", request.Headers.Authorization.ToString());
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            });
            using (var client = Client(inner))
            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Content = new ByteArrayContent(originalBody);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic");
                request.Headers.Add("X-Device-Signature", "synthetic-signature");
                request.Headers.Add("X-Device-Nonce", "synthetic-nonce");
                using (var response = await client.SendAsync(request))
                    Assert.Equal(Endpoint, response.RequestMessage.RequestUri);
                Assert.Equal(Endpoint, request.RequestUri);
                Assert.Null(request.Headers.Host);
                Assert.Equal(1, inner.Calls);
            }
        }

        [Fact]
        public async Task Failed_POST_is_not_replayed_and_original_URI_is_restored()
        {
            var inner = new Handler(_ => throw new HttpRequestException("Synthetic connection failure"));
            using (var client = Client(inner))
            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
                Assert.Equal(1, inner.Calls);
                Assert.Equal(Endpoint, request.RequestUri);
                Assert.Null(request.Headers.Host);
            }
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Proxy_and_explicit_host_requests_keep_original_routing(bool proxy, bool differentHost)
        {
            var inner = new Handler(request =>
            {
                Assert.Equal(Endpoint, request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
            using (var client = new HttpClient(new ApiAddressHandler(inner,
                _ => throw new InvalidOperationException("Must let proxy resolve target"), _ => proxy)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, Endpoint))
            {
                if (differentHost) request.Headers.Host = "explicit.example.test";
                using (await client.SendAsync(request)) { }
            }
        }

        [Fact]
        public async Task IPv6_only_destination_remains_usable()
        {
            var inner = new Handler(request =>
            {
                Assert.Equal(Endpoint, request.RequestUri);
                Assert.Null(request.Headers.Host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
            using (var client = new HttpClient(new ApiAddressHandler(inner,
                _ => Task.FromResult(new[] { IPAddress.IPv6Loopback }), _ => false)))
            using (await client.GetAsync(Endpoint)) { }
        }

        [Fact]
        public async Task Cancellation_during_DNS_does_not_send_a_request()
        {
            var lookup = new TaskCompletionSource<IPAddress[]>();
            var entered = new TaskCompletionSource<bool>();
            var inner = new Handler(_ => throw new InvalidOperationException("Must not send"));
            using (var client = new HttpClient(new ApiAddressHandler(inner,
                _ => { entered.SetResult(true); return lookup.Task; }, _ => false)))
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = client.GetAsync(Endpoint, cancellation.Token);
                await entered.Task;
                cancellation.Cancel();
                var completed = await Task.WhenAny(pending, Task.Delay(2000));
                Assert.Same(pending, completed);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                lookup.SetException(new SocketException((int)SocketError.HostNotFound));
                Assert.Equal(0, inner.Calls);
            }
        }

        [Fact]
        public async Task DNS_failure_is_reported_as_transport_error_without_sending()
        {
            var inner = new Handler(_ => throw new InvalidOperationException("Must not send"));
            using (var client = new HttpClient(new ApiAddressHandler(inner,
                _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)), _ => false)))
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Endpoint));
                Assert.Equal(0, inner.Calls);
            }
        }

        static HttpClient Client(Handler inner) => new HttpClient(new ApiAddressHandler(inner,
            _ => Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Parse("192.0.2.8") }), _ => false));

        sealed class Handler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
            public int Calls { get; private set; }
            public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) { this.send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { Calls++; return send(request); }
        }
    }
}
