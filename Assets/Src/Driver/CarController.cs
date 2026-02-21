using System;
using System.Collections;
using System.Collections.Generic;
using CustomTypes;
using TMPro;
using Unity.Netcode;
using UnityEngine;

public struct InputPayload
{
    public int tick;
    public float inputAcceleration;
    public float inputSteering;
    public float inputBrake;
}

public struct StatePayload
{
    public int tick;
    public Vector3 position;
    public Vector3 rotation;
    public float speed;
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
    [SerializeField] public float maxSpeed = 40f;          // Tốc độ tối đa khi tiến
    [SerializeField] public float maxReverseSpeed = 20f;   // Tốc độ tối đa khi lùi
    [SerializeField] public float accelerationRate = 20f;  // Gia tốc
    [SerializeField] public float decelerationRate = 10f;  // Tốc độ giảm tốc khi thả phím
    [SerializeField] public float brakeRate = 30f;         // Lực phanh
    [SerializeField] public float turnSpeed = 120f;        // Tốc độ xoay xe

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
    public bool UseDeadReckoning = false;
    public enum CorrectionMode { SmoothDamp, Lerp }

    [Header("Dead Reckoning Configuration")]
    [SerializeField] private DeadReckoningMode currentDRMode = DeadReckoningMode.Linear;
    [SerializeField] private CorrectionMode currentCorrectionMode = CorrectionMode.SmoothDamp;
    [SerializeField] private float snapThreshold = 10f; 
    
    [Header("Client Side Prediction and Server Reconcilation")]
    private int currentTick;
    private float minTimeBetweenTicks;
    private const float SERVER_TICK_RATE = 30f;
    private const int BUFFER_SIZE = 1024;

    private StatePayload[] stateBuffer;
    private InputPayload[] inputBuffer;
    private StatePayload latestServerState;
    private StatePayload lastProcessedState;

    public bool _useCubicSpline = false;
    public bool _useAdaptiveThreshold = false;
    public bool _useTimeSync = false;

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
            minTimeBetweenTicks = 1f / SERVER_TICK_RATE;
            stateBuffer = new StatePayload[BUFFER_SIZE];
            inputBuffer = new InputPayload[BUFFER_SIZE];
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
        latestServerState.rotation = newVal.Rotation;
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
        if (IsOwner && isBot)
        {
            AutoDrive();
            SubmitBotInputServerRpc(inputSteering, inputAcceleration, inputBrake);
        }

        if (!_rigidbody.isKinematic) 
        {
            if (!IsServer && IsOwner) 
            {
                UpdateTick();
            }
            else UpdateLocalPos();
        }
        
        if (IsServer)
        {
            if (!_rigidbody.isKinematic) 
            {
                _networkData.Value = new PosAndRotNetworkData() { 
                    Position = transform.position, 
                    Rotation = transform.rotation.eulerAngles, 
                    Velocity = _rigidbody.velocity, 
                    Acceleration = (Time.fixedDeltaTime > 0) ? (_rigidbody.velocity - _serverVel) / Time.fixedDeltaTime : Vector3.zero,
                    Timestamp = Time.time,
                    Tick = currentTick,
                    Speed = currentSpeed
                };
            } 
            else 
            { 
                _networkData.Value = new PosAndRotNetworkData() { Position = Vector3.zero, Rotation = Vector3.zero }; 
            }
        }
        else if (IsClient && !IsOwner && !_rigidbody.isKinematic && _networkData.Value.Position != Vector3.zero) {
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
    }
    
    public void Update() { if (IsServer && IsSpawned) { var iSpeed = Mathf.FloorToInt(_rigidbody.velocity.magnitude); if (iSpeed != Speed) Speed = iSpeed; } }

    // --- LOGIC DI CHUYỂN ARCADE MỚI ---
    void UpdateLocalPos() 
    {
        inputSteering = Mathf.Clamp(inputSteering, -1, 1); 
        inputAcceleration = Mathf.Clamp(inputAcceleration, -1, 1); 
        inputBrake = Mathf.Clamp(inputBrake, 0, 1);

        if (Mathf.Abs(inputAcceleration) > 0.01f)
        {
            currentSpeed += inputAcceleration * accelerationRate * Time.fixedDeltaTime;
        }
        else
        {
            currentSpeed = Mathf.Lerp(currentSpeed, 0, decelerationRate * Time.fixedDeltaTime);
        }

        if (inputBrake > 0.1f)
        {
            currentSpeed = Mathf.Lerp(currentSpeed, 0, brakeRate * Time.fixedDeltaTime);
        }

        float currentMaxForward = RubberBand ? maxSpeed * NetworkPlayer.RubberBandCoefficient : maxSpeed;
        currentSpeed = Mathf.Clamp(currentSpeed, -maxReverseSpeed, currentMaxForward);

        if (Mathf.Abs(currentSpeed) > 0.5f)
        {
            float directionMultiplier = Mathf.Sign(currentSpeed);
            float turnAmount = inputSteering * turnSpeed * directionMultiplier * Time.fixedDeltaTime;
            transform.Rotate(0, turnAmount, 0);
        }

        _rigidbody.velocity = transform.forward * currentSpeed;
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

    public void UpdateTick()
    {
        HandleTick();
        currentTick++;
    }

    void HandleTick()
    {
        if (!latestServerState.Equals(default(StatePayload)) &&
            (lastProcessedState.Equals(default(StatePayload)) ||
            !latestServerState.Equals(lastProcessedState)))
        {
            HandleServerReconciliation();
        }

        int bufferIndex = currentTick % BUFFER_SIZE;

        InputPayload inputPayload = new InputPayload();
        inputPayload.tick = currentTick;
        inputPayload.inputAcceleration = inputAcceleration;
        inputPayload.inputBrake = inputBrake;
        inputPayload.inputSteering = inputSteering;

        inputBuffer[bufferIndex] = inputPayload;
        stateBuffer[bufferIndex] = ProcessMovement(inputPayload);
    }

    StatePayload ProcessMovement(InputPayload input)
    {
        float accel = Mathf.Clamp(input.inputAcceleration, -1, 1);
        float steer = Mathf.Clamp(input.inputSteering, -1, 1);
        float brake = Mathf.Clamp(input.inputBrake, 0, 1);

        if (Mathf.Abs(accel) > 0.01f)
        {
            currentSpeed += accel * accelerationRate * Time.fixedDeltaTime;
        }
        else
        {
            currentSpeed = Mathf.Lerp(currentSpeed, 0, decelerationRate * Time.fixedDeltaTime);
        }

        if (brake > 0.1f)
        {
            currentSpeed = Mathf.Lerp(currentSpeed, 0, brakeRate * Time.fixedDeltaTime);
        }

        float currentMaxForward = RubberBand ? maxSpeed * NetworkPlayer.RubberBandCoefficient : maxSpeed;
        currentSpeed = Mathf.Clamp(currentSpeed, -maxReverseSpeed, currentMaxForward);

        if (Mathf.Abs(currentSpeed) > 0.5f)
        {
            float directionMultiplier = Mathf.Sign(currentSpeed);
            float turnAmount = steer * turnSpeed * directionMultiplier * Time.fixedDeltaTime;
            transform.Rotate(0, turnAmount, 0);
        }

        //transform.position += transform.forward * currentSpeed * Time.fixedDeltaTime;
        _rigidbody.velocity = transform.forward * currentSpeed;

        return new StatePayload()
        {
            tick = input.tick,
            position = transform.position,
            rotation = transform.rotation.eulerAngles,
            speed = currentSpeed
        };
    }

    void HandleServerReconciliation()
    {
        lastProcessedState = latestServerState;

        int serverStateBufferIndex = latestServerState.tick % BUFFER_SIZE;
        float positionError = Vector3.Distance(latestServerState.position, stateBuffer[serverStateBufferIndex].position);

        if (positionError > 100f)
        {
            Debug.Log("Reconcile now");

            transform.position = latestServerState.position;
            transform.rotation = Quaternion.Euler(latestServerState.rotation);
            currentSpeed = latestServerState.speed;

            stateBuffer[serverStateBufferIndex] = latestServerState;

            int tickToProcess = latestServerState.tick + 1;

            while (tickToProcess < currentTick)
            {
                int bufferIndex = tickToProcess % BUFFER_SIZE;
                StatePayload statePayload = ProcessMovement(inputBuffer[bufferIndex]);
                stateBuffer[bufferIndex] = statePayload;
                tickToProcess++;
            }

            _rigidbody.velocity = transform.forward * currentSpeed;
        }
    }
    #endregion
}