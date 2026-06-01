using UnityEngine;
using System.Collections;
using System.IO;
using System.Net;
using System.Threading;

public class MJPEGStream : MonoBehaviour
{
    public Renderer screen;
    public string publicUrl =
        "http://100.94.87.80:8080/stream";

    private const int READ_TIMEOUT_MS = 1000;
    private const int CONNECT_TIMEOUT_MS = 3000;
    private const int RETRY_DELAY_MS = 1000;
    private const int FRAME_BUFFER_SIZE = 300000;
    private const int READ_BUFFER_SIZE = 32768;

    private Texture2D _tex;
    private volatile byte[] _latestFrame;
    private volatile bool _streamConnected = false;

    // Controls the background thread
    private volatile bool _threadRunning = false;
    private Thread _streamThread;

    // ─────────────────────────────────────────────────────────
    void Start()
    {
        ALog("MJPEGStream", "Start()");

        Application.runInBackground = true;
        Application.targetFrameRate = 120;
        QualitySettings.vSyncCount = 0;

        if (screen == null)
        {
            ALog("MJPEGStream", "ERROR: screen is NULL");
            return;
        }

        screen.material = new Material(screen.material);

        _tex = new Texture2D(
                              2, 2,
                              TextureFormat.RGB24, false);
        _tex.wrapMode = TextureWrapMode.Clamp;
        _tex.filterMode = FilterMode.Bilinear;

        screen.material.mainTexture = _tex;

        StartStreamThread();
        StartCoroutine(ApplyFrames());
    }

    // ── Public: called by ModeManager ────────────────────────
    public void RestartStream()
    {
        ALog("MJPEGStream", "RestartStream() called");
        StopStreamThread();
        // Short pause so old thread fully exits
        StartCoroutine(DelayedThreadStart(0.2f));
    }

    IEnumerator DelayedThreadStart(float delay)
    {
        yield return new WaitForSeconds(delay);
        StartStreamThread();
    }

    // ── Thread management ─────────────────────────────────────
    void StartStreamThread()
    {
        if (_threadRunning)
        {
            ALog("MJPEGStream",
                 "StartStreamThread: already running");
            return;
        }

        _threadRunning = true;
        _streamThread = new Thread(StreamThread);
        _streamThread.IsBackground = true;
        _streamThread.Start();

        ALog("MJPEGStream",
             $"Thread started → {publicUrl}");
    }

    void StopStreamThread()
    {
        _threadRunning = false;
        ALog("MJPEGStream", "Thread stop requested");
    }

    // ── Background stream thread ──────────────────────────────
    void StreamThread()
    {
        while (_threadRunning)
        {
            HttpWebResponse response = null;
            Stream stream = null;

            try
            {
                ALog("MJPEGStream", "Connecting...");

                var request =
                    (HttpWebRequest)
                    WebRequest.Create(publicUrl);

                request.Timeout = CONNECT_TIMEOUT_MS;
                request.KeepAlive = false;
                request.ProtocolVersion =
                    HttpVersion.Version10;

                response = (HttpWebResponse)
                            request.GetResponse();
                stream = response.GetResponseStream();
                stream.ReadTimeout = READ_TIMEOUT_MS;

                _streamConnected = true;
                ALog("MJPEGStream", "Connected OK");

                byte[] readBuf = new byte[READ_BUFFER_SIZE];
                byte[] frameBuf = new byte[FRAME_BUFFER_SIZE];
                int frameIdx = 0;
                bool capturing = false;

                while (_threadRunning)
                {
                    int bytesRead = stream.Read(
                        readBuf, 0, readBuf.Length);

                    if (bytesRead <= 0) break;

                    for (int i = 0; i < bytesRead; i++)
                    {
                        // JPEG START marker FF D8
                        if (!capturing &&
                            i < bytesRead - 1 &&
                            readBuf[i] == 0xFF &&
                            readBuf[i + 1] == 0xD8)
                        {
                            capturing = true;
                            frameIdx = 0;
                        }

                        if (capturing)
                        {
                            if (frameIdx < frameBuf.Length)
                                frameBuf[frameIdx++] =
                                    readBuf[i];

                            // JPEG END marker FF D9
                            if (i < bytesRead - 1 &&
                                readBuf[i] == 0xFF &&
                                readBuf[i + 1] == 0xD9)
                            {
                                frameBuf[frameIdx++] =
                                    readBuf[i + 1];

                                byte[] newFrame =
                                    new byte[frameIdx];

                                System.Buffer.BlockCopy(
                                    frameBuf, 0,
                                    newFrame, 0,
                                    frameIdx);

                                _latestFrame = newFrame;
                                capturing = false;
                                i++;
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                _streamConnected = false;
                ALog("MJPEGStream",
                     $"Exception: {e.Message}");
            }
            finally
            {
                try { stream?.Close(); } catch { }
                try { response?.Close(); } catch { }
            }

            if (_threadRunning)
            {
                ALog("MJPEGStream",
                     $"Retry in {RETRY_DELAY_MS}ms");
                Thread.Sleep(RETRY_DELAY_MS);
            }
        }

        ALog("MJPEGStream", "Thread exited cleanly");
    }

    // ── Main thread: apply frames to texture ──────────────────
    IEnumerator ApplyFrames()
    {
        while (true)
        {
            while (_latestFrame != null)
            {
                byte[] frame = _latestFrame;
                _latestFrame = null;
                _tex.LoadImage(frame, false);
            }

            // Re-assign every frame in case
            // material was recreated
            if (screen != null)
                screen.material.mainTexture = _tex;

            yield return null;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────
    void OnDestroy()
    {
        StopStreamThread();
        if (_tex != null) Destroy(_tex);
    }

    void OnDisable()
    {
        StopStreamThread();
    }

    void OnEnable()
    {
        // Only restart if we were already initialised
        // (Start has run — tex exists)
        if (_tex != null && !_threadRunning)
            StartStreamThread();
    }

    // ── Android logcat ────────────────────────────────────────
    static void ALog(string tag, string msg)
    {
        Debug.Log($"[{tag}] {msg}");

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var logClass =
                new AndroidJavaClass("android.util.Log");
            logClass.CallStatic<int>("d", tag, msg);
        }
        catch { }
#endif
    }
}