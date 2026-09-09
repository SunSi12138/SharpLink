using System.Runtime.InteropServices;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack4DecimalTests
{
    [Test]
    public void ValidBitsShouldPreserveScaleSignAndNegativeZero()
    {
        foreach (var negative in new[] { false, true })
            for (byte scale = 0; scale <= 28; scale++)
                foreach (var word in new[] { 0, 1, int.MinValue, int.MaxValue, -1 })
                {
                    var value = new decimal(word, word, word, negative, scale);
                    var actual = CodecHelpers.ValidateDecimal(value);
                    if (!decimal.GetBits(value).SequenceEqual(decimal.GetBits(actual)))
                        throw new InvalidOperationException("Valid decimal bit representation changed.");
                }
    }

    [Test]
    public void InvalidFlagsShouldRetainDataLossAndFrameworkInnerException()
    {
        var valid = 1.25m;
        var native = MemoryMarshal.Cast<decimal, int>(MemoryMarshal.CreateSpan(ref valid, 1));
        var flags = decimal.GetBits(valid)[3];
        var flagIndex = -1;
        for (var i = 0; i < native.Length; i++)
            if (native[i] == flags) flagIndex = i;
        if (flagIndex < 0) throw new InvalidOperationException("Unable to locate test corruption slot.");
        foreach (var invalid in new[] { 1, 0x1000000, 29 << 16, 255 << 16 })
        {
            var value = valid;
            MemoryMarshal.Cast<decimal, int>(MemoryMarshal.CreateSpan(ref value, 1))[flagIndex] = invalid;
            try
            {
                _ = CodecHelpers.ValidateDecimal(value);
                throw new InvalidOperationException("Invalid decimal accepted.");
            }
            catch (SharpLinkException ex)
            {
                if (ex.Code != SharpLinkErrorCode.DataLoss || ex.InnerException is not ArgumentException ||
                    ex.Message != "Invalid Decimal payload.")
                    throw new InvalidOperationException("Decimal exception contract changed.", ex);
            }
        }
    }
}
