using System;
using System.Collections;
using CustomTypes;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public struct RocketStatePayload : INetworkSerializable
{
    public int tick;
    public float time;
    public bool isActive;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref time);
        serializer.SerializeValue(ref isActive);
    }

    public void Copy(RocketStatePayload state)
    {
        tick = state.tick;
        time = state.time;
        isActive = state.isActive;
    }

    public override bool Equals(object obj)
    {
        if (obj is not RocketStatePayload other) return false;
        float timeThreshold = 0.01f;
        return tick == other.tick &&
                Mathf.Abs(time - other.time) < timeThreshold &&
                isActive == other.isActive;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + tick;
            hash = hash * 23 + time.GetHashCode();
            hash = hash * 23 + isActive.GetHashCode();
            return hash;
        }
    }
}

[Serializable]
public class RocketController : NetworkBehaviour
{
    #region Enablers, Collisions or Triggers

    private void OnCollisionEnter()
    {
        Explode();
    }

    private void OnTriggerEnter(Collider other)
    {
        var car = other.gameObject.GetComponent<CarController>();
        if (other.gameObject.CompareTag("Player") && car.State is CarState.Vulnerable)
        {
            if (IsServer) StartCoroutine(UpdateKills());
            car.OnRocketHit();
        }
    }

    #endregion

    #region Variables

    private static AppConfig APP_CONFIG => AppConfig.Singleton;
    
    private const int TRAJECTORY_DURATION = 1;
    //private const int SPEED = 3;
    private const int START_ROTATION = 65;
    private const int END_ROTATION = 115;
    private const int TRAJECTORY_LENGTH = 60;

    private readonly NetworkVariable<FixedString64Bytes> _playerName = new("default");
    private readonly NetworkVariable<PosAndRotNetworkData> _networkData = new();
    
    [SerializeField] [HideInInspector] private Rigidbody _rigidbody;
    [SerializeField] [HideInInspector] private Vector3 _vel;
    [SerializeField] [HideInInspector] private Vector3 _velRot;
    [SerializeField] [HideInInspector] private Vector3 _endPosition;
    [SerializeField] [HideInInspector] private Vector3 _startPosition;
    [SerializeField] [HideInInspector] private Quaternion _endRotation;
    [SerializeField] [HideInInspector] private Quaternion _startRotation;
    [SerializeField] [HideInInspector] private float _elapsedTime;
    [SerializeField] [HideInInspector] private bool _hasCollision;

    public string PlayerName
    {
        get => _playerName.Value.ToString();
        set => _playerName.Value = value;
    }

    private bool NameIsDefault => PlayerName.Equals("default");

    private ParticleSystem vfx;

    private bool UseDeadReckoning;

    private DeadReckoningSystem deadReckoningSystem;
    private ulong ID;

    #endregion

    #region Unity Callbacks

    public override void OnNetworkSpawn()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _rigidbody.isKinematic = false;

        _startRotation = Quaternion.Euler(START_ROTATION,
            transform.rotation.eulerAngles.y,
            transform.rotation.eulerAngles.z);
        _endRotation = Quaternion.Euler(END_ROTATION,
            transform.rotation.eulerAngles.y,
            transform.rotation.eulerAngles.z);

        _startPosition = transform.position;
        _endPosition = transform.position + transform.forward * TRAJECTORY_LENGTH;

        vfx = GetComponentInChildren<ParticleSystem>();

        vfx.Stop();

        deadReckoningSystem = new DeadReckoningSystem();

        _networkData.OnValueChanged += OnNetworkDataChanged;

        ID = GetComponent<NetworkObject>().NetworkObjectId;

        //Invoke(nameof(Explode), 1f);
    }

    private void OnNetworkDataChanged(PosAndRotNetworkData oldVal, PosAndRotNetworkData newVal)
    {
        if (UseDeadReckoning) 
        {
            deadReckoningSystem.OnServerStateReceived(newVal.Position, newVal.Velocity, newVal.Acceleration, newVal.Timestamp);
        }
        else
        {
            transform.position = newVal.Position;
        }
    }
    // private void FixedUpdate()
    // {
    //     if (!_hasCollision && IsServer)
    //     {
    //         transform.rotation = Quaternion.Lerp(_startRotation, _endRotation, _elapsedTime / TRAJECTORY_DURATION);
    //         transform.Rotate(Vector3.up, 360 * Time.deltaTime * SPEED, Space.Self);
            
    //         transform.position = Vector3.Lerp(_startPosition, _endPosition, _elapsedTime / TRAJECTORY_DURATION);

    //         if (_elapsedTime >= TRAJECTORY_DURATION)
    //         {
    //             Explode();
    //         }

    //         _elapsedTime += Time.deltaTime;

    //         _networkData.Value = new PosAndRotNetworkData()
    //         {
    //             Position = transform.position,
    //             Rotation = transform.rotation.eulerAngles,
    //             Velocity = (transform.position - _startPosition) / Time.deltaTime,
    //             Acceleration = Vector3.zero,
    //             Timestamp = Time.time,
    //             Tick = RaceManager.Instance.networkTimer.CurrentTick,
    //         };
    //     }
    //     else if (!_hasCollision && !_rigidbody.isKinematic)
    //     {
    //         var targetPosition =
    //             Vector3.SmoothDamp(transform.position, _networkData.Value.Position, ref _vel, APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME);
    //         transform.position = targetPosition;

    //         var targetRotation = Quaternion.Euler(
    //             Mathf.SmoothDampAngle(transform.eulerAngles.x, _networkData.Value.Rotation.x, ref _velRot.x,
    //                 APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME),
    //             Mathf.SmoothDampAngle(transform.eulerAngles.y, _networkData.Value.Rotation.y, ref _velRot.y,
    //                 APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME),
    //             Mathf.SmoothDampAngle(transform.eulerAngles.z, _networkData.Value.Rotation.z, ref _velRot.z,
    //                 APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME));
    //         transform.rotation = targetRotation;
    //     }
    // }

    #endregion

    #region Rocket logic

    private IEnumerator UpdateKills()
    {
        yield return new WaitUntil(() => !NameIsDefault);

        var player = RaceManager.Instance.players.Find(p => p.Name == PlayerName);
        player.Kills++;
    }

    private void Explode()
    {
        if (!_hasCollision)
        {
            _hasCollision = true;

            GetComponent<SphereCollider>().enabled = true;
            GetComponent<MeshRenderer>().enabled = false;
            vfx.Play();

            //StartCoroutine(Disappear());
        }
    }

    public void UpdateMovement(float dt)
    {
        // Quaternion targetRot = Quaternion.Lerp(_startRotation, _endRotation, _elapsedTime / TRAJECTORY_DURATION);
        // transform.Rotate(Vector3.up, 360 * Time.deltaTime * SPEED, Space.Self);
        
        Vector3 targetPosition = Vector3.Lerp(_startPosition, _endPosition, _elapsedTime / TRAJECTORY_DURATION);
        _rigidbody.MovePosition(targetPosition);

        if (_elapsedTime >= TRAJECTORY_DURATION)
        {
            Explode();
        }

        _elapsedTime += dt;
    }

    public void ProcessFixedRocketController()
    {
        if (IsServer)
        {
            UpdateMovement(RaceManager.Instance.networkTimer.MinTimeBetweenTicks);
            ServerSendStateRpc();
            RaceManager.Instance.PendRocket(ID, new RocketStatePayload()
            {
                tick = RaceManager.Instance.networkTimer.CurrentTick,
                time = _elapsedTime,
                isActive = gameObject.activeSelf,
            }, this);
        }
        else
        {
            if (UseDeadReckoning)
            {
                Vector3 targetPos = deadReckoningSystem.CalculateTargetPosition(APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME);
        
                if (deadReckoningSystem.CurrentCorrectionMode == DeadReckoningSystem.CorrectionMode.SmoothDamp)
                {
                    Vector3 smoothPos = deadReckoningSystem.SmoothDampPosition(transform.position, targetPos, deadReckoningSystem.Vel, APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME, Time.fixedDeltaTime);
                    _rigidbody.MovePosition(smoothPos);
                    _rigidbody.velocity = deadReckoningSystem.Vel;
                }
            }
            else
            {
                
                //transform.rotation = Quaternion.Euler(_networkData.Value.Rotation);
            }
        }
    }

    [Rpc(SendTo.Server)]
    public void ServerSendStateRpc()
    {
        _networkData.Value = new PosAndRotNetworkData()
        {
            Position = transform.position,
            Rotation = transform.rotation.eulerAngles,
            Velocity = (transform.position - _startPosition) / Time.deltaTime,
            Acceleration = Vector3.zero,
            Timestamp = Time.time,
            Tick = RaceManager.Instance.networkTimer.CurrentTick,
        };
    }

    private IEnumerator Disappear()
    {
        yield return new WaitForSeconds(0.25f);

        GetComponent<SphereCollider>().enabled = false;
        GetComponent<BoxCollider>().enabled = false;

        yield return new WaitUntil(() => !NameIsDefault);
        yield return new WaitForSeconds(5);

        DespawnRocketRpc();
    }

    [Rpc(SendTo.Server)]
    private void DespawnRocketRpc()
    {
        NetworkObject.Despawn();
    }

    public void OnShoot(Vector3 spawnPos, Quaternion spawnRot)
    {
        _rigidbody = GetComponent<Rigidbody>();
        _rigidbody.isKinematic = false;

        _startRotation = Quaternion.Euler(START_ROTATION,
            spawnRot.eulerAngles.y,
            spawnRot.eulerAngles.z);
        _endRotation = Quaternion.Euler(END_ROTATION,
            spawnRot.eulerAngles.y,
            spawnRot.eulerAngles.z);

        _startPosition = spawnPos;
        _endPosition = spawnPos + spawnRot * Vector3.forward * TRAJECTORY_LENGTH;

        transform.position = _startPosition;
        transform.rotation = _startRotation;

        vfx = GetComponentInChildren<ParticleSystem>();

        vfx.Stop();

        deadReckoningSystem = new DeadReckoningSystem();
    }

    public void ApplyState(RocketStatePayload state)
    {
        _elapsedTime = state.time;
        gameObject.SetActive(state.isActive);
        Vector3 targetPosition = Vector3.Lerp(_startPosition, _endPosition, _elapsedTime / TRAJECTORY_DURATION);
        transform.position = targetPosition;
    }

    #endregion
}