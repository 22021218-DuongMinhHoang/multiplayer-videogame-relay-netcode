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

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref inputAcceleration);
        serializer.SerializeValue(ref inputSteering);
        serializer.SerializeValue(ref inputBrake);
        serializer.SerializeValue(ref isCollide);
    }

    public void Copy(InputPayload input)
    {
        tick = input.tick;
        inputAcceleration = input.inputAcceleration;
        inputSteering = input.inputSteering;
        inputBrake = input.inputBrake;
        isCollide = input.isCollide;
    }

    public override bool Equals(object obj)
    {
        if (obj is not InputPayload other) return false;
        return tick == other.tick &&
               Mathf.Approximately(inputAcceleration, other.inputAcceleration) &&
               Mathf.Approximately(inputSteering, other.inputSteering) &&
               Mathf.Approximately(inputBrake, other.inputBrake) &&
               isCollide == other.isCollide;
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

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref position);
        serializer.SerializeValue(ref rotation);
        serializer.SerializeValue(ref speed);
    }

    public void Copy(StatePayload state)
    {
        tick = state.tick;
        position = state.position;
        rotation = state.rotation;
        speed = state.speed;
    }

    public override bool Equals(object obj)
    {
        if (obj is not StatePayload other) return false;
        float posThreshold = 0.01f;
        float angThreshold = 1f;
        return tick == other.tick &&
               Vector3.Distance(position, other.position) <= posThreshold &&
               Quaternion.Angle(rotation, other.rotation) <= angThreshold &&
               Mathf.Approximately(speed, other.speed);
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
            return hash;
        }
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

    public bool UseDeadReckoning = true;
    public bool UseServerReconciliation = true;
    public bool UseClientSidePrediction = true;
    public bool UseLagCompensation = true;

    public bool IsOwnerCar => IsOwner;

    [Header("Dead Reckoning Configuration")]
    [SerializeField] private DeadReckoningSystem.DeadReckoningMode currentDRMode = DeadReckoningSystem.DeadReckoningMode.Quadratic;
    [SerializeField] private DeadReckoningSystem.CorrectionMode currentCorrectionMode = DeadReckoningSystem.CorrectionMode.SmoothDamp;

    [Header("Client Side Prediction and Server Reconciliation")]
    
    // Helper systems
    private CircularBuffer<StatePayload> clientStateBuffer;
    private CircularBuffer<InputPayload> clientInputBuffer;
    private NetworkTimer networkTimer;
    private InputManager inputManager;
    private CarMovement carMovement;
    private DeadReckoningSystem deadReckoningSystem;
    private ServerReconciliation serverReconciliation;
    private RaceTracker raceTracker;
    
    private int lastProcessedTick = 0;
    private const int BUFFER_SIZE = 1024;
    private const float TICK_RATE = 50f;

    public bool _useCubicSpline 
    { 
        get => deadReckoningSystem?.UseCubicSpline ?? false;
        set { if (deadReckoningSystem != null) deadReckoningSystem.UseCubicSpline = value; }
    }
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
    private Vector3 _prevServerVel;
    private float _lastServerRecvTime;
    private Vector3 _targetPos;
    private float _timeOffset = 0f;
    private const float SYNC_ALPHA = 0.05f;

    private Vector3 _p0, _p1, _t0, _t1;
    private float _splineTimer;

    private double _aeeSum = 0.0;
    private long _aeeCount = 0;
    private long _hitCount = 0;
    private float hitThreshold = 0.5f;
    private float _lastPacketLocalTime;
    private List<float> _packetIntervals = new List<float>();
    

    Vector3 prevPos;
    float prevVel;
    float prevAcc;
    [SerializeField] JerkCounter jerkCounter;

    [Header("Visuals")]
    [SerializeField] private Transform visualTransform;

    private bool ENABLE_DEBUG_LOG = true;

    private int Laps { get => _laps; set { _laps = value; OnLapsChangeEvent?.Invoke(value); } }
    private int Speed { get => _networkSpeed.Value; set => _networkSpeed.Value = value; }
    private bool IsRacing => State != CarState.Dead && State != CarState.Idle;
    private bool IsRace => GameManager.Instance.CLASSIF_STATES.Contains(NetworkPlayer.CurrentRace);
    private bool IsClassif => GameManager.Instance.RACE_STATES.Contains(NetworkPlayer.CurrentRace);
    public int GetLastProcessedTick() => lastProcessedTick;

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
    private void OnPlayerLeft(NetworkPlayer networkPlayer) { if (NetworkPlayer.Equals(networkPlayer)) return; if (RaceManager.Instance.players.Count == 1) SetPlayerEndGame(); }

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
        _rigidbody.isKinematic = true; _rigidbody.position = pos; _rigidbody.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        _serverPos = pos;
        if (deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(pos);
    }

    [Rpc(SendTo.Everyone)]
    private void ResetStatsRpc() {
        Laps = 0; NetworkPlayer.lastLapPos = 0f; NetworkPlayer.checkpointAchieved = false; NetworkPlayer.RubberBandCoefficient = 1f;
        ResetCalculationMetrics();
        if (UIManager.Instance != null) { try { UIManager.Instance.averageExportError.text = "0.00"; UIManager.Instance.hitPercentage.text = "0.0%"; 
        // UIManager.Instance.jitterEstimate.text = "0ms";
        //  UIManager.Instance.instantError.text = "0.00m"; 
         } catch (Exception) { } }
    }

    public void ResetCalculationMetrics()
    {
        if (serverReconciliation != null)
            serverReconciliation.ResetMetrics();
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

        // Initialize helper systems
        clientStateBuffer = new CircularBuffer<StatePayload>(BUFFER_SIZE);
        clientInputBuffer = new CircularBuffer<InputPayload>(BUFFER_SIZE);
        networkTimer = new NetworkTimer(TICK_RATE);
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
            try { UIManager.Instance.gameRespawn.onClick.RemoveListener(RespawnInProjPosRpc); } catch {}
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
                    UIManager.Instance.gameRespawn.onClick.AddListener(RespawnInProjPosRpc);
                    EventManager.Instance.ScreenChange.AddListener(OnScreenChange);
                }
                SetPlayerTag(-1, NetworkPlayer.Name); OnRocketChange(NetworkPlayer.Rockets); SetMainMeshMaterialColor(NetworkPlayer.CarColor); EventManager.Instance.RaisePlayersCarFound(ID);
            }
        
        _networkData.OnValueChanged += OnNetworkDataChanged;
        if (deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(transform.position);

        if (IsOwner && NetworkPlayer != null) NetworkPlayer.StartPos = NetworkPlayer.ID;
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
                if (deadReckoningSystem != null) deadReckoningSystem.UseCubicSpline = value;
                if(value && deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(transform.position);
                break;
            case "Adaptive": 
                if (deadReckoningSystem != null) deadReckoningSystem.UseAdaptiveThreshold = value;
                break;
            case "TimeSync": 
                if (deadReckoningSystem != null) deadReckoningSystem.UseTimeSync = value;
                break;
        }
    }

    private void OnNetworkDataChanged(PosAndRotNetworkData oldVal, PosAndRotNetworkData newVal) {
        if (newVal.Position == Vector3.zero) return;
        
        deadReckoningSystem.OnServerStateReceived(newVal.Position, newVal.Velocity, newVal.Acceleration, newVal.Timestamp);
        
        if (UseServerReconciliation)
        {
            serverReconciliation.RecordServerState(newVal.Tick, newVal.Position, Quaternion.Euler(newVal.Rotation), newVal.Speed);
        }
        else
        {
            if (IsClient && !IsOwner)
            {
                transform.position = newVal.Position;
                transform.rotation = Quaternion.Euler(newVal.Rotation);
                currentSpeed = newVal.Speed;
            }
        }
        
        float error = Vector3.Distance(transform.position, newVal.Position);
        serverReconciliation.RecordError(error);
        
        if (UIManager.Instance != null) 
        { 
            try 
            { 
                UIManager.Instance.averageExportError.text = $"AEE: {serverReconciliation.AverageExportError:F2}"; 
                UIManager.Instance.hitPercentage.text = $"Hit: {serverReconciliation.HitPercentage:F1}%"; 
                // UIManager.Instance.jitterEstimate.text = $"Jitter: {deadReckoningSystem.GetJitterEstimate():F0}ms"; 
                // UIManager.Instance.instantError.text = $"Err: {error:F2}m"; 
            } 
            catch {} 
        }
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

        if (deadReckoningSystem != null) deadReckoningSystem.ResetSplineState(startPos);
        _serverPos = startPos;
        _targetPos = startPos;
    }

    public (bool, int) ProcessFixedCarController()
    {
        if (IsClient && IsOwner && !_rigidbody.isKinematic)
        {
            ProcessClientPrediction();
        }

        if (IsServer && !_rigidbody.isKinematic)
        {
            ProcessServerMovement();
        }

        if (IsServer)
        {
            if (!_rigidbody.isKinematic) 
                _serverVel = _rigidbody.velocity; 
            else 
                _networkData.Value = new PosAndRotNetworkData() { Position = Vector3.zero, Rotation = Vector3.zero };
        }
        else if (IsClient && !IsOwner && !_rigidbody.isKinematic && _networkData.Value.Position != Vector3.zero)
        {
            ProcessClientDeadReckoning();
            CalculateJerk();
        }

        return (false, lastProcessedTick);
    }
    
    private void ProcessClientPrediction()
    {
        if (networkTimer == null)
            networkTimer = new NetworkTimer(TICK_RATE);
        
        if (!networkTimer.ShouldTick())
            return;
        
        int currentTick = networkTimer.CurrentTick;
        
        InputPayload inputPayload = inputManager.CreateInputPayload(inputAcceleration, inputBrake, inputSteering, CheckCollision());
        inputPayload.tick = currentTick;
        
        clientInputBuffer.Add(inputPayload, currentTick);
        inputManager.UpdateLastKnownInput(inputPayload);

        if (UseClientSidePrediction)
        {
            StatePayload predicted = carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, inputPayload, networkTimer.MinTimeBetweenTicks, NetworkPlayer?.RubberBandCoefficient ?? 1f);
            predicted.tick = currentTick;
            clientStateBuffer.Add(predicted, currentTick);
            
            float clampedSpeed = carMovement.ClampSpeed(predicted.speed);
            _rigidbody.MovePosition(predicted.position);
            _rigidbody.MoveRotation(predicted.rotation);
            currentSpeed = clampedSpeed;
        }

        
        
        SubmitInputServerRpc(inputPayload);
        
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
        
        var (positionError, rotationError) = serverReconciliation.CalculateErrors(
            new StatePayload { position = _rigidbody.position, rotation = _rigidbody.rotation, speed = currentSpeed, tick = networkTimer.CurrentTick },
            latestServerState
        );
        
        serverReconciliation.RecordError(positionError);
        
        if (serverReconciliation.ShouldReconcile(positionError, rotationError, currentSpeed, latestServerState.speed))
        {
            _rigidbody.position = latestServerState.position;
            _rigidbody.rotation = latestServerState.rotation;
            currentSpeed = latestServerState.speed;
            
            int tickToReplay = latestServerState.tick + 1;
            while (tickToReplay <= networkTimer.CurrentTick)
            {
                InputPayload pastInput = clientInputBuffer.Get(tickToReplay);
                
                StatePayload stepState = carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, pastInput, networkTimer.MinTimeBetweenTicks, NetworkPlayer?.RubberBandCoefficient ?? 1f);
                stepState.tick = tickToReplay;
                
                _rigidbody.position = stepState.position;
                _rigidbody.rotation = stepState.rotation;
                currentSpeed = carMovement.ClampSpeed(stepState.speed);
                
                clientStateBuffer.Add(stepState, tickToReplay);
                tickToReplay++;
            }
            
            if (ENABLE_DEBUG_LOG)
                Debug.LogWarning($"[Reconciliation] Rewind/Replay từ tick {latestServerState.tick} (Error: {positionError:F2}m)");
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
            
            StatePayload stepState = carMovement.SimulateMovement(tempPos, tempRot, tempSpeed, inputToProcess, Time.fixedDeltaTime, NetworkPlayer?.RubberBandCoefficient ?? 1f);
            tempPos = stepState.position;
            tempRot = stepState.rotation;
            tempSpeed = stepState.speed;
            
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
            _rigidbody.MovePosition(deadReckoningSystem.TargetPos);
            _rigidbody.MoveRotation(Quaternion.Euler(_networkData.Value.Rotation));
            return;
        }
        
        Vector3 targetPos = deadReckoningSystem.CalculateTargetPosition(APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME);
        
        if (deadReckoningSystem.CurrentCorrectionMode == DeadReckoningSystem.CorrectionMode.SmoothDamp)
        {
            Vector3 smoothPos = deadReckoningSystem.SmoothDampPosition(transform.position, targetPos, deadReckoningSystem.Vel, APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME, Time.fixedDeltaTime);
            _rigidbody.MovePosition(smoothPos);
            _rigidbody.velocity = deadReckoningSystem.Vel;
        }
        else
        {
            Vector3 lerpPos = deadReckoningSystem.LerpPosition(transform.position, targetPos, APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME, Time.fixedDeltaTime);
            _rigidbody.MovePosition(lerpPos);
            _rigidbody.velocity = (lerpPos - transform.position) / Time.fixedDeltaTime;
        }
        
        var targetRot = Quaternion.Euler(_networkData.Value.Rotation);
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 10f));
    }
    
    private bool CheckCollision()
    {
        if (!UseLagCompensation) return false;
        Vector3 rayOrigin = _rigidbody.position + transform.forward * 1.5f + Vector3.up * 0.5f;
        RaycastHit[] hits = Physics.RaycastAll(rayOrigin, transform.forward, 2.0f);
        
        foreach (var hit in hits)
        {
            if (!hit.collider.isTrigger && hit.collider.transform.root.gameObject != this.gameObject)
            {
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.8f)
                    return true;
            }
        }
        return false;
    }

    public void ApplyInputForPhysics(InputPayload input)
    {
        if (input.tick == 0) return;
        if (inputManager != null) inputManager.UpdateLastKnownInput(input);
        StatePayload state = carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, input, Time.fixedDeltaTime, NetworkPlayer?.RubberBandCoefficient ?? 1f);
        _rigidbody.position = state.position;
        _rigidbody.rotation = state.rotation;
        currentSpeed = state.speed;
    }

    public void ServerSendState()
    {
        if (!_rigidbody.isKinematic)
        {
            Vector3 currentVel = _rigidbody.velocity;
            Vector3 accel = Time.fixedDeltaTime > 0 ? (currentVel - _serverVel) / Time.fixedDeltaTime : Vector3.zero;

            _networkData.Value = new PosAndRotNetworkData() {
                Position = transform.position,
                Rotation = transform.rotation.eulerAngles,
                Velocity = currentVel,
                Acceleration = accel,
                Timestamp = Time.time,
                Tick = lastProcessedTick, 
                Speed = currentSpeed
            };

            _serverVel = currentVel;
        }
        else
        {
            _networkData.Value = new PosAndRotNetworkData() { Position = Vector3.zero, Rotation = Vector3.zero };
        }
    }

    public void ServerSendState(StatePayload state)
    {
        if (!_rigidbody.isKinematic)
        {
            _networkData.Value = new PosAndRotNetworkData() {
                Position = state.position,
                Rotation = state.rotation.eulerAngles,
                Velocity = _rigidbody.velocity,
                Timestamp = Time.time,
                Tick = state.tick, 
                Speed = state.speed
            };
        }
    }

    public void Update()
    {
        if (IsSpawned && networkTimer != null)
        {
            networkTimer.Update(Time.deltaTime);
        }

        if (visualTransform != null && !_rigidbody.isKinematic)
        {
            visualTransform.position = Vector3.Lerp(visualTransform.position, transform.position, Time.deltaTime * 15f);
            visualTransform.rotation = Quaternion.Slerp(visualTransform.rotation, transform.rotation, Time.deltaTime * 15f);
        }

        if (IsServer && IsSpawned)
        {
            var iSpeed = Mathf.FloorToInt(_rigidbody.velocity.magnitude);
            if (iSpeed != Speed) Speed = iSpeed;
        }
    }

    private void CalculateJerk() {
        float dt = Time.fixedDeltaTime; if (dt <= 0) return;
        float vel = Vector3.Distance(transform.position, prevPos) / dt; prevPos = transform.position;
        float acc = Mathf.Abs(vel - prevVel) / dt; prevVel = vel;
        float jerk = Mathf.Abs(acc - prevAcc) / dt; prevAcc = acc;
        if(jerkCounter != null && UIManager.Instance.carJerk != null) UIManager.Instance.carJerk.text = $"Jerk: {(int)jerkCounter.Update(jerk)}";
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

    private void SetPlayerEndGame() { State = CarState.Idle; StopCoroutine(CheckIsOnTrack()); MoveToPositionRpc(GameManager.Instance.GetPlayerPosById(ID)); NetworkPlayer.FinishRawTime = GameManager.Instance.raceTime; NetworkPlayer.HasFinished = true; UIManager.Instance.gameOverallTime.text = "--:--.---"; UIManager.Instance.gameLapTime.text = "--:--.---"; UIManager.Instance.matchSummaryController.HasFinished = true; EventManager.Instance.RaiseScreenChange(AppScreen.EndGame); }
    public void SetMainMeshMaterialColor(Color color) { carMeshes[0].materials[1].color = color; }
    public void SetPlayerTag(int pos, string playerName) { playerTag.text = pos == -1 ? $"Ready | {NetworkPlayer.Name}" : $"{pos} | {playerName}"; }

    private IEnumerator InitiateDeath() { State = CarState.Dead; _rigidbody.isKinematic = true; SwitchVisibilityRpc(toVisible: false); RespawnInProjPosRpc(); if (IsOwner) { NetworkPlayer.Deaths++; UIManager.Instance.SetNotificationCanvas(true, "YOU DIED", "SECONDS UNTIL REAPPEARANCE"); } for (var i = 3; i > 0; i--) { if (IsOwner) UIManager.Instance.notificationTime.text = $"{i}"; yield return new WaitForSeconds(1); } if (IsOwner) UIManager.Instance.SetNotificationCanvas(false); _rigidbody.isKinematic = false; SwitchVisibilityRpc(); }
    [Rpc(SendTo.Server)] private void RespawnInProjPosRpc() { transform.position = NetworkPlayer.projPos == Vector3.zero ? transform.position : NetworkPlayer.projPos; transform.rotation = Quaternion.LookRotation(transform.forward, Vector3.up); _serverPos = transform.position; }

    private IEnumerator CheckIsOnTrack() { var cachedIsOnTrack = true; while (true) { yield return new WaitForSeconds(0.5f); _isOnTrack = IsOnTrack(); if (!_isOnTrack && !cachedIsOnTrack && IsRacing) { if (IsOwner) UIManager.Instance.SetNotificationCanvas(true, "YOU ARE OUT OF TRACK", "SECONDS UNTIL RESPAWN"); for (var i = 3; i > 0 && !IsOnTrack() && IsRacing; i--) { if (IsOwner) UIManager.Instance.notificationTime.text = $"{i}"; yield return new WaitForSeconds(1); } if (IsRacing) { if (IsOwner) UIManager.Instance.SetNotificationCanvas(false); if (!IsOnTrack()) RespawnInProjPosRpc(); } } cachedIsOnTrack = _isOnTrack; } }
    
    private bool IsOnTrack() { 
        return true; 
    }
    #endregion

    #region Handle Client Side Prediction and Server Reconciliation

    

    public StatePayload ProcessMovement(InputPayload input)
    {
        if (input.tick == 0) return new StatePayload();
        
        return carMovement.SimulateMovement(_rigidbody.position, _rigidbody.rotation, currentSpeed, input, Time.fixedDeltaTime, NetworkPlayer?.RubberBandCoefficient ?? 1f);
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
        currentSpeed = carMovement?.ClampSpeed(state.speed) ?? state.speed;
    }

    public StatePayload GetStateOfCar(int overwriteTick = -1)
    {
        int tickToUse = overwriteTick > 0 ? overwriteTick : lastProcessedTick;
        return new StatePayload()
        {
            tick = tickToUse,
            position = _rigidbody.position, 
            rotation = _rigidbody.rotation, 
            speed = currentSpeed
        };
    }

    public void SyncAfterRewind(int tick)
    {
        lastProcessedTick = tick;
        if (inputManager != null)
            inputManager.RemoveOldPendingInputs(tick);
    }

    #endregion
}