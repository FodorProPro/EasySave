using System;
using System.IO;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace EasySave
{
    internal static class DeliveryStateStore
    {
        internal static string StatePath => Path.Combine(Application.persistentDataPath, "EasySaveState.json");

        internal static void Save(DeliveryState state)
        {
            string path = StatePath;
            string temporaryPath = path + ".tmp";
            if (EasySaveSettings.BackupBeforeWrite && File.Exists(path))
                File.Copy(path, path + ".bak", true);
            var serializer = new DataContractJsonSerializer(typeof(DeliveryState));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, state);
                File.WriteAllBytes(temporaryPath, stream.ToArray());
            }
            File.Copy(temporaryPath, path, true);
            File.Delete(temporaryPath);
        }

        internal static bool TryLoad(out DeliveryState state, out string error)
        {
            state = null;
            error = null;
            try
            {
                if (!File.Exists(StatePath)) return false;
                var serializer = new DataContractJsonSerializer(typeof(DeliveryState));
                using (FileStream stream = File.OpenRead(StatePath))
                    state = serializer.ReadObject(stream) as DeliveryState;
                if (state == null) return false;
                if (state.version != 4)
                {
                    error = $"Unsupported EasySave schema version {state.version}; expected 4.";
                    state = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.ToString();
                return false;
            }
        }

        internal static void BackupBeforeRestore()
        {
            if (!File.Exists(StatePath)) return;
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Copy(StatePath, StatePath + ".bak-before-restore-" + timestamp, true);
        }
    }
}
