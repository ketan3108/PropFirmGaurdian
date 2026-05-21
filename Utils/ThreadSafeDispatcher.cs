using System;
using System.Windows.Threading;

namespace PropFirmGuardian.Utils
{
    public static class ThreadSafeDispatcher
    {
        public static Dispatcher NTDispatcher
        {
            get { return NinjaTrader.Core.Globals.MainThreadDispatcher; }
        }

        public static void SafeInvoke(Action action, DispatcherPriority priority = DispatcherPriority.Background)
        {
            if (action == null)
                return;

            Dispatcher dispatcher = NTDispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.InvokeAsync(action, priority);
        }
    }
}
