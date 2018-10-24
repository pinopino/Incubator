﻿using System;

namespace Incubator.SocketServer.Rpc
{
    internal class DefaultTypeMaker
    {
        public object GetDefault(Type t)
        {
            return this.GetType().GetMethod("GetDefaultGeneric").MakeGenericMethod(t).Invoke(this, null);
        }

        public T GetDefaultGeneric<T>()
        {
            return default(T);
        }
    }
}