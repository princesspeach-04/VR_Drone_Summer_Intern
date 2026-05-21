using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Text;

public class MJPEGStream : MonoBehaviour
{
    public Renderer screen;
    public string jetsonHostname = "100.116.179.56";
    public int jetsonPort = 8080;

    private Texture2D tex;
    private const int FRAME_BUFFER_SIZE = 400000;

    void Start()
    {
        if (screen == null) { Debug.LogError("[MJPEG] Screen is null"); return; }

        screen.material = new Material(screen.material);
        tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        screen.material.mainTexture = tex;

        StartCoroutine(StreamCoroutine());
    }

    IEnumerator StreamCoroutine()
    {
        string url = $"http://{jetsonHostname}:{jetsonPort}/stream";

        while (true)
        {
            Debug.Log("[MJPEG] Connecting: " + url);

            using var req = new UnityWebRequest(url, "GET");

            // Use a custom download handler that feeds us raw bytes
            var handler = new MJPEGDownloadHandler(tex, screen);
            req.downloadHandler = handler;
            req.timeout = 0; // no timeout — persistent stream

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning("[MJPEG] Error: " + req.error + " — retrying in 2s");

            yield return new WaitForSeconds(2f);
        }
    }
}

public class MJPEGDownloadHandler : DownloadHandlerScript
{
    private Texture2D tex;
    private Renderer screen;
    private byte[] frameBuf = new byte[400000];
    private int frameIndex;
    private bool capturing;
    private int frameCount;

    // Preallocate the receive buffer
    public MJPEGDownloadHandler(Texture2D tex, Renderer screen)
        : base(new byte[16384])
    {
        this.tex = tex;
        this.screen = screen;
    }

    // Called on the main thread each time Unity receives a chunk
    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        for (int i = 0; i < dataLength; i++)
        {
            // Detect SOI  0xFF 0xD8
            if (!capturing && i < dataLength - 1 &&
                data[i] == 0xFF && data[i + 1] == 0xD8)
            {
                capturing = true;
                frameIndex = 0;
            }

            if (!capturing) continue;

            if (frameIndex < frameBuf.Length)
                frameBuf[frameIndex++] = data[i];
            else
            {
                Debug.LogWarning("[MJPEG] Buffer overflow — reset");
                capturing = false; frameIndex = 0;
                continue;
            }

            // Detect EOI  0xFF 0xD9
            if (i < dataLength - 1 &&
                data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                frameBuf[frameIndex++] = data[i + 1]; // include 0xD9
                i++;

                byte[] frame = new byte[frameIndex];
                System.Buffer.BlockCopy(frameBuf, 0, frame, 0, frameIndex);

                // Already on main thread — load directly
                if (tex.LoadImage(frame))
                    screen.material.mainTexture = tex;

                frameCount++;
                capturing = false;
            }
        }
        return true; // return false to abort
    }

    protected override void CompleteContent()
    {
        Debug.Log("[MJPEG] Stream ended after " + frameCount + " frames");
    }
}