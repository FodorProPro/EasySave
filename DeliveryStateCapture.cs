using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EasySave
{
    internal static class DeliveryStateCapture
    {
        internal static DeliveryState Capture(MonoBehaviour car, ManualLogSource logger)
        {
            Scene scene = SceneManager.GetActiveScene();
            var state = new DeliveryState
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                sceneBuildIndex = scene.buildIndex,
                sceneName = scene.name,
                carPosition = SavedVector3.From(car.transform.position),
                carRotation = SavedQuaternion.From(car.transform.rotation),
                packageSpawnPoint = SavedVector3.From(Vector3.zero)
            };

            MonoBehaviour board = ReflectionHelpers.FindActiveBehaviour("jobBoard");
            if (board == null)
            {
                logger.LogWarning("EasySave: jobBoard not found; saved car state without delivery state.");
                return state;
            }

            object selectedJob = ReflectionHelpers.Get(board, "selectedJob");
            state.hasActiveDelivery = selectedJob != null;
            state.progress = ReflectionHelpers.Get(board, "progress", 0);
            state.packageSpawnPoint = SavedVector3.From(ReflectionHelpers.Get(board, "packageSpawnPoint", Vector3.zero));

            if (!state.hasActiveDelivery || state.progress <= 0)
            {
                state.hasActiveDelivery = false;
                state.stage = DeliveryStage.None;
                logger.LogInfo("EasySave: saved delivery state (no active delivery).");
                return state;
            }

            state.job = CaptureJob(selectedJob);
            state.route = CaptureRoute(board);
            GameObject currentPayload = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload"));
            Transform payloadParent = ReflectionHelpers.Get<Transform>(board, "payloadParent");
            state.stage = ClassifyStage(car, state.progress, currentPayload, payloadParent, selectedJob);
            state.payload = CapturePayload(board, selectedJob);
            logger.LogInfo($"EasySave: captured delivery state stage={state.stage}, progress={state.progress}, " +
                           $"payload={state.job.payloadIndex}, mode={state.payload?.parentMode ?? "None"}.");
            return state;
        }

        private static DeliveryStage ClassifyStage(MonoBehaviour car, int progress, GameObject currentPayload,
            Transform payloadParent, object selectedJob)
        {
            bool payloadInPivot = payloadParent != null && payloadParent.childCount > 0;
            string payloadName = currentPayload != null ? currentPayload.name : string.Empty;
            if (progress == 1 && currentPayload == null && !payloadInPivot)
                return DeliveryStage.AcceptedGoToPickup;

            if (progress == 1 && currentPayload != null && !payloadInPivot)
                return DeliveryStage.PayloadActiveOrInHands;

            if (progress >= 2)
            {
                object destination = ReflectionHelpers.Get(selectedJob, "to");
                float distance = destination != null
                    ? Vector3.Distance(car.transform.position, ReflectionHelpers.ObjectPosition(destination))
                    : float.MaxValue;
                if (distance <= 35.0f && currentPayload != null)
                    return DeliveryStage.AtDestinationBeforeDelivery;
                if (payloadInPivot || payloadName.IndexOf("Placed In Truck", StringComparison.OrdinalIgnoreCase) >= 0)
                    return DeliveryStage.InTruckOrDelivering;
            }

            return currentPayload != null
                ? DeliveryStage.PayloadActiveOrInHands
                : DeliveryStage.AcceptedGoToPickup;
        }

        private static SavedJobState CaptureJob(object job)
        {
            object from = ReflectionHelpers.Get(job, "from");
            object to = ReflectionHelpers.Get(job, "to");
            object shop = ReflectionHelpers.Get(job, "shop");
            GameObject prefab = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(job, "payloadPrefab"));
            return new SavedJobState
            {
                shopName = ReflectionHelpers.ObjectName(shop),
                fromNodeName = ReflectionHelpers.ObjectName(from),
                fromNodePosition = SavedVector3.From(ReflectionHelpers.ObjectPosition(from)),
                toNodeName = ReflectionHelpers.ObjectName(to),
                toNodePosition = SavedVector3.From(ReflectionHelpers.ObjectPosition(to)),
                payloadIndex = ReflectionHelpers.Get(job, "payloadIndex", -1),
                payloadPrefabName = prefab != null ? prefab.name : null,
                price = ReflectionHelpers.Get(job, "price", 0.0f),
                mass = ReflectionHelpers.Get(job, "mass", 0.0f),
                distance = ReflectionHelpers.Get(job, "distance", 0.0f),
                bonusDistance = ReflectionHelpers.Get(job, "bonusDistance", 0.0f),
                destinationIndex = ReflectionHelpers.Get(job, "destinationIndex", -1),
                duration = ReflectionHelpers.Get(job, "duration", 0.0f),
                timeStart = ReflectionHelpers.Get(job, "timeStart", 0.0f),
                name = ReflectionHelpers.Get<string>(job, "name"),
                startingCityName = ReflectionHelpers.Get<string>(job, "startingCityName"),
                destCityName = ReflectionHelpers.Get<string>(job, "destCityName"),
                isIntercity = ReflectionHelpers.Get(job, "isIntercity", false),
                isChallenge = ReflectionHelpers.Get(job, "isChallenge", false)
            };
        }

        private static SavedRouteState CaptureRoute(MonoBehaviour board)
        {
            object navigation = ReflectionHelpers.Get(board, "navigation") ??
                                ReflectionHelpers.FindActiveBehaviour("sPathFinder");
            object destination = ReflectionHelpers.Get(navigation, "dest");
            if (destination == null) return null;
            return new SavedRouteState
            {
                destinationNodeName = ReflectionHelpers.ObjectName(destination),
                destinationNodePosition = SavedVector3.From(ReflectionHelpers.ObjectPosition(destination))
            };
        }

        private static PayloadState CapturePayload(MonoBehaviour board, object selectedJob)
        {
            GameObject currentPayload = ReflectionHelpers.AsGameObject(ReflectionHelpers.Get(board, "currentPayload"));
            Transform payloadParent = ReflectionHelpers.Get<Transform>(board, "payloadParent");
            Transform root = FindPayloadRoot(currentPayload, payloadParent);
            if (root == null) return null;

            Transform relativeParent = null;
            string mode = "World";
            if (payloadParent != null && root.IsChildOf(payloadParent))
            {
                mode = "InTruck";
                relativeParent = payloadParent;
            }
            else
            {
                Transform packagePoint = FindAncestorByName(root, "packagePoint") ??
                                         FindAncestorByName(currentPayload != null ? currentPayload.transform : null, "packagePoint");
                if (packagePoint != null)
                {
                    mode = "InHands";
                    relativeParent = packagePoint;
                }
            }

            Vector3 localPosition = relativeParent != null
                ? relativeParent.InverseTransformPoint(root.position)
                : root.localPosition;
            Quaternion localRotation = relativeParent != null
                ? Quaternion.Inverse(relativeParent.rotation) * root.rotation
                : root.localRotation;

            var payload = new PayloadState
            {
                payloadIndex = ReflectionHelpers.Get(selectedJob, "payloadIndex", -1),
                currentPayloadName = currentPayload != null ? currentPayload.name : root.name,
                rootPayloadCloneName = root.name,
                parentMode = mode,
                localPosition = SavedVector3.From(localPosition),
                localRotation = SavedQuaternion.From(localRotation),
                worldPosition = SavedVector3.From(root.position),
                worldRotation = SavedQuaternion.From(root.rotation),
                localScale = SavedVector3.From(root.localScale),
                worldScale = SavedVector3.From(root.lossyScale),
                currentPayloadActive = currentPayload == null || currentPayload.activeSelf,
                currentPayloadWorldPosition = SavedVector3.From(
                    currentPayload != null ? currentPayload.transform.position : root.position),
                currentPayloadWorldRotation = SavedQuaternion.From(
                    currentPayload != null ? currentPayload.transform.rotation : root.rotation)
            };

            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != "Rigidbody") continue;
                string path = ReflectionHelpers.TransformPath(root, component.transform);
                if (path == null) continue;
                payload.rigidbodies.Add(new SavedRigidbodyState
                {
                    childPath = path,
                    localPosition = SavedVector3.From(component.transform.localPosition),
                    localRotation = SavedQuaternion.From(component.transform.localRotation),
                    velocity = SavedVector3.From(ReadVectorProperty(component, "linearVelocity", "velocity")),
                    angularVelocity = SavedVector3.From(ReadVectorProperty(component, "angularVelocity")),
                    isKinematic = ReadBoolProperty(component, "isKinematic"),
                    useGravity = ReadBoolProperty(component, "useGravity")
                });
            }
            return payload;
        }

        private static Transform FindPayloadRoot(GameObject currentPayload, Transform payloadParent)
        {
            if (currentPayload != null && currentPayload.name.IndexOf("Placed In Truck", StringComparison.OrdinalIgnoreCase) >= 0 &&
                payloadParent != null && payloadParent.childCount > 0)
                return payloadParent.GetChild(0);

            if (currentPayload != null)
            {
                Transform child = FindPayloadNamedChild(currentPayload.transform);
                return child ?? currentPayload.transform;
            }

            // A child under payloadParent without currentPayload can be the game's
            // pickup/setup object. Treating it as carried cargo would create a
            // duplicate when restoring a job that has not been picked up yet.
            return null;
        }

        private static Transform FindPayloadNamedChild(Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name.StartsWith("PAYLOAD", StringComparison.OrdinalIgnoreCase)) return child;
            }
            return null;
        }

        private static Transform FindAncestorByName(Transform transform, string name)
        {
            while (transform != null)
            {
                if (string.Equals(transform.name, name, StringComparison.OrdinalIgnoreCase)) return transform;
                transform = transform.parent;
            }
            return null;
        }

        private static Vector3 ReadVectorProperty(object target, params string[] names)
        {
            foreach (string name in names)
            {
                PropertyInfo property = target.GetType().GetProperty(name, ReflectionHelpers.AnyInstance);
                if (property?.GetValue(target, null) is Vector3 value) return value;
            }
            return Vector3.zero;
        }

        private static bool ReadBoolProperty(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, ReflectionHelpers.AnyInstance);
            return property?.GetValue(target, null) is bool value && value;
        }
    }
}
