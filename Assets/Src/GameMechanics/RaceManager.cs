using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CustomTypes;
using Unity.Netcode;
using UnityEngine;

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

        inputBuffer = new CircularBuffer<InputPayload[]>(BufferSize);
        stateBuffer = new CircularBuffer<StatePayload[]>(BufferSize);

        networkTimer = new NetworkTimer(TickRate);
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
    private const int PlayerSlotCount = 4;
    private const int BufferSize = 8192;
    private const float TickRate = 50f;
    private const float MaxLagCompensationSeconds = 2f;
    [SerializeField] private float rewindCooldownTime = 0f;
    [SerializeField] private int rewindTickSearchRadius = 8;
    private CircularBuffer<StatePayload[]> stateBuffer;
    public CircularBuffer<InputPayload[]> inputBuffer;
    int serverTick = 1;
    float rewindCooldownCounter = 0f;
    List<int> rewindTickQueue = new();
    private readonly HashSet<(int Tick, int ShooterId)> processedShootInputs = new();
    private readonly Dictionary<(int Tick, int ShooterId), ShootContext> shootContexts = new();

    private int MaxRewindTickAge => Mathf.Min(BufferSize - 1, Mathf.CeilToInt(TickRate * MaxLagCompensationSeconds));

    public NetworkTimer networkTimer { get; private set; }

    bool isRewinding = false;
    public bool IsRewinding => isRewinding;

    public void PendInput(int id, InputPayload input)
    {
        if (!IsValidPlayerId(id)) return;
        int tick = input.tick;

        InputPayload[] inputTemp = GetOrCreateInputs(tick);
        input.isShoot |= inputTemp[id].tick == tick && inputTemp[id].isShoot;
        inputTemp[id] = input;

        if (input.isCollide) QueueRewindTick(tick, networkTimer.CurrentTick);
    }

    public void UpdateAttackInput(int id, int tick, ShootContext shootContext = default)
    {
        if (!IsValidPlayerId(id)) return;

        InputPayload[] inputTemp = GetOrCreateInputs(tick);
        InputPayload input = inputTemp[id];
        input.tick = tick;
        input.isShoot = true;
        inputTemp[id] = input;

        shootContexts[(id, tick)] = shootContext;

        int carTick = networkTimer.CurrentTick;
        bool queuedForRewind = QueueRewindTick(tick, carTick);
        if (ENABLE_DEBUG_LOG)
        {
            Debug.Log($"<color=cyan>[ShootDebug]</color> Queue shot shooter={id} shotTick={tick} serverTick={carTick} age={carTick - tick} maxAge={MaxRewindTickAge} queuedForRewind={queuedForRewind} queueCount={rewindTickQueue.Count} hasContext={shootContext.hasTarget}");
        }
    }

    private bool IsValidPlayerId(int id) => id >= 0 && id < PlayerSlotCount;

    private InputPayload[] GetOrCreateInputs(int tick)
    {
        InputPayload[] inputs = inputBuffer.Get(tick);
        if (inputs == null) inputBuffer.Add(inputs = new InputPayload[PlayerSlotCount], tick);
        return inputs;
    }

    private StatePayload[] GetOrCreateStates(int tick)
    {
        StatePayload[] states = stateBuffer.Get(tick);
        if (states == null) stateBuffer.Add(states = new StatePayload[PlayerSlotCount], tick);
        return states;
    }

    private bool QueueRewindTick(int tick, int currentTick)
    {
        bool queued = currentTick - tick <= MaxRewindTickAge && !rewindTickQueue.Contains(tick);
        if (queued) rewindTickQueue.Add(tick);
        if (rewindTickQueue.Count > BufferSize) rewindTickQueue.RemoveAt(0);
        return queued;
    }

    public void PendState(int id, StatePayload state)
    {
        if (!IsValidPlayerId(id)) return;
        int tick = state.tick;

        GetOrCreateStates(tick)[id] = state;
    }

    void FixedUpdate()
    {
        if (!networkTimer.ShouldTick())
            return;

        List<CarController> cars = new();

        foreach (var player in players)
        {
            CarController car = player.GetCarController;
            if (car == null) continue;

            car.ProcessFixedCarController();
            cars.Add(car);
        }


        // Server side
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        serverTick = networkTimer.CurrentTick;

        foreach (CarController car in cars)
        {
            StatePayload statePayload = car.GetStateOfCar();
            car.ServerSendState(statePayload);
            PendState(car.ID, statePayload);
        }

        if (UseLagCompensation && rewindCooldownCounter >= rewindCooldownTime && TryDequeueRewindTick(out int rewindTick))
        {
            rewindCooldownCounter = 0f;

            if (ENABLE_DEBUG_LOG) 
                Debug.Log($"<color=yellow>[Lag Compensation]</color> Rewind tick {rewindTick} (current tick: {serverTick})");

            RewindServerSingleScene(rewindTick);
        }
    }

    private bool TryDequeueRewindTick(out int rewindTick)
    {
        rewindTick = -1;

        for (int i = 0; i < rewindTickQueue.Count; i++)
        {
            int tick = rewindTickQueue[i];
            int tickAge = serverTick - tick;
            if (tickAge < 0) continue;
            if (tickAge > MaxRewindTickAge)
            {
                rewindTickQueue.RemoveAt(i--);
                continue;
            }

            int resolvedTick = ResolveRewindTick(tick);
            if (resolvedTick < 0) continue;

            if (resolvedTick != tick) MoveShootInputsToTick(tick, resolvedTick);
            rewindTick = resolvedTick;
            rewindTickQueue.RemoveAt(i);
            return true;
        }

        return false;
    }

    // check tick co state khong, khong thi tim tick gan nhat
    private int ResolveRewindTick(int tick)
    {
        if (HasExactRewindScene(tick)) return tick;

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

    // dua input shoot tu tick cu sang tick moi
    private void MoveShootInputsToTick(int sourceTick, int targetTick)
    {
        InputPayload[] sourceInputs = inputBuffer.Get(sourceTick);
        if (sourceInputs == null) return;

        InputPayload[] targetInputs = GetOrCreateInputs(targetTick);

        for (int id = 0; id < sourceInputs.Length; id++)
        {
            InputPayload sourceInput = sourceInputs[id];
            if (sourceInput.tick != sourceTick || !sourceInput.isShoot) continue;

            sourceInput.tick = targetTick;
            targetInputs[id] = sourceInput;

            var sourceKey = (id, sourceTick);
            if (shootContexts.TryGetValue(sourceKey, out ShootContext context))
            {
                shootContexts.Remove(sourceKey);
                shootContexts[(id, targetTick)] = context;
            }

            if (ENABLE_DEBUG_LOG)
            {
                Debug.Log($"<color=cyan>[ShootDebug]</color> Move shot input shooter={id} sourceTick={sourceTick} targetTick={targetTick}");
            }
        }
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
        StatePayload[] firstState = stateBuffer.Get(tick);

        if (isRewinding || inputBuffer.Get(tick) == null || firstState == null)
        {
            return;
        }

        isRewinding = true;
        List<(CarController Shooter, CarController Target)> pendingShootHits = new();

        Physics.simulationMode = SimulationMode.Script;

        try
        {
            CarController[] cars = new CarController[PlayerSlotCount];
            for (int i = 0; i < players.Count; i++)
            {
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

            InputPayload[] lastInputs = new InputPayload[PlayerSlotCount];
            for (int id = 0; id < cars.Length; id++)
            {
                lastInputs[id] = GetLastKnownInput(id, tick);
            }

            ProcessShootInputsAtTick(cars, tick, pendingShootHits);

            if (ENABLE_DEBUG_LOG)
                Debug.Log($"<color=green>[Lag Compensation]</color> Start rewinding from tick {tick} to {lastTick}");

            while (tickToProcess <= lastTick)
            {
                for (int i = 0; i < cars.Length; i++)
                {
                    CarController carReal = cars[i];
                    if (carReal == null) continue;

                    InputPayload[] inputsAtTick = inputBuffer.Get(tickToProcess);
                    if (inputsAtTick != null && inputsAtTick[i].tick == tickToProcess)
                    {
                        lastInputs[i] = inputsAtTick[i];
                    }
                    else
                    {
                        lastInputs[i].tick = tickToProcess;
                    }

                    carReal.ApplyInputForPhysics(lastInputs[i]);
                }

                Physics.Simulate(1f / TickRate);

                ProcessShootInputsAtTick(cars, tickToProcess, pendingShootHits);

                for (int i = 0; i < cars.Length; i++)
                {
                    if (cars[i] == null) continue;
                    StatePayload[] statesAtTick = stateBuffer.Get(tickToProcess);
                    if (statesAtTick != null) statesAtTick[i] = cars[i].GetStateOfCar(tickToProcess);
                }

                tickToProcess++;
                simulatedFrames++;
            }

            if (ENABLE_DEBUG_LOG)
                Debug.Log($"<color=green>[Lag Compensation]</color> Rewinded {simulatedFrames} frames");

            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i] == null) continue;
                cars[i].ServerSendState(cars[i].GetStateOfCar(), true);
            }

            foreach (var hit in pendingShootHits)
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

    private void ProcessShootInputsAtTick(CarController[] cars, int tick, List<(CarController Shooter, CarController Target)> pendingShootHits)
    {
        InputPayload[] inputsAtTick = inputBuffer.Get(tick);
        if (inputsAtTick == null) return;

        for (int id = 0; id < cars.Length; id++)
        {
            CarController shooter = cars[id];
            if (shooter == null) continue;

            InputPayload input = inputsAtTick[id];
            if (input.tick != tick || !input.isShoot) continue;

            var shootKey = (id, tick);
            if (!processedShootInputs.Add(shootKey)) continue;

            bool hasContext = shootContexts.TryGetValue(shootKey, out ShootContext shootContext);
            CarController target = shooter.GetServerShootTarget(shootContext);
            if (ENABLE_DEBUG_LOG)
            {
                string targetText = target != null ? target.ID.ToString() : "none";
                Debug.Log($"<color=cyan>[ShootDebug]</color> Rewind validate shooter={id} shotTick={tick} serverTick={serverTick} target={targetText} hasContext={hasContext}");
            }

            if (target != null)
            {
                pendingShootHits.Add((shooter, target));
            }

            shootContexts.Remove(shootKey);
        }
    }

    private InputPayload GetLastKnownInput(int id, int tick)
    {
        for (int searchTick = tick; searchTick >= 0; searchTick--)
        {
            InputPayload[] inputTemp = inputBuffer.Get(searchTick);
            if (inputTemp != null && inputTemp[id].tick == searchTick)
            {
                return inputTemp[id];
            }
        }

        return new InputPayload { tick = tick };
    }

    public void ResetAll()
    {
        stateBuffer?.Clear();
        inputBuffer?.Clear();
        rewindTickQueue.Clear();
        processedShootInputs.Clear();
        shootContexts.Clear();
        isRewinding = false;
    }


    #endregion
}
