namespace SharpLink.MethodFactsEvidence;
internal static class EdgeCounts
{
    internal static async Task Run()
    {
        foreach(var oneWay in new[]{false,true})
        foreach(var intercepted in new[]{false,true})
        foreach(var name in new[]{"unknown-method","service-error","stale-module","expired-deadline","missing-service","admission-rejected","admission-queued","short-circuit","interceptor-error","cancellation-unsupported"})
        {
            var kind=oneWay?RpcMethodKind.OneWay:RpcMethodKind.Unary;
            var stub=new CountingStub(kind,found:name!="unknown-method",supportsCancellation:name!="cancellation-unsupported",fail:name=="service-error"||(!oneWay&&name=="unknown-method"));
            var admission=name.StartsWith("admission",StringComparison.Ordinal);
            var queued=name=="admission-queued";
            await using var h=new LocalHarness(intercepted,name=="stale-module",admission,stub,queued,
                name=="short-circuit"?"short":name=="interceptor-error"?"throw":"pass");
            if(name=="stale-module")h.Module!.TryBeginDraining();
            if(name=="missing-service")h.RemoveService();
            if(admission)
            {
                var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stub.Block=release.Task;
                var first=Enumerable.Range(0,queued?1:8).Select(_=>h.Dispatch(oneWay).AsTask()).ToArray();
                stub.Reset();
                var target=h.Dispatch(oneWay).AsTask();
                if(queued&&target.IsCompleted)throw new Exception("Queued request did not wait");
                if(!queued)await target;
                stub.Block=null;release.SetResult();
                await Task.WhenAll(first);await target;
            }
            else await h.Dispatch(oneWay,cancellable:name=="cancellation-unsupported",expired:name=="expired-deadline");
            Console.WriteLine($"{{\"edge\":\"{name}\",\"oneWay\":{oneWay.ToString().ToLowerInvariant()},\"intercepted\":{intercepted.ToString().ToLowerInvariant()},\"Descriptors\":{stub.Descriptors},\"Cancellations\":{stub.Cancellations},\"Invocations\":{stub.Invocations}}}");
        }
    }
}
