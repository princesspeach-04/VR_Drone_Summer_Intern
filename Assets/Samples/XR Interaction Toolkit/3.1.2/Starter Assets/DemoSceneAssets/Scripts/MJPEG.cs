using UnityEngine;
using System.Collections;
using System.IO;
using System.Net;
using System.Threading;

public class MJPEGStream : MonoBehaviour
{
    public string url = "http://172.20.10.2:8080";
    public Renderer screen;

    private Texture2D tex;
    private volatile byte[] latestFrame;
    private volatile bool streamConnected = false;
    private volatile int frameCount = 0;

    void Start()
    {
        Debug.Log("[MJPEG] Start() called");
        Debug.Log("[MJPEG] URL: " + url);
        Debug.Log("[MJPEG] Screen assigned: " + (screen != null));

        Application.runInBackground = true;
        Application.targetFrameRate = 120;
        QualitySettings.vSyncCount = 0;

        if (screen == null)
        {
            Debug.LogError("[MJPEG] SCREEN IS NULL!");
            return;
        }

        screen.material = new Material(screen.material);
        tex = new Texture2D(640, 480, TextureFormat.RGB24, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        screen.material.mainTexture = tex;

        Thread thread = new Thread(StreamThread);
        thread.IsBackground = true;
        thread.Start();

        StartCoroutine(ApplyFrames());
        StartCoroutine(StatusLogger());
    }

    IEnumerator StatusLogger()
    {
        while (true)
        {
            yield return new WaitForSeconds(3f);
            Debug.Log("[MJPEG] STATUS — Connected: " + streamConnected +
                      " | Frames: " + frameCount);
        }
    }

    void StreamThread()
    {
        while (true)
        {
            try
            {
                Debug.Log("[MJPEG] Attempting connection...");

                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 10000;
                request.KeepAlive = true;
                request.AllowAutoRedirect = true;

                // Force HTTP/1.0 — avoids chunked transfer issues on Android
                request.ProtocolVersion = HttpVersion.Version10;

                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                Stream stream = response.GetResponseStream();

                streamConnected = true;
                Debug.Log("[MJPEG] Connected! Status: " + response.StatusCode);

                byte[] buffer = new byte[8192];
                byte[] frameBuffer = new byte[200000];
                int frameIndex = 0;
                bool capturing = false;

                while (true)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0) continue;

                    for (int i = 0; i < bytesRead; i++)
                    {
                        if (!capturing && i < bytesRead - 1 &&
                            buffer[i] == 0xFF && buffer[i + 1] == 0xD8)
                        {
                            capturing = true;
                            frameIndex = 0;
                        }

                        if (capturing)
                        {
                            if (frameIndex < frameBuffer.Length)
                                frameBuffer[frameIndex++] = buffer[i];

                            if (i < bytesRead - 1 &&
                                buffer[i] == 0xFF && buffer[i + 1] == 0xD9)
                            {
                                if (frameIndex < frameBuffer.Length)
                                    frameBuffer[frameIndex++] = buffer[i + 1];

                                byte[] newFrame = new byte[frameIndex];
                                System.Buffer.BlockCopy(frameBuffer, 0, newFrame, 0, frameIndex);
                                latestFrame = newFrame;
                                frameCount++;

                                if (frameCount <= 5)
                                    Debug.Log("[MJPEG] Frame #" + frameCount +
                                              " size: " + frameIndex + " bytes");

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
                Debug.LogError("[MJPEG] ERROR: " + e.Message + " — retrying in 2s");
                Thread.Sleep(2000);
            }
        }
    }

    IEnumerator ApplyFrames()
    {
        while (true)
        {
            if (latestFrame != null)
            {
                byte[] frame = latestFrame;
                latestFrame = null;

                bool loaded = tex.LoadImage(frame);
                if (loaded && screen != null)
                    screen.material.mainTexture = tex;
                else if (!loaded)
                    Debug.LogWarning("[MJPEG] LoadImage failed");
            }

            yield return new WaitForEndOfFrame();
        }
    }
}