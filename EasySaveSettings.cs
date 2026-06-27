using BepInEx.Configuration;

namespace EasySave
{
    internal static class EasySaveSettings
    {
        internal static bool EnableNativeSave { get; private set; } = true;
        internal static bool EnableCarCheckpoint { get; private set; } = true;
        internal static bool EnableDeliveryStateCapture { get; private set; } = true;
        internal static bool EnableDeliveryStateRestore { get; private set; } = true;
        internal static bool EnablePayloadRestore { get; private set; } = true;
        internal static bool EnableRouteRestore { get; private set; } = true;
        internal static bool EnableDiagnosticLogging { get; private set; }
        internal static bool EnableEconomyDiagnosticLogging { get; private set; } = true;
        internal static float RestoreTimeoutSeconds { get; private set; } = 10.0f;
        internal static float RestoreRetryIntervalSeconds { get; private set; } = 0.25f;
        internal static bool BackupBeforeWrite { get; private set; } = true;
        internal static bool BackupBeforeRestore { get; private set; } = true;
        internal static bool DisableRestoreAfterFailure { get; private set; } = true;

        internal static void Initialize(ConfigFile config)
        {
            EnableNativeSave = config.Bind("Save", "EnableNativeSave", true,
                "Call the game's native SaveData method on F5.").Value;
            EnableCarCheckpoint = config.Bind("Save", "EnableCarCheckpoint", true,
                "Update the native scene and car checkpoint keys on F5.").Value;
            EnableDeliveryStateCapture = config.Bind("Delivery", "EnableDeliveryStateCapture", true,
                "Capture delivery diagnostics/state into EasySaveState.json.").Value;
            EnableDeliveryStateRestore = config.Bind("Delivery", "EnableDeliveryStateRestore", true,
                "Restore supported delivery stages after scene load.").Value;
            EnablePayloadRestore = config.Bind("Delivery", "EnablePayloadRestore", true,
                "Restore payload for supported in-truck stages.").Value;
            EnableRouteRestore = config.Bind("Delivery", "EnableRouteRestore", true,
                "Restore GPS destination through sPathFinder.SetDestination.").Value;
            EnableDiagnosticLogging = config.Bind("Debug", "EnableDiagnosticLogging", false,
                "Enable additional targeted delivery diagnostics.").Value;
            EnableEconomyDiagnosticLogging = config.Bind("Debug", "EnableEconomyDiagnosticLogging", true,
                "Log the selected job price/source immediately before and after CompleteJob.").Value;
            RestoreTimeoutSeconds = config.Bind("Restore", "RestoreTimeoutSeconds", 10.0f,
                "Maximum time to wait for gameplay objects.").Value;
            RestoreRetryIntervalSeconds = config.Bind("Restore", "RestoreRetryIntervalSeconds", 0.25f,
                "Delay between gameplay object lookup retries.").Value;
            BackupBeforeWrite = config.Bind("Safety", "BackupBeforeWrite", true,
                "Back up the previous JSON before writing a new checkpoint.").Value;
            BackupBeforeRestore = config.Bind("Safety", "BackupBeforeRestore", true,
                "Back up the JSON immediately before delivery restore.").Value;
            DisableRestoreAfterFailure = config.Bind("Safety", "DisableRestoreAfterFailure", true,
                "Disable further delivery restores for the current session after a failure.").Value;
        }
    }
}
