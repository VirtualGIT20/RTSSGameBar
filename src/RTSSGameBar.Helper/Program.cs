using System;
using System.Threading;
using RTSSGameBar.Helper.Ipc;
using RTSSGameBar.Helper.Platform;
using RTSSGameBar.Helper.Rtss;

namespace RTSSGameBar.Helper
{
    internal static class Program
    {
        private const string MutexName = @"Local\RTSSGameBar.Helper.v19.Singleton";

        [STAThread]
        private static void Main(string[] args)
        {
            bool ownsMutex;
            using (var mutex = new Mutex(true, MutexName, out ownsMutex))
            {
                if (!ownsMutex)
                    return;

                Log.Info("RTSSGameBar.Helper started. Elevated=" + Elevation.IsElevated() + ". Backend=RTSS client plugin only. Log=" + Log.FileName);

                var controller = new RtssController();
                var server = new PipeServer(controller);
                try
                {
                    server.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Error("Fatal helper error: " + ex);
                }
            }
        }
    }
}
