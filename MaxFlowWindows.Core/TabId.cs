namespace MaxFlowWindows.Core;

public enum TabId
{
    Dictate,
    History,
    Profile,
    Dictionary,
    Audio,
    Settings
}

public static class TabIdExtensions
{
    public static string ToIdString(this TabId tab) => tab switch
    {
        TabId.Dictate => "dictate",
        TabId.History => "history",
        TabId.Profile => "profile",
        TabId.Dictionary => "dictionary",
        TabId.Audio => "audio",
        TabId.Settings => "settings",
        _ => "dictate"
    };

    public static TabId FromIdString(string id) => id?.ToLowerInvariant() switch
    {
        "dictate" => TabId.Dictate,
        "history" => TabId.History,
        "profile" => TabId.Profile,
        "dictionary" => TabId.Dictionary,
        "audio" => TabId.Audio,
        "settings" => TabId.Settings,
        _ => TabId.Dictate
    };
}