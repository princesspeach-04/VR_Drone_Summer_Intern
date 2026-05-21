using UnityEngine;
using System.Collections;
using System.IO;
using System.Net;
using System.Threading;

public class MJPEGStream : MonoBehaviour
{
    public Renderer screen;

    // Jetson advertises itself as this hostname via avahi/mDNS
    // Works on any network — no IP, no UDP, no config ever needed
    public string jetsonHostname = "100.116.179.56";
    public int    jetsonPort     = 8080;

    private const int READ_TIMEOUT_MS    = 3000;
    private const int CONNECT_TIMEOUT_MS = 10000;
    private const int RETRY_DELAY_MS     = 2000;
    private const int FRAME_BUFFER_SIZE  = 400000;
    private const int READ_BUFFER_SIZE   = 16384;

    private Texture2D tex;
    private volatile byte[] latestFrame;
    private volatile bool   streamConnected = false;
    private volatile int    frameCount      = 0;

    void Start()
    {
        Debug.Log("[MJPEG] Start() — connecting to " + jetsonHostname);
        Application.runInBackground = true;
        Application.targetFrameRate = 120;
        QualitySettings.vSyncCount  = 0;

        if (screen == null) { Debug.LogError("[MJPEG] SCREEN IS NULL!"); return; }

        screen.material             = new Material(screen.material);
        tex                         = new Texture2D(640, 480, TextureFormat.RGB24, false);
        tex.wrapMode                = TextureWrapMode.Clamp;
        tex.filterMode              = FilterMode.Trilinear;
        screen.material.mainTexture = tex;

        Thread streamThread = new Thread(StreamThread);
        streamThread.IsBackground = true;
        streamThread.Start();

        StartCoroutine(ApplyFrames());
        StartCoroutine(StatusLogger());
    }

    void StreamThread()
    {
        string url = "http://" + jetsonHostname + ":" + jetsonPort;

        while (true)
        {
            HttpWebResponse response = null;
            Stream stream            = null;

            try
            {
                Debug.Log("[MJPEG] Connecting to: " + url);

                HttpWebRequest request   = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout         = CONNECT_TIMEOUT_MS;
                request.KeepAlive       = true;
                request.ProtocolVersion = HttpVersion.Version10;

                response           = (HttpWebResponse)request.GetResponse();
                stream             = response.GetResponseStream();
                stream.ReadTimeout = READ_TIMEOUT_MS;

                streamConnected = true;
                Debug.Log("[MJPEG] Connected to " + url);

                byte[] readBuf  = new byte[READ_BUFFER_SIZE];
                byte[] frameBuf = new byte[FRAME_BUFFER_SIZE];
                int  frameIndex = 0;
                bool capturing  = false;

                while (true)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = stream.Read(readBuf, 0, readBuf.Length);
                    }
                    catch (IOException ioEx)
                    {
                        Debug.LogWarning("[MJPEG] Read error: " + ioEx.Message + " — reconnecting");
                        break;
                    }

                    if (bytesRead <= 0) { Debug.LogWarning("[MJPEG] 0 bytes — reconnecting"); break; }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        // SOI 0xFF 0xD8
                        if (!capturing && i < bytesRead - 1 &&
                            readBuf[i] == 0xFF && readBuf[i + 1] == 0xD8)
                        {
                            capturing  = true;
                            frameIndex = 0;
                        }

                        if (capturing)
                        {
                            if (frameIndex < frameBuf.Length)
                                frameBuf[frameIndex++] = readBuf[i];
                            else
                            {
                                Debug.LogWarning("[MJPEG] Frame buffer overflow — resetting");
                                capturing  = false;
                                frameIndex = 0;
                                continue;
                            }

                            // EOI 0xFF 0xD9
                            if (i < bytesRead - 1 &&
                                readBuf[i] == 0xFF && readBuf[i + 1] == 0xD9)
                            {
                                if (frameIndex < frameBuf.Length)
                                    frameBuf[frameIndex++] = readBuf[i + 1];

                                byte[] newFrame = new byte[frameIndex];
                                System.Buffer.BlockCopy(frameBuf, 0, newFrame, 0, frameIndex);
                                latestFrame = newFrame;
                                frameCount++;

                                if (frameCount <= 5 || frameCount % 100 == 0)
                                    Debug.Log("[MJPEG] Frame #" + frameCount +
                                              " | " + frameIndex + " bytes");

                                capturing = false;
                                i++;
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                streamConnected = false;
                Debug.LogError("[MJPEG] Error: " + e.Message + " — retrying in " + RETRY_DELAY_MS + "ms");
            }
            finally
            {
                try { stream?.Close(); }   catch { }
                try { response?.Close(); } catch { }
                streamConnected = false;
            }

            Thread.Sleep(RETRY_DELAY_MS);
        }
    }

    IEnumerator ApplyFrames()
    {
        while (true)
        {
            if (latestFrame != null)
            {
                byte[] frame = latestFrame;
                latestFrame  = null;
                if (tex.LoadImage(frame))
                    screen.material.mainTexture = tex;
                else
                    Debug.LogWarning("[MJPEG] LoadImage failed");
            }
            yield return new WaitForEndOfFrame();
        }
    }

    IEnumerator StatusLogger()
    {
        while (true)
        {
            yield return new WaitForSeconds(3f);
            Debug.Log("[MJPEG] STATUS — Connected: " + streamConnected +
                      " | Frames: " + frameCount +
                      " | Host: "   + jetsonHostname);
        }
    }
}