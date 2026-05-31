using UnityEngine;
using System.Collections;
using System.IO;
using System.Net;
using System.Threading;

public class MJPEGStream : MonoBehaviour
{
    public Renderer screen;

    // USE TAILSCALE OR LOCAL IP
    public string publicUrl =
        "http://100.94.87.80:8080/stream";

    private const int READ_TIMEOUT_MS     = 1000;
    private const int CONNECT_TIMEOUT_MS  = 3000;
    private const int RETRY_DELAY_MS      = 1000;

    private const int FRAME_BUFFER_SIZE   = 300000;
    private const int READ_BUFFER_SIZE    = 32768;

    private Texture2D tex;

    private volatile byte[] latestFrame;

    private volatile bool streamConnected = false;

    void Start()
    {
        Debug.Log("[MJPEG] Starting stream");

        Application.runInBackground = true;
        Application.targetFrameRate = 120;

        QualitySettings.vSyncCount = 0;

        if (screen == null)
        {
            Debug.LogError("SCREEN NULL");
            return;
        }

        screen.material = new Material(screen.material);

        tex = new Texture2D(
            2,
            2,
            TextureFormat.RGB24,
            false
        );

        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        screen.material.mainTexture = tex;

        Thread streamThread =
            new Thread(StreamThread);

        streamThread.IsBackground = true;
        streamThread.Start();

        StartCoroutine(ApplyFrames());
    }

    void StreamThread()
    {
        while (true)
        {
            HttpWebResponse response = null;
            Stream stream = null;

            try
            {
                Debug.Log("[MJPEG] Connecting");

                HttpWebRequest request =
                    (HttpWebRequest)
                    WebRequest.Create(publicUrl);

                request.Timeout = CONNECT_TIMEOUT_MS;

                request.KeepAlive = false;

                request.ProtocolVersion =
                    HttpVersion.Version10;

                response =
                    (HttpWebResponse)
                    request.GetResponse();

                stream =
                    response.GetResponseStream();

                stream.ReadTimeout =
                    READ_TIMEOUT_MS;

                streamConnected = true;

                Debug.Log("[MJPEG] Connected");

                byte[] readBuf =
                    new byte[READ_BUFFER_SIZE];

                byte[] frameBuf =
                    new byte[FRAME_BUFFER_SIZE];

                int frameIndex = 0;

                bool capturing = false;

                while (true)
                {
                    int bytesRead =
                        stream.Read(
                            readBuf,
                            0,
                            readBuf.Length
                        );

                    if (bytesRead <= 0)
                        break;

                    for (int i = 0; i < bytesRead; i++)
                    {
                        // JPEG START
                        if (!capturing &&
                            i < bytesRead - 1 &&
                            readBuf[i] == 0xFF &&
                            readBuf[i + 1] == 0xD8)
                        {
                            capturing = true;
                            frameIndex = 0;
                        }

                        if (capturing)
                        {
                            if (frameIndex < frameBuf.Length)
                            {
                                frameBuf[frameIndex++] =
                                    readBuf[i];
                            }

                            // JPEG END
                            if (i < bytesRead - 1 &&
                                readBuf[i] == 0xFF &&
                                readBuf[i + 1] == 0xD9)
                            {
                                frameBuf[frameIndex++] =
                                    readBuf[i + 1];

                                byte[] newFrame =
                                    new byte[frameIndex];

                                System.Buffer.BlockCopy(
                                    frameBuf,
                                    0,
                                    newFrame,
                                    0,
                                    frameIndex
                                );

                                // REPLACE OLD FRAME
                                latestFrame = newFrame;

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

                Debug.LogWarning(
                    "[MJPEG] Reconnecting: " +
                    e.Message
                );
            }
            finally
            {
                try { stream?.Close(); } catch { }
                try { response?.Close(); } catch { }
            }

            Thread.Sleep(RETRY_DELAY_MS);
        }
    }

    IEnumerator ApplyFrames()
    {
        while (true)
        {
            // DROP OLD FRAMES
            while (latestFrame != null)
            {
                byte[] frame = latestFrame;

                latestFrame = null;

                tex.LoadImage(
                    frame,
                    false
                );
            }

            screen.material.mainTexture = tex;

            yield return null;
        }
    }
}