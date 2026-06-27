using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EasySave
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.easydeliveryco.easysave";
        public const string PluginName = "EasySave";
        public const string PluginVersion = "1.0.0";

        private const string SaveSystemTypeName = "sSaveSystem";
        private const string CarControllerTypeName = "sCarController";
        private const string SceneIndexKey = "deliveryCurrentLastMapBuildIndex";
        private const string CheckpointPositionKey = "deliveryCurrentCheckpointPosition";
        private const float CheckpointYOffset = 1.0f;
        private const float NotificationDuration = 2.5f;
        private const float NotificationFadeInDuration = 0.18f;
        private const float NotificationFadeOutDuration = 0.30f;
        private const float NotificationWidth = 460.0f;
        private const float WeatherHudSideMargin = 264.0f;
        private const float WeatherHudIconCenterOffset = 64.0f;
        private const float WeatherHudTopMargin = 44.0f;

        private string notificationText;
        private float notificationStartedAt;
        private float notificationExpiresAt;
        private GameObject notificationRoot;
        private RectTransform notificationPanel;
        private CanvasGroup notificationCanvasGroup;
        private Text notificationLabel;
        private RawImage notificationIcon;
        private Texture2D fallbackIconTexture;
        private MonoBehaviour cachedGameplayCar;
        private MonoBehaviour cachedHud;
        private MonoBehaviour cachedMainMenuHud;
        private MonoBehaviour cachedPauseMenu;
        private MonoBehaviour cachedCameraPause;
        private FieldInfo pauseSystemPausedField;
        private FieldInfo sceneTransitionLoadingField;
        private FieldInfo cameraPausePausedField;
        private int cachedHudSceneHandle = int.MinValue;
        private float nextHudObjectRefreshAt;
        private Coroutine restoreCoroutine;
        private string lastRestoredTimestamp;
        private int lastRestoredSceneHandle = int.MinValue;
        private DeliveryState trackedDeliveryState;
        private MonoBehaviour trackedJobBoard;
        private float nextDeliveryStateCheckAt;
        private bool isRestoringDelivery;
        private bool deliveryRestoreDisabledForSession;
        private Harmony harmony;

        private void Awake()
        {
            EasySaveSettings.Initialize(Config);
            EconomyRestoreDiagnostics.Initialize(Logger);
            try
            {
                harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception exception)
            {
                Logger.LogWarning($"EasySave: economy diagnostic patch could not be installed: {exception.Message}");
            }
            ResolveHudStateFields();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Press F5 to save the current checkpoint.");
        }

        private void Start()
        {
            QueueStateRestore(SceneManager.GetActiveScene());
        }

        private void Update()
        {
            if (!ShouldShowHudToast())
            {
                HideNotificationImmediately();
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                SaveCurrentCheckpoint();
            }

            UpdateNotification();
            MonitorTrackedDeliveryState();
        }

        private void LateUpdate()
        {
            if (!ShouldShowHudToast())
            {
                HideNotificationImmediately();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            cachedHudSceneHandle = int.MinValue;
            trackedJobBoard = null;
            QueueStateRestore(scene);
        }

        private void QueueStateRestore(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            if (restoreCoroutine != null) StopCoroutine(restoreCoroutine);
            restoreCoroutine = StartCoroutine(RestoreSavedStateWhenReady(scene));
        }

        private IEnumerator RestoreSavedStateWhenReady(Scene scene)
        {
            restoreCoroutine = null;
            yield return null;
            yield return null;
            if (!DeliveryStateStore.TryLoad(out DeliveryState state, out string loadError))
            {
                if (!string.IsNullOrEmpty(loadError))
                    Logger.LogWarning($"EasySave: state file could not be loaded: {loadError}");
                yield break;
            }

            if (state.sceneBuildIndex != scene.buildIndex ||
                (state.timestamp == lastRestoredTimestamp && scene.handle == lastRestoredSceneHandle))
                yield break;

            float deadline = Time.unscaledTime + Mathf.Max(1.0f, EasySaveSettings.RestoreTimeoutSeconds);
            MonoBehaviour car = null;
            MonoBehaviour board = null;
            while (Time.unscaledTime < deadline)
            {
                if (SceneManager.GetActiveScene().handle != scene.handle) yield break;
                MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                car = FindActiveBehaviour(behaviours, CarControllerTypeName);
                board = FindActiveBehaviour(behaviours, "jobBoard");
                bool deliveryRequested = state.hasActiveDelivery && EasySaveSettings.EnableDeliveryStateRestore &&
                                         !deliveryRestoreDisabledForSession;
                bool deliveryReady = !deliveryRequested ||
                                     (board != null && FindActiveBehaviour(behaviours, "sPathFinder") != null &&
                                      FindActiveBehaviour(behaviours, "PayloadManager") != null &&
                                      (state.payload == null || state.payload.parentMode != "InTruck" ||
                                       ReflectionHelpers.Get<Transform>(board, "payloadParent") != null));
                if (car != null && deliveryReady) break;
                yield return new WaitForSecondsRealtime(
                    Mathf.Max(0.05f, EasySaveSettings.RestoreRetryIntervalSeconds));
            }

            if (car == null)
            {
                Logger.LogWarning("EasySave: restore timed out waiting for the gameplay car.");
                yield break;
            }

            yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, EasySaveSettings.RestoreRetryIntervalSeconds));
            car = ReflectionHelpers.FindActiveBehaviour(CarControllerTypeName);
            if (car == null || SceneManager.GetActiveScene().handle != scene.handle) yield break;

            bool asynchronousRestore = false;
            try
            {
                if (EasySaveSettings.BackupBeforeRestore && state.hasActiveDelivery &&
                    EasySaveSettings.EnableDeliveryStateRestore && !deliveryRestoreDisabledForSession)
                    DeliveryStateStore.BackupBeforeRestore();

                isRestoringDelivery = true;
                bool restored = DeliveryStateRestore.Restore(state, car, Logger, out asynchronousRestore,
                    success =>
                    {
                        isRestoringDelivery = false;
                        if (!success)
                        {
                            trackedDeliveryState = null;
                            trackedJobBoard = null;
                            if (EasySaveSettings.DisableRestoreAfterFailure)
                                deliveryRestoreDisabledForSession = true;
                        }
                    });
                if (!restored)
                {
                    if (EasySaveSettings.DisableRestoreAfterFailure)
                        deliveryRestoreDisabledForSession = true;
                    yield break;
                }
                lastRestoredTimestamp = state.timestamp;
                lastRestoredSceneHandle = scene.handle;
                trackedDeliveryState = state;
                trackedJobBoard = board;
            }
            catch (Exception exception)
            {
                if (EasySaveSettings.DisableRestoreAfterFailure)
                    deliveryRestoreDisabledForSession = true;
                Logger.LogWarning($"EasySave: delivery restore failed safely: {exception}");
            }
            finally
            {
                if (!asynchronousRestore)
                    isRestoringDelivery = false;
            }
        }

        private void MonitorTrackedDeliveryState()
        {
            if (isRestoringDelivery || trackedDeliveryState == null || !trackedDeliveryState.hasActiveDelivery ||
                Time.unscaledTime < nextDeliveryStateCheckAt)
                return;

            nextDeliveryStateCheckAt = Time.unscaledTime + 0.5f;
            MonoBehaviour board = trackedJobBoard;
            if (board == null) return;

            object liveJob = ReflectionHelpers.Get(board, "selectedJob");
            bool completedOrCancelled = liveJob == null && ReflectionHelpers.Get(board, "progress", 0) == 0;
            bool differentJob = liveJob != null && trackedDeliveryState.job != null &&
                                (ReflectionHelpers.Get(liveJob, "payloadIndex", -1) != trackedDeliveryState.job.payloadIndex ||
                                 !string.Equals(
                                     ReflectionHelpers.ObjectName(ReflectionHelpers.Get(liveJob, "from")),
                                     trackedDeliveryState.job.fromNodeName,
                                     StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(
                                     ReflectionHelpers.ObjectName(ReflectionHelpers.Get(liveJob, "to")),
                                     trackedDeliveryState.job.toNodeName,
                                     StringComparison.OrdinalIgnoreCase));

            if (!completedOrCancelled && !differentJob) return;

            trackedDeliveryState.hasActiveDelivery = false;
            trackedDeliveryState.progress = 0;
            trackedDeliveryState.job = null;
            trackedDeliveryState.route = null;
            trackedDeliveryState.payload = null;
            trackedDeliveryState.timestamp = DateTime.UtcNow.ToString("o");
            try
            {
                DeliveryStateStore.Save(trackedDeliveryState);
                Logger.LogInfo("EasySave: cleared stale delivery checkpoint after the live job ended or changed.");
            }
            catch (Exception exception)
            {
                Logger.LogWarning($"EasySave: could not clear stale delivery checkpoint: {exception.Message}");
            }
        }

        private void SaveCurrentCheckpoint()
        {
            MonoBehaviour[] behaviours;

            try
            {
                behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            }
            catch (Exception exception)
            {
                ShowNotification("Save failed");
                Logger.LogError($"Failed to scan game objects: {exception}");
                return;
            }

            MonoBehaviour saveSystem = FindSaveSystem(behaviours);
            if (saveSystem == null)
            {
                ShowNotification("Save system not found");
                Logger.LogWarning("Manual save skipped: sSaveSystem was not found.");
                return;
            }

            MonoBehaviour car = FindActiveCar(behaviours);
            if (car == null)
            {
                ShowNotification("Car not found");
                Logger.LogWarning("Manual save skipped: no active sCarController was found.");
                return;
            }

            Vector3 checkpointPosition = car.transform.position + Vector3.up * CheckpointYOffset;
            int sceneIndex = SceneManager.GetActiveScene().buildIndex;

            try
            {
                InvokeNativeSave(saveSystem, sceneIndex, checkpointPosition);
                if (EasySaveSettings.EnableDeliveryStateCapture)
                {
                    try
                    {
                        DeliveryState state = DeliveryStateCapture.Capture(car, Logger);
                        DeliveryStateStore.Save(state);
                        trackedDeliveryState = state;
                        trackedJobBoard = FindActiveBehaviour(behaviours, "jobBoard");
                        Logger.LogInfo($"EasySave: wrote state file {DeliveryStateStore.StatePath}.");
                    }
                    catch (Exception stateException)
                    {
                        Logger.LogWarning($"EasySave: native save succeeded, but mod state could not be saved: {stateException}");
                    }
                }
                ShowNotification("Game saved");
                Logger.LogInfo(
                    $"Saved checkpoint: scene={sceneIndex}, " +
                    $"pos=({checkpointPosition.x:F2}, {checkpointPosition.y:F2}, {checkpointPosition.z:F2})");
            }
            catch (Exception exception)
            {
                Exception loggedException = exception is TargetInvocationException invocationException &&
                                            invocationException.InnerException != null
                    ? invocationException.InnerException
                    : exception;

                ShowNotification("Save failed");
                Logger.LogError($"Manual save failed: {loggedException}");
            }
        }

        private MonoBehaviour FindSaveSystem(MonoBehaviour[] behaviours)
        {
            MonoBehaviour firstMatch = null;
            MonoBehaviour activeMatch = null;

            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour.GetType().Name != SaveSystemTypeName)
                {
                    continue;
                }

                if (firstMatch == null)
                {
                    firstMatch = behaviour;
                }

                if (IsSingletonInstance(behaviour))
                {
                    return behaviour;
                }

                if (activeMatch == null && IsActiveInHierarchy(behaviour))
                {
                    activeMatch = behaviour;
                }
            }

            return activeMatch != null ? activeMatch : firstMatch;
        }

        private static MonoBehaviour FindActiveCar(MonoBehaviour[] behaviours)
        {
            return FindActiveBehaviour(behaviours, CarControllerTypeName);
        }

        private static MonoBehaviour FindActiveBehaviour(MonoBehaviour[] behaviours, string typeName)
        {
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour != null &&
                    behaviour.GetType().Name == typeName &&
                    IsActiveInHierarchy(behaviour))
                {
                    return behaviour;
                }
            }

            return null;
        }

        private static bool IsActiveInHierarchy(MonoBehaviour behaviour)
        {
            try
            {
                return behaviour.gameObject != null && behaviour.gameObject.activeInHierarchy;
            }
            catch (MissingReferenceException)
            {
                return false;
            }
        }

        private bool IsSingletonInstance(MonoBehaviour candidate)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance |
                                           BindingFlags.Static |
                                           BindingFlags.Public |
                                           BindingFlags.NonPublic;
                FieldInfo instanceField = candidate.GetType().GetField("instance", flags);
                if (instanceField == null)
                {
                    return false;
                }

                object value = instanceField.GetValue(instanceField.IsStatic ? null : candidate);
                return ReferenceEquals(value, candidate) ||
                       (value is UnityEngine.Object unityObject && unityObject == candidate);
            }
            catch (Exception exception)
            {
                Logger.LogDebug($"Could not inspect sSaveSystem.instance: {exception.Message}");
                return false;
            }
        }

        private static void InvokeNativeSave(MonoBehaviour saveSystem, int sceneIndex, Vector3 position)
        {
            Type saveSystemType = saveSystem.GetType();
            const BindingFlags flags = BindingFlags.Instance |
                                       BindingFlags.Static |
                                       BindingFlags.Public |
                                       BindingFlags.NonPublic;

            MethodInfo setInt = saveSystemType.GetMethod(
                "SetInt",
                flags,
                null,
                new[] { typeof(string), typeof(int) },
                null);
            MethodInfo setVector3 = saveSystemType.GetMethod(
                "SetVector3",
                flags,
                null,
                new[] { typeof(string), typeof(Vector3) },
                null);
            MethodInfo saveData = saveSystemType.GetMethod(
                "SaveData",
                flags,
                null,
                Type.EmptyTypes,
                null);

            if (setInt == null)
            {
                throw new MissingMethodException(saveSystemType.FullName, "SetInt(string, int)");
            }

            if (setVector3 == null)
            {
                throw new MissingMethodException(saveSystemType.FullName, "SetVector3(string, Vector3)");
            }

            if (saveData == null)
            {
                throw new MissingMethodException(saveSystemType.FullName, "SaveData()");
            }

            if (EasySaveSettings.EnableCarCheckpoint)
            {
                setInt.Invoke(setInt.IsStatic ? null : saveSystem, new object[] { SceneIndexKey, sceneIndex });
                setVector3.Invoke(setVector3.IsStatic ? null : saveSystem, new object[] { CheckpointPositionKey, position });
            }
            if (EasySaveSettings.EnableNativeSave)
                saveData.Invoke(saveData.IsStatic ? null : saveSystem, null);
        }

        private void ResolveHudStateFields()
        {
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            Type pauseSystemType = Type.GetType("PauseSystem, Assembly-CSharp", false);
            Type sceneTransitionType = Type.GetType("SceneTransition, Assembly-CSharp", false);
            Type cameraPauseType = Type.GetType("CameraPause, Assembly-CSharp", false);

            pauseSystemPausedField = pauseSystemType?.GetField("paused", staticFlags);
            sceneTransitionLoadingField = sceneTransitionType?.GetField("loadingScene", staticFlags);
            cameraPausePausedField = cameraPauseType?.GetField("paused", instanceFlags);
        }

        private bool ShouldShowHudToast()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded || IsMenuOnlyScene(activeScene.name))
            {
                return false;
            }

            RefreshHudStateObjectsIfNeeded(activeScene.handle);

            if (!IsEnabledInHierarchy(cachedGameplayCar) || !IsEnabledInHierarchy(cachedHud))
            {
                return false;
            }

            if (ReadBoolField(pauseSystemPausedField, null) ||
                ReadBoolField(sceneTransitionLoadingField, null) ||
                ReadBoolField(cameraPausePausedField, cachedCameraPause))
            {
                return false;
            }

            if (IsEnabledInHierarchy(cachedMainMenuHud) ||
                IsEnabledInHierarchy(cachedPauseMenu))
            {
                return false;
            }

            // Fallback for menu implementations that pause time without exposing
            // one of the game flags above.
            return Time.timeScale > 0.0001f;
        }

        private void RefreshHudStateObjectsIfNeeded(int activeSceneHandle)
        {
            float now = Time.unscaledTime;
            if (cachedHudSceneHandle == activeSceneHandle && now < nextHudObjectRefreshAt)
            {
                return;
            }

            cachedHudSceneHandle = activeSceneHandle;
            nextHudObjectRefreshAt = now + 0.5f;
            cachedGameplayCar = null;
            cachedHud = null;
            cachedMainMenuHud = null;
            cachedPauseMenu = null;
            cachedCameraPause = null;

            try
            {
                MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                foreach (MonoBehaviour behaviour in behaviours)
                {
                    if (behaviour == null)
                    {
                        continue;
                    }

                    Type type = behaviour.GetType();
                    switch (type.Name)
                    {
                        case CarControllerTypeName:
                            if (cachedGameplayCar == null && IsEnabledInHierarchy(behaviour))
                            {
                                cachedGameplayCar = behaviour;
                            }
                            break;
                        case "sHUD":
                            if (cachedHud == null && IsEnabledInHierarchy(behaviour))
                            {
                                cachedHud = behaviour;
                            }
                            break;
                        case "MainMenuHUD":
                            cachedMainMenuHud = cachedMainMenuHud ?? behaviour;
                            break;
                        case "PauseMenuURP":
                            cachedPauseMenu = cachedPauseMenu ?? behaviour;
                            break;
                        case "CameraPause":
                            cachedCameraPause = cachedCameraPause ?? behaviour;
                            cameraPausePausedField = cameraPausePausedField ?? type.GetField("paused", instanceFlags);
                            break;
                        case "PauseSystem":
                            pauseSystemPausedField = pauseSystemPausedField ?? type.GetField("paused", staticFlags);
                            break;
                        case "SceneTransition":
                            sceneTransitionLoadingField = sceneTransitionLoadingField ?? type.GetField("loadingScene", staticFlags);
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.LogDebug($"Could not refresh gameplay HUD state: {exception.Message}");
            }
        }

        private static bool IsMenuOnlyScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return true;
            }

            return sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   sceneName.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   sceneName.IndexOf("loading", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsEnabledInHierarchy(MonoBehaviour behaviour)
        {
            try
            {
                return behaviour != null && behaviour.enabled &&
                       behaviour.gameObject != null && behaviour.gameObject.activeInHierarchy;
            }
            catch (MissingReferenceException)
            {
                return false;
            }
        }

        private static bool ReadBoolField(FieldInfo field, object target)
        {
            try
            {
                return field != null && field.GetValue(field.IsStatic ? null : target) is bool value && value;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void HideNotificationImmediately()
        {
            notificationText = null;

            if (notificationCanvasGroup != null)
            {
                notificationCanvasGroup.alpha = 0.0f;
            }

            if (notificationPanel != null && notificationPanel.gameObject.activeSelf)
            {
                notificationPanel.gameObject.SetActive(false);
            }
        }

        private void ShowNotification(string message)
        {
            if (!ShouldShowHudToast())
            {
                HideNotificationImmediately();
                return;
            }

            EnsureNotificationUi();

            notificationText = message;
            notificationStartedAt = Time.unscaledTime;
            notificationExpiresAt = Time.unscaledTime + NotificationDuration;
            notificationLabel.text = notificationText;
            ApplyNotificationIcon();
            notificationCanvasGroup.alpha = 0.0f;
            notificationPanel.localScale = new Vector3(0.94f, 0.94f, 1.0f);
            notificationPanel.gameObject.SetActive(true);
        }

        private void UpdateNotification()
        {
            if (!ShouldShowHudToast())
            {
                HideNotificationImmediately();
                return;
            }

            if (notificationPanel == null || !notificationPanel.gameObject.activeSelf)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now >= notificationExpiresAt)
            {
                notificationPanel.gameObject.SetActive(false);
                notificationText = null;
                return;
            }

            float elapsed = now - notificationStartedAt;
            float remaining = notificationExpiresAt - now;
            float alpha = 1.0f;
            float scale = 1.0f;

            if (elapsed < NotificationFadeInDuration)
            {
                float progress = Mathf.Clamp01(elapsed / NotificationFadeInDuration);
                progress = Mathf.SmoothStep(0.0f, 1.0f, progress);
                alpha = progress;
                scale = Mathf.Lerp(0.94f, 1.0f, progress);
            }
            else if (remaining < NotificationFadeOutDuration)
            {
                float progress = Mathf.Clamp01(remaining / NotificationFadeOutDuration);
                alpha = Mathf.SmoothStep(0.0f, 1.0f, progress);
                scale = Mathf.Lerp(0.97f, 1.0f, alpha);
            }

            notificationCanvasGroup.alpha = alpha;
            notificationPanel.localScale = new Vector3(scale, scale, 1.0f);
        }

        private void EnsureNotificationUi()
        {
            if (notificationRoot != null)
            {
                return;
            }

            notificationRoot = new GameObject("EasySave Notification");
            notificationRoot.transform.SetParent(transform, false);

            Canvas canvas = notificationRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            CanvasScaler scaler = notificationRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920.0f, 1080.0f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.0f;

            GameObject panelObject = CreateUiObject("Panel", notificationRoot.transform);
            notificationPanel = panelObject.GetComponent<RectTransform>();
            notificationPanel.anchorMin = new Vector2(0.0f, 1.0f);
            notificationPanel.anchorMax = new Vector2(0.0f, 1.0f);
            notificationPanel.pivot = new Vector2(0.5f, 1.0f);
            notificationPanel.anchoredPosition = new Vector2(
                WeatherHudSideMargin + WeatherHudIconCenterOffset,
                -WeatherHudTopMargin);
            notificationPanel.sizeDelta = new Vector2(NotificationWidth, 164.0f);

            notificationCanvasGroup = panelObject.AddComponent<CanvasGroup>();
            notificationCanvasGroup.interactable = false;
            notificationCanvasGroup.blocksRaycasts = false;

            notificationIcon = CreateUiObject("Floppy disk", notificationPanel).AddComponent<RawImage>();
            RectTransform iconRect = notificationIcon.rectTransform;
            iconRect.anchorMin = new Vector2(0.5f, 1.0f);
            iconRect.anchorMax = new Vector2(0.5f, 1.0f);
            iconRect.pivot = new Vector2(0.5f, 1.0f);
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(96.0f, 96.0f);
            notificationIcon.color = Color.white;
            notificationIcon.raycastTarget = false;

            notificationLabel = CreateUiObject("Text", notificationPanel).AddComponent<Text>();
            RectTransform textRect = notificationLabel.rectTransform;
            textRect.anchorMin = new Vector2(0.0f, 1.0f);
            textRect.anchorMax = new Vector2(1.0f, 1.0f);
            textRect.pivot = new Vector2(0.5f, 1.0f);
            textRect.anchoredPosition = new Vector2(0.0f, -108.0f);
            textRect.sizeDelta = new Vector2(0.0f, 48.0f);
            notificationLabel.font = FindBestFont();
            notificationLabel.fontSize = 30;
            notificationLabel.fontStyle = FontStyle.Normal;
            notificationLabel.alignment = TextAnchor.MiddleCenter;
            notificationLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            notificationLabel.verticalOverflow = VerticalWrapMode.Truncate;
            notificationLabel.color = Color.white;
            notificationLabel.raycastTarget = false;

            Shadow textShadow = notificationLabel.gameObject.AddComponent<Shadow>();
            textShadow.effectColor = new Color32(0, 0, 0, 180);
            textShadow.effectDistance = new Vector2(2.0f, -2.0f);

            notificationPanel.gameObject.SetActive(false);
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private void ApplyNotificationIcon()
        {
            Texture gameSpriteSheet = FindGameSpriteSheet();
            if (gameSpriteSheet != null && gameSpriteSheet.width >= 32 && gameSpriteSheet.height >= 192)
            {
                const float iconX = 16.0f;
                const float iconYFromTop = 176.0f;
                const float iconSize = 16.0f;
                notificationIcon.texture = gameSpriteSheet;
                notificationIcon.uvRect = new Rect(
                    iconX / gameSpriteSheet.width,
                    1.0f - (iconYFromTop + iconSize) / gameSpriteSheet.height,
                    iconSize / gameSpriteSheet.width,
                    iconSize / gameSpriteSheet.height);
                return;
            }

            notificationIcon.texture = CreateFallbackIcon();
            notificationIcon.uvRect = new Rect(0.0f, 0.0f, 1.0f, 1.0f);
        }

        private Texture FindGameSpriteSheet()
        {
            try
            {
                MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                foreach (MonoBehaviour behaviour in behaviours)
                {
                    if (behaviour == null || behaviour.GetType().Name != "DesktopDotExe")
                    {
                        continue;
                    }

                    FieldInfo spriteSheetField = behaviour.GetType().GetField("spriteSheet", flags);
                    if (spriteSheetField?.GetValue(behaviour) is Texture desktopSpriteSheet)
                    {
                        return desktopSpriteSheet;
                    }
                }

                foreach (MonoBehaviour behaviour in behaviours)
                {
                    if (behaviour == null || behaviour.GetType().Name != "sHUD")
                    {
                        continue;
                    }

                    FieldInfo rendererField = behaviour.GetType().GetField("R", flags);
                    object renderer = rendererField?.GetValue(behaviour);
                    FieldInfo spriteSheetField = renderer?.GetType().GetField("spriteSheet", flags);
                    if (spriteSheetField?.GetValue(renderer) is Texture hudSpriteSheet)
                    {
                        return hudSpriteSheet;
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.LogDebug($"Could not reuse the game UI sprite sheet: {exception.Message}");
            }

            return null;
        }

        private Texture2D CreateFallbackIcon()
        {
            if (fallbackIconTexture != null)
            {
                return fallbackIconTexture;
            }

            const int size = 16;
            Color32 transparent = new Color32(0, 0, 0, 0);
            Color32 light = new Color32(239, 235, 215, 255);
            Color32 dark = new Color32(31, 43, 45, 255);
            fallbackIconTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            fallbackIconTexture.name = "EasySave fallback floppy disk";
            fallbackIconTexture.filterMode = FilterMode.Point;
            fallbackIconTexture.wrapMode = TextureWrapMode.Clamp;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool body = x >= 2 && x <= 13 && y >= 1 && y <= 14;
                    fallbackIconTexture.SetPixel(x, y, body ? light : transparent);
                }
            }

            for (int y = 9; y <= 13; y++)
            {
                for (int x = 4; x <= 11; x++)
                {
                    fallbackIconTexture.SetPixel(x, y, dark);
                }
            }

            for (int y = 10; y <= 12; y++)
            {
                for (int x = 5; x <= 9; x++)
                {
                    fallbackIconTexture.SetPixel(x, y, light);
                }
            }

            for (int y = 3; y <= 6; y++)
            {
                for (int x = 4; x <= 11; x++)
                {
                    fallbackIconTexture.SetPixel(x, y, dark);
                }
            }

            fallbackIconTexture.SetPixel(10, 4, light);
            fallbackIconTexture.SetPixel(10, 5, light);
            fallbackIconTexture.Apply(false, true);
            return fallbackIconTexture;
        }

        private static Font FindBestFont()
        {
            Font bestFont = null;
            int bestScore = int.MinValue;

            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font == null || !SupportsEnglish(font))
                {
                    continue;
                }

                string fontName = font.name.ToLowerInvariant().Replace(" ", string.Empty);
                int score = 0;
                if (fontName.Contains("lanapixel")) score += 1000;
                if (fontName.Contains("perfectdosvga")) score += 900;
                if (fontName.Contains("pixel")) score += 500;
                if (fontName.Contains("mono")) score += 50;
                if (fontName.Contains("console") || fontName.Contains("terminal")) score += 40;
                if (fontName.Contains("font")) score += 10;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFont = font;
                }
            }

            if (bestFont != null)
            {
                return bestFont;
            }

            try
            {
                return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch
            {
                return Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
        }

        private static bool SupportsEnglish(Font font)
        {
            try
            {
                return font.HasCharacter('G') && font.HasCharacter('a') && font.HasCharacter('e');
            }
            catch
            {
                return false;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            harmony?.UnpatchSelf();
            if (restoreCoroutine != null)
            {
                StopCoroutine(restoreCoroutine);
            }

            if (notificationRoot != null)
            {
                Destroy(notificationRoot);
            }

            if (fallbackIconTexture != null)
            {
                Destroy(fallbackIconTexture);
            }
        }
    }
}
