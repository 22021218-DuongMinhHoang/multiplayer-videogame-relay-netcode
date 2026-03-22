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
        [SerializeField] [HideInInspector] private float _vx, _vy, _vz;
        [SerializeField] [HideInInspector] private float _ax, _ay, _az;
        [SerializeField] [HideInInspector] private float _timestamp;
        [SerializeField] [HideInInspector] private int tick;

        [SerializeField] [HideInInspector] private float speed;

        [SerializeField] [HideInInspector] private int collisionCount;

        internal Vector3 Position
        {
            get => new(_x, _y, _z);
            set { _x = value.x; _y = value.y; _z = value.z; }
        }

        internal Vector3 Rotation
        {
            get => new(_xr, _yr, _zr);
            set { _xr = value.x; _yr = value.y; _zr = value.z; }
        }
        
        internal Vector3 Velocity
        {
            get => new(_vx, _vy, _vz);
            set { _vx = value.x; _vy = value.y; _vz = value.z; }
        }

        // Thêm Property Acceleration
        internal Vector3 Acceleration
        {
            get => new(_ax, _ay, _az);
            set { _ax = value.x; _ay = value.y; _az = value.z; }
        }

        internal float Timestamp
        {
            get => _timestamp;
            set => _timestamp = value;
        }

        internal int Tick
        {
            get => tick;
            set => tick = value;
        }

        internal float Speed
        {
            get => speed;
            set => speed = value;
        }

        internal int CollisionCount
        {
            get => collisionCount;
            set => collisionCount = value;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref _x);
            serializer.SerializeValue(ref _y);
            serializer.SerializeValue(ref _z);

            serializer.SerializeValue(ref _xr);
            serializer.SerializeValue(ref _yr);
            serializer.SerializeValue(ref _zr);
            
            serializer.SerializeValue(ref _vx);
            serializer.SerializeValue(ref _vy);
            serializer.SerializeValue(ref _vz);
            
            // Serialize Acceleration
            serializer.SerializeValue(ref _ax); 
            serializer.SerializeValue(ref _ay); 
            serializer.SerializeValue(ref _az); 
            
            serializer.SerializeValue(ref _timestamp);

            serializer.SerializeValue(ref tick);

            serializer.SerializeValue(ref speed);

            serializer.SerializeValue(ref collisionCount);
        }
    }

    [Serializable]
    public class AxleInfo
    {
        [SerializeField] public WheelCollider leftWheel, rightWheel;
        [SerializeField] public bool motor, steering;
    }
}