using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

class SonarDevice
{
    public string Id;
    public string Name;
    public string Flow;
    public string Role;
    public bool IsVad;
    public bool IsActive;
}

// Client for the local, undocumented Sonar HTTP API.
static class Sonar
{
    static string url;

    public static string GetMode()
    {
        string mode = Get("/mode");
        return mode == null ? null : mode.Trim('"', ' ', '\r', '\n');
    }

    public static List<SonarDevice> GetDevices()
    {
        string json = Get("/audioDevices");
        if (json == null) return null;
        return new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(json)
            .Select(d => new SonarDevice
            {
                Id = (string)d["id"],
                Name = (string)d["friendlyName"],
                Flow = (string)d["dataFlow"],
                Role = (string)d["role"],
                IsVad = (bool)d["isVad"],
                IsActive = (string)d["state"] == "active"
            }).ToList();
    }

    public static string FindVad(List<SonarDevice> devices, string role)
    {
        return devices.Where(d => d.IsVad && d.Flow == "render" && d.Role == role)
                      .Select(d => d.Id).FirstOrDefault();
    }

    public static string Get(string path) { return Request("GET", path); }
    public static void Put(string path) { Request("PUT", path); }

    // Sonar's port changes on every restart, so rediscover it once on failure.
    static string Request(string method, string path)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string baseUrl = url ?? (url = Discover());
            if (baseUrl == null) return null;
            try { return Http(method, baseUrl + path, 3000); }
            catch (WebException) { url = null; }
        }
        return null;
    }

    static string Http(string method, string address, int timeout)
    {
        var req = (HttpWebRequest)WebRequest.Create(address);
        req.Method = method;
        req.Timeout = timeout;
        req.Proxy = null;
        if (method == "PUT") req.ContentLength = 0;
        using (var resp = req.GetResponse())
        using (var reader = new StreamReader(resp.GetResponseStream()))
            return reader.ReadToEnd();
    }

    // The GG endpoint that advertises Sonar's address needs TLS 1.3, which .NET
    // Framework can't do, so find the port Sonar's own process listens on instead.
    static string Discover()
    {
        var pids = new HashSet<int>(Process.GetProcessesByName("SteelSeriesSonar").Select(p => p.Id));
        if (pids.Count == 0) return null;

        var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        string output;
        using (var p = Process.Start(psi)) { output = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }

        foreach (Match m in Regex.Matches(output, @"127\.0\.0\.1:(\d+)\s+\S+\s+LISTENING\s+(\d+)"))
        {
            if (!pids.Contains(int.Parse(m.Groups[2].Value))) continue;
            string candidate = "http://127.0.0.1:" + m.Groups[1].Value;
            try
            {
                Http("GET", candidate + "/mode", 1000);
                return candidate;
            }
            catch (WebException) { }
        }
        return null;
    }
}
