using System;
using PhotoBooth.Core.Models;
using Xunit;

namespace PhotoBooth.UnitTests
{
    public class EventPrintQuotaTests
    {
        [Theory]
        [InlineData(20,3,10,3)]
        [InlineData(2,8,10,2)]
        [InlineData(0,8,10,0)]
        [InlineData(20,0,10,0)]
        [InlineData(20,20,10,10)]
        public void Bounds_both_remaining_quotas_and_local_capacity(int remaining,int capture,int maximum,int expected)
            => Assert.Equal(expected,new EventPrintQuota{Mode="limited",EventRemaining=remaining,CaptureRemaining=capture}.AllowedCopies(maximum));

        [Fact] public void Unlimited_still_respects_supported_capacity()
            => Assert.Equal(10,new EventPrintQuota{Mode="unlimited"}.AllowedCopies(10));

        [Theory]
        [InlineData(null,3)]
        [InlineData(3,null)]
        [InlineData(-1,3)]
        public void Unverified_limits_never_become_unlimited(int? remaining,int? capture)
            => Assert.Throws<InvalidOperationException>(()=>new EventPrintQuota{Mode="limited",EventRemaining=remaining,CaptureRemaining=capture}.AllowedCopies(10));
    }
}
