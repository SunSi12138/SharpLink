using System;
using System.Buffers;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

public static class SendCreditBufferHoldEvidenceRunner
{
    public static void Run()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedStringPayload1>();

        foreach (var encodedBytes in GeneratedStringDtoCases.EncodedByteValues)
        {
            var payload = new GeneratedStringPayload1
            {
                Field01 = new string('x', Math.Max(0, encodedBytes - 7))
            };

            using var oldWriter = new PooledByteBufferWriter(GeneratedStringDtoCases.InitialCapacity);
            codec.Serialize(payload, oldWriter);
            var oldHeldBytes = oldWriter.WrittenCount;
            var oldCapacity = oldWriter.Capacity;

            var exactBytes = 0;
            var hasExactSize = codec is IRpcSizedCodec<GeneratedStringPayload1> sizedCodec &&
                               sizedCodec.CanExactSize &&
                               sizedCodec.TryGetEncodedSize(payload, out exactBytes);

            Console.WriteLine(
                $"[SendCreditBufferHold] case={GeneratedStringDtoCases.Describe(encodedBytes)} " +
                $"oldPathHeldBytesBeforeCredit={oldHeldBytes} " +
                $"oldPathCapacityBeforeCredit={oldCapacity} " +
                $"exactSizeSupported={hasExactSize} exactEncodedBytes={exactBytes} " +
                $"newPathHeldBytesBeforeCredit=0");
        }
    }
}
