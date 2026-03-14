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
    [SerializeField] [HideInInspector] public List<NetworkPlayer> players = new();
    [SerializeField] [HideInInspector] public CircuitController circuitController;

    [SerializeField] [HideInInspector] private NetworkPlayer[] _cachedPos = new NetworkPlayer[APP_CONFIG.GAME.MAX_PLAYERS_PER_ROOM];
    [SerializeField] [HideInInspector] private GameObject[] _debugSpheres;
    [SerializeField] [HideInInspector] private string _debugRaceOrder;

    private bool IsRacing => UIManager.Instance.State is AppScreen.Game or AppScreen.EndGame;
    
    public static RaceManager Instance { get; private set; }

    #endregion

    #region Delegates and Events

    public delegate void PlayerLeft(NetworkPlayer networkPlayer);
    public event PlayerLeft OnPlayerLeft;

    public void OnPlayerHasFinished(bool oldValue, bool newValue)
    {
        if (newValue)
        {
            var finished = true;
            foreach (var player in players) finished &= player.HasFinished;

            if (finished)
            {
                hasFinished = true;
                GameManager.Instance.State = GameState.Finished;
                StartCoroutine(RaceEndCountdown());
            }
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
        if (newState == GameState.Started) foreach (var player in waitList) AddToPlayers(player);
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
            playerData.OrderByDescending(t => t.Item3)
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
    [SerializeField] int bufferSize = 4000;
    float rewindCooldownTime = 0.5f; // Rút ngắn cooldown để phản ứng va chạm nhanh hơn
    private SortedDictionary<int, StatePayload[]> stateBufferDict = new();
    private SortedDictionary<int, InputPayload[]> inputBufferDict = new();
    int serverTick = 1;
    float rewindCooldownCounter = 0;
    List<int> rewindTickQueue = new();
    List<int> collideTickQueue = new();

    bool isRewinding = false;

    public void PendInput(int id, InputPayload input)
    {
        if (id < 0 || id >= 4) return;
        int tick = input.tick;

        if (inputBufferDict.Count >= bufferSize)
        {
            if (tick < inputBufferDict.Keys.First()) return;
            else inputBufferDict.Remove(inputBufferDict.Keys.First());
        }

        if (!inputBufferDict.ContainsKey(tick))
            inputBufferDict.Add(tick, new InputPayload[4]);

        inputBufferDict[tick][id] = input;

        if (Mathf.Abs(serverTick - tick) <= 100 && input.isCollide)
        {
            collideTickQueue.Add(tick);
        }
            
    }

    public void PendState(int id, StatePayload state)
    {
        if (id < 0 || id >= 4) return;
        int tick = state.tick;

        if (stateBufferDict.Count >= bufferSize)
        {
            if (tick < stateBufferDict.Keys.First()) return;
            else stateBufferDict.Remove(stateBufferDict.Keys.First());
        }

        if (!stateBufferDict.ContainsKey(tick))
            stateBufferDict.Add(tick, new StatePayload[4]);

        stateBufferDict[tick][id] = state;

        bool canRewind = stateBufferDict.Count > 0;

        if (Mathf.Abs(serverTick - tick) <= 50 && canRewind)
        {
            rewindTickQueue.Add(tick);

            if (rewindTickQueue.Count > bufferSize)
            {
                rewindTickQueue.RemoveAt(0);
            }
        }
    }

    void FixedUpdate()
    {
        bool canRewind = false;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            serverTick = NetworkManager.Singleton.ServerTime.Tick;
        }

        List<CarController> cars = new();

        // 1. TÍNH TOÁN CỘNG DỒN CHO FRAME HIỆN TẠI (ARCADE MOVEMENT)
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

        // Server side
        if ((NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) && !canRewind) return;

        // 2. KÍCH HOẠT REWIND & FAST-FORWARD (Nếu có va chạm)
        int rewindTick = -1;
        string triggerReason = "";

        while (collideTickQueue.Count > 0)
        {
            int tick = collideTickQueue[0];
            collideTickQueue.RemoveAt(0);

            // Bắt được Tick cũ nhất có va chạm
            if (serverTick - tick <= 100 && serverTick - tick >= 0)
            {
                if (rewindTick == -1 || tick < rewindTick) {
                    rewindTick = tick;
                    triggerReason = "Va chạm (Collision)";
                }
            }
        }

        if (rewindTick >= 0)
        {
            // FIX: THÊM HARD CAP ĐỂ BẢO VỆ SERVER (Chỉ tua lại tối đa 30 Tick ~ 0.5 giây)
            if (serverTick - rewindTick > 30)
            {
                rewindTick = serverTick - 30;
                triggerReason += " [Capped at 30 Ticks]";
            }

            if (ENABLE_DEBUG_LOG) 
                Debug.Log($"<color=yellow>[Lag Compensation]</color> Kích hoạt Rewind! Lý do: {triggerReason}. Quay về Tick: {rewindTick} (Tick hiện tại: {serverTick})");

            RewindServerSingleScene(rewindTick);
            // Sau khi Rewind xong, vị trí của xe đã được cập nhật ĐÚNG chuẩn Physics cho sát với serverTick.
        } 

        // 3. LƯU STATE VÀ GỬI VỀ CHO CLIENT
        for (int i = 0; i < cars.Count; i++)
        {
            var car = cars[i];
            if (car != null) 
            {   
                // Lấy State chuẩn nhất ở cuối frame
                StatePayload statePayload = car.GetStateOfCar();
                
                // Gửi State cập nhật về Client
                car.ServerSendState(statePayload);

                // Lưu vào buffer để nếu tương lai có Rewind thì có dữ liệu khôi phục
                PendState(car.ID, statePayload);
            }
        }
    }
    private void RewindServerSingleScene(int tick)
    {
        if (!isRewinding && inputBufferDict.ContainsKey(tick) && stateBufferDict.ContainsKey(tick))
        {
            isRewinding = true;

            StatePayload[] firstState = stateBufferDict[tick];

            Physics.simulationMode = SimulationMode.Script;

            try
            {
                for (int i = 0; i < players.Count; i++)
                {
                    CarController carReal = players[i].GetCarController;
                    int id = players[i].ID;

                    if (carReal != null && firstState[id].tick != 0)
                    {
                        carReal.ApplyState(firstState[id]);
                        
                        // var rb = carReal.GetComponent<Rigidbody>();
                        
                        // rb.transform.position = firstState[id].position;
                        // rb.transform.rotation = firstState[id].rotation;
                        // rb.position = firstState[id].position;
                        // rb.rotation = firstState[id].rotation;
                        // rb.velocity = firstState[id].rotation * Vector3.forward * firstState[id].speed;
                    }
                }

                Physics.SyncTransforms();

                int tickToProcess = tick + 1;
                int lastTick = serverTick;
                int simulatedFrames = 0;

                if (ENABLE_DEBUG_LOG) 
                    Debug.Log($"<color=cyan>[Lag Compensation]</color> Bắt đầu Fast-Forward (Resimulate) từ Tick {tickToProcess} đến {lastTick}...");

                InputPayload[] lastInputs = new InputPayload[4];
                for (int id = 0; id < 4; id++) 
                {
                    if (inputBufferDict.ContainsKey(tick) && inputBufferDict[tick][id].tick != 0)
                        lastInputs[id] = inputBufferDict[tick][id];
                    else
                        lastInputs[id] = new InputPayload { tick = tickToProcess }; 
                }

                while (tickToProcess <= lastTick)
                {
                    for (int i = 0; i < players.Count; i++)
                    {
                        CarController carReal = players[i].GetCarController;
                        int id = players[i].ID;

                        if (carReal != null)
                        {
                            if (inputBufferDict.ContainsKey(tickToProcess) && inputBufferDict[tickToProcess][id].tick != 0)
                            {
                                lastInputs[id] = inputBufferDict[tickToProcess][id];
                            }
                            else
                            {
                                lastInputs[id].tick = tickToProcess;
                            }

                            // FIX: Sử dụng hàm ApplyInputForPhysics để ép vị trí, giúp Physics.Simulate nhận va chạm ngay!
                            carReal.ApplyInputForPhysics(lastInputs[id]);
                        }
                    }

                    Physics.Simulate(Time.fixedDeltaTime); // Tính toán va chạm Xuyên Tường!

                    // ... Giữ nguyên phần Add State vào stateBufferDict bên dưới ...

                    if (!stateBufferDict.ContainsKey(tickToProcess))
                    {
                        stateBufferDict.Add(tickToProcess, new StatePayload[4]);
                    }

                    for (int i = 0; i < players.Count; i++)
                    {
                        CarController carReal = players[i].GetCarController;
                        int id = players[i].ID;
                        if (carReal != null) 
                            stateBufferDict[tickToProcess][id] = carReal.GetStateOfCar();
                    }

                    tickToProcess++;
                    simulatedFrames++;
                }

                if (ENABLE_DEBUG_LOG) 
                    Debug.Log($"<color=green>[Lag Compensation]</color> Rewind hoàn tất! Đã chạy lại {simulatedFrames} frames mượt mà.");

                collideTickQueue.Clear();
                rewindTickQueue.Clear();
            }
            finally
            {
                Physics.simulationMode = SimulationMode.FixedUpdate;
                isRewinding = false;
            }
        }
    }

    #endregion
}