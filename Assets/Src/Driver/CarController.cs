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
    public enum CorrectionMode { SmoothDamp, Lerp }

    [Header("Dead Reckoning Configuration")]
    [SerializeField] private DeadReckoningMode currentDRMode = DeadReckoningMode.Quadratic;
    [SerializeField] private CorrectionMode currentCorrectionMode = CorrectionMode.SmoothDamp;

    [Header("Client Side Prediction and Server Reconcilation")]
    private int currentTick = 0; 
    private const int BUFFER_SIZE = 4096;

    private StatePayload[] stateBuffer;
    private InputPayload[] inputBuffer;
    private StatePayload latestServerState;
    private StatePayload lastProcessedState;
    private SortedDictionary<int, InputPayload> pendingInputs = new SortedDictionary<int, InputPayload>();
    private int lastProcessedTick = 0;
    private bool isRewinding = false;

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

            if (stateBuffer != null && stateBuffer.Length > 0)
            {
                stateBuffer[0] = new StatePayload()
                {
                    tick = 0,
                    position = GameManager.Instance.RACE_POS[NetworkPlayer.StartPos],
                    rotation = Quaternion.Euler(Vector3.zero)
                };
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

    private int BufIdx(int tick) => ((tick % BUFFER_SIZE) + BUFFER_SIZE) % BUFFER_SIZE;

    private void Start()
    {
        if (GameManager.Instance != null && GameManager.Instance.waypointContainer != null)
        {
            waypoints = GameManager.Instance.GetLevelWaypoints();
        }

        _rigidbody = GetComponent<Rigidbody>();

        if (IsClient && IsOwner)
        {
            stateBuffer = new StatePayload[BUFFER_SIZE];
            inputBuffer = new InputPayload[BUFFER_SIZE];

            for (int i = 0; i < BUFFER_SIZE; i++)
            {
                stateBuffer[i] = new StatePayload() { tick = 0, position = transform.position, rotation = transform.rotation, speed = currentSpeed };
                inputBuffer[i] = new InputPayload() { tick = 0, inputAcceleration = 0f, inputSteering = 0f, inputBrake = 0f, isCollide = false };
            }
        }

        lastKnownInput = new InputPayload() { tick = 1, inputAcceleration = 0f, inputSteering = 0f, inputBrake = 0f, isCollide = false };
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
        ResetSplineState(transform.position);

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
            float rtt = 0f;
            try { rtt = NetworkManager.Singleton.NetworkConfig.NetworkTransport.GetCurrentRtt(APP_CONFIG.GAME.SERVER_ID) / 1000f; } catch { rtt = 0f; }
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

        // Debug.Log($"{GlobalVar.CLIENT} <color=yellow>[{ID}]</color> {GlobalVar.CLIENT_RECEIVE_STATE} " +
        //         GlobalVar.GetStringDataList(new []{
        //             ("tick", $"{newVal.Tick}"),
        //             ("position", $"{newVal.Position}"),
        //             ("rotation", $"{newVal.Rotation}"),
        //             ("speed", $"{newVal.Speed}"),
        //         })
        //     );
    }

    private float CalculateStdDev(List<float> values) { if (values.Count <= 1) return 0; float avg = 0; foreach(var v in values) avg += v; avg /= values.Count; float sumSq = 0; foreach(var v in values) sumSq += (v - avg) * (v - avg); return Mathf.Sqrt(sumSq / (values.Count - 1)); }

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

    public (bool, int) ProcessFixedCarController()
    {
        bool serverHasInput = false;

        float tickDt = Time.fixedDeltaTime;

        if (IsClient && IsOwner && !_rigidbody.isKinematic)
        {
            if (currentTick == 0 && NetworkManager.Singleton != null) 
            {
                currentTick = NetworkManager.Singleton.ServerTime.Tick;
            }
            else 
            {
                currentTick++;
            }

            HandleServerReconciliation();

            InputPayload inputPayload = new InputPayload();
            inputPayload.tick = currentTick;
            inputPayload.inputAcceleration = inputAcceleration;
            inputPayload.inputBrake = inputBrake;
            inputPayload.inputSteering = inputSteering;

            Vector3 rayOrigin = _rigidbody.position + transform.forward * 1.5f + Vector3.up * 0.5f;
            RaycastHit[] hits = Physics.RaycastAll(rayOrigin, transform.forward, 2.0f);
            
            inputPayload.isCollide = false;
            foreach (var hit in hits)
            {
                if (!hit.collider.isTrigger && hit.collider.transform.root.gameObject != this.gameObject)
                {
                    if (Vector3.Dot(hit.normal, Vector3.up) < 0.8f)
                    {
                        inputPayload.isCollide = true;
                        break;
                    }
                }
            }

            int bufferIndex = BufIdx(currentTick);
            inputBuffer[bufferIndex] = inputPayload;

            StatePayload predicted = SimulateMovementFromTransform(_rigidbody.position, _rigidbody.rotation, currentSpeed, inputPayload, tickDt);
            stateBuffer[bufferIndex] = predicted;

            _rigidbody.MovePosition(predicted.position);
            _rigidbody.MoveRotation(predicted.rotation);
            currentSpeed = predicted.speed;
            //_rigidbody.velocity = Vector3.zero;
            SubmitInputServerRpc(inputPayload);

            // Debug.Log($"{GlobalVar.CLIENT} <color=yellow>[{ID}]</color> {GlobalVar.CLIENT_SEND_INPUT} " +
            //     GlobalVar.GetStringDataList(new []{
            //         ("tick", $"{currentTick}"),
            //         ("position", $"{predicted.position}"),
            //         ("rotation", $"{predicted.rotation}"),
            //         ("speed", $"{predicted.speed}"),
            //     })
            // );
        }

        if (IsServer && !_rigidbody.isKinematic)
        {
            int pendedInputCount = pendingInputs.Count;
            int inputProcessedCount = 0;

            Vector3 tempPos = _rigidbody.position;
            Quaternion tempRot = _rigidbody.rotation;
            float tempSpeed = currentSpeed;

            int currentServerTick = NetworkManager.Singleton.ServerTime.Tick;

            while (pendingInputs.Count > 0)
            {
                int nextTick = pendingInputs.Keys.First();

                if (nextTick > currentServerTick) 
                {
                    break;
                }
                
                if (lastProcessedTick == 0)
                {
                    lastProcessedTick = nextTick - 1; 
                }

                if (nextTick <= lastProcessedTick)
                {
                    RaceManager.Instance.PendInput(ID, pendingInputs[nextTick]);
                    pendingInputs.Remove(nextTick);
                    continue;
                }

                if (nextTick > lastProcessedTick + 1)
                {
                    if (nextTick - lastProcessedTick > 30)
                    {
                        lastProcessedTick = nextTick - 1;
                        continue;
                    }

                    InputPayload fallbackInput = lastKnownInput;
                    fallbackInput.tick = lastProcessedTick + 1; 

                    RaceManager.Instance.PendInput(ID, fallbackInput);

                    StatePayload stepStateGap = SimulateMovementFromTransform(tempPos, tempRot, tempSpeed, fallbackInput, Time.fixedDeltaTime);
                    tempPos = stepStateGap.position;
                    tempRot = stepStateGap.rotation;
                    tempSpeed = stepStateGap.speed;

                    lastProcessedTick = fallbackInput.tick;
                    serverHasInput = true;
                    continue;
                }

                InputPayload inputForThisTick = pendingInputs[nextTick];
                pendingInputs.Remove(nextTick);
                lastKnownInput = inputForThisTick;

                RaceManager.Instance.PendInput(ID, inputForThisTick);

                StatePayload stepState = SimulateMovementFromTransform(tempPos, tempRot, tempSpeed, inputForThisTick, Time.fixedDeltaTime);
                tempPos = stepState.position;
                tempRot = stepState.rotation;
                tempSpeed = stepState.speed;

                lastProcessedTick = nextTick;
                serverHasInput = true;
                inputProcessedCount++;
            }

            if (serverHasInput)
            {
                _rigidbody.MovePosition(tempPos);
                _rigidbody.MoveRotation(tempRot);
                currentSpeed = tempSpeed;
            }

            // if (ID != 0) Debug.Log($"Car {ID} pended inputs: {pendedInputCount}. Processed {inputProcessedCount} inputs");
        }

        

        if (IsServer)
        {
            if (!_rigidbody.isKinematic) _serverVel = _rigidbody.velocity; 
            else _networkData.Value = new PosAndRotNetworkData() { Position = Vector3.zero, Rotation = Vector3.zero };
        }
        else if (IsClient && !IsOwner && !_rigidbody.isKinematic && _networkData.Value.Position != Vector3.zero)
        {
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
                    _vel = (lerpPos - transform.position) / Time.fixedDeltaTime;
                    _rigidbody.MovePosition(lerpPos);
                }
                var targetRot = Quaternion.Euler(_networkData.Value.Rotation);
                _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 10f));
            }

            CalculateJerk();
        }

        return (serverHasInput, lastProcessedTick);
    }

    public void ApplyInputForPhysics(InputPayload input)
    {
        if (input.tick == 0) return;

        StatePayload state = SimulateMovementFromTransform(_rigidbody.position, _rigidbody.rotation, currentSpeed, input, Time.fixedDeltaTime);

        _rigidbody.MovePosition(state.position);
        _rigidbody.MoveRotation(state.rotation);
        currentSpeed = state.speed;
    }

    // public void FixedUpdate() {
        
    // }

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
                Position = state.position, // Lấy thẳng tọa độ tính toán từ Payload
                Rotation = state.rotation.eulerAngles,
                Velocity = _rigidbody.velocity,
                Timestamp = Time.time,
                Tick = state.tick, 
                Speed = state.speed
            };
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

    

    public StatePayload ProcessMovement(InputPayload input)
    {
        if (input.tick == 0) return new StatePayload();
        
        Vector3 startPos = _rigidbody.position;
        Quaternion startRot = _rigidbody.rotation;
        float startSpeed = currentSpeed;

        float tickDt = Time.fixedDeltaTime;
        StatePayload result = SimulateMovementFromTransform(startPos, startRot, startSpeed, input, tickDt);

        if (IsServer)
        {
            _rigidbody.position = result.position;
            _rigidbody.rotation = result.rotation;
            currentSpeed = result.speed;
        }

        return result;
    }

    //Simulate deterministically
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

        float rubberMultiplier = 1f;
        if (RubberBand && NetworkPlayer != null)
        {
            rubberMultiplier = NetworkPlayer.RubberBandCoefficient;
        }

        float currentMaxForward = maxSpeed * rubberMultiplier;
        speed = Mathf.Clamp(speed, -maxReverseSpeed, currentMaxForward);

        if (Mathf.Abs(speed) > 0.5f)
        {
            float directionMultiplier = Mathf.Sign(speed);
            float turnAmount = steer * turnSpeed * directionMultiplier * dt;
            Quaternion deltaRot = Quaternion.Euler(0f, turnAmount, 0f);
            rot *= deltaRot;
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

    public StatePayload ProcessMovementPhysically(InputPayload input)
    {
        if (input.tick == 0) return new StatePayload();
        
        Vector3 startPos = _rigidbody.position;
        Quaternion startRot = _rigidbody.rotation;
        float startSpeed = currentSpeed;

        float tickDt = Time.fixedDeltaTime;
        StatePayload result = SimulateMovementPhysically(startPos, startRot, startSpeed, input, tickDt);

        // if (IsServer)
        // {
        //     _rigidbody.position = result.position;
        //     _rigidbody.rotation = result.rotation;
        //     currentSpeed = result.speed;
        // }

        return result;
    }

    public StatePayload SimulateMovementPhysically(Vector3 startPos, Quaternion startRot, float startSpeed, InputPayload input, float dt)
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

        float rubberMultiplier = 1f;
        if (RubberBand && NetworkPlayer != null)
        {
            rubberMultiplier = NetworkPlayer.RubberBandCoefficient;
        }

        float currentMaxForward = maxSpeed * rubberMultiplier;
        speed = Mathf.Clamp(speed, -maxReverseSpeed, currentMaxForward);

        if (Mathf.Abs(speed) > 0.5f)
        {
            float directionMultiplier = Mathf.Sign(speed);
            float turnAmount = steer * turnSpeed * directionMultiplier * dt;
            Quaternion deltaRot = Quaternion.Euler(0f, turnAmount, 0f);
            rot *= deltaRot;

            _rigidbody.MoveRotation(rot);
        }

        pos += rot * Vector3.forward * speed * dt;

        _rigidbody.MovePosition(pos);

        return new StatePayload()
        {
            tick = input.tick,
            position = pos,
            rotation = rot,
            speed = speed
        };
    }


    //Server Reconciliation
    void HandleServerReconciliation()
    {
        if (latestServerState.tick == 0) return;
        if (lastProcessedState.tick == latestServerState.tick && lastProcessedState.Equals(latestServerState)) return;

        lastProcessedState = latestServerState;

        int serverStateBufferIndex = BufIdx(latestServerState.tick);
        StatePayload predictedPastState = stateBuffer[serverStateBufferIndex];

        if (predictedPastState.tick != latestServerState.tick) 
        {
            return; 
        }

        float positionError = Vector3.Distance(latestServerState.position, predictedPastState.position);
        float rotationError = Quaternion.Angle(latestServerState.rotation, predictedPastState.rotation);

        const float RECONCILE_POS_THRESHOLD = 1.5f;
        const float RECONCILE_ROT_THRESHOLD = 1.0f;

        if ((positionError > RECONCILE_POS_THRESHOLD || rotationError > RECONCILE_ROT_THRESHOLD) && !isRewinding)
        {
            isRewinding = true;

            Vector3 rewindPos = latestServerState.position;
            Quaternion rewindRot = latestServerState.rotation;
            float rewindSpeed = latestServerState.speed;

            float tickDt = Time.fixedDeltaTime;

            for (int tickToProcess = latestServerState.tick + 1; tickToProcess <= currentTick; tickToProcess++)
            {
                int bufferIndex = BufIdx(tickToProcess);
                InputPayload inputForTick = inputBuffer[bufferIndex];

                if (inputForTick.tick != tickToProcess)
                {
                    inputForTick = lastKnownInput;
                    inputForTick.tick = tickToProcess;
                }

                StatePayload stepState = SimulateMovementFromTransform(rewindPos, rewindRot, rewindSpeed, inputForTick, tickDt);

                rewindPos = stepState.position;
                rewindRot = stepState.rotation;
                rewindSpeed = stepState.speed;

                stateBuffer[bufferIndex] = stepState;
            }

            _rigidbody.MovePosition(rewindPos);
            _rigidbody.MoveRotation(rewindRot);
            _rigidbody.velocity = rewindRot * Vector3.forward * rewindSpeed;
            currentSpeed = rewindSpeed;

            if (ENABLE_DEBUG_LOG) 
                Debug.Log($"[Reconcile] Đã sửa lỗi! Sai số Vị trí: {positionError:F3} | Góc: {rotationError:F3}. Đã re-simulate từ tick {latestServerState.tick} đến {currentTick}");

            isRewinding = false;
        }
    }

    [Rpc(SendTo.Server)]
    public void SubmitInputServerRpc(InputPayload input)
    {
        if (!IsServer) return;

        if (lastProcessedTick > 0 && (input.tick < lastProcessedTick - BUFFER_SIZE || input.tick > lastProcessedTick + 600))
        {
            return;
        }

        if (!pendingInputs.ContainsKey(input.tick))
        {
            pendingInputs.Add(input.tick, input);
            if (pendingInputs.Count > 5000) pendingInputs.Remove(pendingInputs.Keys.First());
        }
        else
        {
            pendingInputs[input.tick] = input;
        }

        // Debug.Log($"{GlobalVar.SERVER} {GlobalVar.SERVER_RECEIVE_INPUT} " +
        //         GlobalVar.GetStringDataList(new []{
        //             ("tick", $"{input.tick}"),
        //         })
        //     );
    }

    public void ApplyState(StatePayload state)
    {
        _rigidbody.position = state.position;
        _rigidbody.rotation = state.rotation;
        currentSpeed = state.speed;
    }

    // Sửa lại hàm GetStateOfCar để hỗ trợ ép kiểu Tick khi Rewind
    public StatePayload GetStateOfCar(int overwriteTick = -1)
    {
        int currentTick = overwriteTick > 0 ? overwriteTick : lastProcessedTick;

        // Đảm bảo nếu không có NetworkManager thì trả về 0, nhưng ưu tiên overwriteTick
        if (overwriteTick <= 0 && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            currentTick = lastProcessedTick; // Hoặc NetworkManager.Singleton.ServerTime.Tick tùy logic của bạn, nhưng lastProcessedTick chuẩn hơn cho State
        }

        return new StatePayload()
        {
            tick = currentTick, // Sử dụng tick đã được xác định rõ ràng
            position = transform.position,
            rotation = transform.rotation,
            speed = currentSpeed
        };
    }

    #endregion
}