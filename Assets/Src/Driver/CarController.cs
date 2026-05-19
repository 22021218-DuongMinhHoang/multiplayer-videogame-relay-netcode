using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CustomTypes;
using TMPro;
using Unity.Netcode;
using UnityEngine;

public struct InputPayload : INetworkSerializable
{
    public int tick;
    public float inputAcceleration;
    public float inputSteering;
    public float inputBrake;
    public bool isCollide;
    public bool isShoot;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref inputAcceleration);
        serializer.SerializeValue(ref inputSteering);
        serializer.SerializeValue(ref inputBrake);
        serializer.SerializeValue(ref isCollide);
        serializer.SerializeValue(ref isShoot);
    }

    public void Copy(InputPayload input)
    {
        tick = input.tick;
        inputAcceleration = input.inputAcceleration;
        inputSteering = input.inputSteering;
        inputBrake = input.inputBrake;
        isCollide = input.isCollide;
        isShoot = input.isShoot;
    }

    public override bool Equals(object obj)
    {
        if (obj is not InputPayload other) return false;
        return tick == other.tick &&
               Mathf.Approximately(inputAcceleration, other.inputAcceleration) &&
               Mathf.Approximately(inputSteering, other.inputSteering) &&
               Mathf.Approximately(inputBrake, other.inputBrake) &&
               isCollide == other.isCollide &&
                isShoot == other.isShoot;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + tick;
            hash = hash * 23 + inputAcceleration.GetHashCode();
            hash = hash * 23 + inputSteering.GetHashCode();
            hash = hash * 23 + inputBrake.GetHashCode();
            hash = hash * 23 + (isCollide ? 1 : 0);
            hash = hash * 23 + isShoot.GetHashCode();
            return hash;
        }
    }
}

public struct StatePayload : INetworkSerializable
{
    public int tick;
    public Vector3 position;
    public Quaternion rotation;
    public float speed;

    public int collisionCount;

    public bool isShoot;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref position);
        serializer.SerializeValue(ref rotation);
        serializer.SerializeValue(ref speed);
        serializer.SerializeValue(ref collisionCount);
        serializer.SerializeValue(ref isShoot);
    }

    public void Copy(StatePayload state)
    {
        tick = state.tick;
        position = state.position;
        rotation = state.rotation;
        speed = state.speed;
        collisionCount = state.collisionCount;
        isShoot = state.isShoot;
    }

    public override bool Equals(object obj)
    {
        if (obj is not StatePayload other) return false;
        float posThreshold = 0.01f;
        float angThreshold = 1f;
        return tick == other.tick &&
               Vector3.Distance(position, other.position) <= posThreshold &&
               Quaternion.Angle(rotation, other.rotation) <= angThreshold &&
               Mathf.Approximately(speed, other.speed)
               && collisionCount == other.collisionCount
               && isShoot == other.isShoot;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + tick;
            hash = hash * 23 + position.GetHashCode();
            hash = hash * 23 + rotation.GetHashCode();
            hash = hash * 23 + speed.GetHashCode();
            hash = hash * 23 + collisionCount.GetHashCode();
            hash = hash * 23 + isShoot.GetHashCode();
            return hash;
        }
    }
}

public struct ShootContext : INetworkSerializable
{
    public bool hasTarget;
    public int targetId;
    public Vector3 shooterPosition;
    public Vector3 shooterForward;
    public Vector3 targetPosition;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref hasTarget);
        serializer.SerializeValue(ref targetId);
        serializer.SerializeValue(ref shooterPosition);
        serializer.SerializeValue(ref shooterForward);
        serializer.SerializeValue(ref targetPosition);
    }
}

[Serializable]
public class CarController : NetworkBehaviour
{
    #region Enablers, Collisions or Triggers
    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.CompareTag("Finish") && Laps <= GameManager.Instance.NumLaps)
        {
            if (NetworkPlayer.CurrentLap.Equals(0))
            {
                NetworkPlayer.lastLapPos = NetworkPlayer.CurrentPos;
                NetworkPlayer.OnCurrentPosChangeEvent += NetworkPlayer.OnCurrentPosChange;
                Laps++;
            }
            else if (NetworkPlayer.checkpointAchieved)
            {
                NetworkPlayer.checkpointAchieved = false;
                NetworkPlayer.lastLapPos = NetworkPlayer.CurrentPos;
                NetworkPlayer.OnCurrentPosChangeEvent += NetworkPlayer.OnCurrentPosChange;
                Laps++;
            }
        }
    }
    #endregion

    #region Variables

    private static AppConfig APP_CONFIG => AppConfig.Singleton;

    [Header("Car Properties")]
    [SerializeField] public string carName;
    [SerializeField] public Material translucentMaterial;
    [SerializeField] public GameObject PlayerPanel;
    [SerializeField] public GameObject RocketPanel;
    [SerializeField] public TMP_Text playerTag;
    [SerializeField] public TMP_Text rocketTag;
    [SerializeField] public List<MeshRenderer> carMeshes;

    private readonly NetworkVariable<int> _networkSpeed = new();
    private readonly NetworkVariable<PosAndRotNetworkData> _networkData = new(
        writePerm: NetworkVariableWritePermission.Server
    );
    private List<Material[]> _originalMaterials;

    [SerializeField] [HideInInspector] private int _laps;
    [SerializeField] [HideInInspector] private float _lapTime;
    [SerializeField] [HideInInspector] private Vector3 _vel;
    [SerializeField] [HideInInspector] private bool _isOnTrack;
    [SerializeField] [HideInInspector] private Rigidbody _rigidbody;

    [Header("Arcade Movement")]
    [SerializeField] public float maxSpeed = 40f;
    [SerializeField] public float maxReverseSpeed = 20f;
    [SerializeField] public float accelerationRate = 20f;
    [SerializeField] public float decelerationRate = 10f;
    [SerializeField] public float brakeRate = 30f;
    [SerializeField] public float turnSpeed = 120f;

    [SerializeField] public float inputAcceleration;
    [SerializeField] public float inputSteering;
    [SerializeField] public float inputBrake;
    [SerializeField] private float currentSpeed = 0f;

    public NetworkPlayer NetworkPlayer { get; private set; }
    public CarState State { get; private set; }
    public int ID { get; private set; }
    public bool RubberBand { get; private set; } = true;

    [Header("Bot Settings")]
    public bool isBot = false;
    private List<Transform> waypoints = new List<Transform>();
    private int currentWaypointIndex = 0;
    private float waypointThreshold = 5.0f;

    [Header("Auto Shoot Bot")]
    public bool AutoShootBot = false;
    private float autoShootRange = 35f;
    private float autoShootRadius = 3f;
    private float autoShootCooldown = 5f;
    private float autoShootMinForwardDistance = 2f;
    private float nextAutoShootTime = 0f;
    private const float shootMinForwardDistance = 0.25f;

    public bool UseDeadReckoning = true;
    public bool UseServerReconciliation = true;
    public bool UseClientSidePrediction = true;
    public bool UseLagCompensation = true;

    public bool IsOwnerCar => IsOwner;
    private bool UsesOwnerAuthoritativeFallback =>
        IsClient && IsOwner && !IsServer && !UseClientSidePrediction && !UseServerReconciliation;
    private bool ShouldOwnerUseDeadReckoning => UsesOwnerAuthoritativeFallback && UseDeadReckoning;

    [Header("Dead Reckoning Configuration")]
    [SerializeField] private DeadReckoningSystem.DeadReckoningMode currentDRMode = DeadReckoningSystem.DeadReckoningMode.Quadratic;
    [SerializeField] private DeadReckoningSystem.CorrectionMode currentCorrectionMode = DeadReckoningSystem.CorrectionMode.SmoothDamp;

    [Header("Client Side Prediction and Server Reconciliation")]
    
    // Helper systems
    private CircularBuffer<StatePayload> clientStateBuffer;
    private CircularBuffer<InputPayload> clientInputBuffer;
    private InputManager inputManager;
    private CarMovement carMovement;
    public DeadReckoningSystem deadReckoningSystem;
    private ServerReconciliation serverReconciliation;
    private RaceTracker raceTracker;
    
    private int lastProcessedTick = 0;
    private const int BUFFER_SIZE = 1024;
    private const float TICK_RATE = 50f;

    // public bool _useCubicSpline 
    // { 
    //     get => deadReckoningSystem?.UseCubicSpline ?? false;
    //     set { if (deadReckoningSystem != null) deadReckoningSystem.UseCubicSpline = value; }
    // }
    public bool _useAdaptiveThreshold 
    { 
        get => deadReckoningSystem?.UseAdaptiveThreshold ?? true;
        set { if (deadReckoningSystem != null) deadReckoningSystem.UseAdaptiveThreshold = value; }
    }
    public bool _useTimeSync 
    { 
        get => deadReckoningSystem?.UseTimeSync ?? true;
        set { if (deadReckoningSystem != null) deadReckoningSystem.UseTimeSync = value; }
    }

    private Vector3 _serverPos;
    private Vector3 _serverVel;
    private Vector3 _serverAcc;
    private float _lastServerRecvTime;
    private int _lastServerStateTick = -1;
    private Vector3 _targetPos;
    private float _timeOffset = 0f;
    private const float SYNC_ALPHA = 0.05f;

    private Vector3 _p0, _p1, _t0, _t1;
    private float _splineTimer;

    private double _aeeSum = 0.0;
    private long receiveDataCount = 0;
    private long _hitCount = 0;
    private float hitThreshold = 2.5f;
    private float _lastPacketLocalTime;
    private List<float> _packetIntervals = new List<float>();

    private int collisionCounter = 0;
    private int serverCollisionCounter = 0;
    

    Vector3 prevPos;
    float prevVel;
    float prevAcc;
    [SerializeField] JerkCounter jerkCounter;
    [SerializeField] JerkCounter accuracyCounter;

    [Header("Visuals")]
    [SerializeField] private Transform visualTransform;
    [SerializeField] ParticleSystem shootVFX;

    private bool ENABLE_DEBUG_LOG = true;

    private int Laps { get => _laps; set { _laps = value; OnLapsChangeEvent?.Invoke(value); } }
    private int Speed { get => _networkSpeed.Value; set => _networkSpeed.Value = value; }
    private bool IsRacing => State != CarState.Dead && State != CarState.Idle;
    private bool IsRace => GameManager.Instance.CLASSIF_STATES.Contains(NetworkPlayer.CurrentRace);
    private bool IsClassif => GameManager.Instance.RACE_STATES.Contains(NetworkPlayer.CurrentRace);
    public float CurrentAverageAccuracyPercent => accuracyCounter != null ? accuracyCounter.AverageValue * 100f : 0f;
    public float CurrentAverageJerk => jerkCounter != null ? jerkCounter.AverageValue / 1000f : 0f;
    public int CurrentKills => kills;
    public int GetLastProcessedTick() => lastProcessedTick;

    private int ShootDebugTick => RaceManager.Instance?.networkTimer?.CurrentTick ?? -1;

    #endregion

    #region Delegates and Events
    public delegate void OnLapsChangeDelegate(int newVal);
    public event OnLapsChangeDelegate OnLapsChangeEvent;

    private void OnSpeedChange(int oldVal, int newVal) { if (UIManager.Instance != null) UIManager.Instance.gameKph.text = $"{newVal:0}"; }
    private void OnLapsChange(int newVal) {
        if (newVal == 1) _lapTime = 0f;
        _lapTime = GameManager.Instance.raceTime - _lapTime;
        UIManager.Instance.gameLapTime.text = GameManager.Instance.ConvertTimeToString(_lapTime);
        if (newVal.Equals(GameManager.Instance.NumLaps + 1)) SetPlayerEndGame();
        else if (newVal <= GameManager.Instance.NumLaps + 1) UIManager.Instance.gameLaps.text = $"{newVal}/{GameManager.Instance.NumLaps}";
    }
    private void OnPlayerLeft(NetworkPlayer networkPlayer)
    {
        if (NetworkPlayer.Equals(networkPlayer)) return;
        if (RaceManager.Instance.players.Count == 1)
        {
            RaceManager.Instance.SuppressNextFinishLog(NetworkPlayer);
            SetPlayerEndGame(logFinish: false);
        }
    }

    private void OnScreenChange(AppScreen screen) {
        if (screen.Equals(AppScreen.Game)) {
            if (IsServer && IsRace) GameManager.Instance.SpawnItemBox(APP_CONFIG.GAME.ITEM_BOXES_PER_RACE);
            else if (IsServer && IsClassif) GameManager.Instance.DespawnItemBox();

            if (IsClassif || NetworkPlayer.CurrentRace.Equals(RaceState.Schedule)) 
            {
                UIManager.Instance.gameTitle.text = "Classification";
                RocketPanel.SetActive(false);
            }
            else
            {
                UIManager.Instance.gameTitle.text = "Race";
                RocketPanel.SetActive(true);
            }
            MoveToPositionRpc(GameManager.Instance.RACE_POS[NetworkPlayer.StartPos]);

            if (clientStateBuffer != null)
            {
                clientStateBuffer.Add(new StatePayload()
                {
                    tick = 0,
                    position = GameManager.Instance.RACE_POS[NetworkPlayer.StartPos],
                    rotation = Quaternion.Euler(Vector3.zero)
                }, 0);
            }

            SwitchVisibilityRpc();
            ResetStatsRpc();
            NetworkPlayer.Location = "/game"; NetworkPlayer.FinishRawTime = 0f; NetworkPlayer.Rockets = 0; NetworkPlayer.HasFinished = false;
            UIManager.Instance.SetNotificationCanvas(true, "WAITING FOR PLAYERS"); UIManager.Instance.gameLaps.text = ""; UIManager.Instance.matchSummaryController.HasFinished = false;
            OnRocketChange(NetworkPlayer.Rockets); StartCoroutine(CheckIsOnTrack()); NetworkPlayer.IsReady = true;
        }
        if (screen.Equals(AppScreen.Room)) NetworkPlayer.Location = "/room";
    }

    [Rpc(SendTo.Everyone)]
    private void MoveToPositionRpc(Vector3 pos) {
        _rigidbody.isKinematic = true; 
        _rigidbody.position = pos; 
        _rigidbody.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        _serverPos = pos;
        ResetServerMotionSample();
        //if (deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(pos);
    }

    [Rpc(SendTo.Everyone)]
    private void ResetStatsRpc() {
        Laps = 0; NetworkPlayer.lastLapPos = 0f; 
        NetworkPlayer.checkpointAchieved = false; 
        NetworkPlayer.RubberBandCoefficient = 1f;
        ResetCalculationMetrics();
        if (UIManager.Instance != null) 
        { 
            try 
            { 
                UIManager.Instance.averageExportError.text = "0.00"; 
                UIManager.Instance.hitPercentage.text = "0.0%"; 
        // UIManager.Instance.jitterEstimate.text = "0ms";
        //  UIManager.Instance.instantError.text = "0.00m"; 
         } catch (Exception) { } }
    }

    public void ResetCalculationMetrics()
    {
        if (serverReconciliation != null)
            serverReconciliation.ResetMetrics();

        receiveDataCount = 0;
        _hitCount = 0;
        accuracyCounter?.Reset();
    }

    private void OnRocketChange(int newVal) { rocketTag.text = newVal.ToString(); }
    private void OnGameStateChange(GameState oldState, GameState newState) { if (newState.Equals(GameState.Started)) { if (IsOwner) NetworkPlayer.IsRacing = true; State = CarState.Vulnerable; _rigidbody.isKinematic = false; } }
    public void OnRocketHit() { StartCoroutine(InitiateDeath()); StartCoroutine(SwitchVulnerability(3.25f, toVulnerable: false)); StartCoroutine(SwitchVulnerability(8f)); }
    #endregion


    #region Unity Callbacks

    private void Start()
    {
        if (GameManager.Instance != null && GameManager.Instance.waypointContainer != null)
        {
            waypoints = GameManager.Instance.GetLevelWaypoints();
        }

        _rigidbody = GetComponent<Rigidbody>();

        shootVFX = GetComponentInChildren<ParticleSystem>();

        // Initialize helper systems
        clientStateBuffer = new CircularBuffer<StatePayload>(BUFFER_SIZE);
        clientInputBuffer = new CircularBuffer<InputPayload>(BUFFER_SIZE);
        inputManager = new InputManager();
        carMovement = new CarMovement();
        carMovement.SetMovementParameters(maxSpeed, maxReverseSpeed, accelerationRate, decelerationRate, brakeRate, turnSpeed);
        deadReckoningSystem = new DeadReckoningSystem();
        serverReconciliation = new ServerReconciliation();
        raceTracker = new RaceTracker();

        if (IsClient && IsOwner)
        {
            deadReckoningSystem.Reset(transform.position);
        }
    }

    public override void OnNetworkDespawn() {
        try { OnLapsChangeEvent -= NetworkPlayer.OnLapsChange; } catch {}
        try { GameManager.Instance.OnGameStateChange -= OnGameStateChange; } catch {}
        if (IsOwner) {
            try { NetworkPlayer.OnRocketChangeEvent -= OnRocketChange; } catch {}
            try { _networkSpeed.OnValueChanged -= OnSpeedChange; } catch {}
            try { OnLapsChangeEvent -= OnLapsChange; } catch {}
            try { RaceManager.Instance.OnPlayerLeft -= OnPlayerLeft; } catch {}
            try { UIManager.Instance.gameRespawn.onClick.RemoveListener(RespawnInProjPos); } catch {}
            try { EventManager.Instance.ScreenChange.RemoveListener(OnScreenChange); } catch {}

            if (UIManager.Instance != null && UIManager.Instance.botToggle != null)
                UIManager.Instance.botToggle.onValueChanged.RemoveListener(OnBotToggleChanged);
        }
        try { _networkData.OnValueChanged -= OnNetworkDataChanged; } catch {}
    }

    public override void OnNetworkSpawn() {
        State = CarState.Idle; _rigidbody = GetComponent<Rigidbody>(); _rigidbody.isKinematic = true;
        _originalMaterials = new List<Material[]>(); foreach (var mesh in carMeshes) _originalMaterials.Add(mesh.materials);

        if (IsOwner && UIManager.Instance != null && UIManager.Instance.botToggle != null)
        {
            isBot = UIManager.Instance.botToggle.isOn;
            if (isBot) currentWaypointIndex = 0;
            UIManager.Instance.botToggle.onValueChanged.AddListener(OnBotToggleChanged);
        }

        var networkObjects = FindObjectsOfType<NetworkObject>();
        foreach (var networkObject in networkObjects) if (networkObject.IsPlayerObject && networkObject.OwnerClientId == OwnerClientId) {
                NetworkPlayer = networkObject.gameObject.GetComponent<NetworkPlayer>(); NetworkPlayer.car = gameObject; NetworkPlayer.carName = carName; ID = NetworkPlayer.ID;
                OnLapsChangeEvent += NetworkPlayer.OnLapsChange; GameManager.Instance.OnGameStateChange += OnGameStateChange;
                if (IsOwner) {
                    RocketPanel.SetActive(true); NetworkPlayer.OnRocketChangeEvent += OnRocketChange; _networkSpeed.OnValueChanged += OnSpeedChange;
                    OnLapsChangeEvent += OnLapsChange; RaceManager.Instance.OnPlayerLeft += OnPlayerLeft;
                    UIManager.Instance.gameRespawn.onClick.AddListener(RespawnInProjPos);
                    EventManager.Instance.ScreenChange.AddListener(OnScreenChange);
                }
                SetPlayerTag(-1, NetworkPlayer.Name); OnRocketChange(NetworkPlayer.Rockets); SetMainMeshMaterialColor(NetworkPlayer.CarColor); EventManager.Instance.RaisePlayersCarFound(ID);
            }
        
        _networkData.OnValueChanged += OnNetworkDataChanged;
        //if (deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(transform.position);

        if (IsOwner && NetworkPlayer != null) NetworkPlayer.StartPos = NetworkPlayer.ID;

        UIManager.Instance?.SetInitOption(this);
    }

    private void OnBotToggleChanged(bool isOn)
    {
        isBot = isOn;
        if (isBot) currentWaypointIndex = 0;
        else
        {
            inputSteering = 0; inputAcceleration = 0; inputBrake = 0;
            //SubmitBotInputServerRpc(0, 0, 0);
        }
    }

    public void SetAutoShootBot(bool enable)
    {
        AutoShootBot = enable;
        nextAutoShootTime = 0f;
        if (AutoShootBot)
        {
            SetAutoShootBotPose();
        }

        SendAutoShootBotStateToServerRpc(enable);
    }

    [Rpc(SendTo.Server)]
    void SendAutoShootBotStateToServerRpc(bool enable)
    {
        AutoShootBot = enable;
        if (AutoShootBot)
        {
            SetAutoShootBotPose();
            int tick = RaceManager.Instance?.networkTimer?.CurrentTick ?? lastProcessedTick;
            lastProcessedTick = tick;
            RaceManager.Instance?.PendState(ID, GetStateOfCar(tick));
        }
    }

    private void SetAutoShootBotPose()
    {
        if (GameManager.Instance == null || GameManager.Instance.botShootPoint == null) return;

        Vector3 botPosition = GameManager.Instance.botShootPoint.position;
        Quaternion botRotation = Quaternion.Euler(0f, 90f, 0f);

        if (_rigidbody != null)
        {
            _rigidbody.position = botPosition;
            _rigidbody.rotation = botRotation;
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }

        transform.SetPositionAndRotation(botPosition, botRotation);
        currentSpeed = 0f;
    }

    public void SetDRMode(DeadReckoningSystem.DeadReckoningMode mode) 
    { 
        currentDRMode = mode;
        if (deadReckoningSystem != null) deadReckoningSystem.SetDRMode(mode);
    }
    
    public void SetCorrectionMode(int index) 
    { 
        currentCorrectionMode = (DeadReckoningSystem.CorrectionMode)index;
        if (deadReckoningSystem != null) deadReckoningSystem.SetCorrectionMode(index);
    }

    public void SetImprovement(string option, bool value) {
        switch (option) {
            case "Spline":
                //if (deadReckoningSystem != null) deadReckoningSystem.UseCubicSpline = value;
                //if(value && deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(transform.position);
                break;
            case "Adaptive": 
                if (deadReckoningSystem != null) deadReckoningSystem.UseAdaptiveThreshold = value;
                break;
            case "TimeSync": 
                if (deadReckoningSystem != null) deadReckoningSystem.UseTimeSync = value;
                break;
        }
    }

    public void SetAdaptiveThresholdConfig(float baseThreshold, float kvCoeff, float kaCoeff)
    {
        if (deadReckoningSystem != null)
        {
            deadReckoningSystem.SetAdaptiveThresholdConfig(baseThreshold, kvCoeff, kaCoeff);
        }
    }

    CircularBuffer<int> recievedTickBuffer = new CircularBuffer<int>(BUFFER_SIZE);

    private void OnNetworkDataChanged(PosAndRotNetworkData oldVal, PosAndRotNetworkData newVal)
    {
        if (newVal.Position == Vector3.zero)
            return;

        if (newVal.Rewinded)
        {
            transform.position = newVal.Position;
            transform.rotation = Quaternion.Euler(newVal.Rotation);
            currentSpeed = newVal.Speed;

            ResetAll();
            return;
        }

        float measuredError = -1f;
        float rotMeasuredError = -1f;
        bool usesOwnerAuthoritativeFallback = UsesOwnerAuthoritativeFallback;

        if (!IsOwner || ShouldOwnerUseDeadReckoning)
        {
            Vector3 predictedAtServerTimestamp =
                deadReckoningSystem.CalculateTargetPositionAtServerTime(newVal.Timestamp);

            measuredError = Vector3.Distance(predictedAtServerTimestamp, newVal.Position);

            if (ShouldOwnerUseDeadReckoning)
            {
                rotMeasuredError = Quaternion.Angle(transform.rotation, Quaternion.Euler(newVal.Rotation));
            }
        }

        deadReckoningSystem.OnServerStateReceived(
            newVal.Position,
            newVal.Velocity,
            newVal.Acceleration,
            newVal.Timestamp
        );

        if (IsOwner && IsClient)
        {
            serverReconciliation.RecordServerState(
                newVal.Tick,
                newVal.Position,
                Quaternion.Euler(newVal.Rotation),
                newVal.Speed
            );

            if (usesOwnerAuthoritativeFallback)
            {
                currentSpeed = newVal.Speed;
                collisionCounter = newVal.CollisionCount;

                if (!UseDeadReckoning)
                {
                    measuredError = Vector3.Distance(transform.position, newVal.Position);
                    rotMeasuredError = Quaternion.Angle(transform.rotation, Quaternion.Euler(newVal.Rotation));
                    ApplyNetworkStateDirectly(newVal);
                }
            }
        }
        else if (IsClient && !IsOwner)
        {
            if (!UseDeadReckoning)
            {
                measuredError = Vector3.Distance(transform.position, newVal.Position);
                ApplyNetworkStateDirectly(newVal);
            }

            currentSpeed = newVal.Speed;
        }

        if (IsOwner)
        {
            serverCollisionCounter = newVal.CollisionCount;
        }

        if (IsOwner && !usesOwnerAuthoritativeFallback)
        {
            StatePayload predictedState = clientStateBuffer.Get(newVal.Tick);

            if (predictedState.tick == newVal.Tick)
            {
                StatePayload serverState = new StatePayload
                {
                    tick = newVal.Tick,
                    position = newVal.Position,
                    rotation = Quaternion.Euler(newVal.Rotation),
                    speed = newVal.Speed,
                    collisionCount = newVal.CollisionCount,
                    isShoot = newVal.IsShoot
                };

                var (positionError, rotationError) =
                    serverReconciliation.CalculateErrors(predictedState, serverState);

                measuredError = positionError;
                rotMeasuredError = rotationError;

                serverReconciliation.RecordError(positionError);
            }
            else
            {
                measuredError = -1f;
                rotMeasuredError = -1f;
            }
        }

        if (measuredError >= 0f)
        {
            receiveDataCount++;

            bool isHit;

            if (IsOwner)
            {
                bool positionHit = measuredError <= hitThreshold;
                bool rotationHit = rotMeasuredError >= 0f && rotMeasuredError <= 5f;

                isHit = positionHit && rotationHit;
            }
            else
            {
                isHit = measuredError <= hitThreshold;
            }

            if (accuracyCounter == null)
                return;

            if (isHit)
            {
                _hitCount++;
            }

            float positionAccuracy = Mathf.Clamp01(1f - measuredError / hitThreshold);
            float sampleAccuracy = positionAccuracy;

            if (IsOwner && rotMeasuredError >= 0f)
            {
                float rotationAccuracy = Mathf.Clamp01(1f - rotMeasuredError / 5f);
                sampleAccuracy = Mathf.Min(positionAccuracy, rotationAccuracy);
            }

            float averageAccuracy = accuracyCounter.Update(sampleAccuracy);

            if (UIManager.Instance != null)
            {
                try
                {
                    UIManager.Instance.UpdateCarAccuracy(
                        ID,
                        averageAccuracy * 100f
                    );
                }
                catch
                {
                }
            }
        }
    }

    private void ApplyNetworkStateDirectly(PosAndRotNetworkData state)
    {
        transform.position = state.Position;
        transform.rotation = Quaternion.Euler(state.Rotation);
        currentSpeed = state.Speed;
        collisionCounter = state.CollisionCount;
    }

    public void TeleportToStart()
    {
        if (waypoints == null || waypoints.Count < 2) return;

        Vector3 startPos = waypoints[0].position;
        Vector3 nextPos = waypoints[1].position;
        Vector3 lookDir = (nextPos - startPos).normalized;
        Quaternion lookRot = Quaternion.LookRotation(lookDir, Vector3.up);

        _rigidbody.velocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;

        _rigidbody.MovePosition(startPos);
        _rigidbody.MoveRotation(lookRot);

        currentWaypointIndex = 0;
        inputAcceleration = 0;
        inputSteering = 0;
        inputBrake = 1;

        //if (deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(startPos);
        _serverPos = startPos;
        _targetPos = startPos;
        ResetServerMotionSample();
    }

    public (bool, int) ProcessFixedCarController()
    {
        if (AutoShootBot)
        {
            if (IsServer)
            {
                SetAutoShootBotPose();
                lastProcessedTick = RaceManager.Instance.networkTimer.CurrentTick;
            }

            return (false, lastProcessedTick);
        }

        if (IsClient && IsOwner && !_rigidbody.isKinematic)
        {
            ProcessClientPrediction();

            if (ShouldOwnerUseDeadReckoning && _networkData.Value.Position != Vector3.zero)
            {
                ProcessClientDeadReckoning();

                var networkTimer = RaceManager.Instance.networkTimer;

                clientStateBuffer.Add(new StatePayload()
                {
                    tick = networkTimer.CurrentTick,
                    position = transform.position,
                    rotation = transform.rotation,
                    speed = currentSpeed
                }, networkTimer.CurrentTick);
            }
        }

        if (IsServer && !_rigidbody.isKinematic)
        {
            ProcessServerMovement();
        }

        if (Mathf.Abs(currentSpeed) < 0.05f) _rigidbody.velocity = Vector3.zero;

        if (IsServer)
        {
            if (_rigidbody.isKinematic)
            {
                ResetServerMotionSample();
                _networkData.Value = new PosAndRotNetworkData() { Position = Vector3.zero, Rotation = Vector3.zero };
            }
        }
        else if (IsClient && !IsOwner && !_rigidbody.isKinematic && _networkData.Value.Position != Vector3.zero)
        {
            ProcessClientDeadReckoning();
            

            var networkTimer = RaceManager.Instance.networkTimer;

            clientStateBuffer.Add(new StatePayload()
            {
                tick = networkTimer.CurrentTick,
                position = transform.position,
                rotation = transform.rotation,
                speed = currentSpeed
            }, networkTimer.CurrentTick);
        }

        CalculateJerk();

        return (false, lastProcessedTick);
    }

    float botSteering = 0f;
    float botAccel = 0f;
    
    private void ProcessClientPrediction()
    {
        var networkTimer = RaceManager.Instance.networkTimer;
        int currentTick = networkTimer.CurrentTick;

        InputPayload inputPayload;

        if (isBot)
        {
            if (Vector3.Distance(transform.position, waypoints[currentWaypointIndex].position) < waypointThreshold)
            {
                currentWaypointIndex = (currentWaypointIndex + 1) % waypoints.Count;
            }

            float targetAngle = Vector3.SignedAngle(transform.forward, waypoints[currentWaypointIndex].position - transform.position, Vector3.up);
            Debug.Log("Angle to waypoint: " + targetAngle);

            if (Mathf.Abs(targetAngle) <= 25f)
            {
                botSteering = targetAngle / 25f;
                botAccel = 1f;
            }
            else
            {
                botSteering = targetAngle > 0 ? 1f : -1f;
                botAccel = Mathf.Clamp(1 - Mathf.Abs(targetAngle / 50f), 0f, 1f);
            }
            
            inputPayload = inputManager.CreateInputPayload(botAccel, 0f, botSteering, CheckCollision());
        }
        else
        {
            inputPayload = inputManager.CreateInputPayload(inputAcceleration, inputBrake, inputSteering, CheckCollision());
        }
        
        inputPayload.tick = currentTick;
        
        clientInputBuffer.Add(inputPayload, currentTick);
        inputManager.UpdateLastKnownInput(inputPayload);

        if (IsServer)
        {
            inputManager.AddPendingInput(inputPayload);
            return;
        }

        SubmitInputServerRpc(inputPayload);

        if (UseClientSidePrediction)
        {
            StatePayload predicted = carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, inputPayload, RaceManager.Instance.networkTimer.MinTimeBetweenTicks, NetworkPlayer?.RubberBandCoefficient ?? 1f, collisionCounter);
            predicted.tick = currentTick;
            clientStateBuffer.Add(predicted, currentTick);
            
            float clampedSpeed = carMovement.ClampSpeed(predicted.speed);
            _rigidbody.MovePosition(predicted.position);
            _rigidbody.MoveRotation(predicted.rotation);
            currentSpeed = clampedSpeed;
        }

        HandleServerReconciliation();
    }

    // private void HandleClientTick()
    // {
    //     if (!IsClient || !IsOwner || _rigidbody.isKinematic) return;
        
    //     int currentTick = networkTimer.CurrentTick;
        
    //     InputPayload inputPayload = new InputPayload()
    //     {
    //         tick = currentTick,
    //         inputAcceleration = inputAcceleration,
    //         inputSteering = inputSteering,
    //         inputBrake = inputBrake,
    //         isCollide = CheckCollision()
    //     };
        
    //     clientInputBuffer.Add(inputPayload, currentTick);
    //     SubmitInputServerRpc(inputPayload);
        
    //     StatePayload predicted = carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, inputPayload, networkTimer.MinTimeBetweenTicks, NetworkPlayer?.RubberBandCoefficient ?? 1f);
    //     predicted.tick = currentTick;
    //     clientStateBuffer.Add(predicted, currentTick);
        
    //     _rigidbody.MovePosition(predicted.position);
    //     _rigidbody.MoveRotation(predicted.rotation);
    //     currentSpeed = carMovement.ClampSpeed(predicted.speed);
        
    //     HandleServerReconciliation();
    // }

    private void HandleServerReconciliation()
    {
        if (!UseServerReconciliation)
            return;

        StatePayload latestServerState = serverReconciliation.LatestServerState;

        if (latestServerState.tick == 0 || latestServerState.position == Vector3.zero)
            return;

        var networkTimer = RaceManager.Instance.networkTimer;

        if (latestServerState.tick <= serverReconciliation.LastProcessedState.tick)
            return;

        if (networkTimer.CurrentTick - latestServerState.tick > serverReconciliation.MaxRewindTickAge)
            return;

        StatePayload predictedState = clientStateBuffer.Get(latestServerState.tick);

        if (predictedState.tick == 0)
        {
            _rigidbody.position = latestServerState.position;
            _rigidbody.rotation = latestServerState.rotation;
            currentSpeed = latestServerState.speed;

            serverReconciliation.UpdateLastProcessedState(
                latestServerState.tick,
                latestServerState.position,
                latestServerState.rotation,
                latestServerState.speed
            );

            Debug.LogWarning($"[Reconciliation] Missing buffer, hard snap at tick {latestServerState.tick}");
            return;
        }

        var (positionError, rotationError) = serverReconciliation.CalculateErrors(
            predictedState,
            latestServerState
        );

        serverReconciliation.RecordError(positionError);

        bool needCorrection =
            serverReconciliation.ShouldReconcile(positionError, rotationError);

        if (!needCorrection)
        {
            serverReconciliation.UpdateLastProcessedState(
                latestServerState.tick,
                latestServerState.position,
                latestServerState.rotation,
                latestServerState.speed
            );

            return;
        }

        Vector3 replayPos = latestServerState.position;
        Quaternion replayRot = latestServerState.rotation;
        float replaySpeed = latestServerState.speed;

        int tickToReplay = latestServerState.tick + 1;

        while (tickToReplay <= networkTimer.CurrentTick)
        {
            InputPayload pastInput = clientInputBuffer.Get(tickToReplay);

            if (pastInput.tick == 0)
            {
                Debug.LogWarning($"[Reconciliation] Missing input at tick {tickToReplay}, stop replay");
                break;
            }

            StatePayload stepState = carMovement.SimulateMovement(
                replayPos,
                replayRot,
                replaySpeed,
                pastInput,
                networkTimer.MinTimeBetweenTicks,
                NetworkPlayer?.RubberBandCoefficient ?? 1f,
                collisionCounter
            );

            stepState.tick = tickToReplay;

            replayPos = stepState.position;
            replayRot = stepState.rotation;
            replaySpeed = carMovement.ClampSpeed(stepState.speed);

            clientStateBuffer.Add(stepState, tickToReplay);

            tickToReplay++;
        }

        _rigidbody.position = replayPos;
        _rigidbody.rotation = replayRot;
        currentSpeed = replaySpeed;

        serverReconciliation.UpdateLastProcessedState(
            latestServerState.tick,
            latestServerState.position,
            latestServerState.rotation,
            latestServerState.speed
        );

        if (ENABLE_DEBUG_LOG)
        {
            Debug.LogWarning(
                $"[Reconciliation] Rewind/Replay tick {latestServerState.tick}, " +
                $"posError: {positionError:F3}, rotError: {rotationError:F3}"
            );
        }
    }
    
    private void ProcessServerMovement()
    {
        if (lastProcessedTick == 0)
        {
            if (inputManager.HasPendingInputs) 
                lastProcessedTick = inputManager.GetNextPendingInputTick() - 1;
            else 
                return;
        }
        
        Vector3 tempPos = _rigidbody.position;
        Quaternion tempRot = _rigidbody.rotation;
        float tempSpeed = currentSpeed;
        bool hasProcessed = false;
        
        inputManager.RemoveOldPendingInputs(lastProcessedTick);
        
        int maxProcess = inputManager.PendingInputCount > 2 ? 2 : 1;
        for (int i = 0; i < maxProcess && inputManager.HasPendingInputs; i++)
        {
            int targetTick = lastProcessedTick + 1;
            InputPayload inputToProcess = inputManager.GetNextInputToProcess(targetTick);
            
            if (inputManager.TryGetPendingInput(targetTick, out InputPayload received))
            {
                inputManager.RemoveOldPendingInputs(targetTick);
            }
            
            RaceManager.Instance.PendInput(ID, inputToProcess);
            
            StatePayload stepState = carMovement.SimulateMovement(tempPos, tempRot, tempSpeed, inputToProcess, Time.fixedDeltaTime, NetworkPlayer?.RubberBandCoefficient ?? 1f, collisionCounter);
            tempPos = stepState.position;
            tempRot = stepState.rotation;
            tempSpeed = stepState.speed;
            stepState.isShoot = inputToProcess.isShoot;
            
            lastProcessedTick = targetTick;
            hasProcessed = true;
        }
        
        if (hasProcessed)
        {
            _rigidbody.position = tempPos;
            _rigidbody.rotation = tempRot;
            currentSpeed = tempSpeed;
        }
    }
    
    private void ProcessClientDeadReckoning()
    {
        if (!UseDeadReckoning)
        {
            //_rigidbody.MovePosition(serverReconciliation.LatestServerState.position);
            //_rigidbody.MoveRotation(serverReconciliation.LatestServerState.rotation);
            return;
        }
        
        Vector3 targetPos = deadReckoningSystem.CalculateTargetPosition();
        
        if (deadReckoningSystem.ShouldHardSnap(_rigidbody.position, targetPos))
        {
            _rigidbody.MovePosition(targetPos);
            _rigidbody.velocity = deadReckoningSystem.ServerVel;
            _rigidbody.MoveRotation(Quaternion.Euler(_networkData.Value.Rotation));
            return;
        }
        else
        {
            float deltaTime = RaceManager.Instance.networkTimer.MinTimeBetweenTicks;
            if (deadReckoningSystem.CurrentCorrectionMode == DeadReckoningSystem.CorrectionMode.SmoothDamp)
            {
                Vector3 smoothPos = deadReckoningSystem.SmoothDampPosition(transform.position, targetPos, deadReckoningSystem.Vel, deadReckoningSystem.PredictTime, deltaTime);
                _rigidbody.MovePosition(smoothPos);
                _rigidbody.velocity = deadReckoningSystem.Vel;
            }
            else
            {
                Vector3 lerpPos = deadReckoningSystem.LerpPosition(transform.position, targetPos, deadReckoningSystem.PredictTime, deltaTime);
                _rigidbody.MovePosition(lerpPos);
                _rigidbody.velocity = (lerpPos - transform.position) / deltaTime;
            }
        }
        
        var targetRot = Quaternion.Euler(_networkData.Value.Rotation);
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 10f));
    }
    
    private bool CheckCollision()
    {
        if (!UseLagCompensation || IsServer) return false;
        //Vector3 rayOrigin = _rigidbody.position + transform.forward * 1.5f + Vector3.up * 0.5f;
        //RaycastHit[] hits = Physics.RaycastAll(rayOrigin, transform.forward, 2.0f);

        Collider[] hits = Physics.OverlapSphere(_rigidbody.position, 2.5f);

        foreach (var hit in hits)
        {
            if (!hit.isTrigger && hit.gameObject != gameObject && hit.gameObject.layer != LayerMask.NameToLayer("Unhittable"))
            {
                return true;
            }
        }
        return false;
    }

    // void OnDrawGizmos()
    // {
    //     Gizmos.color = Color.red;
    //     Gizmos.DrawSphere(transform.position, 2.5f);
    // }

    public void ApplyInputForPhysics(InputPayload input)
    {
        if (input.tick == 0) return;
        if (inputManager != null) inputManager.UpdateLastKnownInput(input);
        StatePayload state = carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, input, Time.fixedDeltaTime, NetworkPlayer?.RubberBandCoefficient ?? 1f, collisionCounter);
        _rigidbody.MovePosition(state.position);
        _rigidbody.MoveRotation(state.rotation);
        currentSpeed = state.speed;
    }

    // public void ServerSendState()
    // {
    //     if (!_rigidbody.isKinematic)
    //     {
    //         Vector3 currentVel = _rigidbody.velocity;
    //         Vector3 accel = Time.fixedDeltaTime > 0 ? (currentVel - _serverVel) / Time.fixedDeltaTime : Vector3.zero;

    //         _networkData.Value = new PosAndRotNetworkData() {
    //             Position = transform.position,
    //             Rotation = transform.rotation.eulerAngles,
    //             Velocity = currentVel,
    //             Acceleration = accel,
    //             Timestamp = Time.time,
    //             Tick = lastProcessedTick, 
    //             Speed = currentSpeed,
    //             CollisionCount = collisionCounter
    //         };

    //         _serverVel = currentVel;
    //     }
    //     else
    //     {
    //         _networkData.Value = new PosAndRotNetworkData() { Position = Vector3.zero, Rotation = Vector3.zero };
    //     }
    // }

    public void ServerSendState(StatePayload state, bool rewinded = false)
    {
        if (!_rigidbody.isKinematic)
        {
            if (rewinded)
            {
                ResetServerMotionSample();
            }

            Vector3 currentVelocity = state.rotation * Vector3.forward * state.speed;
            float deltaTime = GetServerStateDeltaTime(state.tick);
            Vector3 acceleration = _lastServerStateTick >= 0 && deltaTime > 0.0001f
                ? (currentVelocity - _serverVel) / deltaTime
                : Vector3.zero;

            _serverVel = currentVelocity;
            _serverAcc = acceleration;
            _lastServerRecvTime = Time.time;
            _lastServerStateTick = state.tick;

            _networkData.Value = new PosAndRotNetworkData() {
                Position = state.position,
                Rotation = state.rotation.eulerAngles,
                Velocity = currentVelocity,
                Acceleration = acceleration,
                Timestamp = Time.time,
                Tick = state.tick, 
                Speed = state.speed,
                CollisionCount = collisionCounter,
                Rewinded = rewinded,
                IsShoot = state.isShoot
            };
        }
    }

    private float GetServerStateDeltaTime(int stateTick)
    {
        float tickDelta = Time.fixedDeltaTime;
        if (RaceManager.Instance != null && RaceManager.Instance.networkTimer != null)
        {
            tickDelta = RaceManager.Instance.networkTimer.MinTimeBetweenTicks;
        }

        if (_lastServerStateTick >= 0 && stateTick > _lastServerStateTick)
        {
            return Mathf.Max((stateTick - _lastServerStateTick) * tickDelta, 0.0001f);
        }

        if (_lastServerRecvTime > 0f)
        {
            return Mathf.Max(Time.time - _lastServerRecvTime, tickDelta);
        }

        return tickDelta;
    }

    private void ResetServerMotionSample()
    {
        _serverVel = Vector3.zero;
        _serverAcc = Vector3.zero;
        _lastServerRecvTime = 0f;
        _lastServerStateTick = -1;
    }

    public void Update()
    {
        // if (visualTransform != null && !_rigidbody.isKinematic)
        // {
        //     visualTransform.position = Vector3.Lerp(visualTransform.position, transform.position, Time.deltaTime * 15f);
        //     visualTransform.rotation = Quaternion.Slerp(visualTransform.rotation, transform.rotation, Time.deltaTime * 15f);
        // }

        if (IsOwner && AutoShootBot)
        {
            SetAutoShootBotPose();
            TryAutoShootBot();
        }

        if (IsServer && IsSpawned)
        {
            var iSpeed = Mathf.FloorToInt(_rigidbody.velocity.magnitude);
            if (iSpeed != Speed) Speed = iSpeed;
        }
    }

    private void TryAutoShootBot()
    {
        if (Time.time < nextAutoShootTime) return;
        if (_rigidbody == null || _rigidbody.isKinematic) return;
        if (State == CarState.Idle || State == CarState.Dead) return;

        if (!TryGetShootTarget(out CarController autoTarget, false, autoShootMinForwardDistance)) return;

        //LogShootDebug($"AutoShootBot fires tick={ShootDebugTick} shooter={ID} {BuildShootTargetInfo(autoTarget)}");
        nextAutoShootTime = Time.time + autoShootCooldown;
        OnAttack();
    }

    private bool HasAutoShootTargetAhead()
    {
        return TryGetShootTarget(out _, false, autoShootMinForwardDistance);
    }

    private void CalculateJerk() {
        float dt = RaceManager.Instance.networkTimer.MinTimeBetweenTicks;
        float vel = Vector3.Distance(transform.position, prevPos) / dt; prevPos = transform.position;
        float acc = Mathf.Abs(vel - prevVel) / dt; prevVel = vel;
        float jerk = Mathf.Abs(acc - prevAcc) / dt; prevAcc = acc;
        if(jerkCounter != null) UIManager.Instance.UpdateCarJerk(ID, jerkCounter.Update(jerk, true) / 1000);
    }
    #endregion

    #region States & Death & Movement
    public IEnumerator SwitchVulnerability(float time, bool toVulnerable = true) {
        yield return new WaitForSeconds(time); State = toVulnerable ? CarState.Vulnerable : CarState.Invincible; _rigidbody.excludeLayers = toVulnerable ? 0 : LayerMask.GetMask("Player");
        if (toVulnerable) for (var i = 0; i < carMeshes.Count; i++) carMeshes[i].materials = _originalMaterials[i];
        else { foreach (var mesh in carMeshes) { var materials = mesh.materials; for (var i = 0; i < materials.Length; i++) materials[i] = translucentMaterial; mesh.materials = materials; } StartCoroutine(ShineWhileInvincible()); }
    }
    private IEnumerator ShineWhileInvincible() {
        const float DURATION = 0.25f; const float MAX_ALPHA = 0.85f; const float MIN_ALPHA = 0.15f;
        while (CarState.Invincible.Equals(State)) { var newAlpha = MIN_ALPHA + (MAX_ALPHA - MIN_ALPHA) * Mathf.Abs(Mathf.Sin(Time.time / DURATION)); foreach (var mesh in carMeshes) { var materials = mesh.materials; foreach (var material in materials) { var color = material.color; color.a = newAlpha; material.color = color; } mesh.materials = materials; } yield return null; }
        foreach (var mesh in carMeshes) { var materials = mesh.materials; foreach (var material in materials) { var color = material.color; color.a = MAX_ALPHA; material.color = color; } mesh.materials = materials; }
    }
    [Rpc(SendTo.Everyone)] private void SwitchVisibilityRpc(bool toVisible = true) { PlayerPanel.SetActive(toVisible); _rigidbody.excludeLayers = toVisible ? 0: LayerMask.GetMask("Player"); foreach (var mesh in carMeshes) mesh.enabled = toVisible; }
    [Rpc(SendTo.NotMe)] private void SwitchToInvisibleExceptMeRpc() { PlayerPanel.SetActive(false); _rigidbody.excludeLayers = LayerMask.GetMask("Player"); foreach (var mesh in carMeshes) mesh.enabled = false; }

    private void SetPlayerEndGame(bool logFinish = true)
    {
        State = CarState.Idle;
        StopCoroutine(CheckIsOnTrack());
        MoveToPositionRpc(GameManager.Instance.GetPlayerPosById(ID));
        NetworkPlayer.FinishRawTime = GameManager.Instance.raceTime;
        NetworkPlayer.HasFinished = true;
        if (logFinish) RaceManager.Instance.LogPlayerFinish(NetworkPlayer);
        UIManager.Instance.gameOverallTime.text = "--:--.---";
        UIManager.Instance.gameLapTime.text = "--:--.---";
        UIManager.Instance.matchSummaryController.HasFinished = true;
        EventManager.Instance.RaiseScreenChange(AppScreen.EndGame);
    }
    public void SetMainMeshMaterialColor(Color color) { carMeshes[0].materials[1].color = color; }
    public void SetPlayerTag(int pos, string playerName) { playerTag.text = pos == -1 ? $"Ready | {NetworkPlayer.Name}" : $"{pos} | {playerName}"; }

    private IEnumerator InitiateDeath() { State = CarState.Dead; _rigidbody.isKinematic = true; SwitchVisibilityRpc(toVisible: false); RespawnInProjPos(); if (IsOwner) { NetworkPlayer.Deaths++; UIManager.Instance.SetNotificationCanvas(true, "YOU DIED", "SECONDS UNTIL REAPPEARANCE"); } for (var i = 3; i > 0; i--) { if (IsOwner) UIManager.Instance.notificationTime.text = $"{i}"; yield return new WaitForSeconds(1); } if (IsOwner) UIManager.Instance.SetNotificationCanvas(false); _rigidbody.isKinematic = false; SwitchVisibilityRpc(); }
    private void RespawnInProjPos() 
    {
        transform.position = NetworkPlayer.projPos == Vector3.zero ? transform.position : NetworkPlayer.projPos; 
        transform.rotation = Quaternion.LookRotation(transform.forward, Vector3.up); 
        _serverPos = transform.position; 
        ResetServerMotionSample();
        ResetAll();
        SendToServerRespawnSignalRpc();
    }

    [Rpc(SendTo.Server)] 
    private void SendToServerRespawnSignalRpc()
    {
        transform.position = NetworkPlayer.projPos == Vector3.zero ? transform.position : NetworkPlayer.projPos; 
        transform.rotation = Quaternion.LookRotation(transform.forward, Vector3.up); 
        _serverPos = transform.position; 
        ResetServerMotionSample();
        RaceManager.Instance.ResetAll();
    }

    private IEnumerator CheckIsOnTrack() { var cachedIsOnTrack = true; while (true) { yield return new WaitForSeconds(0.5f); _isOnTrack = IsOnTrack(); if (!_isOnTrack && !cachedIsOnTrack && IsRacing) { if (IsOwner) UIManager.Instance.SetNotificationCanvas(true, "YOU ARE OUT OF TRACK", "SECONDS UNTIL RESPAWN"); for (var i = 3; i > 0 && !IsOnTrack() && IsRacing; i--) { if (IsOwner) UIManager.Instance.notificationTime.text = $"{i}"; yield return new WaitForSeconds(1); } if (IsRacing) { if (IsOwner) UIManager.Instance.SetNotificationCanvas(false); if (!IsOnTrack()) RespawnInProjPos(); } } cachedIsOnTrack = _isOnTrack; } }
    
    private bool IsOnTrack() { 
        return true; 
    }
    #endregion

    #region Handle Client Side Prediction and Server Reconciliation

    

    public StatePayload ProcessMovement(InputPayload input)
    {
        if (input.tick == 0) return new StatePayload();
        
        return carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, input, Time.fixedDeltaTime, NetworkPlayer?.RubberBandCoefficient ?? 1f, collisionCounter);
    }

    [System.Obsolete("Use HandleServerReconciliation instead")]
    private IEnumerator SmoothReconcileLerpWithBufferSync(Vector3 targetPos, Quaternion targetRot, float targetSpeed, float duration)
    {
        yield return null;
    }

    [Rpc(SendTo.Server)]
    public void SubmitInputServerRpc(InputPayload input)
    {
        if (!IsServer) return;
        
        if (inputManager != null && inputManager.IsInputValid(input, lastProcessedTick))
        {
            inputManager.AddPendingInput(input);
        }
    }

    public void ApplyState(StatePayload state)
    {
        if (state.tick == 0) return;
        _rigidbody.position = state.position;
        _rigidbody.rotation = state.rotation;
        transform.SetPositionAndRotation(state.position, state.rotation);
        currentSpeed = carMovement?.ClampSpeed(state.speed) ?? state.speed;
        collisionCounter = state.collisionCount;
    }

    public StatePayload GetStateOfCar(int overwriteTick = -1)
    {
        int tickToUse = overwriteTick > 0 ? overwriteTick : lastProcessedTick;
        return new StatePayload()
        {
            tick = tickToUse,
            position = _rigidbody.position, 
            rotation = _rigidbody.rotation, 
            speed = currentSpeed,
            collisionCount = collisionCounter
        };
    }

    public void SyncAfterRewind(int tick)
    {
        lastProcessedTick = tick;
        if (inputManager != null)
            inputManager.RemoveOldPendingInputs(tick);
    }

    void OnCollisionEnter(Collision collision)
    {
        // if (IsOwner || (IsServer && RaceManager.Instance.IsRewinding))
        // {
        //     collisionCounter++;
        // }

        // if (IsOwner)
        // {
        //     UIManager.Instance.UpdateClientCollisionCounts(collisionCounter);
        // }
    }

    void ResetAll()
    {
        clientStateBuffer?.Clear();
        clientInputBuffer?.Clear();
        serverReconciliation?.Reset();
        ResetServerMotionSample();

    }

    int kills;

    public void OnAttack()
    {
        OnCarClientShoot();

        int tick = RaceManager.Instance.networkTimer.CurrentTick;
        //LogShootDebug($"OnAttack local tick={tick} shooter={ID} isOwner={IsOwner} isServer={IsServer} autoBot={AutoShootBot}");

        bool hasClientTarget = TryGetShootTarget(out CarController car, true, shootMinForwardDistance, true);
        ShootContext shootContext = CreateShootContext(car);

        if (hasClientTarget)
        {
            var player = RaceManager.Instance.players.FirstOrDefault(p => p != null && p.ID == car.ID);
            if (player != null && car.ID != ID)
            {
                kills++;
                UIManager.Instance.UpdateClientCollisionCounts(kills);
            }
        }

        OnAttackRpc(tick, shootContext);
    }

    [Rpc(SendTo.Server)]
    private void OnAttackRpc(int tick, ShootContext shootContext)
    {
        //ulong rocketID = GameManager.Instance.SpawnRocket(spawnPos, spawnRot, OwnerClientId);

        int serverTick = RaceManager.Instance?.networkTimer?.CurrentTick ?? -1;
        //LogShootDebug($"OnAttackRpc server received shooter={ID} shotTick={tick} serverTick={serverTick} age={serverTick - tick} lagComp={RaceManager.Instance != null && RaceManager.Instance.UseLagCompensation} autoBot={AutoShootBot}");

        if (RaceManager.Instance != null && RaceManager.Instance.UseLagCompensation)
        {
            SendShootSignalToClientsRpc();
            if (AutoShootBot)
            {
                tick = RegisterAutoShootBotState(tick);
                //LogShootDebug($"AutoShootBot registered state shooter={ID} registeredTick={tick} serverTick={serverTick} pos={transform.position}");
            }

            RaceManager.Instance.UpdateAttackInput(ID, tick, shootContext);
            return;
        }

        OnCarServerShoot();
    }

    

    public void OnCarClientShoot()
    {
        shootVFX?.Play();
        Debug.Log($"Car {ID} shoots");
    }

    private int RegisterAutoShootBotState(int tick)
    {
        int tickToUse = tick > 0 ? tick : RaceManager.Instance.networkTimer.CurrentTick;
        SetAutoShootBotPose();
        lastProcessedTick = tickToUse;
        RaceManager.Instance.PendState(ID, GetStateOfCar(tickToUse));
        return tickToUse;
    }

    private ShootContext CreateShootContext(CarController target)
    {
        return new ShootContext
        {
            hasTarget = target != null,
            targetId = target != null ? target.ID : -1,
            shooterPosition = transform.position,
            shooterForward = transform.forward,
            targetPosition = target != null ? target.transform.position : Vector3.zero
        };
    }

    public CarController GetServerShootTarget()
    {
        if (!IsServer) return null;
        return TryGetShootTarget(out CarController target, true, shootMinForwardDistance, true) ? target : null;
    }

    public CarController GetServerShootTarget(ShootContext shootContext)
    {
        if (!IsServer) return null;
        if (TryGetShootTargetFromContext(shootContext, out CarController contextTarget))
        {
            return contextTarget;
        }

        return GetServerShootTarget();
    }

    public void ApplyServerShootHit(CarController target)
    {
        if (!IsServer || target == null || target == this || target.ID == ID) return;
        if (target.State is not CarState.Vulnerable) return;

        var player = RaceManager.Instance.players.FirstOrDefault(p => p != null && p.ID == target.ID);
        if (player == null) return;

        //LogShootDebug($"Server applies hit shooter={ID} target={target.ID} tick={ShootDebugTick} {BuildShootTargetInfo(target)}");
        target.OnRocketHit();
        NetworkPlayer.Kills++;
        if (IsOwner) UIManager.Instance.UpdateServerCollisionCounts(NetworkPlayer.Kills);
        SendUpdateKillsRpc(ID, NetworkPlayer.Kills);
    }

    public void OnCarServerShoot()
    {
        if (GetServerShootTarget() is CarController target)
        {
            ApplyServerShootHit(target);
        }

        SendShootSignalToClientsRpc();
    }

    private bool TryGetShootTargetFromContext(ShootContext shootContext, out CarController target)
    {
        target = null;
        if (!shootContext.hasTarget) return false;

        target = FindCarById(shootContext.targetId);
        if (target == null) return false;
        if (!IsValidShootCar(target, true)) return false;
        if (!target.CompareTag("Player")) return false;

        Vector3 forward = shootContext.shooterForward.sqrMagnitude > 0.0001f
            ? shootContext.shooterForward.normalized
            : transform.forward.normalized;
        Vector3 origin = shootContext.shooterPosition + Vector3.up * 0.5f;
        Vector3 targetPosition = shootContext.targetPosition != Vector3.zero
            ? shootContext.targetPosition
            : target.transform.position;

        Vector3 toTarget = targetPosition - origin;
        float forwardDistance = Vector3.Dot(forward, toTarget);
        float lateralDistance = Vector3.Cross(forward, toTarget).magnitude;
        float allowedLateral = Mathf.Max(autoShootRadius, 0.1f) + GetApproximateShootTargetRadius(target);
        float range = Mathf.Max(autoShootRange, autoShootRadius);

        bool accepted =
            forwardDistance >= shootMinForwardDistance &&
            forwardDistance <= range &&
            lateralDistance <= allowedLateral;

        if (accepted)
        {
            LogShootDebug(
                $"ClientContext HIT shooter={ID} target={target.ID} tick={ShootDebugTick} " +
                $"fwd={forwardDistance:F2} lateral={lateralDistance:F2} allowedLateral={allowedLateral:F2} " +
                $"clientTargetPos={targetPosition} serverTargetPos={target.transform.position}"
            );
        }

        return accepted;
    }

    private CarController FindCarById(int id)
    {
        if (RaceManager.Instance == null || RaceManager.Instance.players == null) return null;

        foreach (NetworkPlayer player in RaceManager.Instance.players)
        {
            if (player == null || player.ID != id) continue;
            return player.GetCarController;
        }

        return null;
    }

    private bool TryGetShootTarget(
        out CarController target,
        bool requireVulnerableTarget = true,
        float minForwardDistance = shootMinForwardDistance,
        bool logResult = false)
    {
        target = null;

        Vector3 origin = transform.position + Vector3.up * 0.5f;
        Vector3 forward = transform.forward.normalized;
        float radius = Mathf.Max(autoShootRadius, 0.1f);
        float range = Mathf.Max(autoShootRange, radius);
        float bestForwardDistance = float.MaxValue;

        // SphereCastAll does not report colliders that already overlap the cast volume.
        Collider[] overlappingColliders = Physics.OverlapSphere(
            origin,
            radius,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        foreach (Collider collider in overlappingColliders)
        {
            TrySelectShootTarget(
                collider,
                origin,
                forward,
                range,
                minForwardDistance,
                requireVulnerableTarget,
                ref target,
                ref bestForwardDistance
            );
        }

        RaycastHit[] hits = Physics.SphereCastAll(
            origin,
            radius,
            forward,
            range,
            ~0,
            QueryTriggerInteraction.Ignore
        );
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            TrySelectShootTarget(
                hit.collider,
                origin,
                forward,
                range,
                minForwardDistance,
                requireVulnerableTarget,
                ref target,
                ref bestForwardDistance
            );
        }

        if (target == null && IsServer && RaceManager.Instance != null && RaceManager.Instance.IsRewinding)
        {
            TrySelectTransformShootTarget(
                origin,
                forward,
                radius,
                range,
                minForwardDistance,
                requireVulnerableTarget,
                ref target,
                ref bestForwardDistance
            );
        }

        if (logResult)
        {
            string result = target != null ? "HIT" : "MISS";
            //LogShootDebug($"TargetScan {result} tick={ShootDebugTick} shooter={ID} requireVulnerable={requireVulnerableTarget} overlapHits={overlappingColliders.Length} castHits={hits.Length} minFwd={minForwardDistance:F2} range={range:F2} {BuildShootTargetInfo(target)}");
            if (target == null)
            {
                LogShootMissDetails(
                    origin,
                    forward,
                    range,
                    minForwardDistance,
                    requireVulnerableTarget,
                    overlappingColliders,
                    hits
                );
            }
        }

        return target != null;
    }

    private void LogShootDebug(string message)
    {
        if (!ENABLE_DEBUG_LOG) return;
        Debug.Log($"<color=cyan>[ShootDebug]</color> {message}");
    }

    private string BuildShootTargetInfo(CarController target)
    {
        if (target == null)
        {
            return $"target=none shooterPos={transform.position} shooterForward={transform.forward}";
        }

        Vector3 origin = transform.position + Vector3.up * 0.5f;
        Vector3 forward = transform.forward.normalized;
        Vector3 toTarget = target.transform.position - origin;
        float forwardDistance = Vector3.Dot(forward, toTarget);
        float lateralDistance = Vector3.Cross(forward, toTarget).magnitude;
        float distance = Vector3.Distance(transform.position, target.transform.position);

        return $"target={target.ID} targetState={target.State} dist={distance:F2} fwd={forwardDistance:F2} lateral={lateralDistance:F2} shooterPos={transform.position} targetPos={target.transform.position}";
    }

    private void LogShootMissDetails(
        Vector3 origin,
        Vector3 forward,
        float range,
        float minForwardDistance,
        bool requireVulnerableTarget,
        Collider[] overlappingColliders,
        RaycastHit[] castHits)
    {
        HashSet<CarController> scannedCars = new();

        foreach (Collider collider in overlappingColliders)
        {
            LogShootColliderCandidate(
                "overlap",
                collider,
                origin,
                forward,
                range,
                minForwardDistance,
                requireVulnerableTarget,
                scannedCars
            );
        }

        foreach (RaycastHit hit in castHits)
        {
            LogShootColliderCandidate(
                $"cast@{hit.distance:F2}",
                hit.collider,
                origin,
                forward,
                range,
                minForwardDistance,
                requireVulnerableTarget,
                scannedCars
            );
        }

        if (RaceManager.Instance == null || RaceManager.Instance.players == null) return;

        foreach (NetworkPlayer player in RaceManager.Instance.players)
        {
            CarController car = player != null ? player.GetCarController : null;
            if (car == null || car == this) continue;

            Vector3 toCar = car.transform.position - origin;
            float forwardDistance = Vector3.Dot(forward, toCar);
            float lateralDistance = Vector3.Cross(forward, toCar).magnitude;
            float distance = Vector3.Distance(transform.position, car.transform.position);

            // LogShootDebug(
            //     $"WorldCandidate shooter={ID} target={car.ID} state={car.State} " +
            //     $"dist={distance:F2} fwd={forwardDistance:F2} lateral={lateralDistance:F2} " +
            //     $"inForwardRange={forwardDistance >= minForwardDistance && forwardDistance <= range} " +
            //     $"targetPos={car.transform.position}"
            // );
        }
    }

    private void LogShootColliderCandidate(
        string source,
        Collider collider,
        Vector3 origin,
        Vector3 forward,
        float range,
        float minForwardDistance,
        bool requireVulnerableTarget,
        HashSet<CarController> scannedCars)
    {
        CarController car = collider.GetComponentInParent<CarController>();
        if (car == null)
        {
            //LogShootDebug($"ScanCandidate source={source} collider={collider.name} car=none layer={LayerMask.LayerToName(collider.gameObject.layer)} tag={collider.tag}");
            return;
        }

        if (!scannedCars.Add(car)) return;

        Vector3 toCar = car.transform.position - origin;
        float forwardDistance = Vector3.Dot(forward, toCar);
        float lateralDistance = Vector3.Cross(forward, toCar).magnitude;
        float distance = Vector3.Distance(transform.position, car.transform.position);
        string rejectReason = GetShootRejectReason(
            collider,
            car,
            forwardDistance,
            range,
            minForwardDistance,
            requireVulnerableTarget
        );

        // LogShootDebug(
        //     $"ScanCandidate source={source} shooter={ID} car={car.ID} state={car.State} " +
        //     $"reason={rejectReason} dist={distance:F2} fwd={forwardDistance:F2} lateral={lateralDistance:F2} " +
        //     $"collider={collider.name} colliderTag={collider.tag} carTag={car.tag} targetPos={car.transform.position}"
        // );
    }

    private string GetShootRejectReason(
        Collider collider,
        CarController car,
        float forwardDistance,
        float range,
        float minForwardDistance,
        bool requireVulnerableTarget)
    {
        if (car == null) return "NoCarController";
        if (car == this) return "Self";
        if (car.ID == ID) return "SameId";
        if (requireVulnerableTarget && car.State is not CarState.Vulnerable) return "NotVulnerable";
        if (!collider.CompareTag("Player") && !car.CompareTag("Player")) return "NotPlayerTag";
        if (forwardDistance < minForwardDistance) return "TooCloseOrBehind";
        if (forwardDistance > range) return "OutOfRange";

        return "WouldAccept";
    }

    private void TrySelectTransformShootTarget(
        Vector3 origin,
        Vector3 forward,
        float castRadius,
        float range,
        float minForwardDistance,
        bool requireVulnerableTarget,
        ref CarController bestTarget,
        ref float bestForwardDistance)
    {
        if (RaceManager.Instance == null || RaceManager.Instance.players == null) return;

        foreach (NetworkPlayer player in RaceManager.Instance.players)
        {
            CarController car = player != null ? player.GetCarController : null;
            if (!IsValidShootCar(car, requireVulnerableTarget)) continue;
            if (!car.CompareTag("Player")) continue;

            Vector3 toCar = car.transform.position - origin;
            float forwardDistance = Vector3.Dot(forward, toCar);
            if (forwardDistance < minForwardDistance || forwardDistance > range) continue;
            if (forwardDistance >= bestForwardDistance) continue;

            float lateralDistance = Vector3.Cross(forward, toCar).magnitude;
            float targetRadius = GetApproximateShootTargetRadius(car);
            if (lateralDistance > castRadius + targetRadius) continue;

            bestTarget = car;
            bestForwardDistance = forwardDistance;
            // LogShootDebug(
            //     $"TransformFallback HIT shooter={ID} target={car.ID} tick={ShootDebugTick} " +
            //     $"fwd={forwardDistance:F2} lateral={lateralDistance:F2} allowedLateral={(castRadius + targetRadius):F2} " +
            //     $"targetPos={car.transform.position}"
            // );
        }
    }

    private float GetApproximateShootTargetRadius(CarController car)
    {
        const float fallbackRadius = 2.5f;
        float radius = fallbackRadius;
        Collider[] colliders = car.GetComponentsInChildren<Collider>();

        foreach (Collider collider in colliders)
        {
            if (collider == null || collider.isTrigger) continue;

            Vector3 extents = collider.bounds.extents;
            float horizontalRadius = new Vector2(extents.x, extents.z).magnitude;
            radius = Mathf.Max(radius, horizontalRadius);
        }

        return radius;
    }

    private void TrySelectShootTarget(
        Collider collider,
        Vector3 origin,
        Vector3 forward,
        float range,
        float minForwardDistance,
        bool requireVulnerableTarget,
        ref CarController bestTarget,
        ref float bestForwardDistance)
    {
        if (!TryGetValidShootCandidate(collider, requireVulnerableTarget, out CarController car))
        {
            return;
        }

        Vector3 toCar = car.transform.position - origin;
        float forwardDistance = Vector3.Dot(forward, toCar);
        if (forwardDistance < minForwardDistance || forwardDistance > range)
        {
            return;
        }

        if (forwardDistance >= bestForwardDistance)
        {
            return;
        }

        bestTarget = car;
        bestForwardDistance = forwardDistance;
    }

    private bool TryGetValidShootCandidate(
        Collider collider,
        bool requireVulnerableTarget,
        out CarController car)
    {
        car = collider.GetComponentInParent<CarController>();
        if (!IsValidShootCar(car, requireVulnerableTarget)) return false;
        if (!collider.CompareTag("Player") && !car.CompareTag("Player")) return false;

        return true;
    }

    private bool IsValidShootCar(CarController car, bool requireVulnerableTarget)
    {
        if (car == null || car == this || car.ID == ID) return false;
        if (requireVulnerableTarget && car.State is not CarState.Vulnerable) return false;

        return true;
    }

    [Rpc(SendTo.NotOwner)]
    void SendShootSignalToClientsRpc()
    {
        OnCarClientShoot();
    }

    [Rpc(SendTo.NotServer)]
    void SendUpdateKillsRpc(int id, int newKills)
    {
        if (ID != id) return;
        Debug.Log($"Recieved updated kills count: {newKills}");
        UIManager.Instance.UpdateServerCollisionCounts(newKills);
    }

    #endregion
}
