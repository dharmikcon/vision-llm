using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Linq;
using UnityEngine.SceneManagement;
public class Configration : MonoBehaviour
{
    public TMP_InputField promptInputField;
    public TMP_Dropdown googleLLMModelDropdown;
    public Button startButton;
    public Button clearButton;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        promptInputField.text = AppConstant.DEMO_PROMPT;
        googleLLMModelDropdown.options = AppConstant.GoogleLLMModels.ToList().Select(model => new TMP_Dropdown.OptionData(model)).ToList();
        startButton.onClick.AddListener(StartButtonClicked);
        clearButton.onClick.AddListener(ClearButtonClicked);    
    }

    void StartButtonClicked()
    {
        AppConstant.GOOGLE_MODEL = AppConstant.GoogleLLMModels[googleLLMModelDropdown.value];
        AppConstant.PROMPT = promptInputField.text;
        Debug.Log("StartButtonClicked");
        SceneManager.LoadScene(1);
    }

    void ClearButtonClicked()
    {
        Debug.Log("ClearButtonClicked");
        promptInputField.text = AppConstant.DEMO_PROMPT;
        googleLLMModelDropdown.value = 0;
    }
}
