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

    public override bool Equals(object obj) {
        if (obj is not InputPayload other) return false;
        return 
            tick == other.tick &&  
            Mathf.Approximately(inputAcceleration, other.inputAcceleration) && 
            Mathf.Approximately(inputSteering, other.inputSteering) && 
            Mathf.Approximately(inputBrake, other.inputBrake);
    }
    public override int GetHashCode() {
        return tick.GetHashCode();
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

    public override bool Equals(object obj) {
        if (obj is not StatePayload other) return false;
        return tick == other.tick && position == other.position && rotation == other.rotation && Mathf.Approximately(speed, other.speed);
    }
    public override int GetHashCode() {
        return tick.GetHashCode();
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

    [SerializeField] [HideInInspector] public float inputAcceleration;
    [SerializeField] [HideInInspector] public float inputSteering;
    [SerializeField] [HideInInspector] public float inputBrake;
    [SerializeField] [HideInInspector] private float currentSpeed = 0f;

    public NetworkPlayer NetworkPlayer { get; private set; }
    public CarState State { get; private set; }
    public int ID { get; private set; }
    public bool RubberBand { get; private set; } = true;
    
    // --- BOT CONFIGURATION ---
    [Header("Bot Settings")]
    public bool isBot = false;
    private List<Transform> waypoints = new List<Transform>(); 
    private int currentWaypointIndex = 0;
    private float waypointThreshold = 5.0f;

    // --- DEAD RECKONING VARIABLES ---
    public bool UseDeadReckoning = true;
    public enum CorrectionMode { SmoothDamp, Lerp }

    [Header("Dead Reckoning Configuration")]
    [SerializeField] private DeadReckoningMode currentDRMode = DeadReckoningMode.Quadratic;
    [SerializeField] private CorrectionMode currentCorrectionMode = CorrectionMode.SmoothDamp;
    [SerializeField] private float snapThreshold = 10f; 
    
    [Header("Client Side Prediction and Server Reconcilation")]
    private int currentTick = 1;              // client tick counter (owner)
    private int serverTickProcessing = 1;     // server tick counter (advance every FixedUpdate)
    private const float SERVER_TICK_RATE = 30f;
    private const int BUFFER_SIZE = 4096;

    // server input delay in ticks to allow inputs to arrive before processing (trade-off: input lag vs smoothness)
    private const int SERVER_INPUT_DELAY = 2;

    private StatePayload[] stateBuffer;
    private InputPayload[] inputBuffer;
    private StatePayload latestServerState;
    private StatePayload lastProcessedState;
    private SortedDictionary<int, InputPayload> pendingInputs = new SortedDictionary<int, InputPayload>(); // used by server to store incoming inputs
    private int lastProcessedTick;
    private bool isRewinding = false;

    private List<InputPayload> clientInputList;

    // fallback when server missing input
    private InputPayload lastKnownInput;

    public bool _useCubicSpline = false;
    public bool _useAdaptiveThreshold = true;
    public bool _useTimeSync = true;

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

    // --- NEW: correction state (no Coroutine) ---
    private bool correctionActive = false;
    private StatePayload correctionTarget;
    private StatePayload correctionStartState;
    private float correctionTimer = 0f;
    private float correctionDuration = 0.15f; // increased smoothing (150ms)
    private float teleportThreshold = 5f; // >5m -> immediate teleport to target

    // debug flag
    private bool ENABLE_DEBUG_LOG = true;

    private int Laps { get => _laps; set { _laps = value; OnLapsChangeEvent?.Invoke(value); } }
    private int Speed { get => _networkSpeed.Value; set => _networkSpeed.Value = value; }
    private bool IsRacing => State != CarState.Dead && State != CarState.Idle;
    private bool IsRace => GameManager.Instance.CLASSIF_STATES.Contains(NetworkPlayer.CurrentRace);
    private bool IsClassif => GameManager.Instance.RACE_STATES.Contains(NetworkPlayer.CurrentRace);

    #endregion

    #region Delegates and Events
    public delegate void OnLapsChangeDelegate(int newVal);
    public event OnLapsChangeDelegate OnLapsChangeEvent;

    private void OnSpeedChange(int oldVal, int newVal) { UIManager.Instance.gameKph.text = $"{newVal:0}"; }
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

            stateBuffer[0] = new StatePayload()
            {
                tick = 0,
                position = GameManager.Instance.RACE_POS[NetworkPlayer.StartPos],
                rotation = Quaternion.Euler(Vector3.zero)
            };

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
        _rigidbody.isKinematic = true; transform.position = pos; transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        _serverPos = pos;
        ResetSplineState(pos); 
    }

    [Rpc(SendTo.Everyone)]
    private void ResetStatsRpc() {
        Laps = 0; NetworkPlayer.lastLapPos = 0f; NetworkPlayer.checkpointAchieved = false; NetworkPlayer.RubberBandCoefficient = 1f;
        ResetCalculationMetrics(); 
        if (UIManager.Instance != null) { try { UIManager.Instance.averageExportError.text = "0.00"; UIManager.Instance.hitPercentage.text = "0.0%"; UIManager.Instance.jitterEstimate.text = "0ms"; UIManager.Instance.instantError.text = "0.00m"; } catch (Exception) { } }
    }

    public void ResetCalculationMetrics()
    {
        _aeeSum = 0.0;
        _aeeCount = 0;
        _hitCount = 0;
        _packetIntervals.Clear();
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
        else
        {
            var containerObj = GameObject.Find("Waypoints");
            if (containerObj != null)
            {
                foreach (Transform child in containerObj.transform) waypoints.Add(child);
            }
        }

        if (IsClient && IsOwner)
        {
            //minTimeBetweenTicks = 1f / SERVER_TICK_RATE;
            stateBuffer = new StatePayload[BUFFER_SIZE];
            inputBuffer = new InputPayload[BUFFER_SIZE];
            clientInputList = new List<InputPayload>();
            // ensure same fixedDeltaTime for client (recommend to set same on server headless too)
            Time.fixedDeltaTime = 1f / SERVER_TICK_RATE;
        }

        // server should also ensure fixedDeltaTime same as clients (configure on server init)
        if (IsServer)
        {
            Time.fixedDeltaTime = 1f / SERVER_TICK_RATE;
        }
    }

    public override void OnNetworkDespawn() {
        OnLapsChangeEvent -= NetworkPlayer.OnLapsChange; GameManager.Instance.OnGameStateChange -= OnGameStateChange;
        if (IsOwner) { 
            NetworkPlayer.OnRocketChangeEvent -= OnRocketChange; _networkSpeed.OnValueChanged -= OnSpeedChange; 
            OnLapsChangeEvent -= OnLapsChange; RaceManager.Instance.OnPlayerLeft -= OnPlayerLeft; 
            UIManager.Instance.gameRespawn.onClick.RemoveListener(RespawnInProjPosRpc); 
            EventManager.Instance.ScreenChange.RemoveListener(OnScreenChange);
            
            if (UIManager.Instance != null && UIManager.Instance.botToggle != null)
                UIManager.Instance.botToggle.onValueChanged.RemoveListener(OnBotToggleChanged);
        }
        _networkData.OnValueChanged -= OnNetworkDataChanged;
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
        if (NetworkPlayer == null) throw new Exception("Player not found!");
    
        _networkData.OnValueChanged += OnNetworkDataChanged;
        ResetSplineState(transform.position); 
        
        if (IsOwner) NetworkPlayer.StartPos = NetworkPlayer.ID;
    }
    
    private void OnBotToggleChanged(bool isOn)
    {
        isBot = isOn;
        if (isBot) currentWaypointIndex = 0; 
        else
        {
            inputSteering = 0; inputAcceleration = 0; inputBrake = 0;
            SubmitBotInputServerRpc(0, 0, 0);
        }
    }
    
    public void SetDRMode(DeadReckoningMode mode) { currentDRMode = mode; }
    public void SetCorrectionMode(int index) { currentCorrectionMode = (CorrectionMode)index; }
    
    public void SetImprovement(string option, bool value) {
        switch (option) {
            case "Spline": 
                _useCubicSpline = value; 
                if(_useCubicSpline) ResetSplineState(transform.position);
                break;
            case "Adaptive": _useAdaptiveThreshold = value; break;
            case "TimeSync": _useTimeSync = value; break;
        }
    }

    private void ResetSplineState(Vector3 pos) {
        _p0 = pos; _p1 = pos;
        _t0 = Vector3.zero; _t1 = Vector3.zero;
        _serverPos = pos; _splineTimer = 0;
        _vel = Vector3.zero;
        _targetPos = pos;
    }

    private void OnNetworkDataChanged(PosAndRotNetworkData oldVal, PosAndRotNetworkData newVal) {
        if (newVal.Position == Vector3.zero) return;
        float now = Time.time;
        float packetTime = _useTimeSync ? newVal.Timestamp : now;

        if (_useTimeSync) {
            float rtt = NetworkManager.Singleton.NetworkConfig.NetworkTransport.GetCurrentRtt(APP_CONFIG.GAME.SERVER_ID) / 1000f;
            if (rtt >= 0) {
                float estimatedServerNow = newVal.Timestamp + rtt / 2f;
                float currentOffset = estimatedServerNow - now;
                if (_timeOffset == 0f) _timeOffset = currentOffset;
                else _timeOffset = Mathf.Lerp(_timeOffset, currentOffset, SYNC_ALPHA);
            }
        }
        
        float dt = packetTime - _lastServerRecvTime;
        if (!_useTimeSync) dt = now - _lastServerRecvTime;
        if (dt < 0.001f) dt = 0.05f;

        _lastServerRecvTime = packetTime;

        float interval = now - _lastPacketLocalTime; _lastPacketLocalTime = now;
        if (_packetIntervals.Count >= 20) _packetIntervals.RemoveAt(0);
        _packetIntervals.Add(interval);
        float jitter = CalculateStdDev(_packetIntervals) * 1000f;

        _serverPos = newVal.Position;
        _serverVel = newVal.Velocity;
        _serverAcc = newVal.Acceleration;
        
        if (dt > 0.0001f) _serverAcc = (_serverVel - _prevServerVel) / dt;
        _prevServerVel = _serverVel;

        float error = Vector3.Distance(transform.position, newVal.Position);
        _aeeSum += error; _aeeCount++; if (error <= hitThreshold) _hitCount++;
        
        if (UIManager.Instance != null) { try { UIManager.Instance.averageExportError.text = $"AEE: {(_aeeSum / Math.Max(1, _aeeCount)):F2}"; UIManager.Instance.hitPercentage.text = $"Hit: {(_aeeCount > 0 ? (_hitCount * 100f / _aeeCount) : 0f):F1}%"; UIManager.Instance.jitterEstimate.text = $"Jitter: {jitter:F0}ms"; UIManager.Instance.instantError.text = $"Err: {error:F2}m"; } catch {} }

        latestServerState = new StatePayload();
        latestServerState.tick = newVal.Tick;
        latestServerState.position = newVal.Position;
        latestServerState.rotation = Quaternion.Euler(newVal.Rotation);
        latestServerState.speed = newVal.Speed;
    }

    private float CalculateStdDev(List<float> values) { if (values.Count <= 1) return 0; float avg = 0; foreach(var v in values) avg += v; avg /= values.Count; float sumSq = 0; foreach(var v in values) sumSq += (v - avg) * (v - avg); return Mathf.Sqrt(sumSq / (values.Count - 1)); }

    private void AutoDrive()
    {
        if (waypoints == null || waypoints.Count == 0) return;

        Transform targetWaypoint = waypoints[currentWaypointIndex];
        float distance = Vector3.Distance(transform.position, targetWaypoint.position);

        if (distance < waypointThreshold) {
            currentWaypointIndex = (currentWaypointIndex + 1) % waypoints.Count;
        }

        Vector3 relativeVector = transform.InverseTransformPoint(targetWaypoint.position);
        float perfectSteer = (relativeVector.x / relativeVector.magnitude);

        float noise = (Mathf.PerlinNoise(Time.time * 2.0f, 0) - 0.5f) * 0.2f; 
        inputSteering = Mathf.Lerp(inputSteering, perfectSteer + noise, Time.fixedDeltaTime * 5f);

        float throttleNoise = 1.0f;
        if (Time.time % 2.0f > 1.8f) throttleNoise = 0.5f; 
        inputAcceleration = 1f * throttleNoise;
        if (Mathf.Abs(inputSteering) > 0.5f) inputAcceleration *= 0.5f; 
        inputBrake = 0f;
    }

    [Rpc(SendTo.Server)]
    private void SubmitBotInputServerRpc(float steer, float accel, float brake)
    {
        this.inputSteering = steer;
        this.inputAcceleration = accel;
        this.inputBrake = brake;
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
        
        ResetSplineState(startPos);
        _serverPos = startPos;
        _targetPos = startPos; 
    }

    public void FixedUpdate() {
        // Owner client: send input each fixed tick (deterministic tick)
        if (!IsServer && IsOwner && !_rigidbody.isKinematic)
        {
            // Build input for THIS tick
            InputPayload inputPayload = new InputPayload();
            inputPayload.tick = currentTick;
            inputPayload.inputAcceleration = inputAcceleration;
            inputPayload.inputBrake = inputBrake;
            inputPayload.inputSteering = inputSteering;

            // collision flag if needed
            RaycastHit[] hits = Physics.RaycastAll(transform.position, transform.forward);
            inputPayload.isCollide = false;
            foreach (var hit in hits)
            {
                if (!hit.collider.isTrigger)
                {
                    inputPayload.isCollide = true;
                    break;
                }
            }

            int bufferIndex = currentTick % BUFFER_SIZE;
            inputBuffer[bufferIndex] = inputPayload;

            // local prediction using the same deterministic simulate
            StatePayload predicted = SimulateMovementFromTransform(transform.position, transform.rotation, currentSpeed, inputPayload, Time.fixedDeltaTime);
            stateBuffer[bufferIndex] = predicted;

            // apply immediate local predicted transform so client "feels" instant response
            transform.position = predicted.position;
            transform.rotation = predicted.rotation;
            currentSpeed = predicted.speed;

            // send *every tick* to server
            SubmitInputServerRpc(inputPayload);
            if (ENABLE_DEBUG_LOG) Debug.Log($"[Client send] id={ID} tick={inputPayload.tick} acc={inputPayload.inputAcceleration:F2} steer={inputPayload.inputSteering:F2} time={Time.time:F3}");

            // advance tick exactly once per FixedUpdate
            currentTick++;

            // After sending, run reconciliation using latest server state (if any)
            HandleServerReconciliation();
        }

        // Server: process ticks with an input delay to allow packets to arrive
        if (IsServer && !_rigidbody.isKinematic)
        {
            int tickToProcess = serverTickProcessing - SERVER_INPUT_DELAY;

            if (tickToProcess >= 1)
            {
                InputPayload inputForThisTick;
                if (pendingInputs.TryGetValue(tickToProcess, out inputForThisTick))
                {
                    // consume
                    pendingInputs.Remove(tickToProcess);
                    lastKnownInput = inputForThisTick;
                    if (ENABLE_DEBUG_LOG) Debug.Log($"[Server process] id={ID} processing tick={tickToProcess} acc={inputForThisTick.inputAcceleration:F2} steer={inputForThisTick.inputSteering:F2} time={Time.time:F3}");
                }
                else
                {
                    // missing input => use lastKnownInput fallback (less jarring than zero)
                    inputForThisTick = lastKnownInput;
                    inputForThisTick.tick = tickToProcess;
                    if (ENABLE_DEBUG_LOG) Debug.Log($"[Server process] id={ID} missing input tick={tickToProcess}, using lastKnownInput");
                }

                StatePayload processedState = ProcessMovement(inputForThisTick);
                RaceManager.Instance.PendState(ID, processedState);

                // update lastProcessedTick and network data tick to inform clients
                lastProcessedTick = tickToProcess;
            }

            // always advance serverTickProcessing counter
            serverTickProcessing++;
        }

        // Server updates network data for clients
        if (IsServer)
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
        else if (IsClient && !IsOwner && !_rigidbody.isKinematic && _networkData.Value.Position != Vector3.zero) 
        {
            // remote client: dead reckoning / interpolation (unchanged)
            if (!UseDeadReckoning)
            {
                _rigidbody.MovePosition(_serverPos);
                _rigidbody.MoveRotation(Quaternion.Euler(_networkData.Value.Rotation));
            }
            else
            {
                float now = Time.time;
                float serverTimeNow = _useTimeSync ? (now + _timeOffset) : now;
                float predictTime = Mathf.Clamp(serverTimeNow - _lastServerRecvTime, 0f, 0.5f);
                
                _targetPos = transform.position;
                switch (currentDRMode) {
                    case DeadReckoningMode.None: 
                        _targetPos = _serverPos; 
                        break;
                    case DeadReckoningMode.Linear: 
                        _targetPos = _serverPos + _serverVel * predictTime; 
                        break;
                    case DeadReckoningMode.Quadratic: 
                        _targetPos = _serverPos + _serverVel * predictTime + 0.5f * _serverAcc * predictTime * predictTime * 0.8f; 
                        break;
                }

                if (currentCorrectionMode == CorrectionMode.SmoothDamp)
                {
                    Vector3 smoothPos = Vector3.SmoothDamp(transform.position, _targetPos, ref _vel, APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME, float.PositiveInfinity, Time.fixedDeltaTime);
                    _rigidbody.velocity = _vel;
                    _rigidbody.MovePosition(smoothPos);
                }
                else
                {
                    float t = Time.fixedDeltaTime / Mathf.Max(APP_CONFIG.GAME.SMOOTH_INTERPOLATION_TIME, 0.001f);
                    Vector3 lerpPos = Vector3.Lerp(transform.position, _targetPos, t);
                    _rigidbody.MovePosition(lerpPos);
                    _vel = (lerpPos - transform.position) / Time.fixedDeltaTime;
                }
                var targetRot = Quaternion.Euler(_networkData.Value.Rotation);
                _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 10f));
            }
            
            CalculateJerk();
        }

        // APPLY ACTIVE CORRECTION (blend each FixedUpdate) - only owner does correction to its local predicted transform
        if (correctionActive && IsOwner)
        {
            ApplyActiveCorrectionStep();
        }
    }
    
    // Apply a single fixed-step of blending towards correctionTarget.
    private void ApplyActiveCorrectionStep()
    {
        correctionTimer += Time.fixedDeltaTime;
        float alpha = Mathf.Clamp01(correctionTimer / correctionDuration);

        float dist = Vector3.Distance(correctionStartState.position, correctionTarget.position);
        if (dist >= teleportThreshold) alpha = 1f;

        Vector3 newPos = Vector3.Lerp(correctionStartState.position, correctionTarget.position, alpha);
        Quaternion newRot = Quaternion.Slerp(correctionStartState.rotation, correctionTarget.rotation, alpha);
        float newSpeed = Mathf.Lerp(correctionStartState.speed, correctionTarget.speed, alpha);

        transform.position = newPos;
        transform.rotation = newRot;
        _rigidbody.position = newPos;
        _rigidbody.rotation = newRot;
        _rigidbody.velocity = newRot * Vector3.forward * newSpeed;
        currentSpeed = newSpeed;

        if (alpha >= 1f)
        {
            correctionActive = false;
            correctionTimer = 0f;
        }
    }
    
    public void Update() { if (IsServer && IsSpawned) { var iSpeed = Mathf.FloorToInt(_rigidbody.velocity.magnitude); if (iSpeed != Speed) Speed = iSpeed; } }

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

    public void UpdateTick()
    {
        HandleTick();
    }

    void HandleTick()
    {
        // deprecated - sending logic moved to FixedUpdate owner block
    }

    // Pure deterministic simulation used by client/server (single-step)
    StatePayload SimulateMovementFromTransform(Vector3 startPos, Quaternion startRot, float startSpeed, InputPayload input, float dt)
    {
        Vector3 pos = startPos;
        Quaternion rot = startRot;
        float speed = startSpeed;

        float accel = Mathf.Clamp(input.inputAcceleration, -1f, 1f);
        float steer = Mathf.Clamp(input.inputSteering, -1f, 1f);
        float brake = Mathf.Clamp(input.inputBrake, 0f, 1f);

        if (Mathf.Abs(accel) > 0.01f)
        {
            speed += accel * accelerationRate * dt;
        }
        else
        {
            speed = Mathf.Lerp(speed, 0f, decelerationRate * dt);
        }

        if (brake > 0.1f)
        {
            speed = Mathf.Lerp(speed, 0f, brakeRate * dt);
        }

        float currentMaxForward = RubberBand ? maxSpeed * NetworkPlayer.RubberBandCoefficient : maxSpeed;
        speed = Mathf.Clamp(speed, -maxReverseSpeed, currentMaxForward);

        if (Mathf.Abs(speed) > 0.5f)
        {
            float directionMultiplier = Mathf.Sign(speed);
            float turnAmount = steer * turnSpeed * directionMultiplier * dt;
            Quaternion deltaRot = Quaternion.Euler(0f, turnAmount, 0f);
            rot = rot * deltaRot;
        }

        pos += rot * Vector3.forward * speed * dt;

        return new StatePayload()
        {
            tick = input.tick,
            position = pos,
            rotation = rot,
            speed = speed
        };
    }

    // Server-side and client-side use ProcessMovement to step rigidbody (server) or to compute authoritative state (client replay uses SimulateMovementFromTransform)
    public StatePayload ProcessMovement(InputPayload input)
    {
        if (input.tick == 0) return new StatePayload();
        // Server uses rigidbody current pos/rot as start
        Vector3 startPos = _rigidbody.position;
        Quaternion startRot = _rigidbody.rotation;
        float startSpeed = currentSpeed;

        StatePayload result = SimulateMovementFromTransform(startPos, startRot, startSpeed, input, Time.fixedDeltaTime);

        // apply to server rigidbody (server authoritative)
        if (IsServer)
        {
            _rigidbody.MoveRotation(result.rotation);
            _rigidbody.MovePosition(result.position);
            currentSpeed = result.speed;
        }

        return result;
    }

    // Deterministic replay from a given server state (no Rigidbody modification)
    StatePayload SimulateFromState(StatePayload startState, int startTickExclusive, int endTickExclusive)
    {
        Vector3 pos = startState.position;
        Quaternion rot = startState.rotation;
        float speed = startState.speed;

        for (int t = startTickExclusive + 1; t < endTickExclusive; t++)
        {
            int idx = t % BUFFER_SIZE;
            InputPayload inPayload = inputBuffer[idx];

            // if inputBuffer doesn't have matching tick, use lastKnownInput fallback
            if (inPayload.tick != t)
            {
                inPayload = lastKnownInput;
                inPayload.tick = t;
            }

            StatePayload step = SimulateMovementFromTransform(pos, rot, speed, inPayload, Time.fixedDeltaTime);
            pos = step.position;
            rot = step.rotation;
            speed = step.speed;
        }

        return new StatePayload()
        {
            tick = Mathf.Max(startTickExclusive + 1, endTickExclusive - 1),
            position = pos,
            rotation = rot,
            speed = speed
        };
    }

    void HandleServerReconciliation()
    {
        if (latestServerState.tick == 0) return;
        if (lastProcessedState.tick == latestServerState.tick && lastProcessedState.Equals(latestServerState)) return;

        lastProcessedState = latestServerState;

        int serverStateBufferIndex = latestServerState.tick % BUFFER_SIZE;

        if (stateBuffer[serverStateBufferIndex].tick == 0 || stateBuffer[serverStateBufferIndex].tick != latestServerState.tick)
        {
            if (ENABLE_DEBUG_LOG) Debug.Log($"[Reconcile] Missing local state for serverTick={latestServerState.tick}");
            return;
        }

        float positionError = Vector3.Distance(latestServerState.position, stateBuffer[serverStateBufferIndex].position);

        const float RECONCILE_THRESHOLD = 0.05f; // meter

        if (positionError > RECONCILE_THRESHOLD && !isRewinding && Mathf.Abs(currentTick - latestServerState.tick) < BUFFER_SIZE)
        {
            isRewinding = true;

            // replay from server state
            StatePayload predicted = SimulateFromState(latestServerState, latestServerState.tick, currentTick);

            float dist = Vector3.Distance(transform.position, predicted.position);
            if (dist >= teleportThreshold)
            {
                // immediate apply
                transform.position = predicted.position;
                transform.rotation = predicted.rotation;
                _rigidbody.position = predicted.position;
                _rigidbody.rotation = predicted.rotation;
                _rigidbody.velocity = predicted.rotation * Vector3.forward * predicted.speed;
                currentSpeed = predicted.speed;
                correctionActive = false;
                correctionTimer = 0f;
                if (ENABLE_DEBUG_LOG) Debug.Log($"[Reconcile] Teleported to predicted tick={predicted.tick} dist={dist:F2}");
            }
            else
            {
                correctionStartState = new StatePayload() {
                    tick = (stateBuffer[(currentTick-1+BUFFER_SIZE)%BUFFER_SIZE].tick),
                    position = transform.position,
                    rotation = transform.rotation,
                    speed = currentSpeed
                };
                correctionTarget = predicted;
                correctionTimer = 0f;
                correctionActive = true;
                if (ENABLE_DEBUG_LOG) Debug.Log($"[Reconcile] Start blend to tick={predicted.tick} error={positionError:F3} distToPred={dist:F3}");
            }

            // update local stateBuffer by replaying so future reconciles have consistent base
            int tickToProcess = latestServerState.tick + 1;
            while (tickToProcess < currentTick)
            {
                int bufferIndex = tickToProcess % BUFFER_SIZE;
                StatePayload sim = SimulateFromState(latestServerState, latestServerState.tick, tickToProcess + 1);
                stateBuffer[bufferIndex] = sim;
                tickToProcess++;
            }

            isRewinding = false;
        }
    }

    [Rpc(SendTo.Server)]
    public void SubmitInputServerRpc(InputPayload input)
    {
        // executed on server
        if (!pendingInputs.ContainsKey(input.tick))
        {
            pendingInputs.Add(input.tick, input);
            lastKnownInput = input;
            if (ENABLE_DEBUG_LOG) Debug.Log($"[Server][RPC received] id={ID} input.tick={input.tick} acc={input.inputAcceleration:F2} steer={input.inputSteering:F2} time={Time.time:F3}");
            if (pendingInputs.Count > 5000) pendingInputs.Remove(pendingInputs.Keys.First());
        }
        else
        {
            // duplicate/retransmit - replace
            pendingInputs[input.tick] = input;
            if (ENABLE_DEBUG_LOG) Debug.Log($"[Server][RPC replace] id={ID} input.tick={input.tick}");
        }
    }

    public void ApplyState(StatePayload state)
    {
        _rigidbody.position = state.position;
        _rigidbody.rotation = state.rotation;
        
        currentSpeed = state.speed;
    }

    public StatePayload GetStateOfCar()
    {
        return new StatePayload()
        {
            position = transform.position,
            rotation = transform.rotation,
            speed = currentSpeed
        };
    }

    public void ClientPendNewInput(Vector2 moveInput, float brake)
    {
        InputPayload input = new InputPayload()
        {
            inputAcceleration = moveInput.y,
            inputSteering = moveInput.x,
            inputBrake = brake
        };

        clientInputList.Add(input);
    }
    #endregion
}