using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Core.Services;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public sealed class ModeHandoffPerformanceTests
    {
        [Fact]
        public async Task Soft_handoff_runs_in_order_without_camera_reconnect_operations()
        {
            var events = new List<string>();
            var coordinator = new ModeHandoffCoordinator();

            await coordinator.TransferAsync(
                _ => { events.Add("admin-live-stop"); return Task.CompletedTask; },
                _ => { events.Add("customer-live-start"); return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(new[] { "admin-live-stop", "customer-live-start" }, events);
            Assert.DoesNotContain("camera-disconnect", events);
            Assert.DoesNotContain("camera-connect", events);
        }

        [Fact]
        public async Task Soft_handoff_removes_simulated_session_reopen_cost()
        {
            const int transitions = 10;
            var coordinator = new ModeHandoffCoordinator();
            var soft = Stopwatch.StartNew();
            for (var i = 0; i < transitions; i++)
                await coordinator.TransferAsync(_ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
            soft.Stop();

            var legacy = Stopwatch.StartNew();
            for (var i = 0; i < transitions; i++)
            {
                await Task.Delay(15); // close SDK session
                await Task.Delay(15); // reopen SDK session
            }
            legacy.Stop();

            Assert.True(soft.ElapsedTicks * 2 < legacy.ElapsedTicks,
                $"Soft={soft.ElapsedMilliseconds}ms Legacy={legacy.ElapsedMilliseconds}ms");
        }
    }
}
