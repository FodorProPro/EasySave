using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;

namespace EasySave
{
    internal enum DeliveryStage
    {
        None = 0,
        AcceptedGoToPickup = 1,
        PayloadActiveOrInHands = 2,
        InTruckOrDelivering = 3,
        AtDestinationBeforeDelivery = 4
    }

    [DataContract]
    internal sealed class DeliveryState
    {
        [DataMember(Order = 1)] public int version = 4;
        [DataMember(Order = 2)] public string timestamp;
        [DataMember(Order = 3)] public int sceneBuildIndex;
        [DataMember(Order = 4)] public string sceneName;
        [DataMember(Order = 5)] public SavedVector3 carPosition;
        [DataMember(Order = 6)] public SavedQuaternion carRotation;
        [DataMember(Order = 7)] public bool hasActiveDelivery;
        [DataMember(Order = 8)] public DeliveryStage stage;
        [DataMember(Order = 9)] public int progress;
        [DataMember(Order = 10)] public SavedVector3 packageSpawnPoint;
        [DataMember(Order = 11)] public SavedJobState job;
        [DataMember(Order = 12)] public SavedRouteState route;
        [DataMember(Order = 13)] public PayloadState payload;
        [DataMember(Order = 14)] public List<string> warnings = new List<string>();
    }

    [DataContract]
    internal sealed class SavedJobState
    {
        [DataMember(Order = 1)] public string shopName;
        [DataMember(Order = 2)] public string fromNodeName;
        [DataMember(Order = 3)] public SavedVector3 fromNodePosition;
        [DataMember(Order = 4)] public string toNodeName;
        [DataMember(Order = 5)] public SavedVector3 toNodePosition;
        [DataMember(Order = 6)] public int payloadIndex;
        [DataMember(Order = 7)] public string payloadPrefabName;
        [DataMember(Order = 8)] public float price;
        [DataMember(Order = 9)] public float mass;
        [DataMember(Order = 10)] public float distance;
        [DataMember(Order = 11)] public float bonusDistance;
        [DataMember(Order = 12)] public int destinationIndex;
        [DataMember(Order = 13)] public float duration;
        [DataMember(Order = 14)] public float timeStart;
        [DataMember(Order = 15)] public string name;
        [DataMember(Order = 16)] public string startingCityName;
        [DataMember(Order = 17)] public string destCityName;
        [DataMember(Order = 18)] public bool isIntercity;
        [DataMember(Order = 19)] public bool isChallenge;
    }

    [DataContract]
    internal sealed class SavedRouteState
    {
        [DataMember(Order = 1)] public string destinationNodeName;
        [DataMember(Order = 2)] public SavedVector3 destinationNodePosition;
    }

    [DataContract]
    internal sealed class PayloadState
    {
        [DataMember(Order = 1)] public int payloadIndex;
        [DataMember(Order = 2)] public string currentPayloadName;
        [DataMember(Order = 3)] public string rootPayloadCloneName;
        [DataMember(Order = 4)] public string parentMode;
        [DataMember(Order = 5)] public SavedVector3 localPosition;
        [DataMember(Order = 6)] public SavedQuaternion localRotation;
        [DataMember(Order = 7)] public SavedVector3 worldPosition;
        [DataMember(Order = 8)] public SavedQuaternion worldRotation;
        [DataMember(Order = 9)] public SavedVector3 localScale;
        [DataMember(Order = 10)] public SavedVector3 worldScale;
        [DataMember(Order = 11)] public bool currentPayloadActive;
        [DataMember(Order = 12)] public SavedVector3 currentPayloadWorldPosition;
        [DataMember(Order = 13)] public SavedQuaternion currentPayloadWorldRotation;
        [DataMember(Order = 14)] public List<SavedRigidbodyState> rigidbodies = new List<SavedRigidbodyState>();
    }

    [DataContract]
    internal sealed class SavedRigidbodyState
    {
        [DataMember(Order = 1)] public string childPath;
        [DataMember(Order = 2)] public SavedVector3 localPosition;
        [DataMember(Order = 3)] public SavedQuaternion localRotation;
        [DataMember(Order = 4)] public SavedVector3 velocity;
        [DataMember(Order = 5)] public SavedVector3 angularVelocity;
        [DataMember(Order = 6)] public bool isKinematic;
        [DataMember(Order = 7)] public bool useGravity;
    }

    [DataContract]
    internal sealed class SavedVector3
    {
        [DataMember(Order = 1)] public float x;
        [DataMember(Order = 2)] public float y;
        [DataMember(Order = 3)] public float z;

        public static SavedVector3 From(Vector3 value)
        {
            return new SavedVector3 { x = value.x, y = value.y, z = value.z };
        }

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
    }

    [DataContract]
    internal sealed class SavedQuaternion
    {
        [DataMember(Order = 1)] public float x;
        [DataMember(Order = 2)] public float y;
        [DataMember(Order = 3)] public float z;
        [DataMember(Order = 4)] public float w;

        public static SavedQuaternion From(Quaternion value)
        {
            return new SavedQuaternion { x = value.x, y = value.y, z = value.z, w = value.w };
        }

        public Quaternion ToQuaternion()
        {
            return new Quaternion(x, y, z, w);
        }
    }
}
