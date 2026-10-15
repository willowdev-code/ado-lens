using System.Runtime.InteropServices;

namespace AdoLens;

/// <summary>
/// Texts of the interactive setup in the language of Windows: Polish when the user interface is Polish, else English.
/// (The program runs with invariant globalization, so the UI language is read from Windows directly.)
/// </summary>
public static class Ui
{
    [DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();

    /// <summary>The language chosen in the settings (the installer saves it), else the language of Windows.</summary>
    public static bool Polish { get; set; } = Chosen() switch
    {
        "pl" => true,
        "en" => false,
        _ => OperatingSystem.IsWindows() && (GetUserDefaultUILanguage() & 0x3FF) == 0x15,
    };

    static string? Chosen()
    {
        try { return Settings.Load(Settings.DefaultPath).Language; }
        catch (Exception) { return null; }
    }

    public static string T(string english, string polish) => Polish ? polish : english;
}
