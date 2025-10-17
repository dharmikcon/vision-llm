using JetBrains.Annotations;
using UnityEngine;

public static class AppConstant 
{
    public static string GOOGLE_API_KEY = "AIzaSyDUO6pHlBubUP-pc_6hQELvl9Ll7naJjIU";
    public static string GOOGLE_MODEL = "gemini-2.5-flash-image";
    public static string GOOGLE_API_VERSION = "v1beta";
    public static string GOOGLE_API_URL = "https://generativelanguage.googleapis.com";
    public static string PROMPT = "There is any interesting acitivity is happning replay in yes or no. if yes then send the details about it.";
    public const string DEMO_GOOGLE_API_KEY = "AIzaSyDUO6pHlBubUP-pc_6hQELvl9Ll7naJjIU";
    public const string DEMO_GOOGLE_MODEL = "gemini-2.5-flash-image";
    public const string DEMO_GOOGLE_API_VERSION = "v1beta";
    public const string DEMO_GOOGLE_API_URL = "https://generativelanguage.googleapis.com";
    public const string DEMO_PROMPT = "There is any interesting acitivity is happning replay in yes or no. if yes then send the details about it.";

    public static string[] GoogleLLMModels = 
    {
        "gemini-flash-lite-latest",
        "gemini-2.5-flash-image",
        "gemini-2.5-flash",
        "gemini-2.5-pro"
    };
}