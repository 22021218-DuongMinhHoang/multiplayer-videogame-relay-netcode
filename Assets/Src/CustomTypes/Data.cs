using System;
using Unity.Netcode;
using UnityEngine;

namespace CustomTypes
{
    [Serializable]
    internal struct PosAndRotNetworkData : INetworkSerializable
    {
        [SerializeField] [HideInInspector] private float _x, _y, _z;
        [SerializeField] [HideInInspector] private float _xr, _yr, _zr;
        [SerializeField] [HideInInspector] private float _timestamp; // Added Timestamp

        internal Vector3 Position
        {
            get => new(_x, _y, _z);
            set
            {
                _x = value.x;
                _y = value.y;
                _z = value.z;
            }
        }

        internal Vector3 Rotation
        {
            get => new(_xr, _yr, _zr);
            set
            {
                _xr = value.x;
                _yr = value.y;
                _zr = value.z;
            }
        }

        internal float Timestamp
        {
            get => _timestamp;
            set => _timestamp = value;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref _x);
            serializer.SerializeValue(ref _y);
            serializer.SerializeValue(ref _z);

            serializer.SerializeValue(ref _xr);
            serializer.SerializeValue(ref _yr);
            serializer.SerializeValue(ref _zr);
            
            serializer.SerializeValue(ref _timestamp); // Serialize Timestamp
        }
    }

    [Serializable]
    public class AxleInfo
    {
        [SerializeField] public WheelCollider leftWheel, rightWheel;
        [SerializeField] public bool motor, steering;
    }
}