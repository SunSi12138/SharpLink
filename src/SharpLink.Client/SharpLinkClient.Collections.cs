namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed class LongConcurrentSet
    {
        private const int StripeCount = 32;
        private readonly Lock[] _locks = new Lock[StripeCount];
        private readonly HashSet<long>[] _sets = new HashSet<long>[StripeCount];

        public LongConcurrentSet()
        {
            for (var i = 0; i < StripeCount; i++)
            {
                _locks[i] = new Lock();
                _sets[i] = [];
            }
        }

        public bool Add(long value)
        {
            var stripe = GetStripe(value);
            lock (_locks[stripe])
                return _sets[stripe].Add(value);
        }

        public bool Remove(long value)
        {
            var stripe = GetStripe(value);
            lock (_locks[stripe])
                return _sets[stripe].Remove(value);
        }

        public void Clear()
        {
            for (var i = 0; i < StripeCount; i++)
            {
                lock (_locks[i])
                    _sets[i].Clear();
            }
        }

        private static int GetStripe(long value)
        {
            var hash = unchecked((int)(value ^ (value >> 32)));
            hash &= int.MaxValue;
            return hash & (StripeCount - 1);
        }
    }
}
