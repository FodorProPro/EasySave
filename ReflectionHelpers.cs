using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace EasySave
{
    internal static class ReflectionHelpers
    {
        internal const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name, AnyInstance | BindingFlags.Static);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        internal static object Get(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = FindField(target.GetType(), name);
            return field?.GetValue(field.IsStatic ? null : target);
        }

        internal static T Get<T>(object target, string name, T fallback = default(T))
        {
            object value = Get(target, name);
            if (value is T typed) return typed;
            if (value == null) return fallback;
            try { return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        internal static bool Set(object target, string name, object value)
        {
            if (target == null) return false;
            FieldInfo field = FindField(target.GetType(), name);
            if (field == null) return false;
            field.SetValue(field.IsStatic ? null : target, value);
            return true;
        }

        internal static object Invoke(object target, string name, params object[] args)
        {
            if (target == null) return null;
            foreach (MethodInfo method in target.GetType().GetMethods(AnyInstance))
            {
                if (method.Name != name) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != args.Length) continue;
                bool compatible = true;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (compatible) return method.Invoke(target, args);
            }
            return null;
        }

        internal static MonoBehaviour FindActiveBehaviour(string typeName)
        {
            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour != null && behaviour.GetType().Name == typeName &&
                    behaviour.enabled && behaviour.gameObject.activeInHierarchy)
                {
                    return behaviour;
                }
            }
            return null;
        }

        internal static List<MonoBehaviour> FindBehaviours(string typeName)
        {
            var result = new List<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour != null && behaviour.GetType().Name == typeName)
                    result.Add(behaviour);
            }
            return result;
        }

        internal static GameObject AsGameObject(object value)
        {
            if (value is GameObject gameObject) return gameObject;
            if (value is Component component) return component.gameObject;
            return null;
        }

        internal static Vector3 ObjectPosition(object value)
        {
            GameObject gameObject = AsGameObject(value);
            return gameObject != null ? gameObject.transform.position : Vector3.zero;
        }

        internal static string ObjectName(object value)
        {
            return value is UnityEngine.Object unityObject ? unityObject.name : null;
        }

        internal static string TransformPath(Transform root, Transform child)
        {
            if (root == child) return string.Empty;
            var parts = new List<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                parts.Add(current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
                current = current.parent;
            }
            if (current != root) return null;
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        internal static Transform ResolveTransformPath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return root;
            Transform current = root;
            foreach (string part in path.Split('/'))
            {
                if (!int.TryParse(part, out int index) || index < 0 || index >= current.childCount)
                    return null;
                current = current.GetChild(index);
            }
            return current;
        }

        internal static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
