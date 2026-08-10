using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using RTSSGameBar.Helper.Platform;

namespace RTSSGameBar.Helper.Ipc
{
    internal static class PipeSecurityFactory
    {
        // ALL APPLICATION PACKAGES. AppContainer clients need an ACE that is
        // present in the restricted side of their access token. Combined with
        // the current-user ACE below, this keeps the application pipe scoped to the
        // signed-in user while allowing the UWP Game Bar widget to connect.
        private const string AllApplicationPackagesSid = "S-1-15-2-1";

        public static PipeSecurity Create()
        {
            var security = new PipeSecurity();

            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.User == null)
                        throw new InvalidOperationException("Current Windows identity has no user SID.");

                    security.AddAccessRule(new PipeAccessRule(
                        identity.User,
                        PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                        AccessControlType.Allow));

                    Log.Info("Pipe ACL includes current-user SID " + identity.User.Value + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not add current-user SID to pipe ACL: " + ex);
                throw;
            }

            var allApplicationPackages = new SecurityIdentifier(AllApplicationPackagesSid);
            security.AddAccessRule(new PipeAccessRule(
                allApplicationPackages,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            Log.Info("Pipe ACL includes ALL APPLICATION PACKAGES SID " + allApplicationPackages.Value + ".");
            return security;
        }
    }
}
