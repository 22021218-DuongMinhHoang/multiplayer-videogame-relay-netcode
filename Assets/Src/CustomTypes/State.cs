using System;

namespace CustomTypes
{
    [Serializable]
    public enum AppScreen
    {
        Menu,
        Room,
        Game,
        EndGame
    }

    [Serializable]
    public enum CarState
    {
        Idle,
        Vulnerable,
        Dead,
        Invincible
    }

    [Serializable]
    public enum GameState
    {
        Idle,
        Started,
        Finished
    }

    [Serializable]
    public enum RaceState
    {
        Schedule,
        Classification1,
        Race1,
        Classification2,
        Race2,
        Classification3,
        Race3
    }

    [Serializable]
    public enum DeadReckoningMode
    {
        None,       // Bậc 0: Chỉ nội suy về vị trí mới nhất
        Linear,     // Bậc 1: P + V*t
        Quadratic   // Bậc 2: P + V*t + 0.5*A*t^2
    }
}