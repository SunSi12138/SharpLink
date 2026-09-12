using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal static class AdmissionSingleSlot
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryAcquire(
        AdmissionStateKernel owner,
        AdmissionLimiterSlot slot,
        IAdmissionLimiter? suppliedLimiter,
        RateLimitLease? suppliedLease,
        ref AdmissionPartitionLease? partition,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
    {
        if (suppliedLease is not null && !ReferenceEquals(slot.Limiter, suppliedLimiter))
        {
            suppliedLease.Dispose();
            throw new InvalidOperationException(
                "The supplied admission limiter is not part of this request.");
        }

        var lease = suppliedLease ?? slot.Limiter.AttemptAcquire(1);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            admissionLease = null;
            failedSlot = slot;
            return false;
        }

        admissionLease = new AdmissionLease(
            owner,
            lease,
            Interlocked.Exchange(ref partition, null));
        failedSlot = default;
        return true;
    }
}
