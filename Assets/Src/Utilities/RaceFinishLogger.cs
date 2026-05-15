using System;
using System.Globalization;
using System.IO;
using System.Text;
using CustomTypes;
using Unity.Netcode;
using UnityEngine;

public static class RaceFinishLogger
{
    private const string LogDirectoryName = "RaceLogs";
    private const string LogFileName = "race_finish_log.csv";

    private static readonly string[] HeaderColumns =
    {
        "timestamp_local",
        "finish_time_seconds",
        "ping_ms",
        "jitter_ms",
        "finished_car_id",
        "owner_car_id",
        "use_dead_reckoning",
        "use_client_side_prediction",
        "use_server_reconciliation",
        "use_lag_compensation",
        "dr_mode",
        "correction_mode",
        "threshold_base",
        "kv_coefficient",
        "ka_coefficient",
        "use_adaptive_threshold",
        "use_time_sync",
        "accuracy_percent",
        "jerk",
        "client_kills",
        "server_kills"
    };

    public static void LogFinish(NetworkPlayer finishedPlayer)
    {
        if (finishedPlayer == null)
        {
            return;
        }

        CarController car = finishedPlayer.GetCarController;
        if (car == null)
        {
            Debug.LogWarning("[RaceFinishLogger] Cannot log finish because the finished player has no car.");
            return;
        }

        try
        {
            string logPath = GetLogPath();
            string directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bool needsHeader = !File.Exists(logPath) || new FileInfo(logPath).Length == 0;
            string headerLine = string.Join(",", HeaderColumns);
            bool needsSchemaHeader = !needsHeader && !HasHeader(logPath, headerLine);
            using StreamWriter writer = new StreamWriter(logPath, append: true, Encoding.UTF8);

            if (needsHeader)
            {
                writer.WriteLine(headerLine);
            }
            else if (needsSchemaHeader)
            {
                writer.WriteLine();
                writer.WriteLine(headerLine);
            }

            writer.WriteLine(BuildCsvRow(finishedPlayer, car));
            Debug.Log($"[RaceFinishLogger] Logged race finish to {logPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RaceFinishLogger] Failed to write race finish log: {ex.Message}");
        }
    }

    private static string BuildCsvRow(NetworkPlayer finishedPlayer, CarController car)
    {
        DeadReckoningSystem deadReckoning = car.deadReckoningSystem;

        float thresholdBase = deadReckoning?.BaseErrorThreshold ?? 0f;
        float kvCoefficient = deadReckoning?.VelocityCoefficient ?? 0f;
        float kaCoefficient = deadReckoning?.AccelerationCoefficient ?? 0f;
        string drMode = deadReckoning != null ? deadReckoning.CurrentDRMode.ToString() : "None";
        string correctionMode = deadReckoning != null ? deadReckoning.CurrentCorrectionMode.ToString() : "None";
        bool useAdaptiveThreshold = deadReckoning?.UseAdaptiveThreshold ?? false;
        bool useTimeSync = deadReckoning?.UseTimeSync ?? false;

        string[] columns =
        {
            DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            FormatFloat(finishedPlayer.FinishRawTime),
            GetPingMs().ToString(CultureInfo.InvariantCulture),
            GetJitterMs().ToString(CultureInfo.InvariantCulture),
            finishedPlayer.ID.ToString(CultureInfo.InvariantCulture),
            GetOwnerCarId().ToString(CultureInfo.InvariantCulture),
            FormatBool(car.UseDeadReckoning),
            FormatBool(car.UseClientSidePrediction),
            FormatBool(car.UseServerReconciliation),
            FormatBool(car.UseLagCompensation),
            drMode,
            correctionMode,
            FormatFloat(thresholdBase),
            FormatFloat(kvCoefficient),
            FormatFloat(kaCoefficient),
            FormatBool(useAdaptiveThreshold),
            FormatBool(useTimeSync),
            FormatFloat(car.CurrentAverageAccuracyPercent),
            FormatFloat(car.CurrentAverageJerk),
            car.CurrentKills.ToString(CultureInfo.InvariantCulture),
            finishedPlayer.Kills.ToString(CultureInfo.InvariantCulture)
        };

        return string.Join(",", Array.ConvertAll(columns, EscapeCsv));
    }

    private static string GetLogPath()
    {
        return Path.Combine(Application.persistentDataPath, LogDirectoryName, LogFileName);
    }

    private static bool HasHeader(string logPath, string headerLine)
    {
        try
        {
            foreach (string line in File.ReadLines(logPath))
            {
                if (line == headerLine)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static int GetPingMs()
    {
        if (UIManager.Instance != null && UIManager.Instance.pingSlider != null)
        {
            return Mathf.RoundToInt(UIManager.Instance.pingSlider.value);
        }

        try
        {
            return Mathf.RoundToInt(NetworkManager.Singleton.NetworkConfig.NetworkTransport.GetCurrentRtt(AppConfig.Singleton.GAME.SERVER_ID));
        }
        catch
        {
            return 0;
        }
    }

    private static int GetJitterMs()
    {
        if (UIManager.Instance != null && UIManager.Instance.jitterSlider != null)
        {
            return Mathf.RoundToInt(UIManager.Instance.jitterSlider.value);
        }

        return 0;
    }

    private static int GetOwnerCarId()
    {
        if (RaceManager.Instance == null)
        {
            return -1;
        }

        foreach (NetworkPlayer player in RaceManager.Instance.players)
        {
            if (player != null && player.IsOwner)
            {
                return player.ID;
            }
        }

        foreach (NetworkPlayer player in RaceManager.Instance.waitList)
        {
            if (player != null && player.IsOwner)
            {
                return player.ID;
            }
        }

        return -1;
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(",") && !value.Contains("\"") && !value.Contains("\n") && !value.Contains("\r"))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
