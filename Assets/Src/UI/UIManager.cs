using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using CustomTypes;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class UIManager : MonoBehaviour
{
    #region Variables

    private static AppConfig APP_CONFIG => AppConfig.Singleton;
    
    [Header("Menu")] [SerializeField] public GameObject menuCanvas;
    [SerializeField] public TMP_InputField menuNickname;
    [SerializeField] public TMP_InputField menuRoomCode; 
    [SerializeField] public Button menuJoin;
    [SerializeField] public Button menuHost;

    [Header("Load screen")] [SerializeField]
    public GameObject loadCanvas;
    [SerializeField] public Button loadHide;

    [Header("Room")] [SerializeField] public GameObject roomCanvas;
    [SerializeField] public Image roomCarSkin;
    [SerializeField] public Slider roomColorSlider;
    [SerializeField] public Button roomReady;
    [SerializeField] public Button roomExit;

    [Header("Players Hub")] [SerializeField]
    public GameObject playersHubCanvas;
    [SerializeField] public List<GameObject> playersHubPanels;
    [SerializeField] public List<TMP_Text> playersHubNames;
    
    [Header("Notification")] [SerializeField]
    public GameObject notificationCanvas;
    [SerializeField] public TMP_Text notificationHeader;
    [SerializeField] public TMP_Text notificationTime;
    [SerializeField] public TMP_Text notificationBody;

    [Header("Game")] [SerializeField] public GameObject gameCanvas;
    [SerializeField] public TMP_Text gameTitle;
    [SerializeField] public TMP_Text gameKph;
    [SerializeField] public TMP_Text gamePosition;
    [SerializeField] public TMP_Text gameLaps;
    [SerializeField] public TMP_Text gameOverallTime;
    [SerializeField] public TMP_Text gameLapTime;
    [SerializeField] public TMP_Text carJerk;
    [SerializeField] public TMP_Text highestJerk;
    [SerializeField] public TMP_Text averageExportError;
    [SerializeField] public TMP_Text hitPercentage;
    [SerializeField] public Button gameRespawn;
    [SerializeField] public Toggle gameInterpolation; 
    [SerializeField] public Button gameExit;

    [Header("Networking Features")]
    [SerializeField] public Toggle clientSidePredictionToggle;
    [SerializeField] public Toggle serverReconciliationToggle;
    [SerializeField] public Toggle lagCompensationToggle;

    [Header("Car Dead Reckoning Toggles")]
    [SerializeField] public List<Toggle> carDeadReckoningToggles; // List of toggles for each car
    [SerializeField] public List<Text> carDeadReckoningLabels;

    [Header("Car Dead Reckoning Mode Selection")]
    [SerializeField] public List<TMP_Dropdown> carDRModeDropdowns; // Dropdown for Dead Reckoning Mode (None, Linear, Quadratic)
    [SerializeField] public List<TMP_Dropdown> carCorrectionModeDropdowns; // Dropdown for Correction Mode (SmoothDamp, Lerp)

    [Header("Car Dead Reckoning Settings")]
    [SerializeField] public List<Toggle> carAdaptiveThresholdToggles; // Toggle for Adaptive Threshold per car
    [SerializeField] public List<Toggle> carTimeSyncToggles; // Toggle for Time Sync per car

    [Header("Dead Reckoning Accuracy List")]
    [SerializeField] public List<Text> carAccuracyTexts; // List showing accuracy for each car
    [SerializeField] public List<Text> carJerkTexts; // List showing jerk for each car

    // [Header("Server Reconciliation Metrics")]
    // [SerializeField] public TMP_Text serverReconciliationAccuracy;

    [Header("Lag Compensation Metrics")]
    [SerializeField] public Text clientCollisionCountText;
    [SerializeField] public Text serverCollisionCountText;
    
    [Header("Debug & Simulation")]
    [SerializeField] public Slider pingSlider;       
    [SerializeField] public TMP_Text pingDisplay;    
    [SerializeField] public Slider jitterSlider;     
    [SerializeField] public TMP_Text jitterDisplay;  
    [SerializeField] public Toggle enableSimToggle; 
    [SerializeField] public Toggle botToggle; 

    [Header("Adaptive Threshold Configuration")]
    [SerializeField] public TMP_InputField baseThresholdInput;
    [SerializeField] public TMP_InputField kvCoefficientInput;
    [SerializeField] public TMP_InputField kaCoefficientInput;

    [Header("End Game")] [SerializeField] public GameObject endGameCanvas;
    
    [SerializeField] [HideInInspector] public MatchSummaryController matchSummaryController;
    [SerializeField] [HideInInspector] public ChatController chatController;
    [SerializeField] [HideInInspector] public DebugController debugController;
    
    [SerializeField] [HideInInspector] private string _joinCode; 

    [SerializeField] public Button botResetButton;
    [SerializeField] public Button SetAutoShootBot;

    public AppScreen State { get; private set; } = AppScreen.Menu;

    private bool IsAuthenticated => NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer ||
                                    NetworkManager.Singleton.IsHost;

    public static UIManager Instance { get; private set; }

    #endregion

    #region Delegates and Events
    
    private void Update()
    {
        if (State == AppScreen.Game || State == AppScreen.Room)
        {
            bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
            if (pingSlider != null) pingSlider.interactable = isHost;
            if (enableSimToggle != null) enableSimToggle.interactable = isHost;
        }
    }

    private IEnumerator OnConnection()
    {
        loadCanvas.SetActive(true);
        yield return new WaitUntil(() => NetworkManager.Singleton.IsConnectedClient);
        yield return new WaitForSeconds(1f);
        loadCanvas.SetActive(false);
        var mode = NetworkManager.Singleton.IsHost ? "Host" : NetworkManager.Singleton.IsServer ? "Server" : "Client";
        
        string displayInfo = NetworkManager.Singleton.IsHost ? GetLocalIPAddress() : menuRoomCode.text;
        if(string.IsNullOrEmpty(displayInfo)) displayInfo = "Localhost";
        
        debugController.statRoomProperties.text = $"LAN: {displayInfo} | {mode}";
        EventManager.Instance.RaiseScreenChange(State = AppScreen.Room);
        StartCoroutine(debugController.ShowClientRTT());
    }

    private string GetLocalIPAddress()
    {
        try {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
        } catch { }
        return "127.0.0.1";
    }

    private void OnScreenChange(AppScreen screen)
    {
        switch (screen)
        {
            case AppScreen.Menu:
                State = AppScreen.Menu;
                menuCanvas.SetActive(true);
                chatController.chatCanvas.SetActive(false);
                playersHubCanvas.SetActive(false);
                roomCanvas.SetActive(false);
                gameCanvas.SetActive(false);
                endGameCanvas.SetActive(false);
                break;
            case AppScreen.Room:
                State = AppScreen.Room;
                menuCanvas.SetActive(false);
                chatController.chatCanvas.SetActive(true);
                playersHubCanvas.SetActive(true);
                roomCanvas.SetActive(true);
                gameCanvas.SetActive(false);
                endGameCanvas.SetActive(false);
                break;
            case AppScreen.Game:
                State = AppScreen.Game;
                menuCanvas.SetActive(false);
                chatController.chatCanvas.SetActive(true);
                playersHubCanvas.SetActive(true);
                roomCanvas.SetActive(false);
                gameCanvas.SetActive(true);

                

                endGameCanvas.SetActive(false);
                break;
            case AppScreen.EndGame:
                State = AppScreen.EndGame;
                menuCanvas.SetActive(false);
                chatController.chatCanvas.SetActive(true);
                playersHubCanvas.SetActive(true);
                roomCanvas.SetActive(false);
                gameCanvas.SetActive(false);
                endGameCanvas.SetActive(true);

                gameKph.text = "";
                gamePosition.text = "";
                gameLaps.text = "";
                gameOverallTime.text = "--:--.---";
                gameLapTime.text = "--:--.---";
                break;
        }
    }
    
    private async void OnStartHost()
    {
        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData("127.0.0.1", 7777); 

        GameManager.Instance.PlayerName = string.IsNullOrEmpty(menuNickname.text) ? "Host" : menuNickname.text;
        menuNickname.text = "";
        
        NetworkManager.Singleton.StartHost();
    }

    private async void OnStartClient()
    {
        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();

        string ipAddress = "127.0.0.1"; 
        if (!string.IsNullOrEmpty(menuRoomCode.text))
        {
            ipAddress = menuRoomCode.text;
        }
        
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData(ipAddress, 7777);

        GameManager.Instance.PlayerName = string.IsNullOrEmpty(menuNickname.text) ? "Client" : menuNickname.text;
        menuNickname.text = "";

        try
        {
            NetworkManager.Singleton.StartClient();
        }
        catch (Exception e)
        {
            Debug.LogError($"Connection failed: {e.Message}");
            loadCanvas.SetActive(false);
        }
    }

    private void OnExitClient()
    {
        if (IsAuthenticated) NetworkManager.Singleton.Shutdown();
        StopCoroutine(debugController.ShowClientRTT());
        debugController.statRoomProperties.text = "";
        debugController.statRtt.text = "";
        debugController.statRoomPlayers.text = "";
        StartCoroutine(RaceManager.Instance.LeaveRace());
    }

    #endregion

    #region Unity Callbacks

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Start()
    {
        if (debugController == null) debugController = GetComponent<DebugController>();
        if (matchSummaryController == null) matchSummaryController = GetComponent<MatchSummaryController>();
        if (chatController == null) chatController = GetComponent<ChatController>();

        EventManager.Instance.ScreenChange.AddListener(OnScreenChange);

        loadHide.onClick.AddListener(() => loadCanvas.SetActive(false));

        menuNickname.text = "";
        menuRoomCode.text = ""; 
        if (menuRoomCode.placeholder != null)
            menuRoomCode.placeholder.GetComponent<TMP_Text>().text = "Enter Host IP (Default: 127.0.0.1)"; 

        menuJoin.onClick.AddListener(OnStartClient);
        menuJoin.onClick.AddListener(() => StartCoroutine(OnConnection()));
        menuHost.onClick.AddListener(OnStartHost);
        menuHost.onClick.AddListener(() => StartCoroutine(OnConnection()));

        roomColorSlider.value = 0;
        roomReady.onClick.AddListener(StartGame);
        roomExit.onClick.AddListener(OnExitClient);

        notificationHeader.text = "";
        notificationTime.text = "";
        notificationBody.text = "";

        gameKph.text = "";
        gamePosition.text = "";
        gameLaps.text = "";
        gameOverallTime.text = "--:--.---";
        gameLapTime.text = "--:--.---";
        gameExit.onClick.AddListener(OnExitClient);

        if (clientSidePredictionToggle != null)
        {
            clientSidePredictionToggle.onValueChanged.AddListener(OnClientSidePredictionChanged);

            var players = RaceManager.Instance.players;
            foreach (var player in players)
            {
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController != null && carController.IsOwnerCar)
                {
                    clientSidePredictionToggle.isOn = carController.UseClientSidePrediction;
                    break;
                }
            }
        }
        if (serverReconciliationToggle != null)
        {
            serverReconciliationToggle.onValueChanged.AddListener(OnServerReconciliationChanged);

            var players = RaceManager.Instance.players;
            foreach (var player in players)
            {
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController != null && carController.IsOwnerCar)
                {
                    serverReconciliationToggle.isOn = carController.UseServerReconciliation;
                    break;
                }
            }
        }
        if (lagCompensationToggle != null)
        {
            lagCompensationToggle.onValueChanged.AddListener(OnLagCompensationChanged);

            var players = RaceManager.Instance.players;
            foreach (var player in players)
            {
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController != null && carController.IsOwnerCar)
                {
                    lagCompensationToggle.isOn = carController.UseLagCompensation;
                    break;
                }
            }
        }

        if (carDeadReckoningToggles != null)
        {
            for (int i = 0; i < carDeadReckoningToggles.Count; i++)
            {
                var toggle = carDeadReckoningToggles[i];
                int id = i;
                toggle.onValueChanged.AddListener((enable) => OnCarDeadReckoningToggleChanged(id, enable));
            }
        }

        if (carDRModeDropdowns != null)
        {
            for (int i = 0; i < carDRModeDropdowns.Count; i++)
            {
                var dropdown = carDRModeDropdowns[i];
                int id = i;
                dropdown.ClearOptions();
                dropdown.AddOptions(Enum.GetNames(typeof(DeadReckoningSystem.DeadReckoningMode)).ToList());
                dropdown.onValueChanged.AddListener((mode) => OnCarDRModeChanged(id, mode));

                if (id < 0 || id >= RaceManager.Instance.players.Count) continue;
                var player = RaceManager.Instance.players[id];
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController == null) continue;

                Debug.Log($"??? {(int)carController.deadReckoningSystem.CurrentDRMode}");

                dropdown.SetValueWithoutNotify((int)carController.deadReckoningSystem.CurrentDRMode);
            }
        }

        if (carCorrectionModeDropdowns != null)
        {
            for (int i = 0; i < carCorrectionModeDropdowns.Count; i++)
            {
                var dropdown = carCorrectionModeDropdowns[i];
                int id = i;
                dropdown.ClearOptions();
                dropdown.AddOptions(Enum.GetNames(typeof(DeadReckoningSystem.CorrectionMode)).ToList());
                dropdown.onValueChanged.AddListener((mode) => OnCarCorrectionModeChanged(id, mode));

                if (id < 0 || id >= RaceManager.Instance.players.Count) continue;
                var player = RaceManager.Instance.players[id];
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController == null) continue;

                //dropdown.value = (int)carController.deadReckoningSystem.CurrentCorrectionMode;
                dropdown.SetValueWithoutNotify((int)carController.deadReckoningSystem.CurrentCorrectionMode);
            }
        }

        if (carAdaptiveThresholdToggles != null)
        {
            for (int i = 0; i < carAdaptiveThresholdToggles.Count; i++)
            {
                var toggle = carAdaptiveThresholdToggles[i];
                int id = i;
                toggle.onValueChanged.AddListener((enable) => OnCarAdaptiveThresholdChanged(id, enable));

                if (id < 0 || id >= RaceManager.Instance.players.Count) continue;
                var player = RaceManager.Instance.players[id];
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController == null) continue;

                toggle.isOn = carController.deadReckoningSystem.UseAdaptiveThreshold;
            }
        }

        if (carTimeSyncToggles != null)
        {
            for (int i = 0; i < carTimeSyncToggles.Count; i++)
            {
                var toggle = carTimeSyncToggles[i];
                int id = i;
                toggle.onValueChanged.AddListener((enable) => OnCarTimeSyncChanged(id, enable));
                
                if (id < 0 || id >= RaceManager.Instance.players.Count) continue;
                var player = RaceManager.Instance.players[id];
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController == null) continue;

                toggle.isOn = carController.deadReckoningSystem.UseTimeSync;
            }
        }

        

        // if (drAlgorithmDropdown != null)
        // {
        //     drAlgorithmDropdown.ClearOptions();
        //     drAlgorithmDropdown.AddOptions(new List<string> { "None (Lerp)", "Linear (1st Order)", "Quadratic (2nd Order)" });
        //     drAlgorithmDropdown.onValueChanged.AddListener(OnDRAlgorithmChanged);
        // }

        // if (correctionModeDropdown != null)
        // {
        //     correctionModeDropdown.ClearOptions();
        //     correctionModeDropdown.AddOptions(new List<string> { "SmoothDamp", "Lerp" });
        //     correctionModeDropdown.onValueChanged.AddListener(OnCorrectionModeChanged);
        // }
        
        // if (gameInterpolation != null) 
        // {
        //     gameInterpolation.onValueChanged.RemoveAllListeners();
        //     gameInterpolation.onValueChanged.AddListener(OnDeadReckoningToggleChanged);
        // }

        // if (useCubicSplineToggle != null) useCubicSplineToggle.onValueChanged.AddListener((val) => SetImprovementOption("Spline", val));
        // if (useAdaptiveThresholdToggle != null) useAdaptiveThresholdToggle.onValueChanged.AddListener((val) => SetImprovementOption("Adaptive", val));
        // if (useTimeSyncToggle != null) useTimeSyncToggle.onValueChanged.AddListener((val) => SetImprovementOption("TimeSync", val));
        
        if (pingSlider != null)
        {
            pingSlider.minValue = 0;
            pingSlider.maxValue = 2000; 
            pingSlider.value = 0;
            pingSlider.onValueChanged.AddListener(OnPingSliderChanged);
        }
        if (jitterSlider != null)
        {
            jitterSlider.minValue = 0;
            jitterSlider.maxValue = 500;
            jitterSlider.value = 0;
            jitterSlider.onValueChanged.AddListener(OnJitterSliderChanged);
        }
        if (enableSimToggle != null) 
        {
            enableSimToggle.isOn = false;
            enableSimToggle.onValueChanged.AddListener(OnSimToggleChanged);
        }
        
        // Force reset simulator at start
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport != null) transport.SetDebugSimulatorParameters(0, 0, 0);

        if (botToggle != null) botToggle.isOn = false;

        if (botResetButton != null)
        {
            botResetButton.onClick.AddListener(OnBotResetClicked);
        }

        EnsureSetAutoShootBotButton();
        if (SetAutoShootBot != null)
        {
            SetAutoShootBot.onClick.AddListener(OnSetAutoShootBotClicked);
            UpdateAutoShootBotButtonLabel();
        }

        if (baseThresholdInput != null)
        {
            baseThresholdInput.text = "2.5";
            baseThresholdInput.onEndEdit.AddListener(OnAdaptiveThresholdChanged);
        }
        if (kvCoefficientInput != null)
        {
            kvCoefficientInput.text = "0.15";
            kvCoefficientInput.onEndEdit.AddListener(OnAdaptiveThresholdChanged);
        }
        if (kaCoefficientInput != null)
        {
            kaCoefficientInput.text = "0.25";
            kaCoefficientInput.onEndEdit.AddListener(OnAdaptiveThresholdChanged);
        }
    }

    private void OnCarDeadReckoningToggleChanged(int id, bool enable)
    {
        if (id < 0 || id >= RaceManager.Instance.players.Count) return;
        var player = RaceManager.Instance.players[id];
        if (player == null) return;
        CarController carController = player.GetCarController;
        if (carController.IsOwnerCar) return;
        if (carController != null)
        {
            carController.UseDeadReckoning = enable;
            ResetLocalMetrics();
        }
    }

    private void OnCarDRModeChanged(int id, int modeIndex)
    {
        if (id < 0 || id >= RaceManager.Instance.players.Count) return;
        var player = RaceManager.Instance.players[id];
        if (player == null) return;
        CarController carController = player.GetCarController;
        if (carController == null) return;

        // Convert dropdown index to DeadReckoningMode
        DeadReckoningSystem.DeadReckoningMode mode = (DeadReckoningSystem.DeadReckoningMode)modeIndex;
        carController.deadReckoningSystem?.SetDRMode(mode);
    }

    private void OnCarCorrectionModeChanged(int id, int modeIndex)
    {
        if (id < 0 || id >= RaceManager.Instance.players.Count) return;
        var player = RaceManager.Instance.players[id];
        if (player == null) return;
        CarController carController = player.GetCarController;
        if (carController == null) return;

        carController.deadReckoningSystem?.SetCorrectionMode(modeIndex);
    }

    private void OnCarAdaptiveThresholdChanged(int id, bool enable)
    {
        if (id < 0 || id >= RaceManager.Instance.players.Count) return;
        var player = RaceManager.Instance.players[id];
        if (player == null) return;
        CarController carController = player.GetCarController;
        if (carController == null) return;

        // Set UseAdaptiveThreshold property
        carController.deadReckoningSystem.UseAdaptiveThreshold = enable;
    }

    private void OnCarTimeSyncChanged(int id, bool enable)
    {
        if (id < 0 || id >= RaceManager.Instance.players.Count) return;
        var player = RaceManager.Instance.players[id];
        if (player == null) return;
        CarController carController = player.GetCarController;
        if (carController == null) return;

        // Set UseTimeSync property
        carController.deadReckoningSystem.UseTimeSync = enable;
    }

    private void OnLagCompensationChanged(bool enable)
    {
        var players = RaceManager.Instance.players;
        foreach (var player in players)
        {
            if (player == null) continue;
            CarController carController = player.GetCarController;
            if (carController != null && carController.IsOwnerCar)
            {
                carController.UseLagCompensation = enable;
            }
        }
    }

    public void UpdatePlayerList()
    {
        for (int i = 0; i < carDeadReckoningLabels.Count; i++)
        {
            var label = carDeadReckoningLabels[i];
            if (i >= RaceManager.Instance.players.Count)
            {
                label.text = "N/A";
                continue;
            }
            var player = RaceManager.Instance.players[i];
            if (player != null && player.GetCarController != null && player.GetCarController.IsOwnerCar)
            {
                label.text = "Player";
            }
            else if (player == null)
            {
                label.text = "N/A";
            }
        }
    }

    private void OnServerReconciliationChanged(bool enable)
    {
        var players = RaceManager.Instance.players;
        foreach (var player in players)
        {
            if (player == null) continue;
            CarController carController = player.GetCarController;
            if (carController != null && carController.IsOwnerCar)
            {
                carController.UseServerReconciliation = enable;
            }
        }
    }

    private void OnClientSidePredictionChanged(bool enable)
    {
        var players = RaceManager.Instance.players;
        foreach (var player in players)
        {
            if (player == null) continue;
            CarController carController = player.GetCarController;
            if (carController != null && carController.IsOwnerCar)
            {
                carController.UseClientSidePrediction = enable;
            }
        }
    }
    private void ResetLocalMetrics()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null) return;
        var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
        if (localPlayer != null)
        {
            var playerScript = localPlayer.GetComponent<NetworkPlayer>();
            if (playerScript != null && playerScript.car != null)
            {
                var controller = playerScript.car.GetComponent<CarController>();
                if (controller != null) controller.ResetCalculationMetrics();
            }
        }
    }

    private void OnDeadReckoningToggleChanged(bool value)
    {
        var playerCar = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject()?.GetComponent<NetworkPlayer>()?.car;
        if (playerCar != null)
        {
            var controller = playerCar.GetComponent<CarController>();
            if (controller != null) controller.UseDeadReckoning = value;
        }
        ResetLocalMetrics();
    }

    private void OnDRAlgorithmChanged(int index)
    {
        var playerCar = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject()?.GetComponent<NetworkPlayer>()?.car;
        if (playerCar != null)
        {
            var controller = playerCar.GetComponent<CarController>();
            //if (controller != null) controller.SetDRMode((DeadReckoningMode)index);
        }
        ResetLocalMetrics();
    }

    private void OnCorrectionModeChanged(int index)
    {
        var playerCar = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject()?.GetComponent<NetworkPlayer>()?.car;
        if (playerCar != null)
        {
            var controller = playerCar.GetComponent<CarController>();
            if (controller != null) controller.SetCorrectionMode(index);
        }
        ResetLocalMetrics();
    }

    private void SetImprovementOption(string option, bool value)
    {
        var playerCar = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject()?.GetComponent<NetworkPlayer>()?.car;
        if (playerCar != null)
        {
            var controller = playerCar.GetComponent<CarController>();
            if (controller != null) controller.SetImprovement(option, value);
        }
        ResetLocalMetrics();
    }
    
    private void OnSimToggleChanged(bool enabled)
    {
        var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
        if (transport != null)
        {
            if (!enabled) {
                transport.SetDebugSimulatorParameters(packetDelay: 0, packetJitter: 0, dropRate: 0);
                if(pingDisplay) pingDisplay.text = "Sim Ping: OFF";
            }
            else {
                OnPingSliderChanged(pingSlider.value);
            }
        }
    }

    private void OnPingSliderChanged(float ping)
    {
        //if (enableSimToggle != null && !enableSimToggle.isOn) return;
        var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
        if (transport != null) {
            float delay = ping / 2f;
            int jitter = jitterSlider != null ? (int)jitterSlider.value : 0;
            transport.SetDebugSimulatorParameters(packetDelay: (int)delay, packetJitter: jitter, dropRate: 0);
            if(pingDisplay) pingDisplay.text = $"Ping: {(int)ping}ms";
        }
    }

    private void OnJitterSliderChanged(float jitter)
    {
        var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
        if (transport != null) {
            float delay = pingSlider != null ? pingSlider.value / 2f : 0;
            transport.SetDebugSimulatorParameters(packetDelay: (int)delay, packetJitter: (int)jitter, dropRate: 0);
            if(jitterDisplay) jitterDisplay.text = $"Jitter: {(int)jitter}ms";
        }
    }

    private void OnAdaptiveThresholdChanged(string value)
    {
        float baseThreshold = 2.5f;
        float kvCoeff = 0.15f;
        float kaCoeff = 0.25f;

        if (baseThresholdInput != null && float.TryParse(baseThresholdInput.text, out float bt))
        {
            baseThreshold = Mathf.Clamp(bt, 0.01f, 5f);
            baseThresholdInput.text = baseThreshold.ToString("F3");
        }
        if (kvCoefficientInput != null && float.TryParse(kvCoefficientInput.text, out float kv))
        {
            kvCoeff = Mathf.Clamp(kv, 0f, 1f);
            kvCoefficientInput.text = kvCoeff.ToString("F3");
        }
        if (kaCoefficientInput != null && float.TryParse(kaCoefficientInput.text, out float ka))
        {
            kaCoeff = Mathf.Clamp(ka, 0f, 1f);
            kaCoefficientInput.text = kaCoeff.ToString("F3");
        }

        // Apply to all cars' dead reckoning systems
        if (RaceManager.Instance != null && RaceManager.Instance.players != null)
        {
            foreach (var player in RaceManager.Instance.players)
            {
                if (player == null) continue;
                CarController carController = player.GetCarController;
                if (carController != null && carController.IsOwnerCar)
                {
                    carController.SetAdaptiveThresholdConfig(baseThreshold, kvCoeff, kaCoeff);
                }
            }
        }
    }

    #endregion
    
    #region UI properties setters
    public void SetNotificationCanvas(bool active, string header = "", string body = "", string seconds = "")
    {
        notificationHeader.text = header;
        notificationBody.text = body;
        notificationTime.text = seconds;
        notificationCanvas.SetActive(active);
    }
    
    public void SetPlayerHub(int pos, string username, Color color)
    {
        playersHubPanels[pos].SetActive(true);
        playersHubNames[pos].text = username;
        playersHubNames[pos].color = color;
    }

    public void ClearPlayerHub(int pos)
    {
        playersHubPanels[pos].SetActive(false);
        playersHubNames[pos].text = "";
    }

    private void StartGame()
    {
        if (RaceManager.Instance.AllPlayersRacing()) StartCoroutine(FailedStartGame());
        else EventManager.Instance.RaiseScreenChange(State = AppScreen.Game);
    }

    private IEnumerator FailedStartGame()
    {
        SetNotificationCanvas(true, "GAME ALREADY STARTED", "WILL START THE NEXT RACE\nWAITING...", "SOON");
        yield return new WaitForSeconds(3);
        SetNotificationCanvas(false);
    }

    private void OnBotResetClicked()
    {
        CarController controller = GetLocalCarController();
        if (controller != null)
        {
            controller.TeleportToStart();
        }
    }

    private void OnSetAutoShootBotClicked()
    {
        CarController controller = GetLocalCarController();
        if (controller == null) return;

        controller.SetAutoShootBot(!controller.AutoShootBot);
        UpdateAutoShootBotButtonLabel(controller.AutoShootBot);
    }

    private CarController GetLocalCarController()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
            return null;

        var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
        if (localPlayer == null) return null;

        var playerScript = localPlayer.GetComponent<NetworkPlayer>();
        if (playerScript == null || playerScript.car == null) return null;

        return playerScript.car.GetComponent<CarController>();
    }

    private void EnsureSetAutoShootBotButton()
    {
        if (SetAutoShootBot != null || gameCanvas == null) return;

        if (botResetButton != null)
        {
            GameObject buttonObject = Instantiate(botResetButton.gameObject, botResetButton.transform.parent);
            buttonObject.name = "SetAutoShootBot";
            buttonObject.SetActive(true);

            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchoredPosition += new Vector2(0f, 36f);
                rect.sizeDelta = new Vector2(190f, rect.sizeDelta.y);
            }

            SetAutoShootBot = buttonObject.GetComponent<Button>();
            if (SetAutoShootBot != null)
            {
                SetAutoShootBot.onClick.RemoveAllListeners();
            }

            TMP_Text tmpText = buttonObject.GetComponentInChildren<TMP_Text>(true);
            if (tmpText != null)
            {
                tmpText.fontSize = 18f;
            }

            return;
        }

        GameObject fallbackObject = new GameObject("SetAutoShootBot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        fallbackObject.transform.SetParent(gameCanvas.transform, false);

        RectTransform fallbackRect = fallbackObject.GetComponent<RectTransform>();
        fallbackRect.anchorMin = new Vector2(0.5f, 0f);
        fallbackRect.anchorMax = new Vector2(0.5f, 0f);
        fallbackRect.anchoredPosition = new Vector2(196.8f, 251f);
        fallbackRect.sizeDelta = new Vector2(180f, 30f);

        Image image = fallbackObject.GetComponent<Image>();
        image.color = Color.white;

        SetAutoShootBot = fallbackObject.GetComponent<Button>();
        SetAutoShootBot.targetGraphic = image;

        GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(fallbackObject.transform, false);

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.196f, 0.196f, 0.196f, 1f);
        text.fontSize = 18;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private void UpdateAutoShootBotButtonLabel(bool? enabledOverride = null)
    {
        if (SetAutoShootBot == null) return;

        bool enabled = enabledOverride ?? GetLocalCarController()?.AutoShootBot ?? false;
        string label = enabled ? "Auto Shoot: ON" : "Auto Shoot: OFF";

        TMP_Text tmpText = SetAutoShootBot.GetComponentInChildren<TMP_Text>(true);
        if (tmpText != null)
        {
            tmpText.text = label;
            return;
        }

        Text text = SetAutoShootBot.GetComponentInChildren<Text>(true);
        if (text != null)
        {
            text.text = label;
        }
    }

    public void UpdateCarJerk(int id, float jerk)
    {
        if (id < 0 || id >= carJerkTexts.Count) return;
        carJerkTexts[id].text = $"{jerk:0}";
    }

    public void UpdateCarAccuracy(int id, float accuracy)
    {
        if (id < 0 || id >= carAccuracyTexts.Count) return;
        carAccuracyTexts[id].text = $"{accuracy:0.0}%";
    }

    public void UpdateServerReconciliationAccuracy(float accuracy)
    {
        // if (serverReconciliationAccuracy != null) serverReconciliationAccuracy.text = $"Reconciliation Accuracy: {accuracy:0.0}%";
    }

    public void UpdateClientCollisionCounts(int clientCount)
    {
        if (clientCollisionCountText != null) clientCollisionCountText.text = $"{clientCount}";
    }

    public void UpdateServerCollisionCounts(int serverCount)
    {
        if (serverCollisionCountText != null) serverCollisionCountText.text = $"{serverCount}";
    }

    #endregion
}
