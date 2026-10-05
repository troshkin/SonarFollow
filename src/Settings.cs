using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

enum DeviceMode { Sonar, Direct, Ignore }

class DeviceSetting
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Mode { get; set; }
}

class AppSettings
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonarFollow");
    public const string FileName = "settings.json";
    public static readonly string Path = System.IO.Path.Combine(Dir, FileName);

    public bool SyncVolume { get; set; }
    public string NewDeviceMode { get; set; }
    public List<DeviceSetting> Devices { get; set; }

    public AppSettings()
    {
        SyncVolume = true;
        NewDeviceMode = "sonar";
        Devices = new List<DeviceSetting>();
    }

    // Returns null when the file exists but can't be read (e.g. mid-write).
    public static AppSettings TryLoad()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(Path));
            if (s.Devices == null) s.Devices = new List<DeviceSetting>();
            return s;
        }
        catch (Exception e)
        {
            Log.Write("Can't read settings: " + e.Message);
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, Indent(new JavaScriptSerializer().Serialize(this)));
        if (File.Exists(Path)) File.Replace(tmp, Path, null);
        else File.Move(tmp, Path);
    }

    // Device IDs change when drivers are reinstalled, so fall back to the name.
    public DeviceSetting Find(string id, string name)
    {
        return Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? Devices.FirstOrDefault(d => d.Name == name);
    }

    public DeviceMode ModeFor(string id, string name)
    {
        var d = Find(id, name);
        return ParseMode(d != null ? d.Mode : NewDeviceMode);
    }

    public static DeviceMode ParseMode(string mode)
    {
        switch ((mode ?? "").ToLowerInvariant())
        {
            case "direct": return DeviceMode.Direct;
            case "ignore": return DeviceMode.Ignore;
            default: return DeviceMode.Sonar;
        }
    }

    public static string ModeName(DeviceMode mode)
    {
        return mode.ToString().ToLowerInvariant();
    }

    static string Indent(string json)
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\') sb.Append(json[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; sb.Append(c); break;
                case '{':
                case '[':
                    if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                    {
                        sb.Append(c).Append(json[++i]);
                        break;
                    }
                    sb.Append(c).Append("\r\n").Append(' ', ++depth * 2);
                    break;
                case '}':
                case ']': sb.Append("\r\n").Append(' ', --depth * 2).Append(c); break;
                case ',': sb.Append(",\r\n").Append(' ', depth * 2); break;
                case ':': sb.Append(": "); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
