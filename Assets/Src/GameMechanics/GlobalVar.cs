public class GlobalVar
{
    public const string CLIENT = "<color=blue>CLIENT</color>";
    public const string SERVER = "<color=red>SERVER</color>";
    public const string CLIENT_SEND_INPUT = "<color=green>CLIENT_SEND_INPUT</color>";
    public const string SERVER_RECEIVE_INPUT = "<color=green>SERVER_RECEIVE_INPUT</color>";
    public const string CLIENT_RECEIVE_STATE = "<color=green>CLIENT_RECEIVE_STATE</color>";
    public static string GetStringDataList((string, string)[] dataValueList) 
    {
        string res = $"";
        foreach (var val in dataValueList)
        {
            res += $"{val.Item1}: <color=yellow>[{val.Item2}</color>]; ";
        }
        return res;
    }
}
