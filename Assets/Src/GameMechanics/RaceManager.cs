using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CustomTypes;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

[Serializable]
public class RaceManager : MonoBehaviour
{
    #region Variables

    private static AppConfig APP_CONFIG => AppConfig.Singleton;
    
    [SerializeField] public SemaphoreController semaphoreController;

    [SerializeField] [HideInInspector] public bool hasFinished;
    [SerializeField] [HideInInspector] public List<NetworkPlayer> waitList = new();
    [SerializeField] public List<NetworkPlayer> players = new();
    [SerializeField] [HideInInspector] public CircuitController circuitController;

    [SerializeField] [HideInInspector] private NetworkPlayer[] _cachedPos = new NetworkPlayer[APP_CONFIG.GAME.MAX_PLAYERS_PER_ROOM];
    [SerializeField] [HideInInspector] private GameObject[] _debugSpheres;
    [SerializeField] [HideInInspector] private string _debugRaceOrder;
    private readonly HashSet<int> _loggedFinishedPlayerIds = new();
    private readonly HashSet<int> _suppressedFinishLogPlayerIds = new();

    private bool IsRacing => UIManager.Instance.State is AppScreen.Game or AppScreen.EndGame;
    
    public static RaceManager Instance { get; private set; }

    public bool UseLagCompensation = true;

    #endregion

    #region Delegates and Events

    public delegate void PlayerLeft(NetworkPlayer networkPlayer);
    public event PlayerLeft OnPlayerLeft;

    public void OnPlayerHasFinished(NetworkPlayer player, bool oldValue, bool newValue)
    {
        if (newValue)
        {
            LogPlayerFinish(player);

            var finished = true;
            foreach (var racingPlayer in players) finished &= racingPlayer != null && racingPlayer.HasFinished;

            if (finished)
            {
                hasFinished = true;
                GameManager.Instance.State = GameState.Finished;
                StartCoroutine(RaceEndCountdown());
            }
        }
    }

    public void LogPlayerFinish(NetworkPlayer player)
    {
        if (player == null)
        {
            return;
        }

        if (_suppressedFinishLogPlayerIds.Remove(player.ID))
        {
            return;
        }

        if (!_loggedFinishedPlayerIds.Add(player.ID))
        {
            return;
        }

        RaceFinishLogger.LogFinish(player);
    }

    public void SuppressNextFinishLog(NetworkPlayer player)
    {
        if (player != null)
        {
            _suppressedFinishLogPlayerIds.Add(player.ID);
        }
    }
    
    private IEnumerator RaceEndCountdown()
    {
        var playersRacing = waitList.Where(p => p.StartPos != -1).ToList();
        playersRacing.ForEach(p => { if (p.IsOwner) p.IsReady = false; });

        UIManager.Instance.SetNotificationCanvas(true, "RACE ENDS IN", "SECONDS");
        
        for (var i = 3; i > 0; i--)
        {
            UIManager.Instance.notificationTime.text = i.ToString();
            semaphoreController.lights[Mathf.Abs(i - 3)].color = Color.red;
            yield return new WaitForSeconds(1);
        }

        UIManager.Instance.SetNotificationCanvas(false);

        players.Clear();

        EventManager.Instance.RaiseScreenChange(AppScreen.Game);
    }

    private void OnGameStateChange(GameState oldState, GameState newState)
    {
        if (newState == GameState.Started)
        {
            _loggedFinishedPlayerIds.Clear();
            _suppressedFinishLogPlayerIds.Clear();
            foreach (var player in waitList) AddToPlayers(player);
        }
    }

    #endregion

    #region Unity Callbacks

    private void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }

    private void Start()
    {
        if (circuitController == null) circuitController = GetComponent<CircuitController>();

        _debugSpheres = new GameObject[APP_CONFIG.GAME.MAX_PLAYERS_PER_ROOM];

        for (var i = 0; i < APP_CONFIG.GAME.MAX_PLAYERS_PER_ROOM; ++i)
        {
            _debugSpheres[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _debugSpheres[i].GetComponent<MeshRenderer>().enabled = false;
            _debugSpheres[i].GetComponent<SphereCollider>().enabled = false;
        }

        GameManager.Instance.OnGameStateChange += OnGameStateChange;

        inputBuffer = new CircularBuffer<InputPayload[]>(bufferSize);
        stateBuffer = new CircularBuffer<StatePayload[]>(bufferSize);
        //rocketBuffer = new CircularBuffer<Dictionary<ulong, RocketStatePayload>>(bufferSize);

        networkTimer = new NetworkTimer(TICK_RATE);
    }

    private void Update()
    {
        if (players.Count > 0 && IsRacing)
            try
            {
                UpdateRaceProgress();
            }
            catch (Exception)
            {
                StartCoroutine(LeaveRace());
            }

        if (waitList.Count > 0 && !AppScreen.Menu.Equals(UIManager.Instance.State))
            try
            {
                var pos = waitList[^1].car.transform;
            }
            catch (Exception)
            {
                StartCoroutine(LeaveRace());
            }

        if (GameManager.Instance.State == GameState.Started && networkTimer != null)
        {
            networkTimer.Update(Time.deltaTime);
        }

        rewindCooldownCounter += Time.deltaTime;
    }

    #endregion

    #region Update race progress

    private void UpdateRaceProgress()
    {
        var arcLengths = new float[players.Count];

        for (var i = 0; i < players.Count; ++i) arcLengths[i] = ComputeCarArcLength(i);

        var sortedPos = hasFinished
            ? SortPlayersByTime(players.ToArray())
            : SortPlayersByLengths(players.ToArray(), arcLengths);

        var sb = new StringBuilder();
        sb.Append("Race order");

        for (var i = 0; i < sortedPos.Length && !hasFinished; i++)
        {
            if (_cachedPos[i] != sortedPos[i])
            {
                _cachedPos[i] = sortedPos[i];
                sortedPos[i].car.GetComponent<CarController>().SetPlayerTag(i + 1, sortedPos[i].Name);
                if (sortedPos[i].IsOwner) sortedPos[i].StartPos = i;
            }

            if (sortedPos[i].IsOwner) UIManager.Instance.gamePosition.text = $"{i + 1}/{players.Count}";

            sb.Append($"\n{sortedPos[i].Name}\\> {i + 1}º:{sortedPos[i].CurrentPos:F2}" +
                      $"|Lap:{sortedPos[i].CurrentLap}" +
                      $"|Checkpoint:{sortedPos[i].checkpointAchieved}" +
                      $"|RB_K:{sortedPos[i].RubberBandCoefficient:F2}" +
                      $"|Race:{sortedPos[i].CurrentRace}");
        }

        UIManager.Instance.matchSummaryController.UpdateMatchSummary(sortedPos);

        _debugRaceOrder = sb.ToString();
    }

    private float ComputeCarArcLength(int id)
    {
        var carPos = players[id].car.transform.position;

        var minArcL = circuitController.ComputeClosestPointArcLength(carPos, out _, out var carProj, out _);

        _debugSpheres[id].transform.position = carProj;
        players[id].projPos = carProj;

        if (players[id].CurrentLap == 0)
            minArcL -= circuitController.CircuitLength;
        else
            minArcL += circuitController.CircuitLength *
                       (players[id].CurrentLap - 1);

        return minArcL;
    }

    private NetworkPlayer[] SortPlayersByLengths(NetworkPlayer[] p, float[] len)
    {
        var playerData = new List<Tuple<NetworkPlayer, float, float>>();

        for (var i = 0; i < p.Length; i++)
        {
            var dist = len[i];
            var finishTime = p[i].FinishRawTime;
            if (dist > p[i].lastLapPos + APP_CONFIG.GAME.CHECKPOINT_DISTANCE * 1.05f && !p[i].checkpointAchieved) dist = p[i].lastLapPos;
            playerData.Add(new Tuple<NetworkPlayer, float, float>(p[i], dist, finishTime));
        }

        var sortedPlayerData = playerData.Any(p => p.Item1.FinishRawTime != 0f) ?
            playerData.OrderBy(t => t.Item3)
                .ThenByDescending(t => t.Item2).ToList() :
            playerData.OrderByDescending(t => t.Item2).ToList();
        
        var sortPlayers = sortedPlayerData.Select(t => t.Item1).ToArray();
        var sortPos = sortedPlayerData.Select(t => t.Item2).ToArray();

        for (var i = 0; i < sortPlayers.Length; i++)
            if (sortPlayers[i].IsOwner)
                sortPlayers[i].CurrentPos = sortPos[i];

        if (sortPlayers.Length > 1)
        {
            var range = sortPos[1] - sortPos[0];
            var sigmoid = Sigmoid(range);
            sortPlayers[0].RubberBandCoefficient = sigmoid;
            for (var i = sortPlayers.Length - 1; i > 0; i--)
            {
                range = sortPos[0] - sortPos[i];
                sigmoid = Sigmoid(range);
                sortPlayers[i].RubberBandCoefficient = sigmoid;
            }
        }

        return sortPlayers;
    }

    private float Sigmoid(float value, float min = 0.8f, float max = 1.2f)
    {
        var s = value switch
        {
            < 50f => 300f,
            < 200f => 290f,
            _ => 270f
        };
        var k = Mathf.Exp(value / s);
        return Mathf.Clamp(k / (1f + k) + 0.5f, min, max);
    }

    private NetworkPlayer[] SortPlayersByTime(NetworkPlayer[] playerList)
    {
        var playerData =
            (from player in playerList let time = player.FinishRawTime select new Tuple<NetworkPlayer, float>(player, time)).ToList();

        var sortedPlayersData = playerData.OrderBy(t => t.Item2).ToList();
        var sortedPlayers = sortedPlayersData.Select(t => t.Item1).ToArray();

        return sortedPlayers;
    }

    #endregion

    #region Race management

    public bool AllPlayersRacing()
    {
        var currentPlayers = waitList.Where(p => p.IsRacing).ToList();
        return currentPlayers.Count > 0;
    }
    
    public void AddToWaitList(NetworkPlayer networkPlayer)
    {
        waitList.Add(networkPlayer);
        UIManager.Instance.debugController.SetRoomPlayers(waitList.Count, players.Count);
    }

    public void RemoveFromWaitList(NetworkPlayer networkPlayer)
    {
        waitList.Remove(networkPlayer);
        if (players.Exists((p) => p.Equals(networkPlayer))) RemoveFromPlayers(networkPlayer);
        UIManager.Instance.debugController.SetRoomPlayers(waitList.Count, players.Count);
    }

    public void AddToPlayers(NetworkPlayer networkPlayer)
    {
        players.Add(networkPlayer);
        UIManager.Instance.debugController.SetRoomPlayers(waitList.Count, players.Count);
    }

    public void RemoveFromPlayers(NetworkPlayer networkPlayer)
    {
        players.Remove(networkPlayer);
        UIManager.Instance.debugController.SetRoomPlayers(waitList.Count, players.Count);
        OnPlayerLeft?.Invoke(networkPlayer);
    }

    public void UpdateRaceState(NetworkPlayer player)
    {
        var currentPlayers = waitList.Where(p => p.IsReady && !p.Equals(player)).ToList();
        var raceStarted = currentPlayers.TrueForAll(p => p.IsReady);
        
        if (currentPlayers.Count > 0 && raceStarted)
        {
            currentPlayers.ForEach(AddToPlayers);
            player.StartPos = currentPlayers.Count + (waitList.Count - (currentPlayers.Count + 1));
            player.CurrentRace = currentPlayers[0].CurrentRace + 1;
            GameManager.Instance.currentRace = player.CurrentRace;
        }
    }
    
    public IEnumerator LeaveRace()
    {
        waitList.Clear();
        players.Clear();

        circuitController.SetNextCircuit(true);

        EventManager.Instance.RaiseScreenChange(AppScreen.Menu);
        GameManager.Instance.State = GameState.Idle;

        UIManager.Instance.chatController.chatBody.text = "";
        UIManager.Instance.debugController.debugConsole.text = "";
        UIManager.Instance.debugController.statFpsCounter.text = "";
        UIManager.Instance.debugController.statRoomProperties.text = "";
        UIManager.Instance.debugController.statRtt.text = "";

        UIManager.Instance.SetNotificationCanvas(true, "ROOM'S BEEN CLOSED");
        for (var i = 3; i > 0; i--) yield return new WaitForSeconds(1);
        UIManager.Instance.SetNotificationCanvas(false);
    }

    #endregion

    #region Server Rewind

    [Header("Lag Compensation Settings")]
    [SerializeField] private bool ENABLE_DEBUG_LOG = true; 
    int bufferSize = 8192;
    float TICK_RATE = 50f;
    float rewindCooldownTime = 0f;
    private float maxLagCompensationSeconds = 2f;
    [SerializeField] private int rewindTickSearchRadius = 8;
    private CircularBuffer<StatePayload[]> stateBuffer;
    public CircularBuffer<InputPayload[]> inputBuffer;
    int serverTick = 1;
    float rewindCooldownCounter = 0;
    //Dictionary<int, int> rewindTickQueue = new();
    List<int> rewindTickQueue = new();
    private readonly HashSet<long> processedShootInputs = new();
    private readonly Dictionary<long, ShootContext> shootContexts = new();

    private int MaxRewindTickAge => Mathf.Min(bufferSize - 1, Mathf.CeilToInt(TICK_RATE * maxLagCompensationSeconds));

    private readonly struct PendingShootHit
    {
        public readonly CarController Shooter;
        public readonly CarController Target;

        public PendingShootHit(CarController shooter, CarController target)
        {
            Shooter = shooter;
            Target = target;
        }
    }

    public NetworkTimer networkTimer { get; private set; }

    bool isRewinding = false;
    public bool IsRewinding => isRewinding;

    public void PendInput(int id, InputPayload input)
    {
        if (id < 0 || id >= 4) return;
        int tick = input.tick;

        InputPayload[] inputTemp = inputBuffer.Get(tick);
        
        if (inputTemp == null)
        {
            inputTemp = new InputPayload[4];
            inputBuffer.Add(inputTemp, tick);
        }

        input.isShoot |= inputTemp[id].tick == tick && inputTemp[id].isShoot;
        inputTemp[id] = input;

        CarController car = null;
        foreach (var p in players) {
            if (p.ID == id) {
                car = p.GetCarController;
                break;
            }
        }

        if (car != null && input.isCollide)
        {
            int carTick = networkTimer.CurrentTick;
            if (carTick - tick <= MaxRewindTickAge)
            {
                if (!rewindTickQueue.Contains(tick)) rewindTickQueue.Add(tick);
            }
            if (rewindTickQueue.Count > bufferSize)
            {
                rewindTickQueue.RemoveAt(0);
            }
        }
    }

    public void UpdateAttackInput(int id, int tick, ShootContext shootContext = default)
    {
        if (id < 0 || id >= 4) return;

        InputPayload[] inputTemp = inputBuffer.Get(tick);
        
        if (inputTemp == null)
        {
            inputTemp = new InputPayload[4];
            inputTemp[id] = new InputPayload { tick = tick, isShoot = true };
            inputBuffer.Add(inputTemp, tick);
        }
        else
        {
            InputPayload input = inputTemp[id];
            input.tick = tick;
            input.isShoot = true;
            inputTemp[id] = input;
        }

        //inputTemp[id] = input;
        shootContexts[GetShootKey(id, tick)] = shootContext;

        CarController car = null;
        foreach (var p in players) {
            if (p.ID == id) {
                car = p.GetCarController;
                break;
            }
        }

        if (car != null)
        {
            int carTick = networkTimer.CurrentTick;
            bool queuedForRewind = false;
            if (carTick - tick <= MaxRewindTickAge)
            {
                if (!rewindTickQueue.Contains(tick))
                {
                    rewindTickQueue.Add(tick);
                    queuedForRewind = true;
                }
            }
            if (rewindTickQueue.Count > bufferSize)
            {
                rewindTickQueue.RemoveAt(0);
            }

            if (ENABLE_DEBUG_LOG)
            {
                Debug.Log($"<color=cyan>[ShootDebug]</color> Queue shot shooter={id} shotTick={tick} serverTick={carTick} age={carTick - tick} maxAge={MaxRewindTickAge} queuedForRewind={queuedForRewind} queueCount={rewindTickQueue.Count} hasContext={shootContext.hasTarget}");
            }
        }
    }

    public void PendState(int id, StatePayload state)
    {
        if (id < 0 || id >= 4) return;
        int tick = state.tick;

        StatePayload[] stateTemp = stateBuffer.Get(tick);
        
        if (stateTemp == null)
        {
            stateTemp = new StatePayload[4];
            stateBuffer.Add(stateTemp, tick);
        }

        stateTemp[id] = state;

        // if (Mathf.Abs(serverTick - tick) <= 50)
        // {
        //     rewindTickQueue.Add(tick);

        //     if (rewindTickQueue.Count > bufferSize)
        //     {
        //         rewindTickQueue.RemoveAt(0);
        //     }
        // }
    }

    // public void SignUpRocket(ulong id, RocketController rocketController)
    // {
    //     if (!rocketDict.ContainsKey(id))
    //     {
    //         rocketDict.Add(id, rocketController);
    //     }
    // }
    
    // public void PendRocketState(ulong id, RocketStatePayload rocketState)
    // {
    //     int tick = rocketState.tick;

    //     Dictionary<ulong, RocketStatePayload> rocketTemp = rocketBuffer.Get(tick);
        
    //     if (rocketTemp == null)
    //     {
    //         rocketTemp = new Dictionary<ulong, RocketStatePayload>();
    //         rocketTemp.Add(id, rocketState);
    //         rocketBuffer.Add(rocketTemp, tick);
    //     }

    //     rocketTemp[id] = rocketState;
    // }

    void FixedUpdate()
    {
        if (!networkTimer.ShouldTick())
            return;

        bool canRewind = false;

        List<CarController> cars = new();

        foreach (var player in players)
        {
            CarController car = player.GetCarController;
            if (car != null)
            {
                (bool hasInput, int lastTick) = car.ProcessFixedCarController();
                if (hasInput)
                {
                    canRewind = true;
                }
                cars.Add(car);
            }
        }

        // foreach (var rocket in rocketDict.Values)
        // {
        //     rocket.ProcessFixedRocketController();
        // }

        // Server side
        if ((NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) && !canRewind) return;

        serverTick = networkTimer.CurrentTick;

        for (int i = 0; i < cars.Count; i++)
        {
            var car = cars[i];
            if (car != null) 
            {   
                StatePayload statePayload = car.GetStateOfCar();
                
                car.ServerSendState(statePayload);

                PendState(car.ID, statePayload);
            }
        }

        if (UseLagCompensation && rewindCooldownCounter >= rewindCooldownTime)
        {
            int rewindTick = -1;
            string triggerReason = "";
            rewindCooldownCounter = 0;
            for (int i = 0; i < rewindTickQueue.Count; i++)
            {
                int tick = rewindTickQueue[i];
                int tickAge = serverTick - tick;

                if (tickAge < 0)
                {
                    continue;
                }

                if (tickAge > MaxRewindTickAge)
                {
                    rewindTickQueue.RemoveAt(i);
                    i--;
                    continue;
                }

                int resolvedTick = ResolveRewindTick(tick);
                if (resolvedTick < 0)
                {
                    continue;
                }

                if (rewindTick == -1)
                {
                    if (resolvedTick != tick)
                    {
                        MoveShootInputsToTick(tick, resolvedTick);
                    }

                    rewindTick = resolvedTick;
                    triggerReason = resolvedTick == tick ? "Collision" : $"Collision [NearestTick {tick}->{resolvedTick}]";
                    rewindTickQueue.RemoveAt(i);
                    break;
                }
            }

            if (rewindTick >= 0)
            {
                // if (serverTick - rewindTick > 30)
                // {
                //     rewindTick = serverTick - 30;
                //     triggerReason += " [Capped at 30 Ticks]";
                // }

                if (ENABLE_DEBUG_LOG) 
                    Debug.Log($"<color=yellow>[Lag Compensation]</color> Kích hoạt Rewind! Lý do: {triggerReason}. Quay về Tick: {rewindTick} (Tick hiện tại: {serverTick})");

                RewindServerSingleScene(rewindTick);
            } 
        }
    }

    private int ResolveRewindTick(int tick)
    {
        if (CanRewindTick(tick)) return tick;

        int maxOffset = Mathf.Max(0, rewindTickSearchRadius);
        for (int offset = 1; offset <= maxOffset; offset++)
        {
            int earlierTick = tick - offset;
            if (earlierTick >= 0 && HasExactRewindScene(earlierTick))
            {
                return earlierTick;
            }

            int laterTick = tick + offset;
            if (laterTick <= serverTick && HasExactRewindScene(laterTick))
            {
                return laterTick;
            }
        }

        return -1;
    }

    private void MoveShootInputsToTick(int sourceTick, int targetTick)
    {
        InputPayload[] sourceInputs = inputBuffer.Get(sourceTick);
        if (sourceInputs == null) return;

        InputPayload[] targetInputs = inputBuffer.Get(targetTick);
        if (targetInputs == null)
        {
            targetInputs = new InputPayload[4];
            inputBuffer.Add(targetInputs, targetTick);
        }

        for (int id = 0; id < sourceInputs.Length; id++)
        {
            InputPayload sourceInput = sourceInputs[id];
            if (sourceInput.tick != sourceTick || !sourceInput.isShoot) continue;

            sourceInput.tick = targetTick;
            targetInputs[id] = sourceInput;

            long sourceKey = GetShootKey(id, sourceTick);
            if (shootContexts.TryGetValue(sourceKey, out ShootContext context))
            {
                shootContexts.Remove(sourceKey);
                shootContexts[GetShootKey(id, targetTick)] = context;
            }

            if (ENABLE_DEBUG_LOG)
            {
                Debug.Log($"<color=cyan>[ShootDebug]</color> Move shot input shooter={id} sourceTick={sourceTick} targetTick={targetTick}");
            }
        }
    }

    private bool CanRewindTick(int tick)
    {
        InputPayload[] inputsAtTick = inputBuffer.Get(tick);
        if (inputsAtTick == null) return false;

        for (int id = 0; id < inputsAtTick.Length; id++)
        {
            InputPayload input = inputsAtTick[id];
            if (input.tick != tick || !input.isShoot) continue;

            return HasExactRewindScene(tick);
        }

        return HasExactRewindScene(tick);
    }

    private bool HasExactRewindScene(int tick)
    {
        StatePayload[] statesAtTick = stateBuffer.Get(tick);
        if (statesAtTick == null) return false;

        bool hasCar = false;
        foreach (NetworkPlayer player in players)
        {
            if (player == null) continue;
            CarController car = player.GetCarController;
            int id = player.ID;
            if (car == null || id < 0 || id >= statesAtTick.Length) continue;

            hasCar = true;
            if (statesAtTick[id].tick != tick)
            {
                return false;
            }
        }

        return hasCar;
    }

    private void RewindServerSingleScene(int tick)
    {
        InputPayload[] startInputs = inputBuffer.Get(tick);
        StatePayload[] firstState = stateBuffer.Get(tick);

        if (isRewinding || startInputs == null || firstState == null)
        {
            return;
        }

        isRewinding = true;
        List<PendingShootHit> pendingShootHits = new();

        Physics.simulationMode = SimulationMode.Script;

        try
        {
            CarController[] cars = new CarController[4];
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == null) continue;
                CarController carReal = players[i].GetCarController;
                int id = players[i].ID;

                if (carReal != null && id >= 0 && id < cars.Length && firstState[id].tick == tick)
                {
                    carReal.ApplyState(firstState[id]);
                    cars[id] = carReal;
                }
            }

            Physics.SyncTransforms();

            int tickToProcess = tick + 1;
            int lastTick = serverTick;
            int simulatedFrames = 0;

            InputPayload[] lastInputs = new InputPayload[4];
            for (int id = 0; id < cars.Length; id++)
            {
                int searchTick = tick;
                while (searchTick >= 0)
                {
                    InputPayload[] inputTemp = inputBuffer.Get(searchTick);
                    if (inputTemp != null && inputTemp[id].tick == searchTick)
                    {
                        lastInputs[id] = inputTemp[id];
                        break;
                    }
                    searchTick--;
                }
                if (lastInputs[id].tick == 0)
                {
                    lastInputs[id] = new InputPayload { tick = tick };
                }
            }

            ProcessShootInputsAtTick(cars, tick, pendingShootHits);

            Debug.Log($"<color=green>[Lag Compensation]</color> Start rewinding from tick {tick} to {lastTick}");

            while (tickToProcess <= lastTick)
            {
                for (int i = 0; i < cars.Length; i++)
                {
                    CarController carReal = cars[i];
                    int id = i;

                    if (carReal != null)
                    {
                        InputPayload[] inputsAtTick = inputBuffer.Get(tickToProcess);
                        if (inputsAtTick != null && inputsAtTick[id].tick == tickToProcess)
                        {
                            lastInputs[id] = inputsAtTick[id];
                        }
                        else
                        {
                            lastInputs[id].tick = tickToProcess;
                        }

                        carReal.ApplyInputForPhysics(lastInputs[id]);
                    }
                }

                Physics.Simulate(1f / TICK_RATE);

                ProcessShootInputsAtTick(cars, tickToProcess, pendingShootHits);

                for (int i = 0; i < cars.Length; i++)
                {
                    if (cars[i] == null) continue;
                    CarController carReal = cars[i];
                    int id = i;
                    StatePayload[] statesAtTick = stateBuffer.Get(tickToProcess);
                    if (carReal != null && statesAtTick != null)
                        statesAtTick[id] = carReal.GetStateOfCar(tickToProcess);
                }

                tickToProcess++;
                simulatedFrames++;
            }

            if (ENABLE_DEBUG_LOG)
                Debug.Log($"<color=green>[Lag Compensation]</color> Rewinded {simulatedFrames} frames");

            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i] == null) continue;
                CarController carReal = cars[i];
                if (carReal != null)
                {
                    carReal.ServerSendState(carReal.GetStateOfCar(), true);
                }
            }

            foreach (PendingShootHit hit in pendingShootHits)
            {
                hit.Shooter.ApplyServerShootHit(hit.Target);
            }
        }
        finally
        {
            Physics.simulationMode = SimulationMode.FixedUpdate;
            isRewinding = false;
        }
    }

    private void ProcessShootInputsAtTick(CarController[] cars, int tick, List<PendingShootHit> pendingShootHits)
    {
        InputPayload[] inputsAtTick = inputBuffer.Get(tick);
        if (inputsAtTick == null) return;

        for (int id = 0; id < cars.Length; id++)
        {
            CarController shooter = cars[id];
            if (shooter == null) continue;

            InputPayload input = inputsAtTick[id];
            if (input.tick != tick || !input.isShoot) continue;
            if (!TryMarkShootProcessed(id, tick)) continue;

            bool hasContext = shootContexts.TryGetValue(GetShootKey(id, tick), out ShootContext shootContext);
            CarController target = hasContext
                ? shooter.GetServerShootTarget(shootContext)
                : shooter.GetServerShootTarget();
            if (ENABLE_DEBUG_LOG)
            {
                string targetText = target != null ? target.ID.ToString() : "none";
                Debug.Log($"<color=cyan>[ShootDebug]</color> Rewind validate shooter={id} shotTick={tick} serverTick={serverTick} target={targetText} hasContext={hasContext}");
            }

            if (target != null)
            {
                pendingShootHits.Add(new PendingShootHit(shooter, target));
            }

            shootContexts.Remove(GetShootKey(id, tick));
        }
    }

    private bool TryMarkShootProcessed(int id, int tick)
    {
        return processedShootInputs.Add(GetShootKey(id, tick));
    }

    private long GetShootKey(int id, int tick)
    {
        return ((long)tick << 32) | (uint)id;
    }

    public void ResetAll()
    {
        stateBuffer?.Clear();
        inputBuffer?.Clear();
        //rewindTickQueue.Clear();
        rewindTickQueue.Clear();
        processedShootInputs.Clear();
        shootContexts.Clear();
        isRewinding = false;
    }


    #endregion
}
