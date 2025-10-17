using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;
// Drop this on any GameObject and call the example in Start(), or from your own code.
public class OllamaGemmaVision : MonoBehaviour
{
    public enum Provider
    {
        Ollama,
        Google
    }

    [Header("Provider")]
    public Provider provider = Provider.Ollama;
    [Header("Ollama Settings")]
    public string host = "http://localhost:11434"; // Ollama default
    public string model = "gemma3:4b";                // or "gemma3:2b", etc.
    public bool useChatEndpoint = true;            // true = /api/chat, false = /api/generate

    [Header("Google AI Studio Settings")]
	public string googleHost = "https://generativelanguage.googleapis.com";
	public string googleApiVersion = "v1beta"; // e.g. v1 or v1beta
	public string googleModel = "gemini-2.5-flash"; // or "gemini-2.5-pro"
    public string googleApiKey = "";               // set your API key here
	public bool googleListModelsAtStart = false;     // optional: log supported models at Start

    [Header("Test Input (optional)")]
    public string question = "What is in this image?";
    public string imagePath = @"C:\Users\dhgor\OneDrive\Desktop\MergedSnapshot_2025-07-22_16-07-26-038.png";
    public RawImage rawImage;
    public TextMeshProUGUI questionText;
    public TextMeshProUGUI modelText;
    public TMP_Text responseText;
    private Coroutine _webcamStreamCoroutine;
    private WebCamTexture _webcamStreamTexture;
    private Texture2D _webcamCpuTexture;
    private int _webRequestsInFlight;
    private bool _useFrontCamera = true;
    private bool _currentDeviceIsFrontFacing;
    private string _lastQuestion;
    private int _lastFps, _lastWidth, _lastHeight, _lastMaxInFlight, _lastBatchSize, _lastJpgQuality;
    private Action<string> _lastOnSuccess;
    private Action<string> _lastOnError;
    public Button toggleCameraButton;
    public Button backButton;
    void Start()
    {
        backButton.onClick.AddListener(BackButtonClicked);
        question = AppConstant.PROMPT;
        questionText.text = question;
        googleModel = AppConstant.GOOGLE_MODEL;
        if (provider == Provider.Google)
        {
            modelText.text = "Provider: Google | Model: " + googleModel;
			if (googleListModelsAtStart && !string.IsNullOrEmpty(googleApiKey))
			{
				StartCoroutine(GoogleListModels(models =>
				{
					Debug.Log("[Gemini] Available models:\n" + models);
				}, err => Debug.LogWarning("[Gemini] ListModels failed: " + err)));
			}
        }
        else
        {
            modelText.text = "Provider: Ollama | Model: " + model;
        }
		// Demo: start continuous non-blocking stream
		StartWebcamContinuous(question, 5, reply =>
		{
			responseText.text+="\n"+reply;
		}, err =>
		{
			Debug.LogError(err);
		}, null, 640, 480, 2, 1, 80);
        toggleCameraButton.onClick.AddListener(ToggleCamera);
    }

    void OnDisable()
    {
		StopWebcamContinuous();
        toggleCameraButton.onClick.RemoveListener(ToggleCamera);
    }

    void BackButtonClicked()
    {
        StopWebcamContinuous();
        SceneManager.LoadScene(0);
    }

    public IEnumerator AskImageQuestion(string questionText, string localImagePath, Action<string> onSuccess, Action<string> onError = null, bool nonBlocking = false)
    {
        if (string.IsNullOrEmpty(localImagePath) || !File.Exists(localImagePath))
        {
            onError?.Invoke($"Image not found: {localImagePath}");
            yield break;
        }

        // 1) Read + base64 the image
        string base64;
        try
        {
            byte[] bytes = File.ReadAllBytes(localImagePath);
            base64 = Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            onError?.Invoke($"Failed to read image: {ex.Message}");
            yield break;
        }

        // 2) Build JSON body
        string json;
        string url;
        if (useChatEndpoint)
        {
            // /api/chat expects messages[i].content as a STRING, and images as base64 array.
            var body = new ChatBody
            {
                model = model,
                messages = new[]
                {
                    new ChatMessage
                    {
                        role = "user",
                        content = questionText,
                        images = new[] { base64 }
                    }
                },
                stream = false
            };
            json = JsonUtility.ToJson(body);
            url = $"{host}/api/chat";
        }
        else
        {
            // /api/generate uses prompt + images.
            var body = new GenerateBody
            {
                model = model,
                prompt = questionText,
                images = new[] { base64 },
                stream = false
            };
            json = JsonUtility.ToJson(body);
            url = $"{host}/api/generate";
        }

        // 3) POST
		if (nonBlocking)
		{
			var req = new UnityWebRequest(url, "POST");
			byte[] payload = Encoding.UTF8.GetBytes(json);
			Debug.Log($"[LLM] POST {url} payload {(payload.Length / 1024f):F1} KB (non-blocking)");
			req.uploadHandler = new UploadHandlerRaw(payload);
			req.downloadHandler = new DownloadHandlerBuffer();
			req.SetRequestHeader("Content-Type", "application/json");
			req.SetRequestHeader("Accept", "application/json");
			req.timeout = 180; // seconds
			// req.chunkedTransfer is obsolete; default is fine

			var op = req.SendWebRequest();
			op.completed += _ =>
			{
#if UNITY_2020_2_OR_NEWER
				bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
				bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif
				if (hasNetworkError)
				{
					onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
					req.Dispose();
					return;
				}

				string text = req.downloadHandler.text;
				if (!string.IsNullOrEmpty(text))
				{
					var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
					Debug.Log($"[LLM] Response preview: {preview}");
				}
				try
				{
					string reply;
					if (useChatEndpoint)
					{
						var resp = JsonUtility.FromJson<OllamaChatResponse>(text);
						reply = resp != null && resp.message != null ? resp.message.content : FallbackStreamless(text);
					}
					else
					{
						var resp = JsonUtility.FromJson<OllamaGenerateResponse>(text);
						reply = resp != null && !string.IsNullOrEmpty(resp.response) ? resp.response : FallbackStreamless(text);
					}
					onSuccess?.Invoke(reply ?? "(no content)");
				}
				catch (Exception ex)
				{
					onError?.Invoke($"Parse error: {ex.Message}\nRaw: {text}");
				}
				finally
				{
					req.Dispose();
				}
			};

			yield break;
		}
		else
		{
			using (var req = new UnityWebRequest(url, "POST"))
			{
				byte[] payload = Encoding.UTF8.GetBytes(json);
				Debug.Log($"[LLM] POST {url} payload {(payload.Length / 1024f):F1} KB");
				req.uploadHandler = new UploadHandlerRaw(payload);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");
				req.SetRequestHeader("Accept", "application/json");
				req.timeout = 180; // seconds
				// req.chunkedTransfer is obsolete; default is fine

				yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
				bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
				bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif

				if (hasNetworkError)
				{
					onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
					yield break;
				}

				string text = req.downloadHandler.text;
				if (!string.IsNullOrEmpty(text))
				{
					var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
					Debug.Log($"[LLM] Response preview: {preview}");
				}
				try
				{
					// 4) Parse response and extract assistant text
					string reply;
					if (useChatEndpoint)
					{
						var resp = JsonUtility.FromJson<OllamaChatResponse>(text);
						reply = resp != null && resp.message != null ? resp.message.content : FallbackStreamless(text);
					}
					else
					{
						var resp = JsonUtility.FromJson<OllamaGenerateResponse>(text);
						reply = resp != null && !string.IsNullOrEmpty(resp.response) ? resp.response : FallbackStreamless(text);
					}

					onSuccess?.Invoke(reply ?? "(no content)");
				}
				catch (Exception ex)
				{
					onError?.Invoke($"Parse error: {ex.Message}\nRaw: {text}");
				}
			}
		}
    }

    public IEnumerator AskImagesQuestion(string questionText, IList<string> base64Images, Action<string> onSuccess, Action<string> onError = null, bool nonBlocking = false)
    {
        if (base64Images == null || base64Images.Count == 0)
        {
            onError?.Invoke("No images provided");
            yield break;
        }

        string json;
        string url;
        if (useChatEndpoint)
        {
            var body = new ChatBody
            {
                model = model,
                messages = new[]
                {
                    new ChatMessage
                    {
                        role = "user",
                        content = questionText,
                        images = base64Images is string[] arr ? arr : new List<string>(base64Images).ToArray()
                    }
                },
                stream = false
            };
            json = JsonUtility.ToJson(body);
            url = $"{host}/api/chat";
        }
        else
        {
            var body = new GenerateBody
            {
                model = model,
                prompt = questionText,
                images = base64Images is string[] arr ? arr : new List<string>(base64Images).ToArray(),
                stream = false
            };
            json = JsonUtility.ToJson(body);
            url = $"{host}/api/generate";
        }

		if (nonBlocking)
		{
			var req = new UnityWebRequest(url, "POST");
			Debug.Log($"[LLM] Sending {base64Images.Count} images to {(useChatEndpoint ? "/api/chat" : "/api/generate")} (non-blocking)");
			byte[] payload = Encoding.UTF8.GetBytes(json);
			Debug.Log($"[LLM] POST {url} payload {(payload.Length / 1024f):F1} KB");
			req.uploadHandler = new UploadHandlerRaw(payload);
			req.downloadHandler = new DownloadHandlerBuffer();
			req.SetRequestHeader("Content-Type", "application/json");
			req.SetRequestHeader("Accept", "application/json");
			req.timeout = 180; // seconds
			// req.chunkedTransfer is obsolete; default is fine

			var op = req.SendWebRequest();
			op.completed += _ =>
			{
#if UNITY_2020_2_OR_NEWER
				bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
				bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif
				if (hasNetworkError)
				{
					onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
					req.Dispose();
					return;
				}

				string text = req.downloadHandler.text;
				if (!string.IsNullOrEmpty(text))
				{
					var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
					Debug.Log($"[LLM] Response preview: {preview}");
				}
				try
				{
					string reply;
					if (useChatEndpoint)
					{
						var resp = JsonUtility.FromJson<OllamaChatResponse>(text);
						reply = resp != null && resp.message != null ? resp.message.content : FallbackStreamless(text);
					}
					else
					{
						var resp = JsonUtility.FromJson<OllamaGenerateResponse>(text);
						reply = resp != null && !string.IsNullOrEmpty(resp.response) ? resp.response : FallbackStreamless(text);
					}

					onSuccess?.Invoke(reply ?? "(no content)");
				}
				catch (Exception ex)
				{
					onError?.Invoke($"Parse error: {ex.Message}\nRaw: {text}");
				}
				finally
				{
					req.Dispose();
				}
			};

			yield break;
		}
		else
		{
			using (var req = new UnityWebRequest(url, "POST"))
			{
				Debug.Log($"[LLM] Sending {base64Images.Count} images to {(useChatEndpoint ? "/api/chat" : "/api/generate")} ");
				byte[] payload = Encoding.UTF8.GetBytes(json);
				Debug.Log($"[LLM] POST {url} payload {(payload.Length / 1024f):F1} KB");
				req.uploadHandler = new UploadHandlerRaw(payload);
				req.downloadHandler = new DownloadHandlerBuffer();
				req.SetRequestHeader("Content-Type", "application/json");
				req.SetRequestHeader("Accept", "application/json");
				req.timeout = 180; // seconds
				// req.chunkedTransfer is obsolete; default is fine

				yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
				bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
				bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif

				if (hasNetworkError)
				{
					onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
					yield break;
				}

				string text = req.downloadHandler.text;
				if (!string.IsNullOrEmpty(text))
				{
					var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
					Debug.Log($"[LLM] Response preview: {preview}");
				}
				try
				{
					string reply;
					if (useChatEndpoint)
					{
						var resp = JsonUtility.FromJson<OllamaChatResponse>(text);
						reply = resp != null && resp.message != null ? resp.message.content : FallbackStreamless(text);
					}
					else
					{
						var resp = JsonUtility.FromJson<OllamaGenerateResponse>(text);
						reply = resp != null && !string.IsNullOrEmpty(resp.response) ? resp.response : FallbackStreamless(text);
					}

					onSuccess?.Invoke(reply ?? "(no content)");
				}
				catch (Exception ex)
				{
					onError?.Invoke($"Parse error: {ex.Message}\nRaw: {text}");
				}
			}
		}
    }

    // ---------- Google AI Studio (Gemini) ----------
    public IEnumerator AskGoogleImageQuestion(string questionText, string localImagePath, Action<string> onSuccess, Action<string> onError = null, bool nonBlocking = false)
    {
        if (string.IsNullOrEmpty(localImagePath) || !File.Exists(localImagePath))
        {
            onError?.Invoke($"Image not found: {localImagePath}");
            yield break;
        }

        string base64;
        try
        {
            byte[] bytes = File.ReadAllBytes(localImagePath);
            base64 = Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            onError?.Invoke($"Failed to read image: {ex.Message}");
            yield break;
        }

        var list = new List<string>(1) { base64 };
        yield return AskGoogleImagesQuestion(questionText, list, onSuccess, onError, nonBlocking);
    }

    public IEnumerator AskGoogleImagesQuestion(string questionText, IList<string> base64Images, Action<string> onSuccess, Action<string> onError = null, bool nonBlocking = false)
    {
        if (string.IsNullOrEmpty(googleApiKey))
        {
            onError?.Invoke("Google API key is empty");
            yield break;
        }
        if (string.IsNullOrEmpty(questionText))
        {
            onError?.Invoke("Question text is empty");
            yield break;
        }
        if (base64Images == null || base64Images.Count == 0)
        {
            onError?.Invoke("No images provided");
            yield break;
        }

        // Build Gemini generateContent request (manual JSON to honor oneof constraints)
        string json = BuildGoogleGenerateContentJson(questionText, base64Images);
        Debug.Log($"[Gemini] Request JSON: {json.Substring(0, Mathf.Min(json.Length, 600))}{(json.Length>600?"...":"")}");
        // Force v1beta for 2.5 models if user set v1
        string versionForRequest = googleApiVersion;
        if (!string.IsNullOrEmpty(googleModel) && googleModel.Contains("2.5") && googleApiVersion == "v1")
        {
            versionForRequest = "v1beta";
        }
        string url = $"{googleHost}/{versionForRequest}/models/{googleModel}:generateContent?key={googleApiKey}";

        if (nonBlocking)
        {
            var req = new UnityWebRequest(url, "POST");
            byte[] payload = Encoding.UTF8.GetBytes(json);
            Debug.Log($"[Gemini] POST {url} payload {(payload.Length / 1024f):F1} KB (non-blocking)");
            req.uploadHandler = new UploadHandlerRaw(payload);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 180;
			// req.chunkedTransfer is obsolete; default is fine

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
#if UNITY_2020_2_OR_NEWER
                bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
                bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif
                if (hasNetworkError)
                {
                    var txt = req.downloadHandler.text;
                    if (req.responseCode == 404)
                    {
                        txt += "\nHint: Model not found for this API version. Try 'gemini-1.5-flash-001' or run ListModels.";
                    }
                    onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{txt}");
                    req.Dispose();
                    return;
                }

                string text = req.downloadHandler.text;
                if (!string.IsNullOrEmpty(text))
                {
                    var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                    Debug.Log($"[Gemini] Response preview: {preview}");
                }
                try
                {
                    string reply = ParseGoogleReply(text);
                    onSuccess?.Invoke(reply ?? "(no content)");
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Parse error: {ex.Message}\nRaw: {text}");
                }
                finally
                {
                    req.Dispose();
                }
            };

            yield break;
        }
        else
        {
            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] payload = Encoding.UTF8.GetBytes(json);
                Debug.Log($"[Gemini] POST {url} payload {(payload.Length / 1024f):F1} KB");
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");
                req.timeout = 180;
				// req.chunkedTransfer is obsolete; default is fine

                yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
                bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif
                if (hasNetworkError)
                {
                    var txt = req.downloadHandler.text;
                    if (req.responseCode == 404)
                    {
                        txt += "\nHint: Model not found for this API version. Try 'gemini-1.5-flash-001' or run ListModels.";
                    }
                    onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{txt}");
                    yield break;
                }

                string text = req.downloadHandler.text;
                if (!string.IsNullOrEmpty(text))
                {
                    var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                    Debug.Log($"[Gemini] Response preview: {preview}");
                }
                try
                {
                    string reply = ParseGoogleReply(text);
                    onSuccess?.Invoke(reply ?? "(no content)");
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Parse error: {ex.Message}\nRaw: {text}");
                }
            }
        }
    }

    private string ParseGoogleReply(string raw)
    {
        // Try to parse Gemini response candidates -> content.parts[].text, fallback to raw
        var resp = JsonUtility.FromJson<GoogleGenerateContentResponse>(raw);
        if (resp != null && resp.candidates != null && resp.candidates.Length > 0 &&
            resp.candidates[0] != null && resp.candidates[0].content != null && resp.candidates[0].content.parts != null)
        {
            var parts = resp.candidates[0].content.parts;
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i].text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(parts[i].text);
                }
            }
            if (sb.Length > 0) return sb.ToString();
        }
        return raw;
    }

    private string BuildGoogleGenerateContentJson(string questionText, IList<string> base64Images)
    {
        var sb = new StringBuilder();
        sb.Append("{\"contents\":[");
        // text content
        sb.Append("{\"role\":\"user\",\"parts\":[{\"text\":\"");
        sb.Append(EscapeJsonString(questionText ?? string.Empty));
        sb.Append("\"}]} ");
        // image contents
        if (base64Images != null)
        {
            for (int i = 0; i < base64Images.Count; i++)
            {
                string b64 = base64Images[i] ?? string.Empty;
                sb.Append(",{\"role\":\"user\",\"parts\":[{\"inlineData\":{");
                sb.Append("\"mimeType\":\"image/jpeg\",");
                sb.Append("\"data\":\"");
                sb.Append(EscapeJsonString(b64));
                sb.Append("\"}}]}");
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private IEnumerator GoogleListModels(Action<string> onSuccess, Action<string> onError)
    {
        if (string.IsNullOrEmpty(googleApiKey))
        {
            onError?.Invoke("Google API key is empty");
            yield break;
        }
        string url = $"{googleHost}/{googleApiVersion}/models?key={googleApiKey}";
        using (var req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 60;
            yield return req.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
            bool hasNetworkError = req.result != UnityWebRequest.Result.Success;
#else
            bool hasNetworkError = req.isNetworkError || req.isHttpError;
#endif
            if (hasNetworkError)
            {
                onError?.Invoke($"HTTP error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                yield break;
            }
            onSuccess?.Invoke(req.downloadHandler.text);
        }
    }

    public IEnumerator AskWebcamBurst(string questionText, int fps, float durationSeconds, Action<string> onSuccess, Action<string> onError = null, string deviceName = null, int requestedWidth = 640, int requestedHeight = 480)
    {
        if (fps <= 0) fps = 5;
        if (durationSeconds <= 0f) durationSeconds = 2f;

        // Ensure webcam permission where required
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                onError?.Invoke("Webcam authorization denied");
                yield break;
            }
        }

        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            onError?.Invoke("No webcam devices found");
            yield break;
        }

        WebCamTexture webcam;
        if (string.IsNullOrEmpty(deviceName))
        {
            webcam = new WebCamTexture(requestedWidth, requestedHeight, fps);
        }
        else
        {
            webcam = new WebCamTexture(deviceName, requestedWidth, requestedHeight, fps);
        }

        webcam.Play();

        // Wait briefly for camera to warm up
        float warmupStart = Time.time;
        while (webcam.width <= 16 && Time.time - warmupStart < 2f)
        {
            yield return null;
        }

        if (webcam.width <= 16)
        {
            onError?.Invoke("Webcam failed to start or resolution unavailable");
            webcam.Stop();
            yield break;
        }

		// Display live webcam feed in UI and size RawImage to texture
		if (rawImage != null)
		{
			rawImage.texture = webcam;
			var rect = rawImage.rectTransform;
			if (rect != null)
			{
				rect.sizeDelta = new Vector2(webcam.width, webcam.height);
			}
		}

        var images = new List<string>();
        var tex = new Texture2D(webcam.width, webcam.height, TextureFormat.RGB24, false);

        int targetFrames = Mathf.Max(1, Mathf.RoundToInt(durationSeconds * fps));
        float interval = 1f / Mathf.Max(1, fps);
		for (int i = 0; i < targetFrames; i++)
        {
            yield return new WaitForSeconds(interval);
            var pixels = webcam.GetPixels32();
            if (pixels == null || pixels.Length == 0)
            {
                continue;
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
			byte[] jpg = tex.EncodeToJPG(80);
			if (jpg != null && jpg.Length > 0)
            {
				images.Add(Convert.ToBase64String(jpg));
            }
        }

		Debug.Log($"[Webcam] Captured frames: {images.Count}/{targetFrames} at {webcam.width}x{webcam.height}");

        if (images.Count == 0)
        {
            onError?.Invoke("Captured zero frames");
			// keep preview running even if no frames captured
			yield break;
        }

		// Free temp CPU texture; preview continues via WebCamTexture
		Destroy(tex);

		// Fire the request without blocking this coroutine
		StartCoroutine(AskVisionImagesQuestion(questionText, images, onSuccess, onError, true));
        yield break;
    }

    public void StartWebcamContinuous(string questionText, int fps, Action<string> onSuccess, Action<string> onError = null, string deviceName = null, int requestedWidth = 640, int requestedHeight = 480, int maxInFlight = 2, int batchSize = 1, int jpgQuality = 80)
    {
		// remember params for restarts (e.g., toggle front/back)
		_lastQuestion = questionText;
		_lastFps = fps;
		_lastOnSuccess = onSuccess;
		_lastOnError = onError;
		_lastWidth = requestedWidth;
		_lastHeight = requestedHeight;
		_lastMaxInFlight = maxInFlight;
		_lastBatchSize = batchSize;
		_lastJpgQuality = jpgQuality;

		if (_webcamStreamCoroutine != null)
		{
			StopCoroutine(_webcamStreamCoroutine);
			_webcamStreamCoroutine = null;
		}
		_webcamStreamCoroutine = StartCoroutine(WebcamContinuousCoroutine(questionText, fps, onSuccess, onError, deviceName, requestedWidth, requestedHeight, maxInFlight, batchSize, jpgQuality));
    }

    public void StopWebcamContinuous()
    {
		if (_webcamStreamCoroutine != null)
		{
			StopCoroutine(_webcamStreamCoroutine);
			_webcamStreamCoroutine = null;
		}
		if (_webcamCpuTexture != null)
		{
			Destroy(_webcamCpuTexture);
			_webcamCpuTexture = null;
		}
		if (_webcamStreamTexture != null)
		{
			if (_webcamStreamTexture.isPlaying) _webcamStreamTexture.Stop();
			_webcamStreamTexture = null;
		}
    }

    // Toggle between front and back cameras and restart the stream with same parameters
    public void ToggleCamera()
    {
		_useFrontCamera = !_useFrontCamera;
		StopWebcamContinuous();
		StartWebcamContinuous(_lastQuestion ?? question, _lastFps > 0 ? _lastFps : 5, _lastOnSuccess, _lastOnError, null, _lastWidth > 0 ? _lastWidth : 640, _lastHeight > 0 ? _lastHeight : 480, _lastMaxInFlight > 0 ? _lastMaxInFlight : 2, _lastBatchSize > 0 ? _lastBatchSize : 1, _lastJpgQuality > 0 ? _lastJpgQuality : 80);
    }

    private IEnumerator WebcamContinuousCoroutine(string questionText, int fps, Action<string> onSuccess, Action<string> onError, string deviceName, int requestedWidth, int requestedHeight, int maxInFlight, int batchSize, int jpgQuality)
    {
		if (fps <= 0) fps = 5;
		if (batchSize <= 0) batchSize = 1;
		if (jpgQuality < 1 || jpgQuality > 100) jpgQuality = 80;

		// Ensure webcam permission where required
		if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
		{
			yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
			if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
			{
				onError?.Invoke("Webcam authorization denied");
				yield break;
			}
		}

		var devices = WebCamTexture.devices;
		if (devices == null || devices.Length == 0)
		{
			onError?.Invoke("No webcam devices found");
			yield break;
		}

		string selectedName = deviceName;
		_currentDeviceIsFrontFacing = false;
		var allDevices = WebCamTexture.devices;
		if (string.IsNullOrEmpty(selectedName))
		{
			for (int i = 0; i < allDevices.Length; i++)
			{
				if (allDevices[i].isFrontFacing == _useFrontCamera)
				{
					selectedName = allDevices[i].name;
					_currentDeviceIsFrontFacing = allDevices[i].isFrontFacing;
					break;
				}
			}
			if (string.IsNullOrEmpty(selectedName) && allDevices.Length > 0)
			{
				selectedName = allDevices[0].name;
				_currentDeviceIsFrontFacing = allDevices[0].isFrontFacing;
			}
		}
		else
		{
			for (int i = 0; i < allDevices.Length; i++)
			{
				if (allDevices[i].name == selectedName)
				{
					_currentDeviceIsFrontFacing = allDevices[i].isFrontFacing;
					break;
				}
			}
		}

		_webcamStreamTexture = new WebCamTexture(selectedName, requestedWidth, requestedHeight, fps);

		_webcamStreamTexture.Play();

		// Wait briefly for camera to warm up
		float warmupStart = Time.time;
		while (_webcamStreamTexture.width <= 16 && Time.time - warmupStart < 2f)
		{
			yield return null;
		}

		if (_webcamStreamTexture.width <= 16)
		{
			onError?.Invoke("Webcam failed to start or resolution unavailable");
			StopWebcamContinuous();
			yield break;
		}

		// Display live webcam feed in UI with proper orientation and mirroring
		if (rawImage != null)
		{
			rawImage.texture = _webcamStreamTexture;
			ApplyWebcamOrientationToRawImage(_webcamStreamTexture);
		}

		_webcamCpuTexture = new Texture2D(_webcamStreamTexture.width, _webcamStreamTexture.height, TextureFormat.RGB24, false);
		float interval = 1f / Mathf.Max(1, fps);
		var imagesBatch = new List<string>(batchSize);

		while (true)
		{
			// pacing
			yield return new WaitForSeconds(interval);

			// backpressure on network requests
			if (_webRequestsInFlight >= maxInFlight)
			{
				continue;
			}

			var pixels = _webcamStreamTexture.GetPixels32();
			if (pixels == null || pixels.Length == 0)
			{
				continue;
			}

			// Transform pixels for LLM: conditional vertical flip, rotation, and optional mirror
			int outW, outH;
			var transformed = TransformPixelsForLlm(
				pixels,
				_webcamStreamTexture.width,
				_webcamStreamTexture.height,
				_webcamStreamTexture.videoVerticallyMirrored,
				_webcamStreamTexture.videoRotationAngle,
				_currentDeviceIsFrontFacing,
				out outW,
				out outH);

			if (_webcamCpuTexture == null || _webcamCpuTexture.width != outW || _webcamCpuTexture.height != outH)
			{
				if (_webcamCpuTexture != null) Destroy(_webcamCpuTexture);
				_webcamCpuTexture = new Texture2D(outW, outH, TextureFormat.RGB24, false);
			}

			_webcamCpuTexture.SetPixels32(transformed);
			_webcamCpuTexture.Apply(false, false);
			byte[] jpg = _webcamCpuTexture.EncodeToJPG(jpgQuality);
			if (jpg == null || jpg.Length == 0)
			{
				continue;
			}

			imagesBatch.Add(Convert.ToBase64String(jpg));
			if (imagesBatch.Count < batchSize)
			{
				continue;
			}

			var toSend = new List<string>(imagesBatch);
			imagesBatch.Clear();

			_webRequestsInFlight++;
			Action<string> onSuccessWrap = reply =>
			{
				try { onSuccess?.Invoke(reply); }
				finally { _webRequestsInFlight--; }
			};
			Action<string> onErrorWrap = err =>
			{
				try { onError?.Invoke(err); }
				finally { _webRequestsInFlight--; }
			};

			// fire-and-forget request
			StartCoroutine(AskVisionImagesQuestion(questionText, toSend, onSuccessWrap, onErrorWrap, true));
		}
    }

    // ---------- Routing helpers (use selected Provider) ----------
    public IEnumerator AskVisionImageQuestion(string questionText, string localImagePath, Action<string> onSuccess, Action<string> onError = null, bool nonBlocking = false)
    {
        if (provider == Provider.Google)
        {
            return AskGoogleImageQuestion(questionText, localImagePath, onSuccess, onError, nonBlocking);
        }
        return AskImageQuestion(questionText, localImagePath, onSuccess, onError, nonBlocking);
    }

    public IEnumerator AskVisionImagesQuestion(string questionText, IList<string> base64Images, Action<string> onSuccess, Action<string> onError = null, bool nonBlocking = false)
    {
        if (provider == Provider.Google)
        {
            return AskGoogleImagesQuestion(questionText, base64Images, onSuccess, onError, nonBlocking);
        }
        return AskImagesQuestion(questionText, base64Images, onSuccess, onError, nonBlocking);
    }

    // If Ollama returns something unexpected (e.g., plugins/versions differ),
    // just return raw text to avoid blocking you.
    private string FallbackStreamless(string raw) => raw;

    // Applies orientation to RawImage for preview: vertical flip and optional mirror for front camera
    private void ApplyWebcamOrientationToRawImage(WebCamTexture cam)
    {
		if (rawImage == null || cam == null) return;
		var rect = rawImage.rectTransform;
		if (rect != null)
		{
			rect.sizeDelta = new Vector2(cam.width, cam.height);
		}
		// WebCamTexture on Android can be vertically flipped; adjust scaleY
		Vector3 scale = rawImage.rectTransform.localScale;
		scale.y = cam.videoVerticallyMirrored ? -Mathf.Abs(scale.y) : Mathf.Abs(scale.y);
		// Mirror horizontally for front-facing cameras so preview looks natural
		scale.x = _currentDeviceIsFrontFacing ? -Mathf.Abs(scale.x) : Mathf.Abs(scale.x);
		rawImage.rectTransform.localScale = scale;
		// Apply rotation: front cameras often need opposite sign to avoid upside-down preview
		int angle = cam.videoRotationAngle;
		rawImage.rectTransform.localEulerAngles = new Vector3(0, 0, _currentDeviceIsFrontFacing ? angle : -angle);
    }

    // In-place adjust pixel array for correct orientation and mirroring used for model input
    private void ApplyOrientationToPixels(Color32[] pixels, int width, int height, bool flipVertically, bool mirrorHorizontally)
    {
		// Flip vertically only when the device reports the texture as vertically mirrored
		if (flipVertically)
		{
			VerticalFlipInPlace(pixels, width, height);
		}
		if (mirrorHorizontally)
		{
			HorizontalMirrorInPlace(pixels, width, height);
		}
    }

    private void VerticalFlipInPlace(Color32[] pixels, int width, int height)
    {
		int rowHalf = height / 2;
		for (int y = 0; y < rowHalf; y++)
		{
			int topIndex = y * width;
			int bottomIndex = (height - 1 - y) * width;
			for (int x = 0; x < width; x++)
			{
				Color32 tmp = pixels[topIndex + x];
				pixels[topIndex + x] = pixels[bottomIndex + x];
				pixels[bottomIndex + x] = tmp;
			}
		}
    }

    private void HorizontalMirrorInPlace(Color32[] pixels, int width, int height)
    {
		int colHalf = width / 2;
		for (int y = 0; y < height; y++)
		{
			int rowStart = y * width;
			for (int x = 0; x < colHalf; x++)
			{
				int left = rowStart + x;
				int right = rowStart + (width - 1 - x);
				Color32 tmp = pixels[left];
				pixels[left] = pixels[right];
				pixels[right] = tmp;
			}
		}
    }

    private Color32[] TransformPixelsForLlm(Color32[] src, int width, int height, bool flipVertically, int rotationAngle, bool mirrorHorizontally, out int outWidth, out int outHeight)
    {
		// Step 1: optional vertical flip (in-place on a copy to keep src immutable for safety)
		var work = new Color32[src.Length];
		Array.Copy(src, work, src.Length);
		if (flipVertically)
		{
			VerticalFlipInPlace(work, width, height);
		}

		// Step 2: rotation
		rotationAngle = ((rotationAngle % 360) + 360) % 360;
		if (rotationAngle == 90 || rotationAngle == 270)
		{
			outWidth = height;
			outHeight = width;
		}
		else
		{
			outWidth = width;
			outHeight = height;
		}

		Color32[] rotated = new Color32[outWidth * outHeight];
		switch (rotationAngle)
		{
			case 0:
				Array.Copy(work, rotated, work.Length);
				break;
			case 90:
				// newX = y, newY = outHeight-1-x
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						int newX = y;
						int newY = outHeight - 1 - x;
						rotated[newY * outWidth + newX] = work[y * width + x];
					}
				}
				break;
			case 180:
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						int newX = outWidth - 1 - x;
						int newY = outHeight - 1 - y;
						rotated[newY * outWidth + newX] = work[y * width + x];
					}
				}
				break;
			case 270:
				// newX = outWidth-1 - y, newY = x
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						int newX = outWidth - 1 - y;
						int newY = x;
						rotated[newY * outWidth + newX] = work[y * width + x];
					}
				}
				break;
			default:
				Array.Copy(work, rotated, work.Length);
				break;
		}

		// Step 3: optional horizontal mirror
		if (mirrorHorizontally)
		{
			HorizontalMirrorInPlace(rotated, outWidth, outHeight);
		}

		return rotated;
    }

    // --------- DTOs for /api/chat ----------
    [Serializable]
    public class ChatBody
    {
        public string model;
        public ChatMessage[] messages;
        public bool stream = false;
    }

    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content; // must be a string for Ollama /api/chat
        public string[] images; // base64 images
    }

    [Serializable]
    public class OllamaChatResponse
    {
        public ChatMessageObj message;
        public bool done;

        [Serializable]
        public class ChatMessageObj
        {
            public string role;
            public string content;
        }
    }

    // --------- DTOs for /api/generate ----------
    [Serializable]
    public class GenerateBody
    {
        public string model;
        public string prompt;
        public string[] images;
        public bool stream = false;
    }

    [Serializable]
    public class OllamaGenerateResponse
    {
        public string response;
        public bool done;
    }

    // --------- DTOs for Google AI Studio (Gemini) ----------
    [Serializable]
    public class GoogleGenerateContentRequest
    {
        public GoogleContent[] contents;
    }

    [Serializable]
    public class GoogleContent
    {
        public string role; // "user"
        public GooglePart[] parts;
    }

    [Serializable]
    public class GooglePart
    {
        public string text; // optional
        public GoogleInlineData inlineData; // optional (note camelCase for API)
    }

    [Serializable]
    public class GoogleInlineData
    {
        public string mimeType;
        public string data; // base64
    }

    [Serializable]
    public class GoogleGenerateContentResponse
    {
        public GoogleCandidate[] candidates;
    }

    [Serializable]
    public class GoogleCandidate
    {
        public GoogleContent content;
    }
}
