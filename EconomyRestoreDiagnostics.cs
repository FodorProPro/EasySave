using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace EasySave
{
    internal static class EconomyRestoreDiagnostics
    {
        private static ManualLogSource logger;
        private static object restoredJob;
        private static RestoredJobSource restoredSource;

        internal static void Initialize(ManualLogSource pluginLogger)
        {
            logger = pluginLogger;
        }

        internal static void RegisterRestoredJob(object job, RestoredJobSource source)
        {
            restoredJob = job;
            restoredSource = source;
        }

        internal static void CompleteJobPrefix(object board)
        {
            if (!EasySaveSettings.EnableEconomyDiagnosticLogging || logger == null) return;
            object job = ReflectionHelpers.Get(board, "selectedJob");
            string source = ReferenceEquals(job, restoredJob) ? restoredSource.ToString() : "Untracked";
            logger.LogInfo($"EasySave: CompleteJob prefix selectedJob payload=" +
                           $"{ReflectionHelpers.Get(job, "payloadIndex", -1)} " +
                           $"price={ReflectionHelpers.Get(job, "price", 0f):F2} " +
                           $"from={ReflectionHelpers.ObjectName(ReflectionHelpers.Get(job, "from")) ?? "null"} " +
                           $"to={ReflectionHelpers.ObjectName(ReflectionHelpers.Get(job, "to")) ?? "null"} " +
                           $"source={source}.");
        }

        internal static void CompleteJobPostfix()
        {
            if (EasySaveSettings.EnableEconomyDiagnosticLogging && logger != null)
                logger.LogInfo("EasySave: CompleteJob postfix done.");
            restoredJob = null;
        }
    }

    [HarmonyPatch]
    internal static class CompleteJobDiagnosticPatch
    {
        private static MethodBase TargetMethod()
        {
            Type boardType = Type.GetType("jobBoard, Assembly-CSharp", false);
            return boardType?.GetMethod("CompleteJob",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance)
        {
            EconomyRestoreDiagnostics.CompleteJobPrefix(__instance);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            EconomyRestoreDiagnostics.CompleteJobPostfix();
        }
    }
}
