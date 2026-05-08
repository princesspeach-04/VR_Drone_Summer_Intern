using UnityEngine;
using System.Diagnostics;

public class VideoStream : MonoBehaviour
{
    Process process;

    void Start()
    {
        process = new Process();
        process.StartInfo.FileName = "C:\\Users\\Arni\\Downloads\\ffmpeg-2026-05-06-git-f2e5eff3ff-essentials_build\\ffmpeg-2026-05-06-git-f2e5eff3ff-essentials_build\\bin\\ffplay.exe";
        process.StartInfo.Arguments = "-fflags nobuffer -flags low_delay -framedrop udp://@:5000";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = false;

        process.Start();
    }

    void OnApplicationQuit()
    {
        if (process != null && !process.HasExited)
        {
            process.Kill();
        }
    }
}