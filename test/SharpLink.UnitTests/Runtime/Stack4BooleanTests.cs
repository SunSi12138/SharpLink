using System.Runtime.InteropServices;

namespace SharpLink.UnitTests.Runtime;

public sealed class Stack4BooleanTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(8)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(17)]
    [Arguments(256)]
    [Arguments(4096)]
    public void BothPathsShouldRejectEveryNonCanonicalByte(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 1);
        CodecHelpers.ValidateBlitElements<bool>(MemoryMarshal.Cast<byte, bool>(bytes));
        for (var position = 0; position < bytes.Length; position++)
        {
            var saved = bytes[position];
            foreach (var invalid in new byte[] { 2, 127, 128, 255 })
            {
                bytes[position] = invalid;
                try
                {
                    CodecHelpers.ValidateBlitElements<bool>(MemoryMarshal.Cast<byte, bool>(bytes));
                    throw new InvalidOperationException($"Invalid boolean accepted at {position}.");
                }
                catch (SharpLinkException ex)
                {
                    if (ex.Code != SharpLinkErrorCode.DataLoss ||
                        ex.Message != "Boolean collection contains a non-canonical element.")
                        throw new InvalidOperationException("Boolean error contract changed.", ex);
                }
            }
            bytes[position] = saved;
        }
    }
}
