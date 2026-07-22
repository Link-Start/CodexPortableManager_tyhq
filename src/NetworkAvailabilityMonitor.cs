using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexPortableManager
{
    internal sealed class NetworkAvailabilityMonitor : IDisposable
    {
        private static readonly Guid NetworkListManagerClassId =
            new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
        private readonly object syncRoot = new object();
        private readonly Func<bool> internetAccessQuery;
        private readonly bool subscribedToSystemEvents;
        private CancellationTokenSource interruption = new CancellationTokenSource();
        private bool disposed;

        internal NetworkAvailabilityMonitor()
            : this(null)
        {
        }

        internal NetworkAvailabilityMonitor(Func<bool> query)
        {
            internetAccessQuery = query;
            subscribedToSystemEvents = query == null;
            if (subscribedToSystemEvents)
            {
                NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
                NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
            }
        }

        internal bool HasInternetAccess
        {
            get
            {
                return internetAccessQuery == null
                    ? QueryInternetAccess()
                    : internetAccessQuery();
            }
        }

        internal CancellationToken InterruptionToken
        {
            get
            {
                lock (syncRoot)
                {
                    return disposed ? new CancellationToken(true) : interruption.Token;
                }
            }
        }

        private void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs args)
        {
            SignalChange();
        }

        private void NetworkAddressChanged(object sender, EventArgs args)
        {
            SignalChange();
        }

        private void SignalChange()
        {
            CancellationTokenSource previous;
            lock (syncRoot)
            {
                if (disposed) return;
                previous = interruption;
                interruption = new CancellationTokenSource();
            }
            previous.Cancel();
            previous.Dispose();
        }

        private static bool QueryInternetAccess()
        {
            object manager = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(NetworkListManagerClassId, true);
                manager = Activator.CreateInstance(type);
                return (bool)manager.GetType().InvokeMember(
                    "IsConnectedToInternet",
                    System.Reflection.BindingFlags.GetProperty,
                    null,
                    manager,
                    null);
            }
            catch
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            finally
            {
                if (manager != null && Marshal.IsComObject(manager))
                {
                    try { Marshal.FinalReleaseComObject(manager); }
                    catch (InvalidComObjectException) { }
                }
            }
        }

        public void Dispose()
        {
            CancellationTokenSource signal;
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                signal = interruption;
            }
            if (subscribedToSystemEvents)
            {
                NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            }
            signal.Cancel();
            signal.Dispose();
        }
    }
}
