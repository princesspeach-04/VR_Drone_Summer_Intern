using Gree.UnityWebView;
using UnityEngine;

public class QuestBrowserView : MonoBehaviour
{
    private WebViewObject webViewObject;

    void Start()
    {
        webViewObject = gameObject.AddComponent<WebViewObject>();

        webViewObject.Init(
            cb: (msg) =>
            {
                Debug.Log("Message: " + msg);
            },

            err: (msg) =>
            {
                Debug.LogError("Error: " + msg);
            },

            started: (msg) =>
            {
                Debug.Log("Started: " + msg);
            },

            hooked: (msg) =>
            {
                Debug.Log("Hooked: " + msg);
            }
        );

        webViewObject.SetMargins(0, 0, 0, 0);

        webViewObject.SetVisibility(true);

        webViewObject.LoadURL("https://vr.noobravenpeach.in");
    }
}