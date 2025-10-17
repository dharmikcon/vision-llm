using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using System;
using System.Threading;
using System.Threading.Tasks;

public class QwenVision : MonoBehaviour
{
    private async void Start()
    {
        string url = "http://localhost:8000/generate";
        WWWForm form = new WWWForm();
        form.AddField("prompt", "Describe this image.");

        byte[] img = File.ReadAllBytes(@"C:\Users\dhgor\Downloads\cat.jpg");
        form.AddBinaryData("file", img, "cat.jpg", "image/jpg");

        Debug.Log("Sending request to " + url);
        try
        {
            string responseText = await SendFormAndWaitForTextAsync(url, form);
            Debug.Log(responseText);
        }
        catch (Exception ex)
        {
            Debug.LogError($"LLM request failed: {ex.Message}");
        }
    }

    private static async Task<string> SendFormAndWaitForTextAsync(string url, WWWForm form, CancellationToken cancellationToken = default)
    {
        using (UnityWebRequest req = UnityWebRequest.Post(url, form))
        {
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    req.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }

#if UNITY_2020_1_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                throw new Exception($"HTTP error {req.responseCode}: {req.error}");
            }

            return req.downloadHandler.text;
        }
    }
}
