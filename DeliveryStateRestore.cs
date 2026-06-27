using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Logging;
using UnityEngine;

namespace EasySave
{
    internal enum RestoredJobSource
    {
        LiveJobs,
        LiveJobsBackup,
        ReconstructedFromSave
    }

    internal static class DeliveryStateRestore
    {
        private const float EconomyTolerance = 0.05f;
        private const float NodePositionTolerance = 2.0f;

        private sealed class JobMatch
        {
            internal object Job;
            internal RestoredJobSource Source;
            internal float Score;
        }

        internal static bool Restore(DeliveryState state, MonoBehaviour car, ManualLogSource logger,
            out bool asynchronous, Action<bool> asynchronousCompletion = null)
        {
            asynchronous = false;
            if (state == null || car == null) return false;

            if (EasySaveSettings.EnableCarCheckpoint)
                car.transform.SetPositionAndRotation(state.carPosition.ToVector3(), state.carRotation.ToQuaternion());

            if (!state.hasActiveDelivery || state.stage == DeliveryStage.None ||
                !EasySaveSettings.EnableDeliveryStateRestore)
                return true;

            logger.LogInfo($"EasySave: restore requested stage={state.stage} payload={state.job?.payloadIndex ?? -1} " +
                           $"savedPrice={state.job?.price ?? 0f:F2} from={state.job?.fromNodeName ?? "null"} " +
                           $"to={state.job?.toNodeName ?? "null"}.");

            MonoBehaviour board = ReflectionHelpers.FindActiveBehaviour("jobBoard");
            MonoBehaviour pathFinder = ReflectionHelpers.Get<MonoBehaviour>(board, "navigation") ??
                                       ReflectionHelpers.FindActiveBehaviour("sPathFinder");
            MonoBehaviour payloadManager = ReflectionHelpers.FindActiveBehaviour("PayloadManager");
            RemovePreviousModPayloads(board, logger);

            if (board == null || pathFinder == null || payloadManager == null || state.job == null)
                return Abort(logger, "required game objects or saved job are missing");
            if (ReflectionHelpers.Get(board, "selectedJob") != null || ReflectionHelpers.Get(board, "progress", 0) > 0)
                return Abort(logger, "the game already has a live delivery; it was left untouched");
            if (state.job.isChallenge)
                return Abort(logger, "challenge deliveries are not safe to reconstruct");
            if (state.job.price <= 0f)
                return Abort(logger, "saved job price is not positive");
            if (!state.job.isIntercity && state.job.price > 20f)
                return Abort(logger, $"saved local delivery price {state.job.price:F2} is outside the economy-safe range");
            if (state.stage == DeliveryStage.PayloadActiveOrInHands)
                return Abort(logger, "Stage 2 restore skipped safely; save before pickup or after placing cargo in truck");
            if (state.stage == DeliveryStage.AtDestinationBeforeDelivery &&
                (state.payload == null || state.payload.parentMode != "InTruck"))
                return Abort(logger, "destination payload-out restore is not yet safe");

            JobMatch match = FindMatchingJobRelaxed(board, state.job);
            if (match == null)
            {
                logger.LogInfo("EasySave: no live matching job found, reconstructing from saved state.");
                object savedFrom = FindNode(state.job.fromNodeName, state.job.fromNodePosition);
                object savedTo = FindNode(state.job.toNodeName, state.job.toNodePosition);
                object reconstructed;
                try
                {
                    reconstructed = ReconstructJobFromSave(
                        board, state.job, savedFrom, savedTo, payloadManager, pathFinder, logger);
                }
                catch (Exception exception)
                {
                    return Abort(logger, $"saved job reconstruction failed: {Unwrap(exception).Message}");
                }
                if (reconstructed == null)
                    return Abort(logger, "saved job could not be matched or reconstructed");
                match = new JobMatch
                {
                    Job = reconstructed,
                    Source = RestoredJobSource.ReconstructedFromSave,
                    Score = 0f
                };
            }

            object job = match.Job;
            object fromNode = ReflectionHelpers.Get(job, "from");
            object toNode = ReflectionHelpers.Get(job, "to");
            LogMatchedJob(logger, match, state.job);
            if (match.Source != RestoredJobSource.ReconstructedFromSave)
                LogNonCriticalDifferences(logger, job, state.job);
            try
            {
                ValidateJobEconomy(job, state.job, match.Source);
            }
            catch (Exception exception)
            {
                return Abort(logger, Unwrap(exception).Message);
            }

            object previousJob = ReflectionHelpers.Get(board, "selectedJob");
            int previousProgress = ReflectionHelpers.Get(board, "progress", 0);
            object previousPayload = ReflectionHelpers.Get(board, "currentPayload");
            Vector3 previousSpawnPoint = ReflectionHelpers.Get(board, "packageSpawnPoint", Vector3.zero);
            object previousDestination = ReflectionHelpers.Get(pathFinder, "dest");

            try
            {
                if (state.stage == DeliveryStage.AcceptedGoToPickup)
                {
                    bool restored = RestoreAcceptedStage(board, pathFinder, state, job, fromNode);
                    if (!restored) throw new InvalidOperationException("accepted-stage invariants were not satisfied");
                    RestoreDeliveryResultEconomy(board, job, state.job, match.Source, logger);
                    EconomyRestoreDiagnostics.RegisterRestoredJob(job, match.Source);
                    LogRestoreSuccess(logger, state, match);
                    return true;
                }

                bool started = RestoreInTruckStage(
                    board, pathFinder, state, job, toNode, match, logger,
                    previousJob, previousProgress, previousPayload, previousSpawnPoint, previousDestination,
                    asynchronousCompletion);
                if (!started) throw new InvalidOperationException("in-truck vanilla restore flow could not start");
                asynchronous = true;
                return true;
            }
            catch (Exception exception)
            {
                RollBack(board, pathFinder, logger, previousJob, previousProgress, previousPayload,
                    previousSpawnPoint, previousDestination);
                logger.LogWarning($"EasySave: delivery restore aborted and rolled back: {Unwrap(exception).Message}");
                return false;
            }
        }

        private static bool RestoreAcceptedStage(MonoBehaviour board, MonoBehaviour pathFinder, DeliveryState state,
            object job, object fromNode)
        {
            Transform payloadPivot = ReflectionHelpers.Get<Transform>(board, "payloadParent");
            if (payloadPivot == null || payloadPivot.childCount != 0) return false;

            ReflectionHelpers.Set(board, "selectedJob", job);
            ReflectionHelpers.Set(board, "progress", 1);
            ReflectionHelpers.Set(board, "currentPayload", null);
            ReflectionHelpers.Set(board, "packageSpawnPoint",
                state.packageSpawnPoint != null ? state.packageSpawnPoint.ToVector3() : Vector3.zero);

            if (EasySaveSettings.EnableRouteRestore)
                SetRoute(pathFinder, fromNode);

            object coroutine = ReflectionHelpers.Invoke(board, "CheckJobProgress", false, Vector3.zero);
            if (!(coroutine is IEnumerator iterator)) return false;
            board.StartCoroutine(iterator);

            object liveJob = ReflectionHelpers.Get(board, "selectedJob");
            return ReferenceEquals(liveJob, job) && ReflectionHelpers.Get(board, "progress", 0) == 1 &&
                   ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload")) == null &&
                   payloadPivot.childCount == 0;
        }

        private static bool RestoreInTruckStage(MonoBehaviour board, MonoBehaviour pathFinder, DeliveryState state,
            object job, object toNode, JobMatch match, ManualLogSource logger,
            object previousJob, int previousProgress, object previousPayload, Vector3 previousSpawnPoint,
            object previousDestination, Action<bool> completion)
        {
            if (!EasySaveSettings.EnablePayloadRestore || state.payload == null ||
                state.payload.parentMode != "InTruck" || state.progress < 2)
                return false;

            Transform payloadPivot = ReflectionHelpers.Get<Transform>(board, "payloadParent");
            if (payloadPivot == null || payloadPivot.childCount != 0) return false;

            ReflectionHelpers.Set(board, "selectedJob", job);
            ReflectionHelpers.Set(board, "progress", 0);
            ReflectionHelpers.Set(board, "currentPayload", null);
            ReflectionHelpers.Set(board, "packageSpawnPoint",
                state.packageSpawnPoint != null ? state.packageSpawnPoint.ToVector3() : Vector3.zero);

            object coroutineObject = ReflectionHelpers.Invoke(board, "CheckJobProgress", true, Vector3.zero);
            if (!(coroutineObject is IEnumerator gameFlow)) return false;

            Coroutine runningFlow = null;
            try
            {
                // This starts the compiler-generated coroutine at state zero. The game
                // itself creates PayloadPickup, DetectPayload converts it to the hidden
                // Placed In Truck wrapper, and CheckJobProgress calls StartPayload.
                runningFlow = board.StartCoroutine(gameFlow);
            }
            finally
            {
                MarkRestoreObjects(board, payloadPivot);
            }
            if (runningFlow == null) return false;

            board.StartCoroutine(ValidateInTruckRestore(
                board, pathFinder, payloadPivot, state, job, toNode, match, logger, runningFlow,
                previousJob, previousProgress, previousPayload, previousSpawnPoint, previousDestination,
                completion));
            return true;
        }

        private static IEnumerator ValidateInTruckRestore(MonoBehaviour board, MonoBehaviour pathFinder,
            Transform payloadPivot, DeliveryState state, object job, object toNode, JobMatch match,
            ManualLogSource logger, Coroutine runningFlow,
            object previousJob, int previousProgress, object previousPayload, Vector3 previousSpawnPoint,
            object previousDestination, Action<bool> completion)
        {
            float deadline = Time.unscaledTime + Mathf.Max(2f, EasySaveSettings.RestoreTimeoutSeconds);
            string failure = null;

            while (Time.unscaledTime < deadline)
            {
                bool ready = false;
                try
                {
                    MarkRestoreObjects(board, payloadPivot);
                    GameObject wrapper = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload"));
                    ready = ReferenceEquals(ReflectionHelpers.Get(board, "selectedJob"), job) &&
                            ReflectionHelpers.Get(board, "progress", 0) == 2 &&
                            wrapper != null && !wrapper.activeSelf && wrapper.name == "Placed In Truck" &&
                            payloadPivot != null && payloadPivot.childCount == 1;
                }
                catch (Exception exception)
                {
                    failure = Unwrap(exception).Message;
                    break;
                }

                if (ready) break;
                yield return null;
            }

            bool success = false;
            if (failure == null)
            {
                try
                {
                    GameObject wrapper = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload"));
                    Transform payloadRoot = payloadPivot != null && payloadPivot.childCount == 1
                        ? payloadPivot.GetChild(0) : null;
                    if (wrapper == null || payloadRoot == null || wrapper.activeSelf || wrapper.name != "Placed In Truck")
                        throw new InvalidOperationException("vanilla in-truck lifecycle did not reach its expected state");

                    payloadRoot.gameObject.SetActive(true);
                    if (state.payload.localPosition != null)
                        payloadRoot.localPosition = state.payload.localPosition.ToVector3();
                    if (state.payload.localRotation != null)
                        payloadRoot.localRotation = state.payload.localRotation.ToQuaternion();
                    if (state.payload.worldScale != null)
                        SetWorldScale(payloadRoot, state.payload.worldScale.ToVector3());
                    RestoreRigidbodies(payloadRoot, state.payload.rigidbodies);

                    if (EasySaveSettings.EnableRouteRestore)
                        SetRoute(pathFinder, toNode);

                    float livePrice = ReflectionHelpers.Get(job, "price", 0f);
                    if (!ReferenceEquals(ReflectionHelpers.Get(board, "selectedJob"), job) ||
                        ReflectionHelpers.Get(board, "progress", 0) != 2 ||
                        livePrice <= 0f ||
                        payloadPivot.childCount != 1)
                        throw new InvalidOperationException("post-restore economy or payload invariant failed");

                    ValidateJobEconomy(job, state.job, match.Source);
                    RestoreDeliveryResultEconomy(board, job, state.job, match.Source, logger);
                    EconomyRestoreDiagnostics.RegisterRestoredJob(job, match.Source);
                    success = true;
                    LogRestoreSuccess(logger, state, match);
                }
                catch (Exception exception)
                {
                    failure = Unwrap(exception).Message;
                }
            }

            if (!success)
            {
                if (runningFlow != null) board.StopCoroutine(runningFlow);
                RollBack(board, pathFinder, logger, previousJob, previousProgress, previousPayload,
                    previousSpawnPoint, previousDestination);
                logger.LogWarning($"EasySave: delivery restore aborted and rolled back: " +
                                  $"{failure ?? "vanilla in-truck restore timed out"}.");
            }

            completion?.Invoke(success);
        }

        private static JobMatch FindMatchingJobRelaxed(MonoBehaviour board, SavedJobState saved)
        {
            JobMatch best = null;
            foreach (RestoredJobSource source in new[]
                     { RestoredJobSource.LiveJobs, RestoredJobSource.LiveJobsBackup })
            {
                string fieldName = source == RestoredJobSource.LiveJobs ? "jobs" : "jobsBackup";
                if (!(ReflectionHelpers.Get(board, fieldName) is IEnumerable jobs)) continue;
                foreach (object candidate in jobs)
                {
                    if (!IsRelaxedJobMatch(candidate, saved)) continue;
                    float score = MatchScore(candidate, saved);
                    if (best == null || score < best.Score)
                        best = new JobMatch { Job = candidate, Source = source, Score = score };
                }
            }
            return best;
        }

        private static bool IsRelaxedJobMatch(object candidate, SavedJobState saved)
        {
            if (candidate == null || saved == null) return false;
            object from = ReflectionHelpers.Get(candidate, "from");
            object to = ReflectionHelpers.Get(candidate, "to");
            FieldInfo destinationIndex = ReflectionHelpers.FindField(candidate.GetType(), "destinationIndex");
            bool destinationMatches = destinationIndex == null ||
                                      ReflectionHelpers.Get(candidate, "destinationIndex", -2) == saved.destinationIndex;
            return ReflectionHelpers.Get(candidate, "payloadIndex", -2) == saved.payloadIndex &&
                   NodeMatches(from, saved.fromNodeName, saved.fromNodePosition) &&
                   NodeMatches(to, saved.toNodeName, saved.toNodePosition) &&
                   destinationMatches;
        }

        private static bool NodeMatches(object node, string savedName, SavedVector3 savedPosition)
        {
            if (node == null) return false;
            bool nameMatches = !string.IsNullOrEmpty(savedName) && string.Equals(
                ReflectionHelpers.ObjectName(node), savedName, StringComparison.OrdinalIgnoreCase);
            bool positionMatches = savedPosition != null &&
                (ReflectionHelpers.ObjectPosition(node) - savedPosition.ToVector3()).sqrMagnitude <=
                NodePositionTolerance * NodePositionTolerance;
            return nameMatches || positionMatches;
        }

        private static float MatchScore(object candidate, SavedJobState saved)
        {
            float price = Mathf.Abs(ReflectionHelpers.Get(candidate, "price", 0f) - saved.price);
            float mass = Mathf.Abs(ReflectionHelpers.Get(candidate, "mass", 0f) - saved.mass);
            float distance = Mathf.Abs(ReflectionHelpers.Get(candidate, "distance", 0f) - saved.distance);
            float bonus = Mathf.Abs(ReflectionHelpers.Get(candidate, "bonusDistance", 0f) - saved.bonusDistance);
            return price * 100f + mass * 10f + distance + bonus;
        }

        private static object ReconstructJobFromSave(MonoBehaviour board, SavedJobState saved,
            object fromNode, object toNode, MonoBehaviour payloadManager, MonoBehaviour pathFinder,
            ManualLogSource logger)
        {
            if (fromNode == null || toNode == null || saved == null) return null;
            FieldInfo selectedJobField = ReflectionHelpers.FindField(board.GetType(), "selectedJob");
            Type jobType = selectedJobField?.FieldType;
            if (jobType == null) return null;

            GameObject payloadPrefab = ReflectionHelpers.AsGameObject(
                ReflectionHelpers.Invoke(payloadManager, "GetPayload", saved.payloadIndex));
            if (payloadPrefab == null) return null;
            if (!string.IsNullOrEmpty(saved.payloadPrefabName) &&
                !string.Equals(payloadPrefab.name, saved.payloadPrefabName, StringComparison.OrdinalIgnoreCase))
                return null;

            object job = FormatterServices.GetUninitializedObject(jobType);
            object shop = FindShop(saved.shopName, fromNode);
            if (!saved.isIntercity && shop == null) return null;
            object path = ReflectionHelpers.Invoke(pathFinder, "FindPath", fromNode, toNode);
            if (path == null)
            {
                FieldInfo pathField = ReflectionHelpers.FindField(jobType, "path");
                if (pathField == null) return null;
                path = Activator.CreateInstance(pathField.FieldType);
            }

            SetRequired(job, "shop", shop);
            SetRequired(job, "from", fromNode);
            SetRequired(job, "to", toNode);
            SetRequired(job, "payloadPrefab", payloadPrefab);
            SetRequired(job, "payloadIndex", saved.payloadIndex);
            SetRequired(job, "price", saved.price);
            SetRequired(job, "mass", saved.mass);
            SetRequired(job, "distance", saved.distance);
            SetRequired(job, "bonusDistance", saved.bonusDistance);
            SetRequired(job, "destinationIndex", saved.destinationIndex);
            SetRequired(job, "duration", saved.duration);
            SetRequired(job, "timeStart", saved.timeStart);
            SetRequired(job, "name", saved.name);
            SetRequired(job, "startingCityName", saved.startingCityName);
            SetRequired(job, "destCityName", saved.destCityName);
            SetRequired(job, "isIntercity", saved.isIntercity);
            SetRequired(job, "isChallenge", false);
            SetRequired(job, "challenge", null);
            SetRequired(job, "path", path);

            ValidateJobEconomy(job, saved, RestoredJobSource.ReconstructedFromSave);
            logger.LogInfo($"EasySave: reconstructed job payload={saved.payloadIndex} price={saved.price:F2} " +
                           $"distance={saved.distance:F3} bonusDistance={saved.bonusDistance:F3} " +
                           $"duration={saved.duration:F2} timeStart={saved.timeStart:F2}.");
            return job;
        }

        private static void SetRequired(object target, string fieldName, object value)
        {
            if (!ReflectionHelpers.Set(target, fieldName, value))
                throw new MissingFieldException(target.GetType().FullName, fieldName);
        }

        private static void ValidateJobEconomy(object job, SavedJobState saved, RestoredJobSource source)
        {
            float restoredPrice = ReflectionHelpers.Get(job, "price", -1f);
            if (restoredPrice <= 0f)
                throw new InvalidOperationException($"restored job price is invalid: {restoredPrice:F2}");
            if (!saved.isChallenge && restoredPrice > 20f)
                throw new InvalidOperationException($"restored job price is unsafe: {restoredPrice:F2}");
            if (source == RestoredJobSource.ReconstructedFromSave &&
                Mathf.Abs(restoredPrice - saved.price) > 0.10f)
                throw new InvalidOperationException(
                    $"reconstructed job failed economy sanity check restoredPrice={restoredPrice:F2} " +
                    $"savedPrice={saved.price:F2}");
        }

        private static void RestoreDeliveryResultEconomy(MonoBehaviour board, object job,
            SavedJobState saved, RestoredJobSource source, ManualLogSource logger)
        {
            float distance = source == RestoredJobSource.ReconstructedFromSave
                ? saved.distance : ReflectionHelpers.Get(job, "distance", -1f);
            float bonusDistance = source == RestoredJobSource.ReconstructedFromSave
                ? saved.bonusDistance : ReflectionHelpers.Get(job, "bonusDistance", -1f);
            float mass = source == RestoredJobSource.ReconstructedFromSave
                ? saved.mass : ReflectionHelpers.Get(job, "mass", -1f);
            float jobPrice = ReflectionHelpers.Get(job, "price", -1f);
            if (distance < 0f || bonusDistance < 0f || mass < 0f)
                throw new InvalidOperationException("delivery result metrics are incomplete");

            object calculatedObject = ReflectionHelpers.Invoke(
                board, "CalculatePrice", distance, bonusDistance, mass);
            if (!(calculatedObject is float calculatedPrice) || calculatedPrice <= 0f ||
                Mathf.Abs(calculatedPrice - jobPrice) > 0.15f)
                throw new InvalidOperationException(
                    $"delivery result economy mismatch calculated={calculatedObject ?? "null"} jobPrice={jobPrice:F2}");

            MethodInfo saveResults = board.GetType().GetMethod(
                "SaveResultsData", ReflectionHelpers.AnyInstance, null,
                new[] { typeof(float), typeof(float), typeof(float) }, null);
            if (saveResults == null)
                throw new MissingMethodException(board.GetType().FullName, "SaveResultsData(float, float, float)");
            saveResults.Invoke(board, new object[] { distance, bonusDistance, mass });
            logger.LogInfo($"EasySave: restored payout metrics distance={distance:F3} " +
                           $"bonusDistance={bonusDistance:F3} mass={mass:F3} " +
                           $"calculatedPrice={calculatedPrice:F2}.");
        }

        private static void LogMatchedJob(ManualLogSource logger, JobMatch match, SavedJobState saved)
        {
            object job = match.Job;
            logger.LogInfo($"EasySave: restore job source={match.Source} payload={saved.payloadIndex} " +
                           $"livePrice={ReflectionHelpers.Get(job, "price", 0f):F2} savedPrice={saved.price:F2} " +
                           $"liveDistance={ReflectionHelpers.Get(job, "distance", 0f):F3} " +
                           $"savedDistance={saved.distance:F3}.");
            float livePrice = ReflectionHelpers.Get(job, "price", 0f);
            if (match.Source != RestoredJobSource.ReconstructedFromSave &&
                Mathf.Abs(livePrice - saved.price) > EconomyTolerance)
                logger.LogWarning($"EasySave: matched live job with different price live={livePrice:F2} " +
                                  $"saved={saved.price:F2}; using live job price.");
        }

        private static void LogNonCriticalDifferences(ManualLogSource logger, object job, SavedJobState saved)
        {
            float liveDistance = ReflectionHelpers.Get(job, "distance", 0f);
            float liveBonus = ReflectionHelpers.Get(job, "bonusDistance", 0f);
            float liveDuration = ReflectionHelpers.Get(job, "duration", 0f);
            float liveStart = ReflectionHelpers.Get(job, "timeStart", 0f);
            if (Mathf.Abs(liveDistance - saved.distance) > EconomyTolerance ||
                Mathf.Abs(liveBonus - saved.bonusDistance) > EconomyTolerance ||
                Mathf.Abs(liveDuration - saved.duration) > 1f ||
                Mathf.Abs(liveStart - saved.timeStart) > 1f)
                logger.LogWarning("EasySave: matched job has non-critical timing/distance differences; " +
                                  "live economy fields were preserved.");
        }

        private static void LogRestoreSuccess(ManualLogSource logger, DeliveryState state, JobMatch match)
        {
            logger.LogInfo($"EasySave: restored delivery state stage={state.stage} " +
                           $"payload={state.job.payloadIndex} price={ReflectionHelpers.Get(match.Job, "price", 0f):F2} " +
                           $"jobSource={match.Source} economySafe=true.");
        }

        private static object FindNode(string name, SavedVector3 savedPosition)
        {
            Vector3 position = savedPosition != null ? savedPosition.ToVector3() : Vector3.zero;
            object best = null;
            float bestScore = float.MaxValue;
            foreach (MonoBehaviour node in ReflectionHelpers.FindBehaviours("sMapNode"))
            {
                if (!node.gameObject.scene.IsValid() || !node.gameObject.activeInHierarchy) continue;
                float score = (node.transform.position - position).sqrMagnitude;
                if (!string.IsNullOrEmpty(name) &&
                    !string.Equals(node.name, name, StringComparison.OrdinalIgnoreCase))
                    score += 1000000f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = node;
                }
            }
            return best;
        }

        private static object FindShop(string name, object fromNode)
        {
            object fallback = null;
            foreach (MonoBehaviour shop in ReflectionHelpers.FindBehaviours("ShopInfo"))
            {
                if (!shop.gameObject.scene.IsValid() || !shop.gameObject.activeInHierarchy) continue;
                if (ReferenceEquals(ReflectionHelpers.Get(shop, "node"), fromNode)) fallback = shop;
                if (!string.IsNullOrEmpty(name) &&
                    string.Equals(shop.name, name, StringComparison.OrdinalIgnoreCase))
                    return shop;
            }
            return fallback;
        }

        private static void SetRoute(MonoBehaviour pathFinder, object destination)
        {
            if (destination == null) throw new InvalidOperationException("route destination is missing");
            ReflectionHelpers.Invoke(pathFinder, "SetDestination", destination);
            object actual = ReflectionHelpers.Get(pathFinder, "dest");
            if (actual == null) throw new InvalidOperationException("SetDestination did not register a destination");
        }

        private static void MarkRestoreObjects(MonoBehaviour board, Transform payloadPivot)
        {
            GameObject wrapper = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload"));
            if (wrapper != null && wrapper.GetComponent<EasySavePayloadMarker>() == null)
                wrapper.AddComponent<EasySavePayloadMarker>();
            if (payloadPivot == null) return;
            for (int i = 0; i < payloadPivot.childCount; i++)
            {
                GameObject child = payloadPivot.GetChild(i).gameObject;
                if (child.GetComponent<EasySavePayloadMarker>() == null)
                    child.AddComponent<EasySavePayloadMarker>();
            }
        }

        private static void RollBack(MonoBehaviour board, MonoBehaviour pathFinder, ManualLogSource logger,
            object previousJob, int previousProgress, object previousPayload, Vector3 previousSpawnPoint,
            object previousDestination)
        {
            RemovePreviousModPayloads(board, logger);
            ReflectionHelpers.Set(board, "selectedJob", previousJob);
            ReflectionHelpers.Set(board, "progress", previousProgress);
            ReflectionHelpers.Set(board, "currentPayload", previousPayload);
            ReflectionHelpers.Set(board, "packageSpawnPoint", previousSpawnPoint);
            try
            {
                if (previousDestination != null) ReflectionHelpers.Invoke(pathFinder, "SetDestination", previousDestination);
                else ReflectionHelpers.Set(pathFinder, "dest", null);
            }
            catch { ReflectionHelpers.Set(pathFinder, "dest", previousDestination); }
        }

        private static bool Abort(ManualLogSource logger, string reason)
        {
            logger.LogWarning($"EasySave: delivery restore aborted: {reason}.");
            return false;
        }

        private static Exception Unwrap(Exception exception)
        {
            return exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : exception;
        }

        private static void SetWorldScale(Transform transform, Vector3 worldScale)
        {
            Vector3 parentScale = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            transform.localScale = new Vector3(
                Mathf.Abs(parentScale.x) > 0.0001f ? worldScale.x / parentScale.x : worldScale.x,
                Mathf.Abs(parentScale.y) > 0.0001f ? worldScale.y / parentScale.y : worldScale.y,
                Mathf.Abs(parentScale.z) > 0.0001f ? worldScale.z / parentScale.z : worldScale.z);
        }

        private static void RemovePreviousModPayloads(MonoBehaviour board, ManualLogSource logger)
        {
            GameObject current = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload"));
            var removedIds = new HashSet<int>();
            foreach (EasySavePayloadMarker marker in Resources.FindObjectsOfTypeAll<EasySavePayloadMarker>())
            {
                if (marker == null) continue;
                if (current == marker.gameObject) ReflectionHelpers.Set(board, "currentPayload", null);
                if (removedIds.Add(marker.gameObject.GetInstanceID())) UnityEngine.Object.Destroy(marker.gameObject);
            }
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject == null ||
                    !gameObject.name.StartsWith("EasySave_RestoredPayload", StringComparison.OrdinalIgnoreCase) ||
                    !removedIds.Add(gameObject.GetInstanceID())) continue;
                if (current == gameObject) ReflectionHelpers.Set(board, "currentPayload", null);
                UnityEngine.Object.Destroy(gameObject);
            }
            if (removedIds.Count > 0)
                logger.LogWarning($"EasySave: cleaned up {removedIds.Count} mod-restored payload object(s).");
        }

        private static void RestoreRigidbodies(Transform root, List<SavedRigidbodyState> bodies)
        {
            if (bodies == null) return;
            foreach (SavedRigidbodyState saved in bodies)
            {
                Transform child = ReflectionHelpers.ResolveTransformPath(root, saved.childPath);
                if (child == null) continue;
                child.localPosition = saved.localPosition.ToVector3();
                child.localRotation = saved.localRotation.ToQuaternion();
                foreach (Component component in child.GetComponents<Component>())
                {
                    if (component == null || component.GetType().Name != "Rigidbody") continue;
                    SetProperty(component, "isKinematic", saved.isKinematic);
                    SetProperty(component, "useGravity", saved.useGravity);
                    if (!SetProperty(component, "linearVelocity", saved.velocity.ToVector3()))
                        SetProperty(component, "velocity", saved.velocity.ToVector3());
                    SetProperty(component, "angularVelocity", saved.angularVelocity.ToVector3());
                    break;
                }
            }
        }

        private static bool SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name, ReflectionHelpers.AnyInstance);
            if (property == null || !property.CanWrite) return false;
            property.SetValue(target, value, null);
            return true;
        }
    }
}
