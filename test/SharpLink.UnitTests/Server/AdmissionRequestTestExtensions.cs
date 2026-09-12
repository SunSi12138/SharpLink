using System.Threading.RateLimiting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

internal static class AdmissionRequestTestExtensions
{
    internal static bool TryAcquire(
        this AdmissionRequest request,
        SharpLinkAdmissionController owner,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
        => request.TryAcquire(owner.Kernel, out admissionLease, out failedSlot);

    internal static bool TryAcquireUsing(
        this AdmissionRequest request,
        SharpLinkAdmissionController owner,
        RateLimiter suppliedLimiter,
        RateLimitLease suppliedLease,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
        => request.TryAcquireUsing(
            owner.Kernel,
            suppliedLimiter,
            suppliedLease,
            out admissionLease,
            out failedSlot);
}
