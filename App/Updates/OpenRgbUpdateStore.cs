using System.Security.Cryptography;
using System.Text.Json;

namespace RgbStudio.Updates;

internal sealed record OpenRgbInstallation(
    string Version, string Directory, string Executable, string ExecutableSha256,
    string PackageSha256, string Source, DateTimeOffset InstalledAt);

internal sealed record OpenRgbInstallations(
    OpenRgbInstallation Current, OpenRgbInstallation? Previous = null);

internal sealed record OpenRgbUpdatePreferences
{
    public bool Automatic { get; init; } = true;
    public DateTimeOffset? LastCheckUtc { get; init; }
}

/// <summary>
/// Las versiones oficiales se instalan aparte: nunca se sobrescribe la copia
/// personalizada ni un ejecutable que OpenRGB pueda tener abierto.
/// </summary>
internal sealed class OpenRgbUpdateStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private readonly object _gate = new();

    internal string Root { get; }
    internal string ManifestPath => Path.Combine(Root, "installation.json");
    private string PreferencesPath => Path.Combine(Root, "updates.json");

    internal OpenRgbUpdateStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RgbStudio", "OpenRGB"));
    }

    internal OpenRgbUpdatePreferences Preferences
    {
        get
        {
            lock (_gate)
                return Read<OpenRgbUpdatePreferences>(PreferencesPath) ?? new();
        }
    }

    internal void SetAutomatic(bool enabled)
    {
        lock (_gate) Write(PreferencesPath, Preferences with { Automatic = enabled });
    }

    internal void RecordCheck(DateTimeOffset time)
    {
        lock (_gate) Write(PreferencesPath, Preferences with { LastCheckUtc = time });
    }

    internal OpenRgbInstallations? ReadInstallations() => Read<OpenRgbInstallations>(ManifestPath);

    internal OpenRgbInstallation? Current()
    {
        OpenRgbInstallations? saved = ReadInstallations();
        if (saved is null) return null;
        if (IsUsable(saved.Current)) return saved.Current;
        return saved.Previous is not null && IsUsable(saved.Previous) ? saved.Previous : null;
    }

    internal bool IsUsable(OpenRgbInstallation? installation)
    {
        if (installation is null) return false;
        try
        {
            if (!System.Version.TryParse(installation.Version, out _)) return false;
            string executable = ExecutablePath(installation);
            using FileStream stream = File.OpenRead(executable);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            return actual.Equals(installation.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal string ExecutablePath(OpenRgbInstallation installation)
    {
        string directory = Child(Path.Combine(Root, "versions"), installation.Directory);
        string executable = Child(directory, installation.Executable);
        if (!Path.GetFileName(executable).Equals("OpenRGB.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El manifiesto no apunta a OpenRGB.exe.");
        return executable;
    }

    internal void Activate(OpenRgbInstallation installation, OpenRgbInstallation? previous)
        => Write(ManifestPath, new OpenRgbInstallations(installation, previous));

    internal static string Child(string parent, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Split('/', '\\').Any(
                part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || part.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) >= 0
                    || System.Text.RegularExpressions.Regex.IsMatch(part,
                        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Ruta no válida dentro de la instalación de OpenRGB.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(parent, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La ruta sale de la carpeta de OpenRGB.");
        return path;
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Format));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
