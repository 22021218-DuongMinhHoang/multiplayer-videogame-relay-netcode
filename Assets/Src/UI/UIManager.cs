using System;
using System.Collections;
using System.Collections.Generic;
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

    // --- DEAD RECKONING UI ---
    [Header("Dead Reckoning Settings")]
    [SerializeField] public TMP_Dropdown drAlgorithmDropdown;
    [SerializeField] public TMP_Dropdown correctionModeDropdown;
    [SerializeField] public Toggle useCubicSplineToggle;
    [SerializeField] public Toggle useAdaptiveThresholdToggle;
    [SerializeField] public Toggle useTimeSyncToggle;

    [Header("Additional Metrics")]
    [SerializeField] public TMP_Text instantError;
    [SerializeField] public TMP_Text jitterEstimate;
    
    // --- NETWORK SIMULATOR & BOT UI ---
    [Header("Debug & Simulation")]
    [SerializeField] public Slider pingSlider;       
    [SerializeField] public TMP_Text pingDisplay;    
    [SerializeField] public Toggle enableSimToggle; 
    [SerializeField] public Toggle botToggle; 
    // -----------------------------

    [Header("End Game")] [SerializeField] public GameObject endGameCanvas;
    
    [SerializeField] [HideInInspector] public MatchSummaryController matchSummaryController;
    [SerializeField] [HideInInspector] public ChatController chatController;
    [SerializeField] [HideInInspector] public DebugController debugController;
    
    [SerializeField] [HideInInspector] private string _joinCode; 

    [SerializeField] public Button botResetButton;

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
    
    // --- LAN HOST ---
    private async void OnStartHost()
    {
        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData("0.0.0.0", 7777); 

        GameManager.Instance.PlayerName = string.IsNullOrEmpty(menuNickname.text) ? "Host" : menuNickname.text;
        menuNickname.text = "";
        
        NetworkManager.Singleton.StartHost();
    }

    // --- LAN CLIENT ---
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

        // --- INIT DEAD RECKONING UI ---
        if (drAlgorithmDropdown != null)
        {
            drAlgorithmDropdown.ClearOptions();
            drAlgorithmDropdown.AddOptions(new List<string> { "None (Lerp)", "Linear (1st Order)", "Quadratic (2nd Order)" });
            drAlgorithmDropdown.onValueChanged.AddListener(OnDRAlgorithmChanged);
        }

        if (correctionModeDropdown != null)
        {
            correctionModeDropdown.ClearOptions();
            correctionModeDropdown.AddOptions(new List<string> { "SmoothDamp", "Lerp" });
            correctionModeDropdown.onValueChanged.AddListener(OnCorrectionModeChanged);
        }
        
        if (gameInterpolation != null) 
        {
            gameInterpolation.onValueChanged.RemoveAllListeners();
            gameInterpolation.onValueChanged.AddListener(OnDeadReckoningToggleChanged);
        }

        if (useCubicSplineToggle != null) useCubicSplineToggle.onValueChanged.AddListener((val) => SetImprovementOption("Spline", val));
        if (useAdaptiveThresholdToggle != null) useAdaptiveThresholdToggle.onValueChanged.AddListener((val) => SetImprovementOption("Adaptive", val));
        if (useTimeSyncToggle != null) useTimeSyncToggle.onValueChanged.AddListener((val) => SetImprovementOption("TimeSync", val));
        
        // --- NETWORK SIMULATOR ---
        if (pingSlider != null)
        {
            pingSlider.minValue = 0;
            pingSlider.maxValue = 500; 
            pingSlider.value = 0;
            pingSlider.onValueChanged.AddListener(OnPingSliderChanged);
        }
        if (enableSimToggle != null) 
        {
            enableSimToggle.isOn = false;
            enableSimToggle.onValueChanged.AddListener(OnSimToggleChanged);
        }
        
        // Force reset simulator at start
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport != null) transport.SetDebugSimulatorParameters(0, 0, 0);

        // --- BOT TOGGLE ---
        if (botToggle != null) botToggle.isOn = false;

        if (botResetButton != null)
        {
            botResetButton.onClick.AddListener(OnBotResetClicked);
        }
    }
    
    // --- EVENT HANDLERS (WITH RESET METRICS) ---
    
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
            if (controller != null) controller.SetDRMode((DeadReckoningMode)index);
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
    
    // --- SIMULATOR LOGIC ---
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

    private void OnPingSliderChanged(float delay)
    {
        if (enableSimToggle != null && !enableSimToggle.isOn) return;
        var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
        if (transport != null) {
            int jitter = (int)(delay * 0.1f); 
            transport.SetDebugSimulatorParameters(packetDelay: (int)delay, packetJitter: jitter, dropRate: 0);
            if(pingDisplay) pingDisplay.text = $"Sim Latency: {(int)delay}ms (RTT: {(int)delay * 2}ms)";
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
        // Tìm xe của Local Player
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null)
        {
            var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer != null)
            {
                var playerScript = localPlayer.GetComponent<NetworkPlayer>();
                if (playerScript != null && playerScript.car != null)
                {
                    var controller = playerScript.car.GetComponent<CarController>();
                    if (controller != null)
                    {
                        controller.TeleportToStart();
                    }
                }
            }
        }
    }

    #endregion
}