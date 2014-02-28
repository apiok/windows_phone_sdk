using Microsoft.Phone.Controls;
using System;
using System.Collections.Generic;

namespace Odnoklassniki.ServiceStructures
{
    class ConcurrentDictionary<TKey, TValue> : Dictionary<TKey, TValue>
    {
        private object lockObject = new Object();

        public void safeAdd(TKey key, TValue value)
        {
            lock (lockObject)
            {
                base.Add(key, value);
            }
        }

        public bool safeRemove(TKey key)
        {
            lock (lockObject)
            {
                return base.Remove(key);
            }
        }

        public TValue safeGet(TKey key)
        {
            lock (lockObject)
            {
                return this[key];
            }
        }

    }

    struct CallbackStruct
    {
        public Action<string> onSuccess;
        public Action<Exception> onError;
        public PhoneApplicationPage callbackContext;
    }

    struct AuthCallbackStruct
    {
        public Action onSuccess;
        public Action<Exception> onError;
        public PhoneApplicationPage callbackContext;
        public bool saveSession;
    }
}
