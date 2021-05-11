using System;
using System.Runtime.Serialization;

namespace Nekoyume.Action
{
    [Serializable]
    public class StakingExpiredException : InvalidOperationException
    {
        public StakingExpiredException(string msg) : base(msg)
        {
        }

        protected StakingExpiredException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}